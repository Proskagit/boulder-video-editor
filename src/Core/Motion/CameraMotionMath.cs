using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;

namespace AiVideoEditor.Core.Motion;

/// <summary>A robust fit of one motion model to point correspondences: the transform (in the correspondences' pixels),
/// which correspondences are inliers, and the residuals of the inliers (pixels).</summary>
public sealed record MotionFit(WallTransform Transform, bool[] Inliers, int InlierCount);

/// <summary>
/// Pure geometry of the camera-motion estimate: the source time of a frame timestamp, least-squares fits of a
/// similarity and a homography to point correspondences, their robust (RANSAC) versions and the residuals. Deterministic:
/// RANSAC draws its samples from a seeded generator.
/// </summary>
public static class CameraMotionMath
{
    /// <summary>The timestamp in 100 ns ticks relative to the file's start, rounded to the nearest tick (exact rational) —
    /// the same rule as the markup frame's source time, so a contour's <c>SourceTime</c> and the frame it was marked on
    /// meet exactly.</summary>
    public static MediaTime SourceTimeOf(SourceTimestamp timestamp, MediaTime startTime)
    {
        var tb = timestamp.TimeBase;
        Int128 numerator = (Int128)timestamp.Pts * tb.Numerator * TimeSpan.TicksPerSecond;
        Int128 denominator = tb.Denominator;
        var ticks = (long)((numerator * 2 + denominator) / (denominator * 2));   // round half up (non-negative)
        return new MediaTime(Math.Max(0, ticks - startTime.Ticks));
    }

    /// <summary>The least-squares similarity (shift, uniform scale, rotation; no reflection) mapping
    /// <paramref name="from"/> onto <paramref name="to"/> over the selected indices; null when degenerate.</summary>
    public static WallTransform? FitSimilarity(IReadOnlyList<PointD> from, IReadOnlyList<PointD> to, IReadOnlyList<int> indices)
    {
        if (indices.Count < 2) return null;
        double px = 0, py = 0, qx = 0, qy = 0;
        foreach (var i in indices) { px += from[i].X; py += from[i].Y; qx += to[i].X; qy += to[i].Y; }
        var n = indices.Count;
        px /= n; py /= n; qx /= n; qy /= n;
        double sxx = 0, sa = 0, sb = 0;
        foreach (var i in indices)
        {
            double x = from[i].X - px, y = from[i].Y - py, u = to[i].X - qx, v = to[i].Y - qy;
            sxx += x * x + y * y;
            sa += x * u + y * v;
            sb += x * v - y * u;
        }
        if (!(sxx > 1e-9)) return null;
        double a = sa / sxx, b = sb / sxx;
        double tx = qx - (a * px - b * py), ty = qy - (b * px + a * py);
        var t = new WallTransform(a, -b, tx, b, a, ty, 0, 0, 1);
        return t.IsFinite && Math.Abs(a * a + b * b) > 1e-12 ? t : null;
    }

    /// <summary>The least-squares homography (DLT with <c>h22 = 1</c>, Hartley normalization) mapping
    /// <paramref name="from"/> onto <paramref name="to"/> over the selected indices; null when degenerate.</summary>
    public static WallTransform? FitHomography(IReadOnlyList<PointD> from, IReadOnlyList<PointD> to, IReadOnlyList<int> indices)
    {
        if (indices.Count < 4) return null;
        var nf = Normalizer(from, indices);
        var nt = Normalizer(to, indices);
        var ata = new double[8, 8];
        var atb = new double[8];
        var row = new double[8];
        foreach (var i in indices)
        {
            var (x, y) = nf.Apply(from[i].X, from[i].Y);
            var (u, v) = nt.Apply(to[i].X, to[i].Y);
            Accumulate(ata, atb, row, [x, y, 1, 0, 0, 0, -u * x, -u * y], u);
            Accumulate(ata, atb, row, [0, 0, 0, x, y, 1, -v * x, -v * y], v);
        }
        if (Solve(ata, atb) is not { } h) return null;
        var normalized = new WallTransform(h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1);
        var result = nf.Then(normalized).Then(nt.Inverse());   // T⁻¹ · Hn · F
        return result.IsFinite && Math.Abs(result.M22) > 1e-12 ? result.Normalized() : null;
    }

