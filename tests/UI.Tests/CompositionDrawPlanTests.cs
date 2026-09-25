using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Rendering;
using Avalonia;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 7 Step 7c: the pure rendering logic — viewport (canvas → control), per-layer transforms,
/// source rectangles in decoded pixels, placeholders, text, order and opacity. Exact values: sizes
/// are chosen so every scale is a binary fraction. Since Phase 8 Step 3 the Preview's plan is the shared
/// Core plan (<see cref="CompositionDrawPlan"/>) plus its placeholders (<see cref="PreviewDrawPlan"/>);
/// these tests keep guarding the Preview's result unchanged.
/// </summary>
public class CompositionDrawPlanTests
{
    private static readonly FrameSize Canvas = new(1920, 1080);

    private static DecodedFrame Frame(int width, int height) =>
        new(width, height, width * 4, new byte[width * height * 4], new SourceTimestamp(0, new TimeBase(1, 25)));

    private static PictureSpan Span(VisualProperties? visual = null, FrameSize? sourceSize = null, SpanStatus status = SpanStatus.Video) =>
        new(Guid.NewGuid(), Guid.NewGuid(), status, MediaTime.Zero, MediaTime.FromSeconds(1), MediaTime.Zero)
        {
            Visual = visual ?? VisualProperties.Default,
            SourceSize = sourceSize
        };

    private static PictureLayer PictureLayerOf(PictureSpan span, FrameSize canvas) =>
        new(Guid.NewGuid(), span, span.SourceSize is { } size ? CompositionMath.Layout(canvas, size, span.Visual) : null);

    private static LayerPicture FramePicture(VisualProperties? visual = null, FrameSize? sourceSize = null, DecodedFrame? frame = null,
        FrameSize? canvas = null, bool current = true) =>
        new(PictureLayerOf(Span(visual, sourceSize ?? new FrameSize(1920, 1080)), canvas ?? Canvas), LayerPictureState.Frame,
            frame ?? Frame(1280, 720), current);

    private static CompositionDrawPlan Plan(double width, double height, params LayerPicture[] layers) =>
        PreviewDrawPlan.Build(Canvas, width, height, layers);

    // --- Viewport: canvas → control ----------------------------------------------------------------

    [Theory]
    [InlineData(960, 540, 0.5, 0, 0)]        // same aspect
    [InlineData(1000, 540, 0.5, 20, 0)]      // wider control: pillarbox
    [InlineData(960, 600, 0.5, 0, 30)]       // taller control: letterbox
    [InlineData(3840, 2160, 2, 0, 0)]        // larger than the canvas
    public void Viewport_fits_the_canvas_uniformly_and_centres_it(double width, double height, double scale, double x, double y)
    {
        var plan = Plan(width, height);

        Assert.Equal(new Affine2D(scale, 0, 0, scale, x, y), plan.CanvasToTarget);
        Assert.Equal(new RectD(x, y, 1920 * scale, 1080 * scale), plan.CanvasBounds);
    }

    [Fact]
    public void Vertical_canvas_uses_its_own_proportions()
    {
        var plan = PreviewDrawPlan.Build(new FrameSize(1080, 1920), 960, 540, Array.Empty<LayerPicture>());

        Assert.Equal(new Affine2D(0.28125, 0, 0, 0.28125, 328.125, 0), plan.CanvasToTarget); // 540/1920 = 0.28125
        Assert.Equal(new RectD(328.125, 0, 303.75, 540), plan.CanvasBounds);
    }

    [Fact]
    public void Nothing_to_draw_without_a_canvas_or_a_size() =>
        Assert.Same(CompositionDrawPlan.Empty, PreviewDrawPlan.Build(Canvas, 0, 540, new[] { FramePicture() }));

    // --- Frames --------------------------------------------------------------------------------------

    [Fact]
    public void Full_frame_maps_the_whole_decoded_frame_into_the_crop_rect_through_viewport_and_layer_transform()
    {
        var plan = Plan(960, 540, FramePicture(frame: Frame(1280, 720)));

        var op = Assert.IsType<FrameDraw>(Assert.Single(plan.Operations));
        Assert.Equal(new RectD(0, 0, 1280, 720), op.FrameSourceRect);       // decoded pixels
        Assert.Equal(new RectD(0, 0, 1920, 1080), op.LocalRect);            // crop rect in source pixels
        Assert.Equal(new Affine2D(0.5, 0, 0, 0.5, 0, 0), op.Transform);     // identity layer, half-size control
        Assert.Equal(1.0, op.Opacity);
    }

