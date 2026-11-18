using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>D069 §7: a clip's colour correction reaches both renderers through the shared plan — the export's pixels are
/// the corrected source's (as <see cref="ColorAdjust"/> computes them), and the Preview shows the same colours; a neutral
/// correction leaves the picture as it was. The decoded frame itself is never changed.</summary>
[Collection(AvaloniaCollection.Name)]
public sealed class ColorCorrectionRenderingTests
{
    private static readonly FrameSize Canvas = new(64, 36);

    private static (CompositionDrawPlan Plan, ResolvedLayer Layer, DecodedFrame Frame) Scene(ColorCorrection color)
    {
        var frame = Rendering.Tests.Scene.Solid(64, 36, 120, 90, 60);
        var picture = Rendering.Tests.Scene.Picture(Canvas, new FrameSize(64, 36), VisualProperties.Default);
        picture = picture with { Span = picture.Span with { Color = color } };
        var layer = new ResolvedLayer(picture, frame);
        return (Rendering.Tests.Scene.Plan(Canvas, layer), layer, frame);
    }

    private static (byte R, byte G, byte B) Expected(ColorCorrection color)
    {
        var px = new byte[] { 60, 90, 120, 255 };   // BGRA of (120, 90, 60)
        ColorAdjust.For(color).ApplyRow(px);
        return (px[2], px[1], px[0]);
    }

    [Fact]
    public void The_export_draws_the_corrected_picture_and_leaves_the_frame_as_decoded()
    {
        var warm = new ColorCorrection(30, 0, 20, 0, 0, -40, 50, 0);
        var (plan, _, frame) = Scene(warm);
        Assert.Equal(warm, Assert.IsType<FrameDraw>(Assert.Single(plan.Operations)).Color);

        var pixel = Render.Export(plan).At(32, 18);

        var (r, g, b) = Expected(warm);
        Assert.InRange(pixel.R, r - 2, r + 2);
        Assert.InRange(pixel.G, g - 2, g + 2);
        Assert.InRange(pixel.B, b - 2, b + 2);
        Assert.Equal(new byte[] { 60, 90, 120, 255 }, frame.Pixels.Span[..4].ToArray());   // shared frame untouched
    }

    [Fact]
    public void The_preview_shows_the_same_colours_as_the_export()
    {
        var cool = new ColorCorrection(-20, 10, 0, -30, 25, 30, -60, 20);
        var (plan, layer, _) = Scene(cool);

        var export = Render.Export(plan).At(32, 18);
        var preview = Render.Preview(Canvas, 64, 36, new[] { Rendering.Tests.Scene.AsPreview(layer) }).At(32, 18);

        Assert.InRange(Math.Abs(export.R - preview.R), 0, 2);
        Assert.InRange(Math.Abs(export.G - preview.G), 0, 2);
        Assert.InRange(Math.Abs(export.B - preview.B), 0, 2);
    }

    [Fact]
    public void A_neutral_correction_draws_the_picture_as_it_is()
    {
        var (plan, _, _) = Scene(ColorCorrection.Neutral);

        var pixel = Render.Export(plan).At(32, 18);

        Assert.InRange(pixel.R, 118, 122);
        Assert.InRange(pixel.G, 88, 92);
        Assert.InRange(pixel.B, 58, 62);
    }
}
