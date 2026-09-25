using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 8 Step 3 (D023): the shared draw plan (Preview and export) independent of any rasterizer — which
/// layers are drawn in which order, with which transform, source rectangle, opacity and clip. Layers come
/// from <see cref="PlaybackSnapshot.LayersAt"/>, geometry from <see cref="CompositionMath"/> (D018).
/// </summary>
public class CompositionDrawPlanTests
{
    private static readonly FrameSize Canvas = new(1920, 1080);
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly MediaTime T = MediaTime.FromFrame(10, Rate);

    private static DecodedFrame Frame(int width, int height) =>
        new(width, height, width * 4, new byte[width * height * 4], new SourceTimestamp(0, new TimeBase(1, 25)));

    private static PictureSpan Span(VisualProperties? visual = null, FrameSize? source = null, SpanStatus status = SpanStatus.Video) =>
        new(Guid.NewGuid(), Guid.NewGuid(), status, MediaTime.Zero, MediaTime.FromSeconds(4), MediaTime.Zero)
        {
            Visual = visual ?? VisualProperties.Default,
            SourceSize = source ?? new FrameSize(1920, 1080)
        };

    private static TextSpan Text(VisualProperties? visual = null, string text = "Title") =>
        new(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(4), visual ?? VisualProperties.Default,
            new TextProperties(text, "Segoe UI", 48, "#FFFFFF", TextAlignment.Center));

    /// <summary>Visible tracks top to bottom, as the snapshot holds them.</summary>
    private static PlaybackSnapshot Snapshot(FrameSize canvas, params VideoLayer[] topToBottom) =>
        new(1, Rate, MediaTime.FromSeconds(4), topToBottom.ToImmutableArray(), ImmutableArray<AudioSpan>.Empty,
            ImmutableDictionary<Guid, PlaybackAsset>.Empty, canvas);

    private static VideoLayer Track(PictureSpan span) => new(Guid.NewGuid(), ImmutableArray.Create(span));
    private static VideoLayer Track(TextSpan text) => new(Guid.NewGuid(), ImmutableArray<PictureSpan>.Empty) { Texts = ImmutableArray.Create(text) };

    /// <summary>The export's plan: every layer of the snapshot at <see cref="T"/> with a decoded frame of the given size.</summary>
    private static CompositionDrawPlan ExportPlan(PlaybackSnapshot snapshot, int decodedWidth = 1920, int decodedHeight = 1080) =>
        CompositionDrawPlan.Build(snapshot.Canvas, Affine2D.Identity, snapshot.LayersAt(T)
            .Select(l => new ResolvedLayer(l, l is PictureLayer ? Frame(decodedWidth, decodedHeight) : null)));

    private static FrameDraw OnlyFrame(CompositionDrawPlan plan) => Assert.IsType<FrameDraw>(Assert.Single(plan.Operations));

    // --- canvas, background, clip -------------------------------------------------------------------

