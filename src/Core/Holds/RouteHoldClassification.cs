using System.Collections.Immutable;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Holds;

/// <summary>
/// The route's colour (D048 §2, <c>PHASE_HOLD_TRACKING.md</c> S1, §10.2): the distance of two hold colours in OKLab with
/// the lightness weighted ½ (light and shade change L far more than the hue), the tolerance the review starts with and its
/// bounds. The numbers are T1's (two clips, preliminary, R-2): τ = 0.08 with a mask score ≥ 0.5.
/// </summary>
public static class RouteHoldColor
{
    public const double LightnessWeight = 0.5;
    public const double DefaultTolerance = 0.08;
    public const double MinTolerance = 0.02;
    public const double MaxTolerance = 0.20;

    /// <summary>A candidate's mask needs this model score to be selected by colour (the control hold always is).</summary>
    public const double MinScore = 0.5;

    public static double Distance(OkLabColor a, OkLabColor b)
    {
        double dl = LightnessWeight * (a.L - b.L), da = a.A - b.A, db = a.B - b.B;
        return Math.Sqrt(dl * dl + da * da + db * db);
    }
}

/// <summary>
/// The colour-map prompts (S1): the frame reduced to ≈ <see cref="MapWidth"/> px by block averages, every block within
/// <see cref="MapThreshold"/> of the reference colour, its 4-connected regions, and one prompt per region at its innermost
/// block (the farthest from the region's edge) — a point well inside a hold, not on its rim. The largest regions first, at
/// most <see cref="MaxPrompts"/>. Pure and deterministic; the prompts are frame pixels.
/// </summary>
public static class ColorPromptPlanner
{
    public const int MapWidth = 480;
    public const double MapThreshold = 0.10;
    public const int MaxPrompts = 300;

    public static IReadOnlyList<PointD> Plan(DecodedFrame frame, OkLabColor reference, double threshold = MapThreshold,
        int mapWidth = MapWidth, int maxPrompts = MaxPrompts) =>
        Plan(frame, reference, threshold, mapWidth, maxPrompts, splitDistance: null, largeRegionFraction: 0);

    /// <summary>
    /// D072 §4 (the D048 research variant «split»): as <see cref="Plan(DecodedFrame, OkLabColor, double, int, int)"/>, but a
    /// region larger than <paramref name="largeRegionFraction"/> of the frame's area is split again — its blocks within
    /// <paramref name="splitDistance"/> of the reference, their 4-connected parts, one prompt per part at its innermost block
    /// (found on the parts' own mask) — the parts in the region's place of the scan order. A large wall or panel of the route's
    /// colour no longer swallows the holds on it. Null <paramref name="splitDistance"/>: the plain planner.
    /// </summary>
    public static IReadOnlyList<PointD> Plan(DecodedFrame frame, OkLabColor reference, double threshold, int mapWidth, int maxPrompts,
        double? splitDistance, double largeRegionFraction)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var block = Math.Max(1, (int)Math.Round(frame.Width / (double)Math.Max(1, mapWidth)));
        int mw = Math.Max(1, frame.Width / block), mh = Math.Max(1, frame.Height / block);
        var distance = new double[mw * mh];
        var near = new bool[mw * mh];
        var pixels = frame.Pixels.Span;
        for (var my = 0; my < mh; my++)
        for (var mx = 0; mx < mw; mx++)
        {
            long b = 0, g = 0, r = 0;
            for (var y = my * block; y < (my + 1) * block; y++)
            for (var x = mx * block; x < (mx + 1) * block; x++)
            {
                var i = y * frame.Stride + x * DecodedFrame.BytesPerPixel;
                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
            }
            var n = (long)block * block;
            var lab = RouteColor.SrgbToOkLab((byte)((r + n / 2) / n), (byte)((g + n / 2) / n), (byte)((b + n / 2) / n));
            var d = RouteHoldColor.Distance(lab, reference);
            distance[my * mw + mx] = d;
            near[my * mw + mx] = d < threshold;
        }

        var largeBlocks = largeRegionFraction * mw * mh;
        var depth = InnerDepth(near, mw, mh);
        var regions = new List<(int Area, int Best, int Order)>();
        foreach (var (members, best) in Components(near, depth, mw, mh, null))
        {
            if (splitDistance is not { } split || members.Count <= largeBlocks)
            {
                regions.Add((members.Count, best, regions.Count));
                continue;
            }
            var inner = new bool[mw * mh];
            foreach (var i in members) inner[i] = distance[i] < split;
            var innerDepth = InnerDepth(inner, mw, mh);
            foreach (var (part, partBest) in Components(inner, innerDepth, mw, mh, members))
                regions.Add((part.Count, partBest, regions.Count));
        }

