using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// D018 geometry. Expected matrices are worked out by hand from
/// <c>M = T(cw/2 + px, ch/2 + py) · R(θ) · S(fit · scale) · T(−w/2, −h/2)</c> with sizes whose fit
/// factors are exact binary fractions, so results are compared exactly — no pixel tolerances.
/// Only arbitrary (non-90°) angles use a 1e-9 bound.
/// </summary>
public class CompositionMathTests
{
    private static readonly FrameSize Canvas = new(1920, 1080);
    private static readonly FrameSize Vertical = new(1080, 1920);
    private static readonly VisualProperties Plain = VisualProperties.Default;

    private static LayerGeometry Layout(int sourceW, int sourceH, VisualProperties? visual = null, FrameSize? canvas = null) =>
        CompositionMath.Layout(canvas ?? Canvas, new FrameSize(sourceW, sourceH), visual ?? Plain);

    private static void AssertPoint(double x, double y, PointD actual) => Assert.Equal(new PointD(x, y), actual);

    private static PointD Local(LayerGeometry g, double u, double v) => g.Transform.Apply(new PointD(u, v));

    // --- Plain fit ------------------------------------------------------------------------------------

    [Fact]
    public void Landscape_same_size_is_identity_and_covers()
    {
        var g = Layout(1920, 1080);

        Assert.Equal(new RectD(0, 0, 1920, 1080), g.SourceRect);
        Assert.Equal(new RectD(0, 0, 1, 1), g.NormalizedSourceRect);
        Assert.Equal(1.0, g.FitScale);
        Assert.Equal(new Affine2D(1, 0, 0, 1, 0, 0), g.Transform);
        Assert.Equal(new RectD(0, 0, 1920, 1080), g.Bounds);
        Assert.Equal(new PointD(960, 540), g.Center);
        Assert.True(g.CoversCanvas);
    }

    [Theory]
    [InlineData(3840, 2160, 0.5)]
    [InlineData(1280, 720, 1.5)]
    [InlineData(960, 540, 2.0)]
    public void Landscape_16_9_of_any_size_fills_the_canvas(int w, int h, double fit)
    {
        var g = Layout(w, h);

        Assert.Equal(fit, g.FitScale);
        Assert.Equal(new Affine2D(fit, 0, 0, fit, 0, 0), g.Transform);
        Assert.Equal(new RectD(0, 0, 1920, 1080), g.Bounds);
        Assert.True(g.CoversCanvas);
    }

    [Fact]
    public void Portrait_is_pillarboxed_by_height()
    {
        var g = Layout(1080, 1920); // fit = min(1920/1080, 1080/1920) = 0.5625 → 607.5 × 1080

        Assert.Equal(0.5625, g.FitScale);
        Assert.Equal(new Affine2D(0.5625, 0, 0, 0.5625, 656.25, 0), g.Transform);
        Assert.Equal(new RectD(656.25, 0, 607.5, 1080), g.Bounds);
        Assert.False(g.CoversCanvas);
    }

    [Fact]
    public void Wider_than_canvas_is_letterboxed_by_width()
    {
        var g = Layout(2560, 1080); // 64:27 → fit 0.75 → 1920 × 810

        Assert.Equal(0.75, g.FitScale);
        Assert.Equal(new Affine2D(0.75, 0, 0, 0.75, 0, 135), g.Transform);
        Assert.Equal(new RectD(0, 135, 1920, 810), g.Bounds);
        Assert.False(g.CoversCanvas);
    }

    // --- Crop -----------------------------------------------------------------------------------------

    public static TheoryData<string, CropRect, RectD, Affine2D, RectD> CropEachSide => new()
    {
        // 1920 × 1080 source, 25 % off one side.
        { "left", new CropRect(0.25, 0, 0, 0), new RectD(480, 0, 1440, 1080), new Affine2D(1, 0, 0, 1, 240, 0), new RectD(240, 0, 1440, 1080) },
        { "right", new CropRect(0, 0, 0.25, 0), new RectD(0, 0, 1440, 1080), new Affine2D(1, 0, 0, 1, 240, 0), new RectD(240, 0, 1440, 1080) },
        { "top", new CropRect(0, 0.25, 0, 0), new RectD(0, 270, 1920, 810), new Affine2D(1, 0, 0, 1, 0, 135), new RectD(0, 135, 1920, 810) },
        { "bottom", new CropRect(0, 0, 0, 0.25), new RectD(0, 0, 1920, 810), new Affine2D(1, 0, 0, 1, 0, 135), new RectD(0, 135, 1920, 810) },
    };

