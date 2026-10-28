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
        int mapWidth = MapWidth, int maxPrompts = MaxPrompts)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var block = Math.Max(1, (int)Math.Round(frame.Width / (double)Math.Max(1, mapWidth)));
        int mw = Math.Max(1, frame.Width / block), mh = Math.Max(1, frame.Height / block);
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
            near[my * mw + mx] = RouteHoldColor.Distance(lab, reference) < threshold;
        }

        var depth = InnerDepth(near, mw, mh);
        var label = new int[mw * mh];
        var regions = new List<(int Area, int Best, int Order)>();
        var queue = new Queue<int>();
        for (var start = 0; start < near.Length; start++)
        {
            if (!near[start] || label[start] != 0) continue;
            var id = regions.Count + 1;
            label[start] = id;
            queue.Enqueue(start);
            int area = 0, best = start;
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                area++;
                if (depth[i] > depth[best] || (depth[i] == depth[best] && i < best)) best = i;
                int x = i % mw, y = i / mw;
                if (x > 0) Visit(i - 1);
                if (x < mw - 1) Visit(i + 1);
                if (y > 0) Visit(i - mw);
                if (y < mh - 1) Visit(i + mw);
            }
            regions.Add((area, best, regions.Count));

            void Visit(int j)
            {
                if (!near[j] || label[j] != 0) return;
                label[j] = id;
                queue.Enqueue(j);
            }
        }

        return regions.OrderByDescending(r => r.Area).ThenBy(r => r.Order).Take(Math.Max(0, maxPrompts))
            .Select(r => new PointD((r.Best % mw + 0.5) * block, (r.Best / mw + 0.5) * block))
            .ToList();
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
        IReadOnlySet<Guid>? rejected = null)
    {
        var chosen = candidates
            .Where(c => rejected is null || !rejected.Contains(c.Id))
            .Where(c => c.IsControl || (c.Score >= RouteHoldColor.MinScore && c.Distance < tolerance))
            .ToList();

        var kept = new List<RouteHoldCandidate>();
        foreach (var c in chosen.OrderByDescending(c => c.IsControl).ThenByDescending(c => c.Mask.Area).ThenBy(c => c.Distance))
            if (!kept.Any(k => c.Mask.Intersection(k.Mask) >= FragmentOverlap * Math.Min(c.Mask.Area, k.Mask.Area)))
                kept.Add(c);

        return kept.OrderByDescending(c => c.IsControl).ThenBy(c => c.Distance).ThenBy(c => c.Centroid.Y).ThenBy(c => c.Centroid.X).ToList();
    }
}
