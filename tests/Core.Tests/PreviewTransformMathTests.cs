using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// D038 §3: the gesture math of the Preview's Move, Scale and Crop. The anchored crop is checked by its definition — every
/// source pixel that stays visible keeps its canvas place (the source → canvas map is unchanged, to 1e-7 px) — for upright,
/// quarter-turned and arbitrary angles, scaled clips and portrait / landscape mixes; the hand-worked cases are exact.
/// </summary>
public class PreviewTransformMathTests
{
    private static readonly FrameSize Landscape = new(1920, 1080);
    private static readonly FrameSize Portrait = new(1080, 1920);
    private static readonly VisualProperties Plain = VisualProperties.Default;

    private static void AssertNear(PointD expected, PointD actual, double tolerance = 1e-7)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
    }

    /// <summary>The source → canvas maps of <paramref name="before"/> and <paramref name="after"/> agree on the part that
    /// <paramref name="after"/> keeps visible (its corners and centre).</summary>
    private static void AssertAnchored(FrameSize canvas, FrameSize source, VisualProperties before, VisualProperties after)
    {
        var g0 = CompositionMath.Layout(canvas, source, before);
        var g1 = CompositionMath.Layout(canvas, source, after);
        var m0 = PreviewTransformMath.SourceToCanvas(g0);
        var m1 = PreviewTransformMath.SourceToCanvas(g1);
        var r = g1.SourceRect;
        foreach (var p in new[] { new PointD(r.X, r.Y), new PointD(r.Right, r.Y), new PointD(r.Right, r.Bottom), new PointD(r.X, r.Bottom),
                     new PointD(r.X + r.Width / 2, r.Y + r.Height / 2) })
            AssertNear(m0.Apply(p), m1.Apply(p));
        Assert.Null(ClipPropertyValidator.ValidateVisual(after, allowCrop: true));
    }

    // --- Move -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Move_rounds_the_delta_to_whole_canvas_pixels_and_keeps_the_fraction_of_the_start()
    {
        var start = Plain with { PositionX = 10.25, PositionY = -3.5 };
        var moved = PreviewTransformMath.Move(start, 4.4, -2.5, keepAxis: false);
        Assert.Equal(14.25, moved.PositionX);
        Assert.Equal(-6.5, moved.PositionY);   // −2.5 away from zero → −3
        Assert.Equal(start with { PositionX = 14.25, PositionY = -6.5 }, moved);
    }

    [Fact]
    public void Move_without_a_whole_pixel_is_the_start_exactly()
    {
        var start = Plain with { PositionX = 1.0 / 3, PositionY = 2.0 / 3 };
        Assert.Equal(start, PreviewTransformMath.Move(start, 0.49, -0.49, keepAxis: false));
    }

    [Theory]
    [InlineData(30, 10, 30, 0)]
    [InlineData(10, -30, 0, -30)]
    [InlineData(-20, 20, -20, 0)]   // a tie keeps the horizontal axis
    public void Move_with_Shift_keeps_the_axis_with_the_larger_delta(double dx, double dy, double ex, double ey)
    {
        var moved = PreviewTransformMath.Move(Plain, dx, dy, keepAxis: true);
        Assert.Equal(ex, moved.PositionX);
        Assert.Equal(ey, moved.PositionY);
    }

    [Fact]
    public void Move_is_clamped_to_the_position_limit()
    {
        var moved = PreviewTransformMath.Move(Plain with { PositionX = 99_990 }, 50, -250_000, keepAxis: false);
        Assert.Equal(ClipPropertyLimits.MaxPositionMagnitude, moved.PositionX);
        Assert.Equal(-ClipPropertyLimits.MaxPositionMagnitude, moved.PositionY);
    }

    [Fact]
    public void Move_shifts_the_picture_by_the_delta_whatever_its_rotation_scale_and_crop()
    {
        var start = Plain with { RotationDegrees = 37, Scale = 0.6, Crop = new CropRect(0.1, 0.2, 0.05, 0) };
        var moved = PreviewTransformMath.Move(start, 120, -45, keepAxis: false);
        var c0 = CompositionMath.Layout(Landscape, Landscape, start).Center;
        var c1 = CompositionMath.Layout(Landscape, Landscape, moved).Center;
        Assert.Equal(new PointD(c0.X + 120, c0.Y - 45), c1);
        Assert.Equal(start with { PositionX = 120, PositionY = -45 }, moved);
    }

    // --- Scale ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(37)]
    [InlineData(-150)]
    public void Scale_follows_the_corner_along_the_diagonal_around_the_centre(double rotation)
    {
        var start = Plain with { RotationDegrees = rotation, Scale = 0.5, PositionX = 100, PositionY = -50 };
        var frame = PreviewTransformMath.PictureFrame(CompositionMath.Layout(Landscape, Portrait, start));
        var center = frame.Center;
        var corner = frame.HandlePoint(CropEdges.Right | CropEdges.Bottom);
        var doubled = new PointD(center.X + 2 * (corner.X - center.X), center.Y + 2 * (corner.Y - center.Y));

        var scaled = PreviewTransformMath.ScaleFromCorner(start, center, corner, doubled);

        Assert.Equal(1.0, scaled.Scale);
        Assert.Equal(start with { Scale = 1.0 }, scaled);   // the position (and everything else) unchanged
        var after = PreviewTransformMath.PictureFrame(CompositionMath.Layout(Landscape, Portrait, scaled));
        AssertNear(center, after.Center);
        AssertNear(doubled, after.HandlePoint(CropEdges.Right | CropEdges.Bottom));
        // The opposite corner moved symmetrically.
        var opposite = frame.HandlePoint(CropEdges.Left | CropEdges.Top);
        AssertNear(new PointD(center.X + 2 * (opposite.X - center.X), center.Y + 2 * (opposite.Y - center.Y)), after.HandlePoint(CropEdges.Left | CropEdges.Top));
    }

    [Fact]
    public void Scale_ignores_a_move_across_the_diagonal()
    {
        var center = new PointD(960, 540);
        var corner = new PointD(1160, 640);
        // (−1, 2) is perpendicular to the diagonal (200, 100).
        var scaled = PreviewTransformMath.ScaleFromCorner(Plain, center, corner, new PointD(1160 - 30, 640 + 60));
        Assert.Equal(1.0, scaled.Scale);
    }

    [Fact]
    public void Scale_is_rounded_to_a_hundredth_of_a_percent()
    {
        var center = new PointD(0, 0);
        var scaled = PreviewTransformMath.ScaleFromCorner(Plain, center, new PointD(300, 0), new PointD(262.123456, 0));
        Assert.Equal(0.8737, scaled.Scale);   // 262.123456 / 300 = 0.87374…
    }

    [Fact]
    public void An_unmoved_corner_keeps_an_unrounded_scale_exactly()
    {
        var start = Plain with { Scale = 0.123456789 };
        Assert.Equal(start, PreviewTransformMath.ScaleFromCorner(start, new PointD(5, 5), new PointD(105, 55), new PointD(105, 55)));
    }

    [Fact]
    public void Scale_stops_at_its_limits_and_past_the_centre_is_the_minimum()
    {
        var center = new PointD(0, 0);
        var corner = new PointD(100, 100);
        Assert.Equal(ClipPropertyLimits.MaxScale, PreviewTransformMath.ScaleFromCorner(Plain, center, corner, new PointD(5000, 5000)).Scale);
        Assert.Equal(ClipPropertyLimits.MinScale, PreviewTransformMath.ScaleFromCorner(Plain, center, corner, new PointD(0.1, 0.1)).Scale);
        Assert.Equal(ClipPropertyLimits.MinScale, PreviewTransformMath.ScaleFromCorner(Plain, center, corner, new PointD(-50, -50)).Scale);
        Assert.Equal(ClipPropertyLimits.MinScale, PreviewTransformMath.ScaleFromCorner(Plain, center, corner, center).Scale);
    }

    [Fact]
    public void Scale_of_a_degenerate_diagonal_changes_nothing()
    {
        var p = new PointD(10, 10);
        Assert.Equal(Plain, PreviewTransformMath.ScaleFromCorner(Plain, p, p, new PointD(50, 50)));
    }

    // --- Anchored crop: hand-worked cases ---------------------------------------------------------------------------

    [Fact]
    public void Crop_of_the_left_edge_of_a_landscape_picture_keeps_the_rest_in_place()
    {
        // 1920 × 1080 on 1920 × 1080: fit 1. Left edge to x = 480: the crop 1440 × 1080 re-fits to 1 (D018) and is centred,
        // so the position compensates by +240 (the crop's centre 1200 stays at canvas 1200).
        var cropped = PreviewTransformMath.AnchoredCrop(Landscape, Landscape, Plain, CropEdges.Left, new PointD(480.3, 9999));
        Assert.Equal(new CropRect(0.25, 0, 0, 0), cropped.Crop);
        Assert.Equal(1.0, cropped.Scale);
        Assert.Equal(240.0, cropped.PositionX);
        Assert.Equal(0.0, cropped.PositionY);
        AssertAnchored(Landscape, Landscape, Plain, cropped);
    }

    [Fact]
    public void Crop_of_the_top_of_a_portrait_picture_on_a_landscape_canvas_does_not_enlarge_it()
    {
        // 1080 × 1920 on 1920 × 1080: fit 0.5625. Top edge to 960: D018 would re-fit the 1080 × 960 crop to 1.125 (twice as
        // large); the anchored crop writes Scale 0.5 so it stays at 0.5625 per source pixel, centre y = 540 + 480 · 0.5625.
        var cropped = PreviewTransformMath.AnchoredCrop(Landscape, Portrait, Plain, CropEdges.Top, new PointD(0, 960));
        Assert.Equal(new CropRect(0, 0.5, 0, 0), cropped.Crop);
        Assert.Equal(0.5, cropped.Scale);
        Assert.Equal(0.0, cropped.PositionX);
        Assert.Equal(270.0, cropped.PositionY);
        Assert.Equal(0.5625, CompositionMath.Layout(Landscape, Portrait, cropped).FitScale * cropped.Scale);
        AssertAnchored(Landscape, Portrait, Plain, cropped);
    }

    [Fact]
    public void A_corner_moves_both_of_its_edges_and_the_others_keep_their_exact_values()
    {
        var start = Plain with { Crop = new CropRect(0.1234567, 1.0 / 3, 0.05, 0.0123) };
        var cropped = PreviewTransformMath.AnchoredCrop(Landscape, Landscape, start, CropEdges.Right | CropEdges.Bottom, new PointD(1500.6, 900.4));
        Assert.Equal(0.1234567, cropped.Crop.Left);
        Assert.Equal(1.0 / 3, cropped.Crop.Top);
        Assert.Equal((1920 - 1501.0) / 1920, cropped.Crop.Right);
        Assert.Equal((1080 - 900.0) / 1080, cropped.Crop.Bottom);
        AssertAnchored(Landscape, Landscape, start, cropped);
    }

    // --- Anchored crop: the definition over many layouts -------------------------------------------------------------

    public static IEnumerable<object[]> Layouts()
    {
        var sources = new[] { Landscape, Portrait, new FrameSize(4000, 3000), new FrameSize(720, 1280) };
        var canvases = new[] { Landscape, Portrait };
        var visuals = new[]
        {
            Plain,
            Plain with { RotationDegrees = 90 },
            Plain with { RotationDegrees = 180, Scale = 2 },
            Plain with { RotationDegrees = 37, Scale = 0.5, PositionX = -300, PositionY = 120 },
            Plain with { RotationDegrees = -123.5, Scale = 1.7, Crop = new CropRect(0.05, 0.1, 0.2, 0.15) }
        };
        var drags = new (CropEdges Edges, double U, double V)[]
        {
            (CropEdges.Left, 0.3, 0.5), (CropEdges.Top, 0.5, 0.4), (CropEdges.Right, 0.6, 0.5), (CropEdges.Bottom, 0.5, 0.7),
            (CropEdges.Left | CropEdges.Top, 0.25, 0.3), (CropEdges.Right | CropEdges.Bottom, 0.8, 0.75),
            (CropEdges.Left, 0.0, 0.5), (CropEdges.Right, 1.0, 0.5)   // out to the source's edge (from a cropped start)
        };
        foreach (var canvas in canvases)
            foreach (var source in sources)
                foreach (var visual in visuals)
                    foreach (var (edges, u, v) in drags)
                        yield return new object[] { canvas.Width, canvas.Height, source.Width, source.Height, visual, edges, u, v };
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Anchored_crop_keeps_every_visible_source_pixel_in_place(int cw, int ch, int sw, int sh, VisualProperties start,
        CropEdges edges, double u, double v)
    {
        var canvas = new FrameSize(cw, ch);
        var source = new FrameSize(sw, sh);
        var point = new PointD(u * sw, v * sh);
        var cropped = PreviewTransformMath.AnchoredCrop(canvas, source, start, edges, point);

        AssertAnchored(canvas, source, start, cropped);
        Assert.Equal(start.RotationDegrees, cropped.RotationDegrees);
        Assert.Equal(start.Opacity, cropped.Opacity);
        // Every dragged edge sits on a whole source pixel (the pointer's, unless it stopped); the others are untouched.
        var crop = cropped.Crop;
        if (edges.HasFlag(CropEdges.Left)) AssertWhole(crop.Left * sw);
        else if (!edges.HasFlag(CropEdges.Right)) Assert.Equal(start.Crop.Left, crop.Left);
        if (edges.HasFlag(CropEdges.Right)) AssertWhole(sw - crop.Right * sw);
        else if (!edges.HasFlag(CropEdges.Left)) Assert.Equal(start.Crop.Right, crop.Right);
        if (edges.HasFlag(CropEdges.Top)) AssertWhole(crop.Top * sh);
        if (edges.HasFlag(CropEdges.Bottom)) AssertWhole(sh - crop.Bottom * sh);
    }

    private static void AssertWhole(double pixels) => Assert.InRange(pixels - Math.Round(pixels), -1e-6, 1e-6);

    // --- Anchored crop: constraints ---------------------------------------------------------------------------------

    [Fact]
    public void A_dragged_edge_stops_at_the_source_edge()
    {
        var start = Plain with { Crop = new CropRect(0.25, 0, 0, 0), PositionX = 240 };
        var uncropped = PreviewTransformMath.AnchoredCrop(Landscape, Landscape, start, CropEdges.Left, new PointD(-800, 0));
        Assert.Equal(CropRect.None, uncropped.Crop);
        Assert.Equal(Plain, uncropped);   // back to the uncropped picture in its place
    }

    [Fact]
    public void A_dragged_edge_stops_at_the_minimum_size()
    {
        // Left past the right edge: at least max(4 px, 1 % of 1920 = 20 px) stays.
        var cropped = PreviewTransformMath.AnchoredCrop(Landscape, Landscape, Plain, CropEdges.Left, new PointD(5000, 0));
        Assert.Equal((1920 - 20.0) / 1920, cropped.Crop.Left);
        AssertAnchored(Landscape, Landscape, Plain, cropped);

        var tiny = new FrameSize(100, 50);   // 1 % of 100 = 1 px < 4 px
        var top = PreviewTransformMath.AnchoredCrop(Landscape, tiny, Plain, CropEdges.Bottom, new PointD(0, -10));
        Assert.Equal((50 - 4.0) / 50, top.Crop.Bottom);
    }

    [Fact]
    public void An_uncrop_stops_where_the_compensating_scale_reaches_the_maximum()
    {
        // Portrait on landscape, cropped to its lower half and scaled 8: k = 1.125 · 8 = 9 canvas px per source px. The
        // whole height would need Scale 9 · 1920 / 1080 = 16 > 10: the top edge stops where cropH = 10 · 1080 / 9 = 1200.
        var start = Plain with { Crop = new CropRect(0, 0.5, 0, 0), Scale = 8 };
        var uncropped = PreviewTransformMath.AnchoredCrop(Landscape, Portrait, start, CropEdges.Top, new PointD(0, 0));
        Assert.Equal(720.0 / 1920, uncropped.Crop.Top);
        Assert.Equal(ClipPropertyLimits.MaxScale, uncropped.Scale, 12);
        AssertAnchored(Landscape, Portrait, start, uncropped);
    }

    [Fact]
    public void At_the_minimum_scale_a_crop_that_needs_a_smaller_scale_stops()
    {
        // Scale 1 % of a fitting picture: k = 0.01, so Scale′ = 0.01 · max(w / 1920, h / 1080) ≥ 0.01 needs one axis whole.
        // An edge crop keeps the other axis whole — allowed.
        var start = Plain with { Scale = ClipPropertyLimits.MinScale };
        var left = PreviewTransformMath.AnchoredCrop(Landscape, Landscape, start, CropEdges.Left, new PointD(600, 0));
        Assert.Equal(600.0 / 1920, left.Crop.Left);
        Assert.Equal(ClipPropertyLimits.MinScale, left.Scale);
        AssertAnchored(Landscape, Landscape, start, left);

        // A corner crop to the centre would need 0.5 %: the horizontal edge stops at the full width, the vertical one moves.
        var corner = PreviewTransformMath.AnchoredCrop(Landscape, Landscape, start, CropEdges.Right | CropEdges.Bottom, new PointD(960, 540));
        Assert.Equal(new CropRect(0, 0, 0, 0.5), corner.Crop);
        Assert.Equal(ClipPropertyLimits.MinScale, corner.Scale);
        AssertAnchored(Landscape, Landscape, start, corner);

        // A slight corner crop: the same — the right edge stays at the full width, only the bottom moves.
        var both = PreviewTransformMath.AnchoredCrop(Landscape, Landscape, start, CropEdges.Right | CropEdges.Bottom, new PointD(1900, 1000));
        Assert.Equal(new CropRect(0, 0, 0, 80.0 / 1080), both.Crop);
        AssertAnchored(Landscape, Landscape, start, both);
    }

    [Fact]
    public void A_crop_whose_compensating_position_would_leave_the_limit_changes_nothing()
    {
        var start = Plain with { PositionX = ClipPropertyLimits.MaxPositionMagnitude };
        Assert.Equal(start, PreviewTransformMath.AnchoredCrop(Landscape, Landscape, start, CropEdges.Left, new PointD(480, 0)));
    }

    [Fact]
    public void An_unchanged_crop_is_the_start_exactly()
    {
        var start = Plain with { Crop = new CropRect(0.25, 0, 0, 0), PositionX = 240.123456, Scale = 0.987654321 };
        Assert.Equal(start, PreviewTransformMath.AnchoredCrop(Landscape, Landscape, start, CropEdges.Left, new PointD(480.2, 0)));
        Assert.Equal(start, PreviewTransformMath.AnchoredCrop(Landscape, Landscape, start, CropEdges.None, new PointD(100, 100)));
    }

    // --- Frames -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_rotated_frame_contains_its_inside_only()
    {
        var g = CompositionMath.Layout(Landscape, Landscape, Plain with { Scale = 0.5, RotationDegrees = 45 });
        var frame = PreviewTransformMath.PictureFrame(g);
        Assert.True(frame.Contains(new PointD(960, 540)));
        // 600 px right of the centre is (424, −424) in the picture's own axes: outside its half height of 270.
        Assert.False(frame.Contains(new PointD(1560, 540)));
        Assert.True(frame.Contains(new PointD(1300, 860)));   // (467, −14) in its own axes: inside (half width 480)

        foreach (var handle in TransformFrame.Handles)
            Assert.True(frame.Contains(frame.HandlePoint(handle)) || IsOnEdge(frame, frame.HandlePoint(handle)));
    }

    private static bool IsOnEdge(TransformFrame frame, PointD p)
    {
        var local = frame.Transform.Invert().Apply(p);
        var r = frame.LocalRect;
        return local.X >= r.X - 1e-9 && local.X <= r.Right + 1e-9 && local.Y >= r.Y - 1e-9 && local.Y <= r.Bottom + 1e-9;
    }

    [Fact]
    public void Handle_points_are_the_corners_and_edge_midpoints()
    {
        var frame = PreviewTransformMath.PictureFrame(CompositionMath.Layout(Landscape, Landscape, Plain with { Scale = 0.5 }));
        Assert.Equal(new PointD(480, 270), frame.HandlePoint(CropEdges.Left | CropEdges.Top));
        Assert.Equal(new PointD(960, 270), frame.HandlePoint(CropEdges.Top));
        Assert.Equal(new PointD(1440, 810), frame.HandlePoint(CropEdges.Right | CropEdges.Bottom));
        Assert.Equal(new PointD(480, 540), frame.HandlePoint(CropEdges.Left));
        Assert.Equal(new PointD(960, 540), frame.Center);
    }

    [Fact]
    public void A_text_frame_is_its_box_centred_on_the_position()
    {
        var frame = PreviewTransformMath.TextFrame(Landscape, Plain with { PositionX = 100, Scale = 2 }, 300, 60);
        Assert.Equal(new PointD(1060, 540), frame.Center);
        Assert.Equal(new PointD(1060 - 300, 540 - 60), frame.HandlePoint(CropEdges.Left | CropEdges.Top));
    }

    [Fact]
    public void The_source_frame_extends_the_visible_picture_over_the_cropped_away_part()
    {
        var start = Plain with { Crop = new CropRect(0.25, 0, 0, 0), PositionX = 240 };
        var g = CompositionMath.Layout(Landscape, Landscape, start);
        var full = PreviewTransformMath.SourceFrame(g);
        Assert.Equal(new PointD(0, 0), full.HandlePoint(CropEdges.Left | CropEdges.Top));
        Assert.Equal(new PointD(1920, 1080), full.HandlePoint(CropEdges.Right | CropEdges.Bottom));
        Assert.Equal(new PointD(480, 0), PreviewTransformMath.PictureFrame(g).HandlePoint(CropEdges.Left | CropEdges.Top));
    }
}
