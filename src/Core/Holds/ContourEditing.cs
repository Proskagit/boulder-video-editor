using System.Collections.Immutable;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Holds;

/// <summary>A hold contour being edited (D035): its full polygon and the vertices shown as handles (ascending indices
/// into <see cref="Points"/>). Every vertex of the polygon is kept; the handles are only what the user grabs.</summary>
public sealed record ContourEdit(ImmutableArray<NormalizedPoint> Points, ImmutableArray<int> Handles);

/// <summary>
/// The pure geometry of the manual correction of a confirmed hold's contour (D035). Points are normalized source
/// coordinates (D034 §6); distances are measured in source pixels of a <c>width</c> × <c>height</c> frame, so a non-square
/// frame is handled correctly.
/// <para>
/// A contour from the model is simplified to ~1 source pixel and can have hundreds of vertices; above
/// <see cref="AllHandlesUpTo"/> vertices only a coarser Douglas–Peucker subset of them is shown as handles
/// (<see cref="Handles"/>). The stored geometry is never simplified: moving a handle carries the hidden vertices between it
/// and its neighbouring handles along (a linear falloff by arc length), every other vertex stays exactly as it was;
/// removing a handle replaces the stretch between its neighbouring handles by a straight edge; inserting puts a vertex on
/// an edge of the full polygon. A contour keeps at least <see cref="MinHandles"/> handles (so at least three vertices),
/// and an edit that makes it cross or touch itself is refused (<see cref="IntroducesCrossing"/>).
/// </para>
/// </summary>
public static class ContourEditing
{
    /// <summary>Contours with at most this many vertices show every vertex as a handle.</summary>
    public const int AllHandlesUpTo = 32;

    /// <summary>The handle simplification: this share of the contour's bounding-box diagonal, at least
    /// <see cref="MinHandleTolerancePixels"/>.</summary>
    public const double HandleToleranceShare = 0.02;

    public const double MinHandleTolerancePixels = 1.5;

    public const int MinHandles = 3;

    public const string CrossingMessage = "The outline can't cross itself — the change is not applied.";

    public const string TooFewPointsMessage = "An outline keeps at least three points.";

    /// <summary>The handles of <paramref name="points"/>: every vertex up to <see cref="AllHandlesUpTo"/>, otherwise the
    /// Douglas–Peucker subset at <see cref="HandleToleranceShare"/> of the contour's size.</summary>
    public static ImmutableArray<int> Handles(ImmutableArray<NormalizedPoint> points, int width, int height)
    {
        if (points.Length <= AllHandlesUpTo) return Enumerable.Range(0, points.Length).ToImmutableArray();
        var pixels = points.Select(p => (p.X * width, p.Y * height)).ToList();
        double x0 = pixels.Min(p => p.Item1), x1 = pixels.Max(p => p.Item1), y0 = pixels.Min(p => p.Item2), y1 = pixels.Max(p => p.Item2);
        var diagonal = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        var tolerance = Math.Max(MinHandleTolerancePixels, HandleToleranceShare * diagonal);
        var handles = MaskContour.SimplifyIndices(pixels, tolerance);
        return handles.Count >= MinHandles ? handles.ToImmutableArray() : Enumerable.Range(0, points.Length).ToImmutableArray();
    }

    /// <summary>The contour with handle <paramref name="handle"/> (an index into <see cref="ContourEdit.Handles"/>) moved
    /// to <paramref name="to"/> (clamped to the frame); the hidden vertices between it and its neighbouring handles follow
    /// it in proportion to their arc length from those neighbours.</summary>
    public static ContourEdit MoveHandle(ContourEdit edit, int handle, NormalizedPoint to, int width, int height)
    {
        var (points, handles) = (edit.Points, edit.Handles);
        var n = points.Length;
        var index = handles[handle];
        to = Clamp(to);
        var dx = to.X - points[index].X;
        var dy = to.Y - points[index].Y;
        var result = points.ToBuilder();
        result[index] = to;
        if (handles.Length < n)
        {
            var previous = handles[(handle - 1 + handles.Length) % handles.Length];
            var next = handles[(handle + 1) % handles.Length];
            Falloff(points, result, previous, index, dx, dy, width, height, towardsEnd: true);
            Falloff(points, result, index, next, dx, dy, width, height, towardsEnd: false);
        }
        return edit with { Points = result.MoveToImmutable() };
    }