    [Theory]
    [MemberData(nameof(CropEachSide))]
    public void Crop_of_each_side_gives_the_source_rect_and_is_refitted(string side, CropRect crop, RectD sourceRect, Affine2D transform, RectD bounds)
    {
        var g = Layout(1920, 1080, Plain with { Crop = crop });

        Assert.Equal(sourceRect, g.SourceRect);
        Assert.Equal(transform, g.Transform);
        Assert.Equal(bounds, g.Bounds);
        Assert.False(g.CoversCanvas, side);
    }

    [Fact]
    public void Crop_to_a_square_changes_the_aspect_ratio()
    {
        // 7/32 off left and right: 1920 · (1 − 14/32) = 1080 → a 1080 × 1080 square, fitted by height.
        var g = Layout(1920, 1080, Plain with { Crop = new CropRect(0.21875, 0, 0.21875, 0) });

        Assert.Equal(new RectD(420, 0, 1080, 1080), g.SourceRect);
        Assert.Equal(new RectD(0.21875, 0, 0.5625, 1), g.NormalizedSourceRect);
        Assert.Equal(1.0, g.FitScale);
        Assert.Equal(new Affine2D(1, 0, 0, 1, 420, 0), g.Transform);
        Assert.Equal(new RectD(420, 0, 1080, 1080), g.Bounds);
    }

    // --- Scale, rotation, position, opacity ------------------------------------------------------------

    [Fact]
    public void Scale_below_one_shrinks_around_the_centre()
    {
        var g = Layout(1920, 1080, Plain with { Scale = 0.5 });

        Assert.Equal(new Affine2D(0.5, 0, 0, 0.5, 480, 270), g.Transform);
        Assert.Equal(new RectD(480, 270, 960, 540), g.Bounds);
        Assert.False(g.CoversCanvas);
    }

    [Fact]
    public void Scale_above_one_grows_around_the_centre_and_still_covers()
    {
        var g = Layout(1920, 1080, Plain with { Scale = 2 });

        Assert.Equal(new Affine2D(2, 0, 0, 2, -960, -540), g.Transform);
        Assert.Equal(new RectD(-960, -540, 3840, 2160), g.Bounds);
        AssertPoint(960, 540, Local(g, 960, 540));
        Assert.True(g.CoversCanvas);
    }

    public static TheoryData<double, Affine2D, RectD, bool> QuarterTurns => new()
    {
        { 0, new Affine2D(1, 0, 0, 1, 0, 0), new RectD(0, 0, 1920, 1080), true },
        { 90, new Affine2D(0, -1, 1, 0, 1500, -420), new RectD(420, -420, 1080, 1920), false },
        { 180, new Affine2D(-1, 0, 0, -1, 1920, 1080), new RectD(0, 0, 1920, 1080), true },
        { 270, new Affine2D(0, 1, -1, 0, 420, 1500), new RectD(420, -420, 1080, 1920), false },
        { -90, new Affine2D(0, 1, -1, 0, 420, 1500), new RectD(420, -420, 1080, 1920), false },
        { 360, new Affine2D(1, 0, 0, 1, 0, 0), new RectD(0, 0, 1920, 1080), true },
        { -360, new Affine2D(1, 0, 0, 1, 0, 0), new RectD(0, 0, 1920, 1080), true },
    };

    [Theory]
    [MemberData(nameof(QuarterTurns))]
    public void Quarter_turns_are_exact(double degrees, Affine2D transform, RectD bounds, bool covers)
    {
        var g = Layout(1920, 1080, Plain with { RotationDegrees = degrees });

        Assert.Equal(transform, g.Transform);
        Assert.Equal(bounds, g.Bounds);
        Assert.Equal(covers, g.CoversCanvas);
        AssertPoint(960, 540, Local(g, 960, 540)); // rotation is around the picture's centre
    }

    [Fact]
    public void Positive_rotation_turns_clockwise_on_screen()
    {
        // The picture's top-left corner goes to the top-right after +90° (Y down).
        var g = Layout(1080, 1080, Plain with { RotationDegrees = 90 }, new FrameSize(1080, 1080));
        AssertPoint(1080, 0, Local(g, 0, 0));
    }