        return regions.OrderByDescending(r => r.Area).ThenBy(r => r.Order).Take(Math.Max(0, maxPrompts))
            .Select(r => new PointD((r.Best % mw + 0.5) * block, (r.Best / mw + 0.5) * block))
            .ToList();
    }

    /// <summary>The 4-connected regions of <paramref name="inside"/> in scan order (only those starting at one of
    /// <paramref name="among"/> when given), each with its deepest block (the first in scan order on ties).</summary>
    private static List<(List<int> Members, int Best)> Components(bool[] inside, int[] depth, int mw, int mh, List<int>? among)
    {
        var label = new bool[mw * mh];
        var result = new List<(List<int>, int)>();
        var queue = new Queue<int>();
        IEnumerable<int> starts = among is null ? Enumerable.Range(0, inside.Length) : among.OrderBy(i => i);
        foreach (var start in starts)
        {
            if (!inside[start] || label[start]) continue;
            label[start] = true;
            queue.Enqueue(start);
            var members = new List<int>();
            var best = start;
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                members.Add(i);
                if (depth[i] > depth[best] || (depth[i] == depth[best] && i < best)) best = i;
                int x = i % mw, y = i / mw;
                if (x > 0) Visit(i - 1);
                if (x < mw - 1) Visit(i + 1);
                if (y > 0) Visit(i - mw);
                if (y < mh - 1) Visit(i + mw);
            }
            result.Add((members, best));

            void Visit(int j)
            {
                if (!inside[j] || label[j]) return;
                label[j] = true;
                queue.Enqueue(j);
            }
        }
        return result;
    }

    /// <summary>For every block of a region, the chessboard distance to the nearest block outside it (the frame's edge
    /// counts as outside).</summary>
    private static int[] InnerDepth(bool[] inside, int w, int h)
    {
        var d = new int[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            if (!inside[i]) continue;
            d[i] = x == 0 || y == 0
                ? 1
                : 1 + Math.Min(Math.Min(d[i - 1], d[i - w]), Math.Min(d[i - w - 1], x < w - 1 ? d[i - w + 1] : 0));
        }
        for (var y = h - 1; y >= 0; y--)
        for (var x = w - 1; x >= 0; x--)
        {
            var i = y * w + x;
            if (!inside[i]) continue;
            d[i] = x == w - 1 || y == h - 1
                ? 1
                : Math.Min(d[i], 1 + Math.Min(Math.Min(d[i + 1], d[i + w]), Math.Min(d[i + w + 1], x > 0 ? d[i + w - 1] : 0)));
        }
        return d;
    }
}

/// <summary>A hold's mask kept as its bounding box (a frame-size mask per candidate would cost megabytes each).</summary>
public sealed class MaskCrop
{
    private MaskCrop(int frameWidth, int frameHeight, int x, int y, int width, int height, byte[] pixels, long area)
    {
        (FrameWidth, FrameHeight, X, Y, Width, Height, Pixels, Area) = (frameWidth, frameHeight, x, y, width, height, pixels, area);
    }

    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public long Area { get; }

    /// <summary>The non-zero pixels of a frame-size <paramref name="mask"/>, or null when it has none.</summary>
    public static MaskCrop? From(byte[] mask, int frameWidth, int frameHeight)
    {
        if (mask.Length != frameWidth * frameHeight) throw new ArgumentException("The mask doesn't have the frame's size.", nameof(mask));
        int x0 = frameWidth, y0 = frameHeight, x1 = -1, y1 = -1;
        long area = 0;
        for (var y = 0; y < frameHeight; y++)
        for (var x = 0; x < frameWidth; x++)
        {
            if (mask[y * frameWidth + x] == 0) continue;
            area++;
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }
        if (area == 0) return null;
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var pixels = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            pixels[y * w + x] = mask[(y + y0) * frameWidth + x + x0] != 0 ? (byte)1 : (byte)0;
        return new MaskCrop(frameWidth, frameHeight, x0, y0, w, h, pixels, area);
    }

    public bool Contains(int x, int y) =>
        x >= X && y >= Y && x < X + Width && y < Y + Height && Pixels[(y - Y) * Width + x - X] != 0;

