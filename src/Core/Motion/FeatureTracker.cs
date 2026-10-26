using AiVideoEditor.Core.Composition;

namespace AiVideoEditor.Core.Motion;

/// <summary>
/// Feature points of the wall and their tracking between two frames: Shi–Tomasi corners (the smaller eigenvalue of the
/// structure tensor) spread over a grid, a coarse translation search on the top pyramid level for a start without a
/// prediction, and pyramidal Lucas–Kanade. Deterministic (the point loop runs in parallel, each point independently).
/// </summary>
public static class FeatureTracker
{
    public const int Levels = 4;
    private const int HalfWindow = 7;
    private const int Iterations = 10;
    private const double MinEigen = 1e-3;

    /// <summary>Corners of <paramref name="image"/>: the strongest per <paramref name="cell"/> × <paramref name="cell"/>
    /// cell above 1 % of the strongest overall, away from the border, at most <paramref name="maxCount"/> (strongest
    /// first).</summary>
    public static PointD[] Corners(GrayImage image, int cell = 16, int maxCount = 700)
    {
        int w = image.Width, h = image.Height;
        var score = new float[w * h];
        var gx = new float[w * h];
        var gy = new float[w * h];
        for (var y = 1; y < h - 1; y++)
        for (var x = 1; x < w - 1; x++)
        {
            gx[y * w + x] = 0.5f * (image[x + 1, y] - image[x - 1, y]);
            gy[y * w + x] = 0.5f * (image[x, y + 1] - image[x, y - 1]);
        }
        const int r = 2;
        var max = 0f;
        for (var y = r + 1; y < h - r - 1; y++)
        for (var x = r + 1; x < w - r - 1; x++)
        {
            float a = 0, b = 0, c = 0;
            for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                var i = (y + dy) * w + x + dx;
                a += gx[i] * gx[i];
                b += gx[i] * gy[i];
                c += gy[i] * gy[i];
            }
            var half = 0.5f * (a + c);
            var root = MathF.Sqrt(0.25f * (a - c) * (a - c) + b * b);
            var s = half - root;
            score[y * w + x] = s;
            if (s > max) max = s;
        }
        var threshold = 0.01f * max;
        var margin = HalfWindow + 2;
        var found = new List<(float Score, PointD Point)>();
        for (var cy = margin; cy < h - margin; cy += cell)
        for (var cx = margin; cx < w - margin; cx += cell)
        {
            float best = threshold;
            int bx = -1, by = -1;
            for (var y = cy; y < Math.Min(cy + cell, h - margin); y++)
            for (var x = cx; x < Math.Min(cx + cell, w - margin); x++)
            {
                var s = score[y * w + x];
                if (s > best) { best = s; bx = x; by = y; }
            }
            if (bx >= 0) found.Add((best, new PointD(bx, by)));
        }
        return found.OrderByDescending(f => f.Score).ThenBy(f => f.Point.Y).ThenBy(f => f.Point.X)
            .Take(maxCount).Select(f => f.Point).ToArray();
    }

    /// <summary>The zoom factors <see cref="CoarseZoomShift"/> tries: a phone switching lenses can change the scale by
    /// two in one tenth of a second.</summary>
    private static readonly double[] CoarseScales = [1.0, 0.87, 1.15, 0.76, 1.32, 0.66, 1.52, 0.57, 1.75, 0.5, 2.0];

    /// <summary>
    /// The zoom about the frame's centre and the shift (as a level-0 pixel transform) that best align
    /// <paramref name="next"/> to <paramref name="reference"/>, searched on the top pyramid level — a start for
    /// Lucas–Kanade after the track was lost (a sudden zoom, a lens switch, a fast pan).
    /// </summary>
    public static WallTransform CoarseZoomShift(GrayImage[] reference, GrayImage[] next, int radius = 16)
    {
        // The coarsest level that still has some detail (a zoom of a 33-pixel image says little).
        var level = Math.Min(reference.Length, next.Length) - 1;
        while (level > 0 && Math.Min(reference[level].Width, reference[level].Height) < 60) level--;
        GrayImage a = reference[level], b = next[level];
        var size = 1 << level;
        double cx = a.Width / 2.0, cy = a.Height / 2.0;
        var best = double.PositiveInfinity;
        var result = WallTransform.Identity;
        foreach (var s in CoarseScales)
        {
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                double sum = 0;
                var n = 0;
                for (var y = 1; y < a.Height - 1; y += 3)
                for (var x = 1; x < a.Width - 1; x += 3)
                {
                    double u = s * (x - cx) + cx + dx, v = s * (y - cy) + cy + dy;
                    if (u < 0 || v < 0 || u > b.Width - 1 || v > b.Height - 1) continue;
                    sum += Math.Abs(a[x, y] - b.Sample(u, v));
                    n++;
                }
                if (n < a.Width * a.Height / 54) continue;   // too little overlap to judge
                var mean = sum / n;
                if (mean < best)
                {
                    best = mean;
                    // In level-0 pixels: x' = s·(x − C) + C + d·2^level, C the level-0 centre.
                    double c0x = cx * size, c0y = cy * size;
                    result = new WallTransform(s, 0, c0x - s * c0x + dx * size, 0, s, c0y - s * c0y + dy * size, 0, 0, 1);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The translation (level-0 pixels) that best aligns <paramref name="next"/> to <paramref name="reference"/>: the
    /// smallest mean absolute difference over a ±<paramref name="radius"/> search on the top pyramid levels — a start for
    /// Lucas–Kanade when there is no prediction.
    /// </summary>
    public static PointD CoarseShift(GrayImage[] reference, GrayImage[] next, int radius = 12)
    {
        var level = Math.Min(reference.Length, next.Length) - 1;
        GrayImage a = reference[level], b = next[level];
        var scale = 1 << level;
        int bestX = 0, bestY = 0;
        var best = double.PositiveInfinity;
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            double sum = 0;
            var n = 0;
            for (var y = Math.Max(0, -dy); y < Math.Min(h, h - dy); y += 2)
            for (var x = Math.Max(0, -dx); x < Math.Min(w, w - dx); x += 2)
            {
                sum += Math.Abs(a[x, y] - b[x + dx, y + dy]);
                n++;
            }
            if (n < w * h / 16) continue;   // too little overlap to judge
            var mean = sum / n;
            if (mean < best) { best = mean; bestX = dx; bestY = dy; }
        }
        return new PointD(bestX * scale, bestY * scale);
    }

    /// <summary>
    /// Pyramidal Lucas–Kanade: each point of <paramref name="points"/> (in <paramref name="reference"/>) is searched in
    /// <paramref name="next"/> starting where <paramref name="guess"/> puts it. The reference window is sampled through
    /// the guess's local linear map (its Jacobian at the point), so a zoom or a rotation the guess already knows does not
    /// break the match. Returns the tracked positions; a point lost (flat window, out of the frame, a large final
    /// difference) is null.
    /// </summary>
    public static PointD?[] Track(GrayImage[] reference, GrayImage[] next, IReadOnlyList<PointD> points, WallTransform guess)
    {
        var levels = Math.Min(reference.Length, next.Length);
        var result = new PointD?[points.Count];
        Parallel.For(0, points.Count, i =>
        {
            var p = points[i];
            var (gx, gy) = guess.Apply(p.X, p.Y);
            // The guess's Jacobian at the point (central differences of one pixel), inverted: next-frame window
            // offsets → reference offsets.
            var (x1, y1) = guess.Apply(p.X + 1, p.Y);
            var (x0, y0) = guess.Apply(p.X - 1, p.Y);
            var (x3, y3) = guess.Apply(p.X, p.Y + 1);
            var (x2, y2) = guess.Apply(p.X, p.Y - 1);
            double j00 = (x1 - x0) / 2, j10 = (y1 - y0) / 2, j01 = (x3 - x2) / 2, j11 = (y3 - y2) / 2;
            var det = j00 * j11 - j01 * j10;
            if (!(Math.Abs(det) > 1e-6) || !double.IsFinite(gx) || !double.IsFinite(gy)) return;
            var inverse = (j11 / det, -j01 / det, -j10 / det, j00 / det);
            result[i] = TrackOne(reference, next, levels, p, new PointD(gx, gy), inverse);
        });
        return result;
    }

    private static PointD? TrackOne(GrayImage[] reference, GrayImage[] next, int levels, PointD point, PointD guess,
        (double A, double B, double C, double D) inverse)
    {
        const int size = 2 * HalfWindow + 1;
        Span<float> template = stackalloc float[size * size];
        Span<float> ix = stackalloc float[size * size];
        Span<float> iy = stackalloc float[size * size];
        var (ia, ib, ic, id) = inverse;
        double qx = guess.X, qy = guess.Y;   // the target, level-0 pixels
        for (var level = levels - 1; level >= 0; level--)
        {
            var scale = 1.0 / (1 << level);
            GrayImage a = reference[level], b = next[level];
            double px = point.X * scale, py = point.Y * scale;
            double tx = qx * scale, ty = qy * scale;
            double gxx = 0, gxy = 0, gyy = 0;
            var k = 0;
            for (var wy = -HalfWindow; wy <= HalfWindow; wy++)
            for (var wx = -HalfWindow; wx <= HalfWindow; wx++, k++)
            {
                // The template in the next frame's axes: the reference at p + J⁻¹·w; its gradient along w.
                double x = px + ia * wx + ib * wy, y = py + ic * wx + id * wy;
                template[k] = a.Sample(x, y);
                ix[k] = 0.5f * (a.Sample(x + ia, y + ic) - a.Sample(x - ia, y - ic));
                iy[k] = 0.5f * (a.Sample(x + ib, y + id) - a.Sample(x - ib, y - id));
                gxx += ix[k] * ix[k];
                gxy += ix[k] * iy[k];
                gyy += iy[k] * iy[k];
            }
            var det = gxx * gyy - gxy * gxy;
            if (!(det > MinEigen * size * size)) return null;
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                double bx = 0, by = 0;
                k = 0;
                for (var wy = -HalfWindow; wy <= HalfWindow; wy++)
                for (var wx = -HalfWindow; wx <= HalfWindow; wx++, k++)
                {
                    var diff = template[k] - b.Sample(tx + wx, ty + wy);
                    bx += diff * ix[k];
                    by += diff * iy[k];
                }
                var vx = (gyy * bx - gxy * by) / det;
                var vy = (gxx * by - gxy * bx) / det;
                tx += vx;
                ty += vy;
                if (vx * vx + vy * vy < 1e-4) break;
            }
            qx = tx / scale;
            qy = ty / scale;
        }
        var image = next[0];
        if (qx < HalfWindow || qy < HalfWindow || qx > image.Width - 1 - HalfWindow || qy > image.Height - 1 - HalfWindow) return null;

        // The final match must look alike (a lost point on the climber or a repeated texture usually does not).
        double error = 0;
        for (var wy = -HalfWindow; wy <= HalfWindow; wy++)
        for (var wx = -HalfWindow; wx <= HalfWindow; wx++)
            error += Math.Abs(reference[0].Sample(point.X + ia * wx + ib * wy, point.Y + ic * wx + id * wy) - image.Sample(qx + wx, qy + wy));
        return error / (size * size) <= 24 ? new PointD(qx, qy) : null;
    }
}