    /// <summary>The contour with a vertex inserted on edge <paramref name="edge"/> (from vertex <c>edge</c> to the next one)
    /// at the parameter <paramref name="t"/> (0..1 along the edge; an affine map keeps it) — the new vertex is a handle.
    /// Null when the contour already has the most points allowed.</summary>
    public static ContourEdit? InsertVertex(ContourEdit edit, int edge, double t)
    {
        var (points, handles) = (edit.Points, edit.Handles);
        if (points.Length >= HoldMarkupRules.MaxPoints || edge < 0 || edge >= points.Length) return null;
        t = Math.Clamp(t, 0, 1);
        var a = points[edge];
        var b = points[(edge + 1) % points.Length];
        var at = edge + 1;
        var inserted = points.Insert(at, Clamp(new NormalizedPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t)));
        var shifted = handles.Select(h => h >= at ? h + 1 : h).Append(at).Order().ToImmutableArray();
        return new ContourEdit(inserted, shifted);
    }

    /// <summary>The contour without handle <paramref name="handle"/>: the stretch between its neighbouring handles becomes
    /// one straight edge (the hidden vertices of that stretch go with it). Null when fewer than <see cref="MinHandles"/>
    /// handles would stay.</summary>
    public static ContourEdit? RemoveHandle(ContourEdit edit, int handle)
    {
        var (points, handles) = (edit.Points, edit.Handles);
        if (handles.Length <= MinHandles) return null;
        var previous = handles[(handle - 1 + handles.Length) % handles.Length];
        var next = handles[(handle + 1) % handles.Length];
        var removed = new HashSet<int>();
        for (var i = (previous + 1) % points.Length; i != next; i = (i + 1) % points.Length) removed.Add(i);

        var map = new int[points.Length];
        var kept = ImmutableArray.CreateBuilder<NormalizedPoint>(points.Length - removed.Count);
        for (var i = 0; i < points.Length; i++)
        {
            map[i] = kept.Count;
            if (!removed.Contains(i)) kept.Add(points[i]);
        }
        var remaining = handles.Where(h => !removed.Contains(h)).Select(h => map[h]).ToImmutableArray();
        return new ContourEdit(kept.MoveToImmutable(), remaining);
    }

    /// <summary>Whether <paramref name="after"/> has an edge that the edit changed (an edge not in
    /// <paramref name="before"/>) and that crosses or touches another edge of <paramref name="after"/>, or folds back onto
    /// its neighbour; or whether it has no area. Crossings the contour already had among its unchanged edges (a traced mask
    /// can touch itself at a one-pixel neck) never block an edit elsewhere.</summary>
    public static bool IntroducesCrossing(IReadOnlyList<NormalizedPoint> before, IReadOnlyList<NormalizedPoint> after)
    {
        var n = after.Count;
        if (n < 3 || Math.Abs(SignedArea(after)) < 1e-12) return true;
        var old = new HashSet<(NormalizedPoint, NormalizedPoint)>();
        for (var i = 0; i < before.Count; i++)
        {
            var (a, b) = (before[i], before[(i + 1) % before.Count]);
            old.Add((a, b));
            old.Add((b, a));
        }
        for (var i = 0; i < n; i++)
        {
            var (a, b) = (after[i], after[(i + 1) % n]);
            if (old.Contains((a, b))) continue;
            for (var j = 0; j < n; j++)
            {
                if (j == i) continue;
                var (c, d) = (after[j], after[(j + 1) % n]);
                if (j == (i + 1) % n) { if (FoldsBack(a, b, d)) return true; continue; }
                if (i == (j + 1) % n) { if (FoldsBack(c, d, b)) return true; continue; }
                if (SegmentsTouch(a, b, c, d)) return true;
            }
        }
        return false;
    }

    /// <summary>The edge of <paramref name="points"/> nearest to <paramref name="p"/> and the parameter of the nearest
    /// point on it, with the distance — all in the space the points are given in.</summary>
    public static (int Edge, double T, double Distance) NearestEdge(IReadOnlyList<(double X, double Y)> points, (double X, double Y) p)
    {
        var best = (Edge: -1, T: 0.0, Distance: double.PositiveInfinity);
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            double ex = b.X - a.X, ey = b.Y - a.Y;
            var len2 = ex * ex + ey * ey;
            var t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * ex + (p.Y - a.Y) * ey) / len2, 0, 1);
            double qx = a.X + t * ex - p.X, qy = a.Y + t * ey - p.Y;
            var d = Math.Sqrt(qx * qx + qy * qy);
            if (d < best.Distance) best = (i, t, d);
        }
        return best;
    }

    /// <summary>Moves the hidden vertices strictly between <paramref name="from"/> and <paramref name="to"/> (cyclic) by a
    /// share of (dx, dy) growing with the arc length towards the moved end.</summary>
    private static void Falloff(ImmutableArray<NormalizedPoint> points, ImmutableArray<NormalizedPoint>.Builder result,
        int from, int to, double dx, double dy, int width, int height, bool towardsEnd)
    {
        var n = points.Length;
        if ((from + 1) % n == to) return;
        var lengths = new List<double>();
        var total = 0.0;
        for (var i = from; i != to; i = (i + 1) % n)
        {
            var (a, b) = (points[i], points[(i + 1) % n]);
            total += Math.Sqrt(Square((b.X - a.X) * width) + Square((b.Y - a.Y) * height));
            lengths.Add(total);
        }
        var k = 0;
        for (var i = (from + 1) % n; i != to; i = (i + 1) % n, k++)
        {
            var share = total > 0 ? lengths[k] / total : (k + 1.0) / lengths.Count;
            var weight = towardsEnd ? share : 1 - share;
            result[i] = Clamp(new NormalizedPoint(points[i].X + dx * weight, points[i].Y + dy * weight));
        }
    }

    private static double Square(double v) => v * v;

    private static NormalizedPoint Clamp(NormalizedPoint p) => new(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1));

    private static double SignedArea(IReadOnlyList<NormalizedPoint> points)
    {
        var sum = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    private static double Cross(NormalizedPoint o, NormalizedPoint a, NormalizedPoint b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>Adjacent edges a→b and b→c lie on one line and c goes back towards a (a spike of zero width).</summary>
    private static bool FoldsBack(NormalizedPoint a, NormalizedPoint b, NormalizedPoint c) =>
        a != b && b != c && Cross(a, b, c) == 0 && (b.X - a.X) * (c.X - b.X) + (b.Y - a.Y) * (c.Y - b.Y) < 0;

    /// <summary>Whether the closed segments a–b and c–d share a point.</summary>
    private static bool SegmentsTouch(NormalizedPoint a, NormalizedPoint b, NormalizedPoint c, NormalizedPoint d)
    {
        if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) || Math.Max(c.X, d.X) < Math.Min(a.X, b.X) ||
            Math.Max(a.Y, b.Y) < Math.Min(c.Y, d.Y) || Math.Max(c.Y, d.Y) < Math.Min(a.Y, b.Y))
            return false;
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        return (d1 == 0 && OnSegment(c, d, a)) || (d2 == 0 && OnSegment(c, d, b)) ||
               (d3 == 0 && OnSegment(a, b, c)) || (d4 == 0 && OnSegment(a, b, d));
    }

    private static bool OnSegment(NormalizedPoint a, NormalizedPoint b, NormalizedPoint p) =>
        p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
}