    [Fact]
    public void Crop_selects_the_normalized_part_of_the_decoded_frame()
    {
        // 25 % off the left: decoded 1280 × 720 → x from 320, 960 wide; the source crop is 1440 × 1080.
        var plan = Plan(1920, 1080, FramePicture(VisualProperties.Default with { Crop = new CropRect(0.25, 0, 0, 0) }, frame: Frame(1280, 720)));

        var op = Assert.IsType<FrameDraw>(Assert.Single(plan.Operations));
        Assert.Equal(new RectD(320, 0, 960, 720), op.FrameSourceRect);
        Assert.Equal(new RectD(0, 0, 1440, 1080), op.LocalRect);
        Assert.Equal(new Affine2D(1, 0, 0, 1, 240, 0), op.Transform);       // D018: re-fitted, centred
    }

    [Fact]
    public void Position_scale_rotation_come_from_the_layer_transform_then_the_viewport()
    {
        var visual = VisualProperties.Default with { Scale = 0.5, RotationDegrees = 90, PositionX = 100 };
        var picture = FramePicture(visual);
        var geometry = ((PictureLayer)picture.Layer).Geometry!;

        var op = Assert.Single(Plan(960, 540, picture).Operations);

        Assert.Equal(new Affine2D(0.5, 0, 0, 0.5, 0, 0).Compose(geometry.Transform), op.Transform);
        Assert.Equal(new PointD(530, 270), op.Transform.Apply(new PointD(960, 540))); // centre (1060, 540) on the canvas → control
    }

    [Fact]
    public void Layer_without_geometry_is_laid_out_from_the_decoded_frame_size()
    {
        // Source size unknown (old metadata); the decoded frame is portrait 405 × 720.
        var span = Span(sourceSize: null);
        var picture = new LayerPicture(new PictureLayer(Guid.NewGuid(), span, null), LayerPictureState.Frame, Frame(405, 720));

        var op = Assert.IsType<FrameDraw>(Assert.Single(Plan(1920, 1080, picture).Operations));

        Assert.Equal(new RectD(0, 0, 405, 720), op.LocalRect);
        Assert.Equal(new Affine2D(1.5, 0, 0, 1.5, 656.25, 0), op.Transform); // 1080/720 = 1.5 → 607.5 × 1080, centred
    }

    [Fact]
    public void Late_frames_are_still_drawn()
    {
        Assert.IsType<FrameDraw>(Assert.Single(Plan(960, 540, FramePicture(current: false)).Operations));
    }

    // --- Order, opacity, pending -----------------------------------------------------------------------

    [Fact]
    public void Layers_keep_their_bottom_to_top_order_and_their_own_opacity_and_pending_is_skipped()
    {
        var bottom = FramePicture(VisualProperties.Default with { Opacity = 0.25 });
        var pending = new LayerPicture(PictureLayerOf(Span(), Canvas), LayerPictureState.Pending, IsCurrent: false);
        var top = FramePicture(VisualProperties.Default with { Opacity = 0.5, Scale = 0.5 });

        var plan = Plan(960, 540, bottom, pending, top);

        Assert.Equal(new[] { bottom.Layer.ClipId, top.Layer.ClipId }, plan.Operations.Select(o => o.ClipId));
        Assert.Equal(new[] { 0.25, 0.5 }, plan.Operations.Select(o => o.Opacity));
    }

    // --- Placeholders ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(LayerPictureState.Offline, "Media offline")]
    [InlineData(LayerPictureState.Unsupported, "Unsupported clip")]
    [InlineData(LayerPictureState.DecodeError, "Cannot decode media")]
    public void Placeholders_use_the_clip_geometry(LayerPictureState state, string label)
    {
        var span = Span(VisualProperties.Default with { Scale = 0.5, PositionY = -100, Opacity = 0.75 }, new FrameSize(1080, 1920),
            state == LayerPictureState.Unsupported ? SpanStatus.Unsupported : SpanStatus.Offline);
        var layer = PictureLayerOf(span, Canvas);

        var op = Assert.IsType<PlaceholderDraw>(Assert.Single(Plan(1920, 1080, new LayerPicture(layer, state)).Operations));

        Assert.Equal(label, op.Label);
        Assert.Equal(new RectD(0, 0, 1080, 1920), op.LocalRect);
        Assert.Equal(layer.Geometry!.Transform, op.Transform);
        Assert.Equal(0.75, op.Opacity);
    }