    /// <summary>The mask at the frame's size again (for the colour samples of the accepted hold).</summary>
    public byte[] ToFrameMask()
    {
        var mask = new byte[FrameWidth * FrameHeight];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            mask[(y + Y) * FrameWidth + x + X] = Pixels[y * Width + x];
        return mask;
    }

    /// <summary>The pixels inside both masks.</summary>
    public long Intersection(MaskCrop other)
    {
        int x0 = Math.Max(X, other.X), y0 = Math.Max(Y, other.Y);
        int x1 = Math.Min(X + Width, other.X + other.Width), y1 = Math.Min(Y + Height, other.Y + other.Height);
        long count = 0;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
            if (Pixels[(y - Y) * Width + x - X] != 0 && other.Pixels[(y - other.Y) * other.Width + x - other.X] != 0) count++;
        return count;
    }

    public double IoU(MaskCrop other)
    {
        var inter = Intersection(other);
        var union = Area + other.Area - inter;
        return union == 0 ? 0 : (double)inter / union;
    }
}

/// <summary>One mask found on the clean frame: its outline (normalized), the colour of its surface
/// (<see cref="HoldColorSampler"/>), the distance of that colour to the route's reference and the model's score.
/// <see cref="IsControl"/>: the control hold itself (the user's click).</summary>
public sealed record RouteHoldCandidate(
    Guid Id,
    ImmutableArray<NormalizedPoint> Contour,
    NormalizedPoint Centroid,
    double Score,
    double AreaFraction,
    OkLabColor Surface,
    double Distance,
    MaskCrop Mask,
    bool IsControl);

/// <summary>
/// The selection rule (S1): the control hold always; any other candidate when its colour is closer to the reference than
/// the tolerance and its score at least <see cref="RouteHoldColor.MinScore"/>; the user's rejections never come back. A
/// fragment mostly (≥ <see cref="FragmentOverlap"/>) inside a larger selected mask is not selected twice. Ranked by colour
/// distance (the control hold first). A tolerance change re-runs only this rule — no model call, no new frame.
/// </summary>
public static class RouteHoldSelector
{
    public const double FragmentOverlap = 0.8;

    public static IReadOnlyList<RouteHoldCandidate> Select(IReadOnlyList<RouteHoldCandidate> candidates, double tolerance,
        IReadOnlySet<Guid>? rejected = null) => Select(candidates, tolerance, rejected, null, default);

    /// <summary>D072 §4: the rule with <paramref name="gates"/> (null: none) against the route's <paramref name="reference"/>
    /// colour — a candidate also needs its chroma and hue close enough (<see cref="RouteHoldGates.Admits"/>).</summary>
    public static IReadOnlyList<RouteHoldCandidate> Select(IReadOnlyList<RouteHoldCandidate> candidates, double tolerance,
        IReadOnlySet<Guid>? rejected, RouteHoldGates? gates, OkLabColor reference)
    {
        // Amendment 1: the route's group is found with the user's rejections still in place — removing a wrong hold in the
        // middle never cuts the route's holds beyond it — and the rejected ones leave afterwards.
        if (gates is { Grouped: true } && rejected is { Count: > 0 })
            return Select(candidates, tolerance, null, gates, reference).Where(c => !rejected.Contains(c.Id)).ToList();

        var chosen = candidates
            .Where(c => rejected is null || !rejected.Contains(c.Id))
            .Where(c => c.IsControl || (c.Score >= RouteHoldColor.MinScore && c.Distance < tolerance &&
                                        (gates is null || gates.Admits(c.Surface, reference))))
            .ToList();

        var kept = new List<RouteHoldCandidate>();
        foreach (var c in chosen.OrderByDescending(c => c.IsControl).ThenByDescending(c => c.Mask.Area).ThenBy(c => c.Distance))
            if (!kept.Any(k => c.Mask.Intersection(k.Mask) >= FragmentOverlap * Math.Min(c.Mask.Area, k.Mask.Area)))
                kept.Add(c);

        if (gates is { Grouped: true }) kept = RouteHoldGrouping.Keep(kept, gates.MaxHolds);
        else if (gates is not null && kept.Count > gates.MaxHolds) kept = kept.Take(gates.MaxHolds).ToList();

        return kept.OrderByDescending(c => c.IsControl).ThenBy(c => c.Distance).ThenBy(c => c.Centroid.Y).ThenBy(c => c.Centroid.X).ToList();
    }
}