    [Fact]
    public void Arbitrary_angle()
    {
        var g = Layout(1920, 1080, Plain with { RotationDegrees = 30 });
        var (sin, cos) = (0.5, Math.Sqrt(3) / 2);

        Assert.Equal(cos, g.Transform.A, 1e-15);
        Assert.Equal(-sin, g.Transform.B, 1e-15);
        Assert.Equal(sin, g.Transform.C, 1e-15);
        Assert.Equal(cos, g.Transform.D, 1e-15);
        Assert.Equal(960 - (cos * 960 - sin * 540), g.Transform.Tx, 1e-9);
        Assert.Equal(540 - (sin * 960 + cos * 540), g.Transform.Ty, 1e-9);

        var center = Local(g, 960, 540);
        Assert.Equal(960, center.X, 1e-9);
        Assert.Equal(540, center.Y, 1e-9);
        Assert.False(g.CoversCanvas); // a rotated 16:9 picture of the canvas' own size leaves corners empty
    }

    [Fact]
    public void Position_moves_the_centre_in_canvas_pixels_x_right_y_down()
    {
        var g = Layout(1920, 1080, Plain with { PositionX = 100, PositionY = -50 });

        Assert.Equal(new Affine2D(1, 0, 0, 1, 100, -50), g.Transform);
        Assert.Equal(new PointD(1060, 490), g.Center);
        AssertPoint(1060, 490, Local(g, 960, 540));
        Assert.False(g.CoversCanvas);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Opacity_is_carried_and_changes_no_geometry(double opacity)
    {
        var g = Layout(1920, 1080, Plain with { Opacity = opacity });

        Assert.Equal(opacity, g.Opacity);
        Assert.Equal(Affine2D.Identity, g.Transform);
        Assert.True(g.CoversCanvas); // coverage is geometric; OccludesBelow also requires opacity 1
    }

    [Fact]
    public void All_transformations_combined()
    {
        // Portrait 1080 × 1920, top 25 % cropped → 1080 × 1440 at y = 480; fit = min(1920/1080, 1080/1440) = 0.75;
        // scale 2 → 1.5; rotate 90°; centre moved to (970, 560); opacity 0.5.
        var visual = new VisualProperties(10, 20, 2, 90, 0.5, new CropRect(0, 0.25, 0, 0));
        var g = Layout(1080, 1920, visual);

        Assert.Equal(new RectD(0, 480, 1080, 1440), g.SourceRect);
        Assert.Equal(0.75, g.FitScale);
        Assert.Equal(new Affine2D(0, -1.5, 1.5, 0, 2050, -250), g.Transform);
        Assert.Equal(new RectD(-110, -250, 2160, 1620), g.Bounds);
        AssertPoint(970, 560, Local(g, 540, 720));
        Assert.Equal(0.5, g.Opacity);
        Assert.True(g.CoversCanvas); // 2160 × 1620 around (970, 560) contains 0..1920 × 0..1080
    }

    // --- Order of operations ------------------------------------------------------------------------------

    [Fact]
    public void Crop_comes_before_fit()
    {
        // Portrait 1080 × 1920 with 25 % off top and bottom → 1080 × 960, re-fitted: 1080/960 = 1.125.
        // (Fit before crop would keep 0.5625 and show only 607.5 × 540.)
        var g = Layout(1080, 1920, Plain with { Crop = new CropRect(0, 0.25, 0, 0.25) });

        Assert.Equal(1.125, g.FitScale);
        Assert.Equal(new RectD(352.5, 0, 1215, 1080), g.Bounds);
    }

    [Fact]
    public void Scale_comes_after_fit_and_multiplies_it()
    {
        var g = Layout(3840, 2160, Plain with { Scale = 0.5 }); // fit 0.5 · scale 0.5

        Assert.Equal((0.5, 0.5), (g.FitScale, g.Scale));
        Assert.Equal(new Affine2D(0.25, 0, 0, 0.25, 480, 270), g.Transform);
        Assert.Equal(new RectD(480, 270, 960, 540), g.Bounds);
    }

    [Fact]
    public void Rotation_comes_after_fit_so_a_rotated_picture_is_not_refitted()
    {
        // Fitted as landscape (1920 × 1080), then turned: 1080 wide, 1920 tall — overflowing vertically.
        var g = Layout(1920, 1080, Plain with { RotationDegrees = 90 });

        Assert.Equal(1.0, g.FitScale);
        Assert.Equal(1920, g.Bounds.Height);
        Assert.Equal(1080, g.Bounds.Width);
    }

    [Fact]
    public void Rotation_comes_after_scale_and_is_around_the_scaled_picture_centre()
    {
        var g = Layout(1920, 1080, Plain with { Scale = 0.5, RotationDegrees = 90 });

        Assert.Equal(new RectD(690, 60, 540, 960), g.Bounds); // 960 × 540 turned → 540 × 960, centred
        AssertPoint(960, 540, Local(g, 960, 540));
    }

    [Fact]
    public void Position_comes_after_rotation_and_is_not_rotated()
    {
        // +100 px in X moves the picture right on screen even though it is turned by 90°.
        // (Position before rotation would move it down to (960, 640).)
        var g = Layout(1920, 1080, Plain with { RotationDegrees = 90, PositionX = 100 });

        AssertPoint(1060, 540, Local(g, 960, 540));
        Assert.Equal(new Affine2D(0, -1, 1, 0, 1600, -420), g.Transform);
    }

    [Fact]
    public void Position_is_not_scaled()
    {
        var g = Layout(1920, 1080, Plain with { Scale = 0.25, PositionY = 100 });

        AssertPoint(960, 640, Local(g, 960, 540));
    }

    // --- Canvas sizes -------------------------------------------------------------------------------------

    [Fact]
    public void Vertical_project_letterboxes_landscape_and_fills_with_portrait()
    {
        var landscape = Layout(1920, 1080, canvas: Vertical); // fit 1080/1920 = 0.5625 → 1080 × 607.5
        Assert.Equal(0.5625, landscape.FitScale);
        Assert.Equal(new Affine2D(0.5625, 0, 0, 0.5625, 0, 656.25), landscape.Transform);
        Assert.Equal(new RectD(0, 656.25, 1080, 607.5), landscape.Bounds);
        Assert.False(landscape.CoversCanvas);

        var portrait = Layout(1080, 1920, canvas: Vertical);
        Assert.Equal(Affine2D.Identity, portrait.Transform);
        Assert.True(portrait.CoversCanvas);

        var moved = Layout(1080, 1920, Plain with { PositionY = 1 }, Vertical);
        Assert.Equal(new PointD(540, 961), moved.Center); // canvas centre of the vertical project
    }

    [Fact]
    public void Other_canvas_sizes()
    {
        var square = Layout(1920, 1080, canvas: new FrameSize(1000, 1000)); // fit 1000/1920 = 0.5208333…
        Assert.Equal(1000.0 / 1920, square.FitScale);
        Assert.Equal(new PointD(500, 500), square.Center);

        var small = Layout(1920, 1080, canvas: new FrameSize(640, 360));
        Assert.Equal(new Affine2D(1.0 / 3, 0, 0, 1.0 / 3, 0, 0), small.Transform);
        Assert.True(small.CoversCanvas);
    }

    // --- Coverage is decided exactly ------------------------------------------------------------------------

    [Fact]
    public void Coverage_is_exact_at_the_edge()
    {
        Assert.True(Layout(1920, 1080).CoversCanvas);
        Assert.False(Layout(1920, 1080, Plain with { PositionX = 1e-9 }).CoversCanvas);   // a sliver uncovered
        Assert.False(Layout(1920, 1080, Plain with { PositionY = -1e-9 }).CoversCanvas);
        Assert.True(Layout(1920, 1080, Plain with { Scale = 1.0 + 1e-15, PositionX = 1e-13 }).CoversCanvas);
    }

    [Fact]
    public void Coverage_is_decided_on_exact_values_not_on_rounded_doubles()
    {
        // 25 % off the left → 1440 × 1080, fit 1. The double nearest 4/3 is slightly below 4/3, so
        // 1440 · scale falls ~1e-13 px short of 1920 although the double product rounds to 1920.
        var crop = new CropRect(0.25, 0, 0, 0);
        const double below = 4.0 / 3;                          // 1.33333333333333325931…
        var above = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(below) + 1);
        Assert.Equal(1920.0, 1440 * below);                    // what naive double math sees

        Assert.False(Layout(1920, 1080, Plain with { Crop = crop, Scale = below }).CoversCanvas);
        Assert.True(Layout(1920, 1080, Plain with { Crop = crop, Scale = above }).CoversCanvas);
    }

