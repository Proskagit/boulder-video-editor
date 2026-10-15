using System.Collections.Immutable;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Holds;

/// <summary>
/// The outer contour of a hold mask (D034 §6): the largest 8-connected component, its boundary traced through the
/// boundary pixels' centres (Moore neighbourhood), simplified with Douglas–Peucker at one pixel and normalized to the
/// frame (0..1). Holes (a donut's hole) are ignored — only the outer contour is drawn.
/// </summary>
public static class MaskContour
{
    public const double SimplifyPixels = 1.0;

    /// <summary>The largest 8-connected component of <paramref name="mask"/> (one byte per pixel, non-zero = inside) as a
    /// new mask, and its area; an empty mask when there is none.</summary>
    public static (byte[] Mask, long Area) LargestComponent(byte[] mask, int width, int height)
    {
        var labels = new int[mask.Length];
        var best = (Label: 0, Area: 0L);
        var label = 0;
        var queue = new Queue<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (mask[start] == 0 || labels[start] != 0) continue;
            label++;
            long area = 0;
            labels[start] = label;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                area++;
                int x = i % width, y = i / width;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var j = ny * width + nx;
                    if (mask[j] == 0 || labels[j] != 0) continue;
                    labels[j] = label;
                    queue.Enqueue(j);
                }
            }
            if (area > best.Area) best = (label, area);
        }

        var result = new byte[mask.Length];
        if (best.Label == 0) return (result, 0);
        for (var i = 0; i < mask.Length; i++)
            if (labels[i] == best.Label) result[i] = 1;
        return (result, best.Area);
    }

    /// <summary>The traced outer boundary of the single component of <paramref name="component"/> in pixel centres
    /// (clockwise in image coordinates), or empty.</summary>
    public static List<(int X, int Y)> TraceOuter(byte[] component, int width, int height)
    {
        var start = Array.FindIndex(component, p => p != 0);
        var path = new List<(int X, int Y)>();
        if (start < 0) return path;

        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && component[y * width + x] != 0;
        // Moore neighbourhood in clockwise order (image coordinates, y down), starting west.
        (int Dx, int Dy)[] dirs = { (-1, 0), (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1) };
        int IndexOf(int dx, int dy) => Array.IndexOf(dirs, (dx, dy));

        // One clockwise step from `cur` (entered with the outside cell `back` next to it): the next boundary pixel and
        // the outside cell checked just before it, or null for an isolated pixel.
        ((int X, int Y) Pixel, (int X, int Y) Back)? Step((int X, int Y) cur, (int X, int Y) back)
        {
            var from = IndexOf(back.X - cur.X, back.Y - cur.Y);
            for (var k = 1; k <= 8; k++)
            {
                var d = (from + k) % 8;
                var n = (cur.X + dirs[d].Dx, cur.Y + dirs[d].Dy);
                if (!Inside(n.Item1, n.Item2)) continue;
                var prev = dirs[(d + 7) % 8];
                return (n, (cur.X + prev.Dx, cur.Y + prev.Dy));
            }
            return null;
        }

        // The first pixel in row-major order: everything before it is outside, so its west neighbour is outside.
        var s = (X: start % width, Y: start / width);
        path.Add(s);
        if (Step(s, (s.X - 1, s.Y)) is not { } first) return path;   // an isolated pixel
        var (current, back) = first;
        var guard = 8L * component.Length + 16;
        while (guard-- > 0)
        {
            if (current == s)
            {
                // Stop when the walk would repeat its first step from the start (handles a start that joins two branches).
                var again = Step(current, back);
                if (again is null || again.Value.Pixel == first.Pixel) break;
            }
            path.Add(current);
            (current, back) = Step(current, back)!.Value;
        }
        return path;
    }

    /// <summary>Douglas–Peucker on a closed polygon with tolerance <paramref name="epsilon"/> pixels.</summary>
    public static List<(double X, double Y)> Simplify(IReadOnlyList<(double X, double Y)> closed, double epsilon) =>
        SimplifyIndices(closed, epsilon).Select(i => closed[i]).ToList();

    /// <summary>The indices (ascending) of the vertices Douglas–Peucker keeps on a closed polygon with tolerance
    /// <paramref name="epsilon"/>.</summary>
    public static List<int> SimplifyIndices(IReadOnlyList<(double X, double Y)> closed, double epsilon)
    {
        if (closed.Count <= 3) return Enumerable.Range(0, closed.Count).ToList();
        // Split the ring at its two farthest-apart points and simplify both halves.
        var (a, b) = (0, 0);
        double far = -1;
        for (var i = 0; i < closed.Count; i++)
        {
            var d = Dist2(closed[0], closed[i]);
            if (d > far) (far, b) = (d, i);
        }
        var keep = new bool[closed.Count];
        keep[a] = keep[b] = true;
        Rdp(closed, a, b, epsilon, keep);
        Rdp(closed, b, closed.Count, epsilon, keep);   // wraps to index 0
        var result = new List<int>();
        for (var i = 0; i < closed.Count; i++) if (keep[i]) result.Add(i);
        return result;
    }

    /// <summary>The outer contour of the largest component of <paramref name="mask"/>, normalized; empty when the mask is
    /// empty. A component too small for a polygon gets the corners of its bounding box (in pixel centres ± 0.5).</summary>
    public static ImmutableArray<NormalizedPoint> Outer(byte[] mask, int width, int height)
    {
        var (component, area) = LargestComponent(mask, width, height);
        if (area == 0) return ImmutableArray<NormalizedPoint>.Empty;
        var traced = TraceOuter(component, width, height);
        var simplified = Simplify(traced.Select(p => ((double)p.X, (double)p.Y)).ToList(), SimplifyPixels);
        if (simplified.Count < 3)
        {
            double x0 = traced.Min(p => p.X) - 0.5, x1 = traced.Max(p => p.X) + 0.5, y0 = traced.Min(p => p.Y) - 0.5, y1 = traced.Max(p => p.Y) + 0.5;
            simplified = new() { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };
        }
        return simplified
            .Select(p => new NormalizedPoint(Math.Clamp((p.X + 0.5) / width, 0, 1), Math.Clamp((p.Y + 0.5) / height, 0, 1)))
            .ToImmutableArray();
    }

    private static void Rdp(IReadOnlyList<(double X, double Y)> ring, int from, int to, double epsilon, bool[] keep)
    {
        if (to - from < 2) return;
        var p0 = ring[from];
        var p1 = ring[to % ring.Count];
        var (index, max) = (-1, -1.0);
        for (var i = from + 1; i < to; i++)
        {
            var d = SegmentDistance(ring[i], p0, p1);
            if (d > max) (max, index) = (d, i);
        }
        if (max <= epsilon) return;
        keep[index] = true;
        Rdp(ring, from, index, epsilon, keep);
        Rdp(ring, index, to, epsilon, keep);
    }

    private static double Dist2((double X, double Y) a, (double X, double Y) b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    private static double SegmentDistance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        var len2 = Dist2(a, b);
        if (len2 == 0) return Math.Sqrt(Dist2(p, a));
        var t = Math.Clamp(((p.X - a.X) * (b.X - a.X) + (p.Y - a.Y) * (b.Y - a.Y)) / len2, 0, 1);
        return Math.Sqrt(Dist2(p, (a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y))));
    }
}
