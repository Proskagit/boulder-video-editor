using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>D041: the canvas the first video offers (§1) and the Fill / Fit scales (§2).</summary>
public class FramingRulesTests
{
    // --- The offered canvas (§1) ----------------------------------------------------------------------

    [Theory]
    [InlineData(2160, 3840, 1080, 1920)]   // 4K portrait
    [InlineData(1080, 1920, 1080, 1920)]
    [InlineData(720, 1280, 1080, 1920)]    // never below the default class either
    [InlineData(1080, 2400, 1080, 2400)]   // 20 : 9 phone portrait
    [InlineData(2400, 1080, 2400, 1080)]
    [InlineData(3840, 2160, 1920, 1080)]   // 16 : 9 landscape = the default canvas
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1440, 1080, 1440, 1080)]   // 4 : 3
    [InlineData(1080, 1440, 1080, 1440)]
    [InlineData(1080, 1080, 1080, 1080)]   // square
    [InlineData(4000, 4000, 1080, 1080)]
    [InlineData(1080, 1350, 1080, 1350)]   // 4 : 5
    public void The_offered_canvas_has_the_source_shape_at_the_short_side_1080(int w, int h, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), ProjectSettingsRules.SuggestedCanvas(w, h));
        Assert.Null(ProjectSettingsRules.CanvasError(expectedW, expectedH));
    }

    [Theory]
    [InlineData(1081, 1921, 1080, 1920)]   // 1919.11… → 1920
    [InlineData(1079, 1919, 1080, 1920)]   // 1920.88… → 1920
    [InlineData(1080, 1921, 1080, 1922)]   // 1921 exactly, a half up → 1922
    [InlineData(1080, 1919, 1080, 1920)]   // 1919 → 1920 (a half up)
    [InlineData(1000, 1777, 1080, 1920)]   // 1919.16 → 1920
    [InlineData(1000, 1779, 1080, 1922)]   // 1921.32 → 1922
    [InlineData(1, 2, 1080, 2160)]
    public void Odd_and_uneven_sources_give_even_sides(int w, int h, int expectedW, int expectedH)
    {
        var canvas = ProjectSettingsRules.SuggestedCanvas(w, h);
        Assert.Equal((expectedW, expectedH), canvas);
        Assert.Equal(0, canvas!.Value.Width % 2);
        Assert.Equal(0, canvas.Value.Height % 2);
    }

    [Theory]
    [InlineData(1000, 4000, 1024, 4096)]   // 4320 → 4096, the short side 1024
    [InlineData(4000, 1000, 4096, 1024)]
    [InlineData(1000, 3790, 1080, 4094)]   // just inside the limit
    [InlineData(100, 6000, 68, 4096)]      // 68.26… → 68
    public void A_long_side_above_the_limit_is_capped_and_the_short_side_follows(int w, int h, int expectedW, int expectedH) =>
        Assert.Equal((expectedW, expectedH), ProjectSettingsRules.SuggestedCanvas(w, h));

    [Theory]
    [InlineData(10, 1000)]    // the short side would be 40 < 64
    [InlineData(0, 1080)]
    [InlineData(1080, 0)]
    [InlineData(-1080, 1920)]
    public void No_canvas_when_the_rules_refuse_it_or_the_size_is_not_positive(int w, int h) =>
        Assert.Null(ProjectSettingsRules.SuggestedCanvas(w, h));

    [Fact]
    public void The_default_canvas_is_the_project_default()
    {
        var settings = new ProjectSettings();
        Assert.Equal((ProjectSettingsRules.DefaultCanvasWidth, ProjectSettingsRules.DefaultCanvasHeight),
            (settings.FrameWidth, settings.FrameHeight));
        Assert.Equal((1920, 1080), (ProjectSettingsRules.DefaultCanvasWidth, ProjectSettingsRules.DefaultCanvasHeight));
    }

    // --- Fill / Fit (§2) ------------------------------------------------------------------------------

    private static readonly FrameSize Landscape = new(1920, 1080);
    private static readonly FrameSize Portrait = new(1080, 1920);

    private static VisualProperties With(double rotation = 0, CropRect? crop = null, double x = 0, double y = 0) =>
        VisualProperties.Default with { RotationDegrees = rotation, Crop = crop ?? CropRect.None, PositionX = x, PositionY = y };

    [Fact]
    public void Fit_without_rotation_is_exactly_one()
    {
        Assert.Equal(1.0, CompositionMath.FitScale(Landscape, new FrameSize(1080, 1920), VisualProperties.Default));
        Assert.Equal(1.0, CompositionMath.FitScale(Portrait, new FrameSize(3840, 2160), VisualProperties.Default));
        Assert.Equal(1.0, CompositionMath.FitScale(Landscape, new FrameSize(641, 359), With(crop: new CropRect(0.1, 0.2, 0.05, 0.3))));
        Assert.Equal(1.0, CompositionMath.FitScale(Landscape, new FrameSize(640, 480), With(rotation: 180)));
    }

    [Theory]
    [InlineData(1920, 1080, 1080, 1920)]   // a portrait clip on a landscape canvas
    [InlineData(1080, 1920, 1920, 1080)]   // a landscape clip on a portrait canvas
    [InlineData(1080, 1920, 3840, 2160)]
    [InlineData(1920, 1080, 1440, 1080)]
    [InlineData(1080, 1080, 1080, 1920)]
    [InlineData(1920, 1080, 1920, 1080)]   // the same shape: 1
    public void Fill_without_rotation_is_the_cover_ratio_and_covers_the_canvas(int cw, int ch, int sw, int sh)
    {
        var canvas = new FrameSize(cw, ch);
        var source = new FrameSize(sw, sh);
        var fill = CompositionMath.FillScale(canvas, source, VisualProperties.Default);

        var fit = Math.Min((double)cw / sw, (double)ch / sh);
        var cover = Math.Max((double)cw / sw, (double)ch / sh);
        Assert.Equal(cover / fit, fill, 12);
        Assert.True(CompositionMath.Layout(canvas, source, VisualProperties.Default with { Scale = fill }).CoversCanvas);
        if (fill > 1)
            Assert.False(CompositionMath.Layout(canvas, source, VisualProperties.Default with { Scale = Math.BitDecrement(fill) }).CoversCanvas);
    }

    [Fact]
    public void Fill_of_a_portrait_phone_clip_on_the_default_canvas()
    {
        // 1080 × 1920 on 1920 × 1080: fit 0.5625, cover 1.777…, Fill = (1920 / 1080) / (1080 / 1920) = 3.160493827…
        var fill = CompositionMath.FillScale(Landscape, Portrait, VisualProperties.Default);
        Assert.Equal(1920.0 * 1920 / (1080.0 * 1080), fill, 12);
        Assert.Equal(1920.0, Portrait.Width * 0.5625 * fill, 9);   // the picture's width = the canvas width
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    [InlineData(-90)]
    public void A_quarter_turn_swaps_the_picture_sides(double rotation)
    {
        // A portrait clip turned a quarter on a landscape canvas: the turned picture is 1920 × 1080 wide at Scale 1 / fit
        // 0.5625 → 1080 × 607.5; Fill = 1080 / 607.5 = 1.777…, Fit = 1920 / 1080 … min → 1.777… as well (same shape).
        var fill = CompositionMath.FillScale(Landscape, Portrait, With(rotation));
        var fit = CompositionMath.FitScale(Landscape, Portrait, With(rotation));
        Assert.Equal(1920.0 / 1080, fill, 12);
        Assert.Equal(1920.0 / 1080, fit, 12);
        Assert.True(fit <= fill);
        Assert.True(CompositionMath.Layout(Landscape, Portrait, With(rotation) with { Scale = fill }).CoversCanvas);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Fill_with_a_crop_covers_exactly_and_Fit_stays_inside(double rotation)
    {
        var source = new FrameSize(3840, 2160);
        var crop = new CropRect(0.1, 0.05, 0.3, 0.15);   // binary-inexact fractions: the exact path rounds outward
        var visual = With(rotation, crop);

        var fill = CompositionMath.FillScale(Portrait, source, visual);
        Assert.True(CompositionMath.Layout(Portrait, source, visual with { Scale = fill }).CoversCanvas);
        Assert.False(CompositionMath.Layout(Portrait, source, visual with { Scale = Math.BitDecrement(fill) }).CoversCanvas);

        var fit = CompositionMath.FitScale(Portrait, source, visual);
        var bounds = CompositionMath.Layout(Portrait, source, visual with { Scale = fit }).Bounds;
        AssertInside(Portrait, bounds);
        Assert.True(Math.Abs(bounds.Width - Portrait.Width) < 1e-6 || Math.Abs(bounds.Height - Portrait.Height) < 1e-6,
            "Fit touches the canvas on one axis");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(-33.3)]
    [InlineData(45)]
    [InlineData(135.5)]
    public void Odd_angles_cover_and_stay_inside_with_the_margin(double rotation)
    {
        var source = new FrameSize(1080, 1920);
        var visual = With(rotation, new CropRect(0, 0.1, 0, 0.1));

        var fill = CompositionMath.FillScale(Landscape, source, visual);
        Assert.True(CompositionMath.Layout(Landscape, source, visual with { Scale = fill }).CoversCanvas);
        Assert.False(CompositionMath.Layout(Landscape, source, visual with { Scale = fill * (1 - 1e-4) }).CoversCanvas);

        var fit = CompositionMath.FitScale(Landscape, source, visual);
        var bounds = CompositionMath.Layout(Landscape, source, visual with { Scale = fit }).Bounds;
        AssertInside(Landscape, bounds, 1e-6);
        Assert.True(Math.Abs(bounds.Width - Landscape.Width) < 1e-6 || Math.Abs(bounds.Height - Landscape.Height) < 1e-6);
    }

    [Fact]
    public void The_position_is_ignored_and_kept_by_the_caller()
    {
        var centred = CompositionMath.FillScale(Landscape, Portrait, VisualProperties.Default);
        var offset = CompositionMath.FillScale(Landscape, Portrait, With(x: 300, y: -200));
        Assert.Equal(centred, offset);
        Assert.Equal(CompositionMath.FitScale(Landscape, Portrait, VisualProperties.Default),
            CompositionMath.FitScale(Landscape, Portrait, With(x: 300, y: -200)));
    }

    [Fact]
    public void The_scale_given_does_not_matter()
    {
        var a = CompositionMath.FillScale(Landscape, Portrait, VisualProperties.Default with { Scale = 0.3 });
        var b = CompositionMath.FillScale(Landscape, Portrait, VisualProperties.Default with { Scale = 7 });
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_value_beyond_the_scale_limits_is_returned_as_it_is()
    {
        // A thin strip of a portrait clip filling a landscape canvas needs more than 1000 %.
        var visual = With(crop: new CropRect(0, 0.49, 0, 0.49));
        var fill = CompositionMath.FillScale(Landscape, new FrameSize(1080, 1920), visual);
        Assert.True(fill > ClipPropertyLimits.MaxScale);
    }

    [Fact]
    public void Invalid_input_throws_as_the_layout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionMath.FillScale(new FrameSize(0, 1080), Portrait, VisualProperties.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionMath.FitScale(Landscape, new FrameSize(1080, 0), VisualProperties.Default));
        Assert.Throws<ArgumentException>(() => CompositionMath.FitScale(Landscape, Portrait, With(crop: new CropRect(0.6, 0, 0.5, 0))));
    }

    private static void AssertInside(FrameSize canvas, RectD bounds, double tolerance = 0)
    {
        Assert.True(bounds.X >= -tolerance && bounds.Y >= -tolerance, $"{bounds}");
        Assert.True(bounds.Right <= canvas.Width + tolerance && bounds.Bottom <= canvas.Height + tolerance, $"{bounds}");
    }
}