    [Fact]
    public void Rotated_and_enlarged_enough_covers_otherwise_not()
    {
        Assert.True(Layout(1920, 1080, Plain with { RotationDegrees = 45, Scale = 3 }).CoversCanvas);
        Assert.False(Layout(1920, 1080, Plain with { RotationDegrees = 45, Scale = 1 }).CoversCanvas);
        Assert.False(Layout(1920, 1080, Plain with { RotationDegrees = 90, Scale = 1.7 }).CoversCanvas);
        Assert.True(Layout(1920, 1080, Plain with { RotationDegrees = 90, Scale = 1.8 }).CoversCanvas); // 1944 × 3456
    }

    private static bool AabbCoversCanvas(LayerGeometry g) =>
        g.Bounds.X <= 0 && g.Bounds.Y <= 0 && g.Bounds.Right >= Canvas.Width && g.Bounds.Bottom >= Canvas.Height;

    private static PointD[] CanvasCornersInSource(LayerGeometry g)
    {
        var inverse = g.Transform.Invert();
        return new[] { new PointD(0, 0), new PointD(1920, 0), new PointD(0, 1080), new PointD(1920, 1080) }
            .Select(inverse.Apply).ToArray();
    }

    private static bool Inside(LayerGeometry g, PointD p) =>
        p.X >= 0 && p.X <= g.SourceRect.Width && p.Y >= 0 && p.Y <= g.SourceRect.Height;

