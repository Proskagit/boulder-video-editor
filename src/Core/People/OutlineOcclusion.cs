using System.Collections.Immutable;
using AiVideoEditor.Core.Composition;

namespace AiVideoEditor.Core.People;

/// <summary>A straight piece of a contour in the outline's crop-local coordinates.</summary>
public readonly record struct OutlineSegment(PointD A, PointD B);

/// <summary>
/// D061 (Occlusion): which parts of a hold outline are seen when people stand in front of the holds. Every contour edge is
/// cut into pieces no longer than one cell of the people's mask; each piece takes the visibility of the coverage at its
/// middle (bilinear on the mask) through a short ramp — fully drawn below <see cref="RampStart"/> of coverage, hidden above
/// <see cref="RampEnd"/> — quantized to <see cref="Levels"/> steps, so a person's edge fades the line over a cell or two
/// instead of cutting it with a stair. The pieces are grouped by their opacity (the hold's own D036 opacity × the
/// visibility) and drawn per group like <see cref="OutlineDraw.OpacityGroups"/> (keyline and core together, one opacity per
/// group): the Preview and the export draw exactly the same, from any thread, with no bitmaps.
/// </summary>
public static class OutlineOcclusion
{
    public const double RampStart = 0.35;
    public const double RampEnd = 0.65;

    /// <summary>The visibility steps above 0 (3: ⅓, ⅔, 1).</summary>
    public const int Levels = 3;

    /// <summary>The visibility (0..1) of a coverage (0..1): smooth from 1 at <see cref="RampStart"/> to 0 at
    /// <see cref="RampEnd"/>.</summary>
    public static double Visibility(double coverage)
    {
        if (coverage <= RampStart) return 1;
        if (coverage >= RampEnd) return 0;
        var t = (coverage - RampStart) / (RampEnd - RampStart);
        return 1 - t * t * (3 - 2 * t);
    }

    /// <summary>The coverage (0..1) of <paramref name="mask"/> at crop-local <paramref name="p"/>, the mask spanning
    /// <paramref name="rect"/>: bilinear between cell centres, 0 outside.</summary>
    public static double CoverageAt(OcclusionMask mask, RectD rect, PointD p)
    {
        if (!(rect.Width > 0) || !(rect.Height > 0)) return 0;
        var u = (p.X - rect.X) / rect.Width * mask.Width - 0.5;
        var v = (p.Y - rect.Y) / rect.Height * mask.Height - 0.5;
        if (u < -0.5 || v < -0.5 || u > mask.Width - 0.5 || v > mask.Height - 0.5) return 0;
        var x0 = (int)Math.Floor(u);
        var y0 = (int)Math.Floor(v);
        var fx = u - x0;
        var fy = v - y0;
        double C(int x, int y) => mask.Coverage[Math.Clamp(y, 0, mask.Height - 1) * mask.Width + Math.Clamp(x, 0, mask.Width - 1)];
        var value = (C(x0, y0) * (1 - fx) + C(x0 + 1, y0) * fx) * (1 - fy) + (C(x0, y0 + 1) * (1 - fx) + C(x0 + 1, y0 + 1) * fx) * fy;
        return value / 255.0;
    }

    /// <summary>
    /// The pieces of <paramref name="op"/>'s contours grouped by opacity (the hold's × the visibility; the layer's opacity is
    /// not included), in order of each group's first piece; hidden pieces are left out. Without a mask — every contour edge
    /// in its hold's group.
    /// </summary>
    public static IEnumerable<(double Opacity, ImmutableArray<OutlineSegment> Segments)> Groups(OutlineDraw op)
    {
        var order = new List<double>();
        var groups = new Dictionary<double, ImmutableArray<OutlineSegment>.Builder>();
        void Add(double opacity, OutlineSegment segment)
        {
            if (!groups.TryGetValue(opacity, out var group))
            {
                groups[opacity] = group = ImmutableArray.CreateBuilder<OutlineSegment>();
                order.Add(opacity);
            }
            group.Add(segment);
        }

        var mask = op.Occlusion;
        var rect = op.OcclusionRect;
        var cell = mask is null ? double.PositiveInfinity : Math.Min(rect.Width / mask.Width, rect.Height / mask.Height);
        for (var i = 0; i < op.Polygons.Length; i++)
        {
            var hold = op.PolygonOpacity(i);
            var polygon = op.Polygons[i];
            for (var k = 0; k < polygon.Length; k++)
            {
                var (a, b) = (polygon[k], polygon[(k + 1) % polygon.Length]);
                if (mask is null)
                {
                    Add(hold, new OutlineSegment(a, b));
                    continue;
                }
                var length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                var pieces = Math.Max(1, (int)Math.Ceiling(length / Math.Max(cell, 1e-9)));
                var runStart = a;
                var runLevel = -1.0;
                for (var p = 0; p < pieces; p++)
                {
                    var t0 = (double)p / pieces;
                    var t1 = (double)(p + 1) / pieces;
                    var mid = new PointD(a.X + (b.X - a.X) * (t0 + t1) / 2, a.Y + (b.Y - a.Y) * (t0 + t1) / 2);
                    var level = Math.Round(Visibility(CoverageAt(mask, rect, mid)) * Levels) / Levels;
                    var start = new PointD(a.X + (b.X - a.X) * t0, a.Y + (b.Y - a.Y) * t0);
                    if (p == 0) (runStart, runLevel) = (start, level);
                    else if (level != runLevel)
                    {
                        if (runLevel > 0) Add(hold * runLevel, new OutlineSegment(runStart, start));
                        (runStart, runLevel) = (start, level);
                    }
                }
                if (runLevel > 0) Add(hold * runLevel, new OutlineSegment(runStart, b));
            }
        }
        foreach (var opacity in order) yield return (opacity, groups[opacity].ToImmutable());
    }
}
