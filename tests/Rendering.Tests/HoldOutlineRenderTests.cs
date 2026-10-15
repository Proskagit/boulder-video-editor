using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Rendering;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 19 Step 19.3 (D034 §4–§6): the route outline in pixels. The stroke measured in the exported frame of a
/// synthetic scene (a white line on mid grey; its width = the summed coverage across the line) is
/// <c>StrokeWidth1080 × short side / 1080</c> within ±0.5 px on 720p, 1080p, 4K and a portrait canvas, independent of the
/// clip's scale; the Preview draws the same plan scaled into its control, never thinner than one screen pixel; the
/// keyline shares the width; the line is clipped to the crop and takes the layer's opacity.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class HoldOutlineRenderTests
{
    private const byte Grey = 64;
    private const double Tolerance = 0.5;

    /// <summary>A rectangle from (0.25, 0.25) to (0.75, 0.75) of the source.</summary>
    private static readonly ImmutableArray<NormalizedPoint> Rect = ImmutableArray.Create(
        new NormalizedPoint(0.25, 0.25), new NormalizedPoint(0.75, 0.25), new NormalizedPoint(0.75, 0.75), new NormalizedPoint(0.25, 0.75));

    private static ResolvedLayer Layer(FrameSize canvas, double width1080, VisualProperties? visual = null, string color = "#FFFFFF",
        bool keyline = false, double fade = 1.0, ImmutableArray<NormalizedPoint>? polygon = null)
    {
        var source = canvas.Width >= canvas.Height ? new FrameSize(160, 90) : new FrameSize(90, 160);
        var v = visual ?? VisualProperties.Default;
        var span = new PictureSpan(Guid.NewGuid(), Guid.NewGuid(), SpanStatus.Video, MediaTime.Zero, MediaTime.FromSeconds(1), MediaTime.Zero)
        {
            Visual = v, SourceSize = source,
            Outline = new HoldOutline(ImmutableArray.Create(polygon ?? Rect), width1080, color, keyline)
        };
        return new ResolvedLayer(new PictureLayer(Guid.NewGuid(), span, CompositionMath.Layout(canvas, source, v), fade),
            Scene.Solid(source.Width, source.Height, Grey, Grey, Grey));
    }

    /// <summary>Coverage of a bright line across row <paramref name="y"/> from <paramref name="x0"/> to <paramref name="x1"/>: the
    /// sum of (green − grey) / (255 − grey), i.e. the line's width in pixels.</summary>
    private static double BrightWidth(Image image, int y, int x0, int x1)
    {
        double sum = 0;
        for (var x = Math.Max(0, x0); x < Math.Min(image.Width, x1); x++)
            sum += Math.Max(0, image.At(x, y).G - Grey) / (255.0 - Grey);
        return sum;
    }

    private static double DarkWidth(Image image, int y, int x0, int x1)
    {
        double sum = 0;
        for (var x = Math.Max(0, x0); x < Math.Min(image.Width, x1); x++)
            sum += Math.Max(0, Grey - image.At(x, y).G) / (double)Grey;
        return sum;
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(1080, 1920)]
    public void The_exported_stroke_is_the_width_per_1080_of_the_short_side(int width, int height)
    {
        var canvas = new FrameSize(width, height);
        var image = Render.Export(Scene.Plan(canvas, Layer(canvas, HoldMarkup.DefaultStrokeWidth1080)));

        var expected = 4.0 * Math.Min(width, height) / 1080;
        var edge = (int)Math.Round(width * 0.25);   // the picture fills the canvas: the left edge of the rectangle
        var measured = BrightWidth(image, height / 2, edge - 30, edge + 30);
        Assert.InRange(measured, expected - Tolerance, expected + Tolerance);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.5)]
    public void The_stroke_does_not_depend_on_the_clips_scale(double scale)
    {
        var canvas = new FrameSize(1920, 1080);
        var image = Render.Export(Scene.Plan(canvas, Layer(canvas, 4, VisualProperties.Default with { Scale = scale })));
        var edge = (int)Math.Round(960 - 960 * scale * 0.5);   // the rectangle's left edge, scaled around the centre
        var measured = BrightWidth(image, 540, edge - 30, edge + 30);
        Assert.InRange(measured, 4 - Tolerance, 4 + Tolerance);
    }

    [Fact]
    public void The_preview_draws_the_same_plan_scaled_into_its_control()
    {
        var canvas = new FrameSize(1920, 1080);
        var layer = Layer(canvas, 4);

        var full = Render.Preview(canvas, 1920, 1080, new[] { Scene.AsPreview(layer) });
        Assert.InRange(BrightWidth(full, 540, 480 - 30, 480 + 30), 4 - Tolerance, 4 + Tolerance);   // = the export

        var half = Render.Preview(canvas, 960, 540, new[] { Scene.AsPreview(layer) });
        Assert.InRange(BrightWidth(half, 270, 240 - 20, 240 + 20), 2 - Tolerance, 2 + Tolerance);   // scaled with the canvas
    }

    [Fact]
    public void In_a_small_preview_the_line_stays_one_screen_pixel_wide_and_the_export_keeps_its_width()
    {
        var canvas = new FrameSize(3840, 2160);
        var layer = Layer(canvas, 4);                       // 8 px on the canvas; × 0.1 = 0.8 px in a 384 px control
        var small = Render.Preview(canvas, 384, 216, new[] { Scene.AsPreview(layer) });
        Assert.InRange(BrightWidth(small, 108, 96 - 10, 96 + 10), 1 - Tolerance, 1 + Tolerance);

        var export = Render.Export(Scene.Plan(canvas, layer));
        Assert.InRange(BrightWidth(export, 1080, 960 - 30, 960 + 30), 8 - Tolerance, 8 + Tolerance);
    }

    [Fact]
    public void The_keyline_shares_the_width_core_in_the_middle_half()
    {
        var canvas = new FrameSize(1920, 1080);
        var image = Render.Export(Scene.Plan(canvas, Layer(canvas, 8, keyline: true)));   // white core, black keyline
        var core = BrightWidth(image, 540, 480 - 30, 480 + 30);
        var keyline = DarkWidth(image, 540, 480 - 30, 480 + 30);
        Assert.InRange(core, 4 - Tolerance, 4 + Tolerance);
        Assert.InRange(core + keyline, 8 - Tolerance, 8 + Tolerance);
    }

    [Fact]
    public void The_outline_is_clipped_to_the_crop()
    {
        var canvas = new FrameSize(1920, 1080);
        // Crop half of the source's left side: the rectangle's left edge (x = 0.25) is cut off; the picture re-fits (D018).
        var visual = VisualProperties.Default with { Crop = new CropRect(0.5, 0, 0, 0) };
        var layer = Layer(canvas, 4, visual);
        var plan = Scene.Plan(canvas, layer);
        var image = Render.Export(plan);

        var picture = (FrameDraw)plan.Operations[0];
        var left = picture.Transform.Apply(new PointD(0, 0)).X;
        for (var x = 0; x < (int)left - 1; x++)
            Assert.True(image.At(x, 540).G <= Grey + 2, $"no outline left of the cropped picture (x = {x})");
        // The right edge (x = 0.75 of the source) is still inside the crop and drawn.
        var right = picture.Transform.Apply(new PointD((0.75 - 0.5) * 160, 0)).X;
        Assert.InRange(BrightWidth(image, 540, (int)right - 30, (int)right + 30), 4 - Tolerance, 4 + Tolerance);
    }

    [Fact]
    public void The_outline_takes_the_layers_opacity_with_its_fade()
    {
        var canvas = new FrameSize(1920, 1080);
        // Opacity 0.5 × a fade factor 0.8 = 0.4: the black background, the grey picture at 0.4 (= 25.6), the white line at
        // 0.4 over it — the operation's opacity is the layer's, fade included, as for its frame.
        var image = Render.Export(Scene.Plan(canvas, Layer(canvas, 8, VisualProperties.Default with { Opacity = 0.5 }, fade: 0.8)));
        var centre = image.At(480, 540).G;
        Assert.InRange(centre, 111, 124);   // 0.6·25.6 + 0.4·255 ≈ 117
    }

    [Fact]
    public void Without_an_outline_nothing_is_added_to_the_plan()
    {
        var canvas = new FrameSize(320, 180);
        var source = new FrameSize(160, 90);
        var span = new PictureSpan(Guid.NewGuid(), Guid.NewGuid(), SpanStatus.Video, MediaTime.Zero, MediaTime.FromSeconds(1), MediaTime.Zero) { SourceSize = source };
        var plan = Scene.Plan(canvas, new ResolvedLayer(new PictureLayer(Guid.NewGuid(), span, CompositionMath.Layout(canvas, source, span.Visual)),
            Scene.Solid(160, 90, Grey, Grey, Grey)));
        Assert.Single(plan.Operations);
    }
    [Fact]
    public void A_corrected_contour_is_drawn_from_its_stored_points_the_same_in_the_preview_and_the_export()
    {
        // Phase 20 (D035): the corner (0.75, 0.75) dragged in to (0.6, 0.6) — the stored polygon is all either path draws;
        // the edit handles belong to the markup overlay only and never reach the composition.
        var canvas = new FrameSize(1920, 1080);
        var corrected = Rect.SetItem(2, new NormalizedPoint(0.6, 0.6));
        var layer = Layer(canvas, 4, polygon: corrected);
        var export = Render.Export(Scene.Plan(canvas, layer));
        var preview = Render.Preview(canvas, 1920, 1080, new[] { Scene.AsPreview(layer) });

        foreach (var image in new[] { export, preview })
        {
            Assert.True(image.At(1296, 459).G > 200);                 // the middle of the new edge (0.75, 0.25)–(0.6, 0.6)
            Assert.True(image.At(1152, 648).G > 200);                 // the moved vertex
            Assert.Equal(Grey, image.At(1440, 810).G);                // the old corner: no line any more
            Assert.Equal(Grey, image.At(1440, 700).G);                // nor the old right edge below the new edge
        }
        Assert.InRange(BrightWidth(export, 540, 480 - 30, 480 + 30), 4 - Tolerance, 4 + Tolerance);
        Assert.InRange(BrightWidth(preview, 540, 480 - 30, 480 + 30), 4 - Tolerance, 4 + Tolerance);
    }
}