    [Fact]
    public void Rotated_picture_whose_bounding_box_covers_the_canvas_but_whose_corners_do_not_is_not_covering()
    {
        // 1920 × 1080 turned 45°: the bounding box is 2121 × 2121 around the centre (covers the
        // canvas), but the canvas corners lie outside the turned rectangle.
        var g = Layout(1920, 1080, Plain with { RotationDegrees = 45 });

        Assert.True(AabbCoversCanvas(g));
        var corners = CanvasCornersInSource(g);
        Assert.Contains(corners, c => !Inside(g, c));
        Assert.Equal(960 - 1500 / Math.Sqrt(2), corners[0].X, 1e-9); // (0, 0) maps 100.66 px left of the picture
        Assert.False(g.CoversCanvas);
    }

    [Fact]
    public void Rotated_and_enlarged_picture_containing_all_four_canvas_corners_covers()
    {
        // 30°, scale 2: 3840 × 2160 turned; every canvas corner maps inside the source rectangle.
        var g = Layout(1920, 1080, Plain with { RotationDegrees = 30, Scale = 2 });

        Assert.All(CanvasCornersInSource(g), c => Assert.True(Inside(g, c), $"corner at {c}"));
        Assert.True(g.CoversCanvas);
    }

    [Fact]
    public void Arbitrary_angle_coverage_is_conservative_at_the_edge()
    {
        // At 45° the smallest covering scale is where the canvas corner (960, 540) from the centre just
        // reaches the picture's short edge: v = (960 + 540)/√2 = 540·s → s = 1500/(540·√2).
        var exact = 1500 / (540 * Math.Sqrt(2));
        Assert.False(Layout(1920, 1080, Plain with { RotationDegrees = 45, Scale = exact }).CoversCanvas);        // touching: doubt → false
        Assert.False(Layout(1920, 1080, Plain with { RotationDegrees = 45, Scale = exact * (1 + 1e-9) }).CoversCanvas); // within the margin
        Assert.True(Layout(1920, 1080, Plain with { RotationDegrees = 45, Scale = exact * (1 + 1e-5) }).CoversCanvas);
    }

    // --- Text -----------------------------------------------------------------------------------------------

    [Fact]
    public void Text_transform_is_centred_scaled_rotated_and_positioned_without_fit()
    {
        var m = CompositionMath.TextTransform(Canvas, Plain with { Scale = 2, RotationDegrees = 90, PositionX = -60, PositionY = 40 });

        Assert.Equal(new Affine2D(0, -2, 2, 0, 900, 580), m);
        AssertPoint(900, 580, m.Apply(new PointD(0, 0))); // the text box centre
        Assert.Equal(new Affine2D(1, 0, 0, 1, 540, 960), CompositionMath.TextTransform(Vertical, Plain));
    }

    // --- Invalid input -----------------------------------------------------------------------------------------

    [Fact]
    public void Invalid_input_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionMath.Layout(new FrameSize(0, 1080), new FrameSize(1920, 1080), Plain));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionMath.Layout(Canvas, new FrameSize(1920, -1), Plain));
        Assert.Throws<ArgumentException>(() => Layout(1920, 1080, Plain with { Crop = new CropRect(0.5, 0, 0.5, 0) }));
        Assert.Throws<ArgumentException>(() => Layout(1920, 1080, Plain with { Scale = 0 }));
        Assert.Throws<ArgumentException>(() => Layout(1920, 1080, Plain with { Opacity = double.NaN }));
        Assert.Throws<ArgumentException>(() => CompositionMath.TextTransform(Canvas, Plain with { Crop = new CropRect(0.1, 0, 0, 0) }));
    }
}
