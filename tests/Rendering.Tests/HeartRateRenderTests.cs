using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 35.5: the heart-rate overlay drawn by the Preview's control and by the export's rasterizer gives the same bytes in
/// both looks, every corner, horizontal and vertical, in and between beats; without a value it draws nothing at all; it
/// stands in its corner; on its plate the block keeps its place and size whatever the value.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class HeartRateRenderTests
{
    private static TemplateLayer Overlay(int? bpm, HeartRateLook look, OverlayCorner corner, double elapsed = 1.5)
    {
        var fields = TemplateRules.Defaults(TemplateKind.HeartRate) with { Look = look, Corner = corner };
        var span = new TemplateSpan(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(8), TemplateKind.HeartRate, fields, MediaTime.Zero);
        var (intro, outro) = TemplateRules.Animation(TemplateKind.HeartRate);
        var clock = new TemplateClock(elapsed, 3, intro, outro, elapsed, (long)(elapsed * 25));
        return new TemplateLayer(Guid.NewGuid(), span, clock) { HeartRate = bpm };
    }

    private static ResolvedLayer[] Scene(FrameSize canvas, TemplateLayer? overlay)
    {
        var frame = Rendering.Tests.Scene.Frame(canvas.Width, canvas.Height, (x, y) => ((byte)(x * 255 / canvas.Width), (byte)(y * 255 / canvas.Height), 128, 255));
        var picture = Rendering.Tests.Scene.Picture(canvas, new FrameSize(canvas.Width, canvas.Height), VisualProperties.Default);
        var layers = new List<ResolvedLayer> { new(picture, frame) };
        if (overlay is not null) layers.Add(new(overlay, null));
        return layers.ToArray();
    }

    private static Image Export(FrameSize canvas, TemplateLayer? overlay) => Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, overlay)));

    private static Image Preview(FrameSize canvas, TemplateLayer overlay) =>
        Render.Preview(canvas, canvas.Width, canvas.Height, Scene(canvas, overlay).Select(l => l.Layer is PictureLayer
            ? new LayerPicture(l.Layer, LayerPictureState.Frame, l.Frame)
            : new LayerPicture(l.Layer, LayerPictureState.Text)));

    /// <summary>The bounds of the pixels that differ between <paramref name="a"/> and <paramref name="b"/>.</summary>
    private static (int Left, int Top, int Right, int Bottom) Changed(Image a, Image b)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            var i = (y * a.Width + x) * 4;
            if (a.Pixels[i] == b.Pixels[i] && a.Pixels[i + 1] == b.Pixels[i + 1] && a.Pixels[i + 2] == b.Pixels[i + 2]) continue;
            (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
        }
        return (left, top, right, bottom);
    }

    public static TheoryData<HeartRateLook, OverlayCorner, int, int, double> Cases()
    {
        var data = new TheoryData<HeartRateLook, OverlayCorner, int, int, double>();
        foreach (var look in Enum.GetValues<HeartRateLook>())
        foreach (var corner in Enum.GetValues<OverlayCorner>())
        foreach (var (w, h) in new[] { (640, 360), (360, 640) })
        foreach (var elapsed in new[] { 0.1, 1.5, 1.52 })   // in the appearance, between beats, in a beat (128 BPM)
            data.Add(look, corner, w, h, elapsed);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_preview_and_the_export_draw_it_byte_for_byte_in_its_corner(HeartRateLook look, OverlayCorner corner, int width, int height,
        double elapsed)
    {
        var canvas = new FrameSize(width, height);
        var overlay = Overlay(128, look, corner, elapsed);

        var export = Export(canvas, overlay);
        Assert.Equal(export.Pixels, Preview(canvas, overlay).Pixels);

        var (left, top, right, bottom) = Changed(Export(canvas, null), export);
        Assert.True(right >= 0, "the overlay drew nothing");
        var leftSide = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft;
        var topSide = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight;
        Assert.True(leftSide ? right < width / 2 : left >= width / 2, $"x {left}–{right} not in the {corner} half");
        Assert.True(topSide ? bottom < height / 2 : top >= height / 2, $"y {top}–{bottom} not in the {corner} half");
    }

    [Theory]
    [InlineData(HeartRateLook.Plate)]
    [InlineData(HeartRateLook.Plain)]
    public void Without_a_value_it_draws_nothing(HeartRateLook look)
    {
        var canvas = new FrameSize(640, 360);
        Assert.Equal(Export(canvas, null).Pixels, Export(canvas, Overlay(null, look, OverlayCorner.TopRight)).Pixels);
    }

    [Fact]
    public void On_its_plate_the_block_keeps_its_place_whatever_the_value()
    {
        var canvas = new FrameSize(640, 360);
        var without = Export(canvas, null);
        // Between beats, so the heart has its resting size: only the digits differ.
        var bounds = new[] { 72, 99, 128, 187 }
            .Select(bpm => Changed(without, Export(canvas, Overlay(bpm, HeartRateLook.Plate, OverlayCorner.BottomLeft, elapsed: 3.9 * 60 / bpm))))
            .Distinct().ToList();
        Assert.Single(bounds);
    }
}