    [Fact]
    public void The_export_draws_the_whole_canvas_on_opaque_black_clipped_to_it()
    {
        var plan = ExportPlan(Snapshot(Canvas));

        Assert.Equal(Canvas, plan.Canvas);
        Assert.Equal(Affine2D.Identity, plan.CanvasToTarget);
        Assert.Equal(new RectD(0, 0, 1920, 1080), plan.CanvasBounds);
        Assert.Empty(plan.Operations);
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), CompositionDrawPlan.Background);
    }

    [Fact]
    public void A_vertical_canvas_keeps_its_size_and_fits_a_landscape_source_to_its_width()
    {
        var vertical = new FrameSize(1080, 1920);
        var op = OnlyFrame(ExportPlan(Snapshot(vertical, Track(Span()))));

        Assert.Equal(new RectD(0, 0, 1080, 1920), ExportPlan(Snapshot(vertical)).CanvasBounds);
        Assert.Equal(new PointD(0, 656.25), op.Transform.Apply(new PointD(0, 0)));          // 1080/1920 = 0.5625 → 607.5 high, centred
        Assert.Equal(new PointD(1080, 1263.75), op.Transform.Apply(new PointD(1920, 1080)));
    }

    // --- picture geometry = CompositionMath (D018) -----------------------------------------------------

    [Theory]
    [InlineData(0.25, 0, 0, 0)]
    [InlineData(0, 0.25, 0, 0)]
    [InlineData(0, 0, 0.25, 0)]
    [InlineData(0, 0, 0, 0.25)]
    [InlineData(0.1, 0.2, 0.3, 0.05)]
    public void Crop_of_each_side_selects_the_normalized_part_of_a_decoded_frame_of_any_size(double left, double top, double right, double bottom)
    {
        var visual = VisualProperties.Default with { Crop = new CropRect(left, top, right, bottom) };
        var span = Span(visual);

        foreach (var (w, h) in new[] { (1920, 1080), (960, 540) })   // full resolution (export) and a smaller decode (Preview)
        {
            var op = OnlyFrame(ExportPlan(Snapshot(Canvas, Track(span)), w, h));
            var geometry = CompositionMath.Layout(Canvas, new FrameSize(1920, 1080), visual);

            Assert.Equal(new RectD(0, 0, geometry.SourceRect.Width, geometry.SourceRect.Height), op.LocalRect);
            Assert.Equal(left * w, op.FrameSourceRect.X, 9);
            Assert.Equal(top * h, op.FrameSourceRect.Y, 9);
            Assert.Equal((1 - left - right) * w, op.FrameSourceRect.Width, 9);
            Assert.Equal((1 - top - bottom) * h, op.FrameSourceRect.Height, 9);
            Assert.Equal(geometry.Transform, op.Transform);
        }
    }

    [Theory]
    [InlineData(1920, 1080, 0, 0, 1)]          // same aspect: fills
    [InlineData(1080, 1920, 656.25, 0, 0.5625)] // portrait: pillarbox
    [InlineData(1920, 540, 0, 270, 1)]          // wide: letterbox
    [InlineData(640, 360, 0, 0, 3)]             // small: scaled up
    public void Contain_fit_scales_uniformly_and_centres(int sourceW, int sourceH, double x, double y, double fit)
    {
        var op = OnlyFrame(ExportPlan(Snapshot(Canvas, Track(Span(source: new FrameSize(sourceW, sourceH))))));

        Assert.Equal(new Affine2D(fit, 0, 0, fit, x, y), op.Transform);
    }

    public static TheoryData<double, double, double, double> Transforms => new()
    {
        // scale, rotation, position x, position y
        { 0.5, 0, 0, 0 }, { 2, 0, 0, 0 }, { 1, 90, 0, 0 }, { 1, 180, 0, 0 }, { 1, 270, 0, 0 }, { 1, 30, 0, 0 },
        { 1, 0, 300, -200 }, { 0.75, 30, -120.5, 64.25 }, { 1, -90, 0, 0 }
    };

    [Theory]
    [MemberData(nameof(Transforms))]
    public void Scale_rotation_and_position_are_the_D018_transform(double scale, double rotation, double x, double y)
    {
        var visual = VisualProperties.Default with { Scale = scale, RotationDegrees = rotation, PositionX = x, PositionY = y };
        var op = OnlyFrame(ExportPlan(Snapshot(Canvas, Track(Span(visual)))));

        Assert.Equal(CompositionMath.Layout(Canvas, new FrameSize(1920, 1080), visual).Transform, op.Transform);
        var centre = op.Transform.Apply(new PointD(960, 540));
        Assert.Equal(960 + x, centre.X, 9);
        Assert.Equal(540 + y, centre.Y, 9);
        // clockwise on screen: the local +X axis turns towards +Y
        var axis = op.Transform.Apply(new PointD(961, 540));
        Assert.Equal(Math.Cos(rotation * Math.PI / 180) * scale, axis.X - centre.X, 9);
        Assert.Equal(Math.Sin(rotation * Math.PI / 180) * scale, axis.Y - centre.Y, 9);
    }

    [Fact]
    public void Quarter_turns_are_exact()
    {
        var op = OnlyFrame(ExportPlan(Snapshot(Canvas, Track(Span(VisualProperties.Default with { RotationDegrees = 90 })))));

        Assert.Equal(new PointD(1500, -420), op.Transform.Apply(new PointD(0, 0)));   // top-left corner after a clockwise quarter turn
    }

    [Fact]
    public void Opacity_is_per_layer_and_the_frame_is_passed_through_with_its_alpha()
    {
        var frame = Frame(4, 4);
        var layer = Snapshot(Canvas, Track(Span(VisualProperties.Default with { Opacity = 0.35 }, new FrameSize(4, 4)))).LayersAt(T).Single();

        var op = OnlyFrame(CompositionDrawPlan.Build(Canvas, Affine2D.Identity, new[] { new ResolvedLayer(layer, frame) }));

        Assert.Equal(0.35, op.Opacity);
        Assert.Same(frame, op.Frame);                                   // straight-alpha BGRA, untouched
    }

    [Fact]
    public void A_source_of_unknown_size_is_laid_out_from_the_decoded_frame()
    {
        var span = Span() with { SourceSize = null };
        var op = OnlyFrame(ExportPlan(Snapshot(Canvas, Track(span)), 405, 720));

        Assert.Equal(new RectD(0, 0, 405, 720), op.LocalRect);
        Assert.Equal(new Affine2D(1.5, 0, 0, 1.5, 656.25, 0), op.Transform);
    }

    [Fact]
    public void A_picture_layer_needs_its_frame()
    {
        var layer = Snapshot(Canvas, Track(Span())).LayersAt(T).Single();

        Assert.Throws<ArgumentException>(() => CompositionDrawPlan.Build(Canvas, Affine2D.Identity, new[] { new ResolvedLayer(layer, null) }));
    }

    // --- layers ------------------------------------------------------------------------------------------

    [Fact]
    public void Layers_are_drawn_bottom_to_top_by_track_order()
    {
        var top = Span(VisualProperties.Default with { Scale = 0.3 });
        var middle = Text();
        var bottom = Span(VisualProperties.Default with { Scale = 0.6 });

        var plan = ExportPlan(Snapshot(Canvas, Track(top), Track(middle), Track(bottom)));

        Assert.Equal(new[] { bottom.ClipId, middle.ClipId, top.ClipId }, plan.Operations.Select(o => o.ClipId));
        Assert.IsType<TextDraw>(plan.Operations[1]);
    }

    [Fact]
    public void Culled_and_invisible_layers_are_not_drawn()
    {
        var hiddenBelow = Span();
        var cover = Span();                                                           // opaque, fills the canvas
        var transparent = Span(VisualProperties.Default with { Opacity = 0 });
        var blank = Text(text: "  \n ");

        var plan = ExportPlan(Snapshot(Canvas, Track(blank), Track(transparent), Track(cover), Track(hiddenBelow)));

        Assert.Equal(cover.ClipId, OnlyFrame(plan).ClipId);
    }

    [Fact]
    public void Text_is_drawn_through_the_text_transform_with_its_properties_and_opacity()
    {
        var visual = VisualProperties.Default with { PositionX = 200, PositionY = -50, Scale = 2, RotationDegrees = 30, Opacity = 0.5 };
        var text = Text(visual);

        var op = Assert.IsType<TextDraw>(Assert.Single(ExportPlan(Snapshot(Canvas, Track(text))).Operations));

        Assert.Equal(CompositionMath.TextTransform(Canvas, visual), op.Transform);
        Assert.Equal(new PointD(1160, 490), op.Transform.Apply(new PointD(0, 0)));   // the box centre
        Assert.Equal(text.Text, op.Text);
        Assert.Equal(0.5, op.Opacity);
    }

    // --- viewport (the Preview's target) ----------------------------------------------------------------

    [Fact]
    public void A_viewport_composes_with_every_layer_transform_and_maps_the_clip()
    {
        var visual = VisualProperties.Default with { Scale = 0.5, RotationDegrees = 90, PositionX = 100 };
        var snapshot = Snapshot(Canvas, Track(Span(visual)), Track(Text()));
        var viewport = CompositionDrawPlan.Viewport(Canvas, 1000, 540);
        var layers = snapshot.LayersAt(T).Select(l => new ResolvedLayer(l, l is PictureLayer ? Frame(1280, 720) : null)).ToList();

        var identity = CompositionDrawPlan.Build(Canvas, Affine2D.Identity, layers);
        var scaled = CompositionDrawPlan.Build(Canvas, viewport, layers);

        Assert.Equal(new Affine2D(0.5, 0, 0, 0.5, 20, 0), viewport);
        Assert.Equal(new RectD(20, 0, 960, 540), scaled.CanvasBounds);
        for (var i = 0; i < layers.Count; i++)
            Assert.Equal(viewport.Compose(identity.Operations[i].Transform), scaled.Operations[i].Transform);
    }
}