    /// <summary>
    /// RANSAC: <paramref name="iterations"/> minimal samples (2 points for a similarity, 4 for a homography) drawn with
    /// <paramref name="seed"/>; the model with most inliers within <paramref name="threshold"/> pixels is refitted on its
    /// inliers twice. Null when no model has at least <paramref name="minInliers"/> inliers.
    /// </summary>
    public static MotionFit? Ransac(IReadOnlyList<PointD> from, IReadOnlyList<PointD> to, CameraMotionModel model,
        double threshold, int iterations, int seed, int minInliers)
    {
        var n = from.Count;
        var sampleSize = model == CameraMotionModel.Similarity ? 2 : 4;
        if (n < Math.Max(sampleSize, minInliers)) return null;
        var random = new Random(seed);
        var sample = new int[sampleSize];
        WallTransform? best = null;
        var bestCount = -1;
        for (var k = 0; k < iterations; k++)
        {
            for (var j = 0; j < sampleSize; j++)
            {
                int pick;
                do pick = random.Next(n); while (Array.IndexOf(sample, pick, 0, j) >= 0);
                sample[j] = pick;
            }
            if (Fit(model, from, to, sample) is not { } candidate) continue;
            var count = CountInliers(candidate, from, to, threshold, null);
            if (count > bestCount) { bestCount = count; best = candidate; }
        }
        if (best is not { } transform || bestCount < minInliers) return null;

        var inliers = new bool[n];
        for (var pass = 0; pass < 2; pass++)
        {
            CountInliers(transform, from, to, threshold, inliers);
            var indices = Enumerable.Range(0, n).Where(i => inliers[i]).ToList();
            if (Fit(model, from, to, indices) is { } refined) transform = refined;
        }
        var final = CountInliers(transform, from, to, threshold, inliers);
        return final >= minInliers ? new MotionFit(transform, inliers, final) : null;
    }

    /// <summary>The distances <c>|T(from) − to|</c> over the selected correspondences, ascending.</summary>
    public static double[] Residuals(WallTransform transform, IReadOnlyList<PointD> from, IReadOnlyList<PointD> to, bool[] selected)
    {
        var list = new List<double>();
        for (var i = 0; i < from.Count; i++)
        {
            if (!selected[i]) continue;
            var (x, y) = transform.Apply(from[i].X, from[i].Y);
            list.Add(Math.Sqrt((x - to[i].X) * (x - to[i].X) + (y - to[i].Y) * (y - to[i].Y)));
        }
        var result = list.ToArray();
        Array.Sort(result);
        return result;
    }

    /// <summary>The value at <paramref name="fraction"/> (0..1) of an ascending array (nearest rank); +∞ when empty.</summary>
    public static double Quantile(double[] ascending, double fraction) =>
        ascending.Length == 0 ? double.PositiveInfinity
        : ascending[Math.Clamp((int)Math.Ceiling(fraction * ascending.Length) - 1, 0, ascending.Length - 1)];

    private static WallTransform? Fit(CameraMotionModel model, IReadOnlyList<PointD> from, IReadOnlyList<PointD> to, IReadOnlyList<int> indices) =>
        model == CameraMotionModel.Similarity ? FitSimilarity(from, to, indices) : FitHomography(from, to, indices);

    private static int CountInliers(WallTransform t, IReadOnlyList<PointD> from, IReadOnlyList<PointD> to, double threshold, bool[]? mark)
    {
        var count = 0;
        var t2 = threshold * threshold;
        for (var i = 0; i < from.Count; i++)
        {
            var (x, y) = t.Apply(from[i].X, from[i].Y);
            double dx = x - to[i].X, dy = y - to[i].Y;
            var inside = double.IsFinite(dx) && double.IsFinite(dy) && dx * dx + dy * dy <= t2;
            if (inside) count++;
            if (mark is not null) mark[i] = inside;
        }
        return count;
    }

    /// <summary>Hartley's normalization: the centroid to the origin, the mean distance √2.</summary>
    private static WallTransform Normalizer(IReadOnlyList<PointD> points, IReadOnlyList<int> indices)
    {
        double cx = 0, cy = 0;
        foreach (var i in indices) { cx += points[i].X; cy += points[i].Y; }
        cx /= indices.Count; cy /= indices.Count;
        double mean = 0;
        foreach (var i in indices) mean += Math.Sqrt((points[i].X - cx) * (points[i].X - cx) + (points[i].Y - cy) * (points[i].Y - cy));
        mean /= indices.Count;
        var s = mean > 1e-12 ? Math.Sqrt(2) / mean : 1;
        return new WallTransform(s, 0, -s * cx, 0, s, -s * cy, 0, 0, 1);
    }

    private static void Accumulate(double[,] ata, double[] atb, double[] row, double[] values, double target)
    {
        for (var r = 0; r < 8; r++)
        {
            atb[r] += values[r] * target;
            for (var c = 0; c < 8; c++) ata[r, c] += values[r] * values[c];
        }
    }

    /// <summary>Gaussian elimination with partial pivoting; null for a (nearly) singular system.</summary>
    private static double[]? Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (!(Math.Abs(m[pivot, col]) > 1e-12)) return null;
            if (pivot != col)
            {
                for (var c = 0; c < n; c++) (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                (x[col], x[pivot]) = (x[pivot], x[col]);
            }
            for (var r = col + 1; r < n; r++)
            {
                var f = m[r, col] / m[col, col];
                if (f == 0) continue;
                for (var c = col; c < n; c++) m[r, c] -= f * m[col, c];
                x[r] -= f * x[col];
            }
        }
        for (var r = n - 1; r >= 0; r--)
        {
            var sum = x[r];
            for (var c = r + 1; c < n; c++) sum -= m[r, c] * x[c];
            x[r] = sum / m[r, r];
        }
        return x.All(double.IsFinite) ? x : null;
    }
}