/// <summary>
/// D072 amendment 1 (the owner: a route is a group of holds; the same colour elsewhere in the frame is another route): the
/// selected candidates grown into one group from the control hold — at each step the candidate whose edge is nearest to the
/// group joins it while that gap is at most <see cref="Link"/> — and at most <c>maxHolds</c> of them. A hold far from the
/// route's group is not proposed. Amendment 3: one fixed link, 20 % of the frame's long side (on a 1500 px canvas a neighbour
/// 200 px away joins, one 460 px away does not); the spacing-based link of amendments 1–2 (7 … 12 %) cut the route's own holds.
/// </summary>
public static class RouteHoldGrouping
{
    /// <summary>The largest gap between a hold and the route's group, as a fraction of the frame's long side.</summary>
    public const double Link = 0.20;

    public static List<RouteHoldCandidate> Keep(IReadOnlyList<RouteHoldCandidate> candidates, int maxHolds)
    {
        var control = candidates.FirstOrDefault(c => c.IsControl);
        if (control is null || candidates.Count <= 1) return candidates.Take(Math.Max(1, maxHolds)).ToList();
        var group = new List<RouteHoldCandidate> { control };
        var rest = candidates.Where(c => !ReferenceEquals(c, control)).ToList();
        while (rest.Count > 0 && group.Count < maxHolds)
        {
            var (next, gap) = rest.Select(c => (c, group.Min(g => Gap(c, g)))).MinBy(x => x.Item2);
            if (gap > Link) break;
            group.Add(next);
            rest.Remove(next);
        }
        return group;
    }

    /// <summary>The gap between two holds' edges (their centres' distance less their radii, at least 0), as a fraction of the
    /// frame's long side.</summary>
    public static double Gap(RouteHoldCandidate a, RouteHoldCandidate b)
    {
        var mask = a.Mask;
        double side = Math.Max(mask.FrameWidth, mask.FrameHeight);
        double dx = (a.Centroid.X - b.Centroid.X) * mask.FrameWidth, dy = (a.Centroid.Y - b.Centroid.Y) * mask.FrameHeight;
        double ra = Math.Sqrt(a.Mask.Area / Math.PI), rb = Math.Sqrt(b.Mask.Area / Math.PI);
        return Math.Max(0, Math.Sqrt(dx * dx + dy * dy) - ra - rb) / side;
    }
}

/// <summary>
/// D072 §4, the gates on top of the tolerance: a candidate's chroma at least <see cref="ChromaRatio"/> of the reference's (a
/// washed-out wall or a grey hold is not the route's colour) and, for a coloured route (the reference's chroma at least
/// <see cref="MinHueChroma"/>), at least <see cref="MinHueChroma"/> itself — a white, grey or black hold never joins a coloured
/// route — and its hue within <see cref="HueToleranceDegrees"/> of the reference's. For a dull route (black, white, grey) the
/// hue is noise and τ alone decides. The Euclidean τ alone admits a hue turn that grows as the chroma falls (≈ 57° at C 0.084).
/// <see cref="Grouped"/> / <see cref="MaxHolds"/>: <see cref="RouteHoldGrouping"/> (amendment 1).
/// </summary>
public sealed record RouteHoldGates(double ChromaRatio, double HueToleranceDegrees, double MinHueChroma, bool Grouped = false,
    int MaxHolds = int.MaxValue)
{
    /// <summary>The D048 research variant's thresholds (independent pilot I1–I7: −22 % false, no hold lost), D072 §4 as first
    /// shipped — since amendment 1 with its rule that a colourless candidate never joins a coloured route.</summary>
    public static readonly RouteHoldGates V1 = new(0.3, 30, 0.025);

    /// <summary>D072 amendment 1 (the owner's check of 1.5.0: «лучше отметить меньше, чем больше»): chroma 0.5, hue 20°, the
    /// route's group from the control hold, at most 15 proposals (a route has about 6–12 holds).</summary>
    public static readonly RouteHoldGates Strict = new(0.5, 20, 0.025, Grouped: true, MaxHolds: 15);

    public bool Admits(OkLabColor candidate, OkLabColor reference)
    {
        double cc = Chroma(candidate), cr = Chroma(reference);
        if (cc < ChromaRatio * cr) return false;
        if (cr < MinHueChroma) return true;
        if (cc < MinHueChroma) return false;
        var dh = Math.Abs(Math.Atan2(candidate.B, candidate.A) - Math.Atan2(reference.B, reference.A)) * 180 / Math.PI;
        if (dh > 180) dh = 360 - dh;
        return dh <= HueToleranceDegrees;
    }

    public static double Chroma(OkLabColor c) => Math.Sqrt(c.A * c.A + c.B * c.B);
}
