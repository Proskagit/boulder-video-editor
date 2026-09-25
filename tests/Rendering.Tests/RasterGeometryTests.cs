using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Rendering;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 8 Step 3 (D023): pixels of the export rasterizer checked numerically against the shared plan.
/// Geometry tolerance: every sampled pixel whose centre lies at least 1 canvas pixel inside a layer's
/// transformed rectangle must have the layer's colour, and every one at least 1 pixel outside it the colour
/// below — so an edge is never off by 1 pixel or more. Colours: ±2 per channel (±3 for blends).
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class RasterGeometryTests
{
    private static readonly FrameSize Canvas = new(320, 180);
    private const double EdgeTolerancePx = 1.0;

    private static readonly (byte, byte, byte) Black = (0, 0, 0);
    private static readonly (byte, byte, byte) Red = (255, 0, 0);

    private static void Near((byte R, byte G, byte B, byte A) actual, (byte R, byte G, byte B) expected, int tolerance, string where)
    {
        Assert.True(Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance &&
                    Math.Abs(actual.B - expected.B) <= tolerance && actual.A == 255,
            $"{where}: expected {expected}, got {actual}");
    }

    /// <summary>
    /// Samples the image and checks the frame operation's area: inside by ≥ 1 px → <paramref name="source"/>
    /// at the mapped source position (null = too close to an internal edge of the pattern, skipped), outside by
    /// ≥ 1 px → <paramref name="outside"/> (null = not checked). Returns the number of checked pixels.
    /// </summary>
    private static (int Inside, int Outside) AssertLayer(Image image, FrameDraw op,
        Func<double, double, double, (byte, byte, byte)?> source, (byte, byte, byte)? outside, int tolerance = 2)
    {
        var inverse = op.Transform.Invert();
        var scale = Math.Sqrt(Math.Abs(op.Transform.Determinant));   // canvas px per local unit (uniform)
        var margin = EdgeTolerancePx / scale;
        var sourcePerLocal = op.FrameSourceRect.Width / op.LocalRect.Width;
        int inside = 0, outsideCount = 0;
        for (var y = 0; y < image.Height; y += 2)
        for (var x = 0; x < image.Width; x += 2)
        {
            var p = inverse.Apply(new PointD(x + 0.5, y + 0.5));
            var r = op.LocalRect;
            var depth = Math.Min(Math.Min(p.X - r.X, r.Right - p.X), Math.Min(p.Y - r.Y, r.Bottom - p.Y));
            if (depth >= margin)
            {
                var sx = op.FrameSourceRect.X + (p.X - r.X) * sourcePerLocal;
                var sy = op.FrameSourceRect.Y + (p.Y - r.Y) * sourcePerLocal;
                if (source(sx, sy, margin * sourcePerLocal) is not { } expected) continue;
                Near(image.At(x, y), expected, tolerance, $"inside ({x},{y})");
                inside++;
            }
            else if (depth <= -margin && outside is { } below)
            {
                Near(image.At(x, y), below, tolerance, $"outside ({x},{y})");
                outsideCount++;
            }
        }
        Assert.True(inside > 20, $"only {inside} inside pixels checked");
        return (inside, outsideCount);
    }

    private static Func<double, double, double, (byte, byte, byte)?> Uniform((byte, byte, byte) color) => (_, _, _) => color;

    // --- background -------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_composition_is_opaque_black_on_the_whole_canvas()
    {
        var image = Render.Export(Scene.Plan(Canvas));

        for (var i = 0; i < image.Pixels.Length; i += 4)
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, image.Pixels[i..(i + 4)]);
    }

    // --- transforms -----------------------------------------------------------------------------------------

    public static TheoryData<int, int, double, double, double, double> Transforms => new()
    {
        // source w, h, scale, rotation, x, y
        { 160, 90, 1, 0, 0, 0 },          // same aspect: fills the canvas
        { 90, 160, 1, 0, 0, 0 },          // portrait: pillarbox
        { 160, 40, 1, 0, 0, 0 },          // wide: letterbox
        { 160, 90, 0.5, 0, 0, 0 },
        { 160, 90, 0.5, 0, 57.25, -31.5 }, // sub-pixel position
        { 160, 90, 0.5, 90, 0, 0 },
        { 160, 90, 0.5, 180, 20, 10 },
        { 160, 90, 0.5, 270, -40, 0 },
        { 160, 90, 0.5, 30, 0, 0 },
        { 160, 90, 0.8, -30, 30, 20 },
        { 160, 90, 1.5, 45, 0, 0 },        // larger than the canvas: clipped
    };

    [Theory]
    [MemberData(nameof(Transforms))]
    public void Fit_scale_rotation_and_position_land_within_one_pixel(int w, int h, double scale, double rotation, double x, double y)
    {
        var visual = VisualProperties.Default with { Scale = scale, RotationDegrees = rotation, PositionX = x, PositionY = y };
        var layer = new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(w, h), visual), Scene.Solid(w, h, 255, 0, 0));
        var plan = Scene.Plan(Canvas, layer);

        var image = Render.Export(plan);

        AssertLayer(image, (FrameDraw)plan.Operations[0], Uniform(Red), Black);
    }

    [Theory]
    [InlineData(0.5, 0, 0, 0)]
    [InlineData(0, 0.5, 0, 0)]
    [InlineData(0, 0, 0.5, 0)]
    [InlineData(0, 0, 0, 0.5)]
    [InlineData(0.25, 0.1, 0.1, 0.3)]
    public void Crop_shows_exactly_the_cropped_part_of_the_source(double left, double top, double right, double bottom)
    {
        // quadrants: red | green over blue | white
        (byte, byte, byte) Quadrant(double sx, double sy) => (sx < 100, sy < 50) switch
        {
            (true, true) => (255, 0, 0), (false, true) => (0, 255, 0), (true, false) => (0, 0, 255), _ => (255, 255, 255)
        };
        var frame = Scene.Frame(200, 100, (px, py) => { var (r, g, b) = Quadrant(px + 0.5, py + 0.5); return (r, g, b, 255); });
        var visual = VisualProperties.Default with { Crop = new CropRect(left, top, right, bottom), Scale = 0.8 };
        var plan = Scene.Plan(Canvas, new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(200, 100), visual), frame));

        var image = Render.Export(plan);

        var op = (FrameDraw)plan.Operations[0];
        AssertLayer(image, op, (sx, sy, m) =>
        {
            Assert.InRange(sx, left * 200 - 1e-9, (1 - right) * 200 + 1e-9);        // never outside the crop
            Assert.InRange(sy, top * 100 - 1e-9, (1 - bottom) * 100 + 1e-9);
            return Math.Abs(sx - 100) < m + 1 || Math.Abs(sy - 50) < m + 1 ? null : Quadrant(sx, sy);
        }, Black);
    }

    // --- opacity, alpha, order ---------------------------------------------------------------------------------

    [Fact]
    public void Opacity_blends_the_whole_layer_with_what_is_below()
    {
        var blue = new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(16, 9), VisualProperties.Default), Scene.Solid(16, 9, 0, 0, 255));
        var red = new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(16, 9), VisualProperties.Default with { Scale = 0.5, Opacity = 0.5 }),
            Scene.Solid(16, 9, 255, 0, 0));
        var plan = Scene.Plan(Canvas, blue, red);

        var image = Render.Export(plan);

        AssertLayer(image, (FrameDraw)plan.Operations[1], Uniform((128, 0, 128)), (0, 0, 255), tolerance: 3);
    }

    [Fact]
    public void Straight_alpha_of_an_image_blends_with_the_layer_below()
    {
        var red = new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(16, 9), VisualProperties.Default), Scene.Solid(16, 9, 255, 0, 0));
        // left half: green at alpha 128; right half: fully transparent (colour channels deliberately non-zero)
        var image = Scene.Frame(32, 18, (x, _) => x < 16 ? ((byte)0, (byte)255, (byte)0, (byte)128) : ((byte)255, (byte)255, (byte)255, (byte)0));
        var top = new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(32, 18), VisualProperties.Default with { Scale = 0.5 }, SpanStatus.StillImage), image);
        var plan = Scene.Plan(Canvas, red, top);

        var result = Render.Export(plan);

        AssertLayer(result, (FrameDraw)plan.Operations[1],
            (sx, _, m) => Math.Abs(sx - 16) < m + 1 ? null : sx < 16 ? ((byte)127, (byte)128, (byte)0) : Red, Red, tolerance: 3);
    }

    [Fact]
    public void Layers_are_painted_bottom_to_top()
    {
        var bottom = new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(16, 9), VisualProperties.Default), Scene.Solid(16, 9, 255, 0, 0));
        var top = new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(16, 9), VisualProperties.Default with { Scale = 0.5, RotationDegrees = 30 }),
            Scene.Solid(16, 9, 0, 0, 255));
        var plan = Scene.Plan(Canvas, bottom, top);

        var image = Render.Export(plan);

        AssertLayer(image, (FrameDraw)plan.Operations[1], Uniform((0, 0, 255)), Red);
    }

    [Fact]
    public void A_vertical_canvas_is_rendered_at_its_own_size()
    {
        var vertical = new FrameSize(180, 320);
        var plan = Scene.Plan(vertical, new ResolvedLayer(Scene.Picture(vertical, new FrameSize(160, 90), VisualProperties.Default), Scene.Solid(160, 90, 255, 0, 0)));

        var image = Render.Export(plan);

        Assert.Equal((180, 320), (image.Width, image.Height));
        AssertLayer(image, (FrameDraw)plan.Operations[0], Uniform(Red), Black);
    }

    // --- the Preview: same routine, its own viewport and clip ------------------------------------------------

    [Fact]
    public void The_preview_control_clips_the_composition_to_the_letterboxed_canvas()
    {
        var huge = new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(16, 9), VisualProperties.Default with { Scale = 3, RotationDegrees = 10 }),
            Scene.Solid(16, 9, 255, 0, 0));

        var image = Render.Preview(Canvas, 400, 180, new[] { Scene.AsPreview(huge) });   // canvas at x ∈ [40, 360)

        for (var y = 0; y < 180; y += 3)
        {
            Assert.Equal(0, image.At(38, y).A);          // letterbox: nothing painted
            Assert.Equal(0, image.At(361, y).A);
            Near(image.At(41, y), Red, 2, $"canvas edge (41,{y})");
            Near(image.At(358, y), Red, 2, $"canvas edge (358,{y})");
        }
    }

    [Fact]
    public void Preview_and_export_draw_pictures_identically()
    {
        // the same resolved layers through the Preview's control (UI thread, identity viewport) and the rasterizer
        var quadrants = Scene.Frame(64, 36, (x, y) => (x < 32 ? (byte)255 : (byte)0, y < 18 ? (byte)255 : (byte)0, 128, 255));
        var layers = new[]
        {
            new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(64, 36), VisualProperties.Default with { Crop = new CropRect(0.1, 0, 0.2, 0.05) }), quadrants),
            new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(64, 36), VisualProperties.Default with { Scale = 0.4, RotationDegrees = 30, PositionX = 50.5, Opacity = 0.6 }), quadrants),
            new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(8, 8), VisualProperties.Default with { Scale = 0.3, PositionY = -40 }, SpanStatus.StillImage),
                Scene.Solid(8, 8, 0, 255, 0, 100)),
        };

        var export = Render.Export(Scene.Plan(Canvas, layers));
        var preview = Render.Preview(Canvas, Canvas.Width, Canvas.Height, layers.Select(Scene.AsPreview));

        Assert.Equal(preview.Pixels, export.Pixels);
    }

    // --- the rasterizer itself ------------------------------------------------------------------------------------

    [Fact]
    public void A_rasterizer_is_reused_across_frames_and_sizes_without_growing()
    {
        using var rasterizer = new AvaloniaCompositionRasterizer();
        var handles = System.Diagnostics.Process.GetCurrentProcess().HandleCount;
        for (var i = 0; i < 300; i++)
        {
            var size = i % 50 < 25 ? new FrameSize(64, 36) : new FrameSize(32, 18);    // the source size changes now and then
            var plan = Scene.Plan(Canvas, new ResolvedLayer(Scene.Picture(Canvas, size, VisualProperties.Default with { RotationDegrees = i }),
                Scene.Solid(size.Width, size.Height, (byte)i, 0, 0)));
            var image = Render.Export(plan, rasterizer);
            Near(image.At(160, 90), ((byte)i, 0, 0), 2, $"frame {i}");
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.InRange(System.Diagnostics.Process.GetCurrentProcess().HandleCount - handles, -50, 50);
    }

    [Fact]
    public void The_rasterizer_only_draws_canvas_size_plans_and_not_after_dispose()
    {
        var rasterizer = new AvaloniaCompositionRasterizer();
        var viewportPlan = CompositionDrawPlan.Build(Canvas, CompositionDrawPlan.Viewport(Canvas, 160, 90), Array.Empty<ResolvedLayer>());
        Assert.Throws<ArgumentException>(() => rasterizer.Render(viewportPlan, new byte[Canvas.Width * Canvas.Height * 4], Canvas.Width * 4));
        Assert.Throws<ArgumentException>(() => rasterizer.Render(Scene.Plan(Canvas), new byte[10], Canvas.Width * 4));

        rasterizer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => rasterizer.Render(Scene.Plan(Canvas), new byte[Canvas.Width * Canvas.Height * 4], Canvas.Width * 4));
    }
}
