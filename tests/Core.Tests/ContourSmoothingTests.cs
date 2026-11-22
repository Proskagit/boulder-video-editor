using System.Collections.Immutable;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 46 / D065: the smoothing of a new hold's contour (§2: a noisy round mask becomes a smooth round outline of the same
/// size, strength 0 changes nothing) and the smooth curve the painter draws through a contour's vertices (§4: through every
/// vertex, corners kept and straight sides straight, a dense contour unchanged, coincident vertices safe); the setting's bounds (§3).
/// </summary>
public class ContourSmoothingTests
{
    private const int Size = 200;

    /// <summary>A round mask of radius 40 whose edge is jagged: the radius jumps by up to ±3 px every degree — the «ёжик»
    /// of a poorly recognised round hold.</summary>
    private static byte[] HedgehogMask(int seed = 7)
    {
        const int sectors = 360;
        var random = new Random(seed);
        var bumps = Enumerable.Range(0, sectors).Select(_ => random.Next(-3, 4)).ToArray();
        var mask = new byte[Size * Size];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var (dx, dy) = (x - 100.0, y - 100.0);
                var angle = (Math.Atan2(dy, dx) + Math.PI) / (2 * Math.PI);
                var r = 40 + bumps[Math.Min(sectors - 1, (int)(angle * sectors))];
                if (dx * dx + dy * dy <= r * r) mask[y * Size + x] = 1;
            }
        return mask;
    }

    private static (double Mean, double Spread) Radii(ImmutableArray<NormalizedPoint> contour)
    {
        var points = contour.Select(p => (X: p.X * Size, Y: p.Y * Size)).ToList();
        // The area centroid: the vertices of a simplified contour are not spread evenly.
        double area = 0, cx = 0, cy = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var (p, q) = (points[i], points[(i + 1) % points.Count]);
            var cross = p.X * q.Y - q.X * p.Y;
            (area, cx, cy) = (area + cross, cx + (p.X + q.X) * cross, cy + (p.Y + q.Y) * cross);
        }
        (cx, cy) = (cx / (3 * area), cy / (3 * area));
        // Sample along the polygon so the vertex density does not weigh the result.
        var radii = new List<double>();
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            for (var t = 0.0; t < 1; t += 0.1)
                radii.Add(Math.Sqrt(Math.Pow(a.X + (b.X - a.X) * t - cx, 2) + Math.Pow(a.Y + (b.Y - a.Y) * t - cy, 2)));
        }
        var mean = radii.Average();
        return (mean, Math.Sqrt(radii.Average(r => (r - mean) * (r - mean))));
    }

    private static double Area(ImmutableArray<NormalizedPoint> contour)
    {
        double a = 0;
        for (var i = 0; i < contour.Length; i++)
        {
            var (p, q) = (contour[i], contour[(i + 1) % contour.Length]);
            a += p.X * Size * q.Y * Size - q.X * Size * p.Y * Size;
        }
        return Math.Abs(a / 2);
    }

    [Fact]
    public void A_hedgehog_round_hold_becomes_smooth_and_keeps_its_size()
    {
        var raw = MaskContour.Outer(HedgehogMask(), Size, Size);
        var smooth = ContourSmoothing.Smooth(raw, Size, Size, ContourSmoothing.DefaultStrength);

        var (rawMean, rawSpread) = Radii(raw);
        var (smoothMean, smoothSpread) = Radii(smooth);
        Assert.True(smoothSpread < rawSpread / 2, $"spread {smoothSpread:0.00} vs raw {rawSpread:0.00}");
        Assert.InRange(smoothMean, rawMean - 1.5, rawMean + 1.5);
        Assert.InRange(Area(smooth) / Area(raw), 0.97, 1.03);
        Assert.True(smooth.Length < raw.Length, $"{smooth.Length} vertices vs raw {raw.Length}");
        Assert.True(smooth.Length >= 8);
        Assert.All(smooth, p => Assert.True(p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1));
    }

    [Fact]
    public void A_stronger_smoothing_gives_a_rounder_outline()
    {
        var raw = MaskContour.Outer(HedgehogMask(11), Size, Size);
        var weak = Radii(ContourSmoothing.Smooth(raw, Size, Size, 20)).Spread;
        var strong = Radii(ContourSmoothing.Smooth(raw, Size, Size, 100)).Spread;
        Assert.True(strong < weak, $"strong {strong:0.00} vs weak {weak:0.00}");
    }

    [Fact]
    public void Strength_zero_and_degenerate_contours_are_returned_unchanged()
    {
        var raw = MaskContour.Outer(HedgehogMask(), Size, Size);
        Assert.Equal(raw, ContourSmoothing.Smooth(raw, Size, Size, 0));
        Assert.Equal(raw, ContourSmoothing.Smooth(raw, 0, 0, 50));

        var line = ImmutableArray.Create(new NormalizedPoint(0.1, 0.1), new NormalizedPoint(0.2, 0.2), new NormalizedPoint(0.3, 0.3));
        Assert.Equal(line, ContourSmoothing.Smooth(line, Size, Size, 50));   // no area
        var two = ImmutableArray.Create(new NormalizedPoint(0.1, 0.1), new NormalizedPoint(0.2, 0.2));
        Assert.Equal(two, ContourSmoothing.Smooth(two, Size, Size, 50));
        var tiny = ImmutableArray.Create(new NormalizedPoint(0.5, 0.5), new NormalizedPoint(0.505, 0.5), new NormalizedPoint(0.505, 0.505));
        Assert.Equal(tiny, ContourSmoothing.Smooth(tiny, Size, Size, 50));   // under a pixel: nothing to smooth
    }

    [Fact]
    public void A_square_keeps_its_area_and_place()
    {
        var square = ImmutableArray.Create(new NormalizedPoint(0.3, 0.3), new NormalizedPoint(0.7, 0.3),
            new NormalizedPoint(0.7, 0.7), new NormalizedPoint(0.3, 0.7));
        var smooth = ContourSmoothing.Smooth(square, Size, Size, 100);
        Assert.InRange(Area(smooth) / Area(square), 0.97, 1.03);
        Assert.InRange(smooth.Average(p => p.X), 0.49, 0.51);
        Assert.InRange(smooth.Average(p => p.Y), 0.49, 0.51);
    }

    [Fact]
    public void A_polygon_with_corners_is_drawn_exactly_as_its_vertices_say()
    {
        // A square (90° turns) and a pentagon (72°): every vertex a corner, every side straight — never bulging out.
        var square = ImmutableArray.Create(new PointD(0, 0), new PointD(100, 0), new PointD(100, 100), new PointD(0, 100));
        Assert.Equal(square.ToArray(), ContourCurve.Flatten(square, 1).ToArray());
        var pentagon = Enumerable.Range(0, 5)
            .Select(i => new PointD(100 + 50 * Math.Cos(i * 2 * Math.PI / 5), 100 + 50 * Math.Sin(i * 2 * Math.PI / 5)))
            .ToImmutableArray();
        Assert.Equal(pentagon.ToArray(), ContourCurve.Flatten(pentagon, 1).ToArray());
    }

    [Fact]
    public void A_smooth_contour_is_drawn_as_a_curve_through_its_vertices()
    {
        // A 24-gon around a circle of radius 100 (15° turns): the curve passes every vertex and keeps close to the circle
        // between them, where the straight sides fall 1.7 px inside.
        const double r = 100;
        var polygon = Enumerable.Range(0, 24)
            .Select(i => new PointD(200 + r * Math.Cos(i * 2 * Math.PI / 24), 200 + r * Math.Sin(i * 2 * Math.PI / 24)))
            .ToImmutableArray();
        var curve = ContourCurve.Flatten(polygon, 1);
        Assert.All(polygon, v => Assert.Contains(v, curve));
        Assert.True(curve.Length > polygon.Length * 4);
        Assert.All(curve, p => Assert.InRange(Math.Sqrt((p.X - 200) * (p.X - 200) + (p.Y - 200) * (p.Y - 200)), r - 0.3, r + 0.3));
    }

    [Fact]
    public void A_smooth_contour_turns_sharply_only_at_its_corner()
    {
        // A half-disc: a smooth arc and a straight diameter meeting at two 90° corners — the diameter stays straight.
        var arc = Enumerable.Range(0, 13)
            .Select(i => new PointD(200 + 100 * Math.Cos(i * Math.PI / 12), 200 - 100 * Math.Sin(i * Math.PI / 12)));
        var curve = ContourCurve.Flatten(arc.ToImmutableArray(), 1);
        Assert.All(curve, p => Assert.True(p.Y <= 200 + 1e-9, $"{p}"));   // nothing below the diameter
    }

    [Fact]
    public void A_dense_contour_is_drawn_as_before()
    {
        var dense = Enumerable.Range(0, 400)
            .Select(i => new PointD(100 + 50 * Math.Cos(i * 2 * Math.PI / 400), 100 + 50 * Math.Sin(i * 2 * Math.PI / 400)))
            .ToImmutableArray();
        Assert.Equal(dense.ToArray(), ContourCurve.Flatten(dense, 1).ToArray());   // every side under 3 target pixels
        Assert.True(ContourCurve.Flatten(dense, 10).Length > dense.Length);   // magnified, it is cut finer
    }

    [Fact]
    public void Coincident_vertices_and_tiny_polygons_are_safe()
    {
        var withDuplicates = ImmutableArray.Create(new PointD(0, 0), new PointD(0, 0), new PointD(50, 0), new PointD(50, 0),
            new PointD(50, 50), new PointD(0, 50));
        var curve = ContourCurve.Flatten(withDuplicates, 1);
        Assert.All(curve, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
        var two = ImmutableArray.Create(new PointD(0, 0), new PointD(10, 10));
        Assert.Equal(two, ContourCurve.Flatten(two, 1));
        Assert.Equal(withDuplicates, ContourCurve.Flatten(withDuplicates, 0));
    }

    [Fact]
    public void The_setting_defaults_to_the_default_strength_and_is_kept_in_bounds()
    {
        Assert.Equal(ContourSmoothing.DefaultStrength, new UserSettings().HoldContourSmoothing);
        Assert.Equal(100, new UserSettings { HoldContourSmoothing = 500 }.Normalized().HoldContourSmoothing);
        Assert.Equal(0, new UserSettings { HoldContourSmoothing = -3 }.Normalized().HoldContourSmoothing);
    }

    /// <summary>A triangle mask (tips at the given points) whose sides are jagged by ±2 px every few pixels.</summary>
    private static byte[] JaggedTriangle((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, int seed = 3)
    {
        var random = new Random(seed);
        var noise = Enumerable.Range(0, Size * Size / 16).Select(_ => random.Next(-2, 3)).ToArray();
        static double Side((double X, double Y) p, (double X, double Y) q, double x, double y) =>
            ((q.X - p.X) * (y - p.Y) - (q.Y - p.Y) * (x - p.X)) / Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
        var mask = new byte[Size * Size];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var jitter = noise[(y / 4) * (Size / 4) + x / 4];
                double s1 = Side(a, b, x, y), s2 = Side(b, c, x, y), s3 = Side(c, a, x, y);
                if (s1 >= jitter && s2 >= jitter && s3 >= jitter) mask[y * Size + x] = 1;
            }
        return mask;
    }

    [Fact]
    public void A_triangle_keeps_its_tips_while_its_teeth_are_smoothed()   // D075 §2 (the owner)
    {
        (double X, double Y) a = (30, 170), b = (100, 30), c = (170, 170);
        var mask = JaggedTriangle(a, b, c);
        var (component, _) = MaskContour.LargestComponent(mask, Size, Size);
        var raw = MaskContour.Outer(component, Size, Size);
        var smoothed = ContourSmoothing.Smooth(raw, Size, Size, ContourSmoothing.MaxStrength);
        var points = smoothed.Select(p => (X: p.X * Size, Y: p.Y * Size)).ToList();
        double Nearest((double X, double Y) tip) => points.Min(p => Math.Sqrt((p.X - tip.X) * (p.X - tip.X) + (p.Y - tip.Y) * (p.Y - tip.Y)));
        foreach (var tip in new[] { a, b, c })
            Assert.True(Nearest(tip) < 6, $"tip ({tip.X},{tip.Y}) cut: nearest outline point {Nearest(tip):F1} px away");
        Assert.True(points.Count < raw.Length, "the teeth are not smoothed");
    }

    [Theory]   // D075 §2: a small hold's pixel staircase is never a corner (a side ≥ 6 px): it is smoothed
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(18)]
    public void A_small_round_hold_has_no_corners_and_is_smoothed(int radius)
    {
        var mask = new byte[Size * Size];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                if ((x - 100) * (x - 100) + (y - 100) * (y - 100) <= radius * radius) mask[y * Size + x] = 1;
        var raw = MaskContour.Outer(mask, Size, Size);
        var ring = raw.Select(p => (X: p.X * Size, Y: p.Y * Size)).ToList();
        Assert.Empty(ContourSmoothing.Corners(ring, 2 * radius));
        Assert.NotEqual(raw, ContourSmoothing.Smooth(raw, Size, Size, ContourSmoothing.MaxStrength));
    }

    [Fact]
    public void A_round_hold_has_no_corners()   // D075 §2: a circle simplified at 4 % never reads as corners
    {
        var (component, _) = MaskContour.LargestComponent(HedgehogMask(), Size, Size);
        var raw = MaskContour.Outer(component, Size, Size);
        var ring = raw.Select(p => (X: p.X * Size, Y: p.Y * Size)).ToList();
        Assert.Empty(ContourSmoothing.Corners(ring, 80));
    }
}
