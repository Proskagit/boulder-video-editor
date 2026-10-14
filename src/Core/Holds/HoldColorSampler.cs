using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Holds;

/// <summary>
/// The colour samples of a confirmed hold (D034 §5), taken once from the frame it was marked on:
/// <list type="bullet">
/// <item><c>SurfaceLab</c> — the mask eroded by ~10 % of its equivalent radius (at least 2 px);</item>
/// <item><c>WallLab</c> — a ring outside the mask, ~15 % of the equivalent radius wide (at least 4 px), without the pixels
/// of the other holds of the clip;</item>
/// </list>
/// each in OKLab, the darkest and brightest 10 % (shadows, highlights, chalk) dropped, the median of L, a and b of the
/// rest. Distances are chessboard distances (in pixels). The frame is the BGRA picture the user sees.
/// </summary>
public static class HoldColorSampler
{
    public const double ErodeFraction = 0.10;
    public const int MinErode = 2;
    public const double RingFraction = 0.15;
    public const int MinRing = 4;
    public const double TrimFraction = 0.10;

    /// <param name="frame">The markup frame (BGRA, straight alpha).</param>
    /// <param name="mask">The hold's mask at the frame's size (non-zero = inside), e.g. its largest component.</param>
    /// <param name="otherHolds">The contours of the clip's other holds (normalized), excluded from the wall ring.</param>
    public static (OkLabColor Surface, OkLabColor Wall) Sample(DecodedFrame frame, byte[] mask, IEnumerable<IReadOnlyList<NormalizedPoint>> otherHolds)
    {
        int w = frame.Width, h = frame.Height;
        if (mask.Length != w * h) throw new ArgumentException("The mask doesn't have the frame's size.", nameof(mask));

        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        long area = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (mask[y * w + x] == 0) continue;
            area++;
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }
        if (area == 0) throw new ArgumentException("The mask is empty.", nameof(mask));

        var radius = Math.Sqrt(area / Math.PI);
        var erode = Math.Max(MinErode, (int)Math.Round(ErodeFraction * radius));
        var ring = Math.Max(MinRing, (int)Math.Round(RingFraction * radius));

        // A region around the mask with room for the ring; distances inside (to the outside) and outside (to the mask).
        int rx0 = Math.Max(0, x0 - ring - 1), ry0 = Math.Max(0, y0 - ring - 1);
        int rx1 = Math.Min(w - 1, x1 + ring + 1), ry1 = Math.Min(h - 1, y1 + ring + 1);
        int rw = rx1 - rx0 + 1, rh = ry1 - ry0 + 1;
        var inside = new bool[rw * rh];
        for (var y = 0; y < rh; y++)
        for (var x = 0; x < rw; x++)
            inside[y * rw + x] = mask[(y + ry0) * w + x + rx0] != 0;
        var toOutside = Chessboard(inside, rw, rh, target: false);   // inside pixels: distance to the nearest outside one
        var toMask = Chessboard(inside, rw, rh, target: true);       // outside pixels: distance to the mask

        var others = otherHolds.Select(p => p.Select(q => (X: q.X * w - 0.5, Y: q.Y * h - 0.5)).ToArray()).Where(p => p.Length >= 3).ToList();
        var surface = new List<OkLabColor>();
        var wall = new List<OkLabColor>();
        var wallAll = new List<OkLabColor>();
        var pixels = frame.Pixels.Span;
        for (var y = 0; y < rh; y++)
        for (var x = 0; x < rw; x++)
        {
            var i = y * rw + x;
            int fx = x + rx0, fy = y + ry0;
            if (inside[i])
            {
                if (toOutside[i] > erode) surface.Add(Lab(pixels, frame.Stride, fx, fy));
            }
            else if (toMask[i] <= ring)
            {
                var lab = Lab(pixels, frame.Stride, fx, fy);
                wallAll.Add(lab);
                if (!others.Any(p => Contains(p, fx, fy))) wall.Add(lab);
            }
        }

        if (surface.Count == 0)   // a hold thinner than the erosion: its whole mask
        {
            for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
                if (inside[y * rw + x]) surface.Add(Lab(pixels, frame.Stride, x + rx0, y + ry0));
        }
        var surfaceLab = RobustMedian(surface);
        var wallLab = wall.Count > 0 ? RobustMedian(wall) : wallAll.Count > 0 ? RobustMedian(wallAll) : surfaceLab;
        return (surfaceLab, wallLab);
    }

    /// <summary>The darkest and brightest <see cref="TrimFraction"/> by L dropped, then the median of each component.</summary>
    public static OkLabColor RobustMedian(IReadOnlyList<OkLabColor> colors)
    {
        if (colors.Count == 0) throw new ArgumentException("No colours.", nameof(colors));
        var byL = colors.OrderBy(c => c.L).ToList();
        var drop = (int)Math.Floor(byL.Count * TrimFraction);
        var kept = byL.Count - 2 * drop >= 1 ? byL.GetRange(drop, byL.Count - 2 * drop) : byL;
        return new OkLabColor(Median(kept.Select(c => c.L)), Median(kept.Select(c => c.A)), Median(kept.Select(c => c.B)));
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    private static OkLabColor Lab(ReadOnlySpan<byte> bgra, int stride, int x, int y)
    {
        var i = y * stride + x * DecodedFrame.BytesPerPixel;
        return RouteColor.SrgbToOkLab(bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    /// <summary>For every pixel not equal to <paramref name="target"/>, the chessboard distance to the nearest pixel that
    /// is (pixels equal to the target get 0; none at all → int.MaxValue / 2). Two-pass.</summary>
    private static int[] Chessboard(bool[] inside, int w, int h, bool target)
    {
        const int Far = int.MaxValue / 2;
        var d = new int[w * h];
        for (var i = 0; i < d.Length; i++) d[i] = inside[i] == target ? 0 : Far;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            if (d[i] == 0) continue;
            var m = d[i];
            if (x > 0) m = Math.Min(m, d[i - 1] + 1);
            if (y > 0)
            {
                m = Math.Min(m, d[i - w] + 1);
                if (x > 0) m = Math.Min(m, d[i - w - 1] + 1);
                if (x < w - 1) m = Math.Min(m, d[i - w + 1] + 1);
            }
            d[i] = m;
        }
        for (var y = h - 1; y >= 0; y--)
        for (var x = w - 1; x >= 0; x--)
        {
            var i = y * w + x;
            if (d[i] == 0) continue;
            var m = d[i];
            if (x < w - 1) m = Math.Min(m, d[i + 1] + 1);
            if (y < h - 1)
            {
                m = Math.Min(m, d[i + w] + 1);
                if (x < w - 1) m = Math.Min(m, d[i + w + 1] + 1);
                if (x > 0) m = Math.Min(m, d[i + w - 1] + 1);
            }
            d[i] = m;
        }
        return d;
    }

    /// <summary>Ray casting: whether the pixel centre (x, y) lies inside <paramref name="polygon"/> (pixel coordinates).</summary>
    private static bool Contains((double X, double Y)[] polygon, int x, int y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