    [Fact]
    public void Placeholder_of_unknown_size_fills_the_canvas()
    {
        var layer = new PictureLayer(Guid.NewGuid(), Span(sourceSize: null, status: SpanStatus.Offline), null);

        var op = Assert.IsType<PlaceholderDraw>(Assert.Single(Plan(960, 540, new LayerPicture(layer, LayerPictureState.Offline)).Operations));

        Assert.Equal(new RectD(0, 0, 1920, 1080), op.LocalRect);
        Assert.Equal(new Affine2D(0.5, 0, 0, 0.5, 0, 0), op.Transform);
    }

    // --- Text ----------------------------------------------------------------------------------------------

    [Fact]
    public void Text_uses_its_layer_transform_opacity_and_properties()
    {
        var text = new TextProperties("Two\nlines", "Arial", 64, "#FF8800", TextAlignment.Right);
        var span = new TextSpan(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(1),
            VisualProperties.Default with { Opacity = 0.5, PositionX = 200 }, text);
        var layer = new TextLayer(Guid.NewGuid(), span, CompositionMath.TextTransform(Canvas, span.Visual));

        var op = Assert.IsType<TextDraw>(Assert.Single(Plan(960, 540, new LayerPicture(layer, LayerPictureState.Text)).Operations));

        Assert.Equal(text, op.Text);
        Assert.Equal(0.5, op.Opacity);
        Assert.Equal(new PointD(580, 270), op.Transform.Apply(new PointD(0, 0))); // text centre (1160, 540) → control
    }

    // --- Avalonia conversion ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(90.0)]
    [InlineData(30.0)]
    [InlineData(-135.0)]
    public void Avalonia_matrix_maps_points_like_the_affine_transform(double degrees)
    {
        var affine = Affine2D.Translation(300, -40).Compose(Affine2D.Rotation(degrees)).Compose(Affine2D.Scaling(1.5));
        var matrix = RenderConversions.ToMatrix(affine);

        foreach (var (x, y) in new[] { (0.0, 0.0), (10.0, 0.0), (0.0, 10.0), (123.5, -77.25) })
        {
            var expected = affine.Apply(new PointD(x, y));
            var actual = new Point(x, y).Transform(matrix);
            Assert.Equal(expected.X, actual.X, 1e-9);
            Assert.Equal(expected.Y, actual.Y, 1e-9);
        }
    }

    // --- Phase 8 Step 3: the Preview's plan is the shared Core plan ------------------------------------------

    [Fact]
    public void Without_playback_states_the_preview_plan_is_exactly_the_shared_core_plan()
    {
        var text = new TextSpan(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(1),
            VisualProperties.Default with { RotationDegrees = 15 }, new TextProperties("T", "Arial", 30, "#FFFFFF", TextAlignment.Left));
        var pictures = new[]
        {
            FramePicture(VisualProperties.Default with { Crop = new CropRect(0.1, 0.2, 0, 0), Opacity = 0.7 }),
            new LayerPicture(new TextLayer(Guid.NewGuid(), text, CompositionMath.TextTransform(Canvas, text.Visual)), LayerPictureState.Text),
            FramePicture(VisualProperties.Default with { Scale = 0.5, RotationDegrees = 30 }, current: false)
        };

        var preview = Plan(1000, 600, pictures);
        var shared = CompositionDrawPlan.Build(Canvas, CompositionDrawPlan.Viewport(Canvas, 1000, 600),
            pictures.Select(p => new ResolvedLayer(p.Layer, p.Frame)));

        Assert.Equal(shared.CanvasToTarget, preview.CanvasToTarget);
        Assert.Equal(shared.CanvasBounds, preview.CanvasBounds);
        Assert.True(shared.Operations.SequenceEqual(preview.Operations));   // record equality, frames by reference
    }
}
