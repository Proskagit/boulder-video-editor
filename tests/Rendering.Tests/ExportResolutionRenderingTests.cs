using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// D060 §3: the export rasterizer draws a canvas at another output size through the stretch of the plan — the same painter,
/// the layout on the canvas unchanged: every element lands where it is on the canvas, scaled, within one output pixel; a
/// picture filling the canvas fills the output with no border. (The canvas's own size takes the identity path unchanged —
/// guarded by the parity suites and <c>ExportResolutionEndToEndTests</c>.)
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class ExportResolutionRenderingTests
{
    private static readonly FrameSize Canvas = new(640, 360);

    private static (int Left, int Top, int Right, int Bottom) RedBox(Image image)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var (R, G, B, _) = image.At(x, y);
            if (R < 200 || G > 60 || B > 60) continue;
            l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y);
        }
        Assert.True(r >= 0, "no red pixels");
        return (l, t, r + 1, b + 1);
    }

    private static ResolvedLayer RedQuarter() => new(
        Scene.Picture(Canvas, new FrameSize(160, 90), VisualProperties.Default with { Scale = 0.5, PositionX = 100, PositionY = -50 }),
        Scene.Solid(160, 90, 255, 0, 0));

    [Theory]
    [InlineData(320, 180)]
    [InlineData(1280, 720)]
    [InlineData(854, 480)]
    public void A_layer_lands_at_its_canvas_place_scaled_to_the_output(int width, int height)
    {
        var target = new FrameSize(width, height);
        var native = Render.Export(Scene.Plan(Canvas, RedQuarter()));
        var scaled = Render.Export(CompositionDrawPlan.Build(Canvas, CompositionDrawPlan.Stretch(Canvas, target), new[] { RedQuarter() })
            with { TargetSize = target });

        Assert.Equal((width, height), (scaled.Width, scaled.Height));
        var (sx, sy) = (width / (double)Canvas.Width, height / (double)Canvas.Height);
        var n = RedBox(native);
        var s = RedBox(scaled);
        Assert.InRange(s.Left, n.Left * sx - 1.5, n.Left * sx + 1.5);
        Assert.InRange(s.Right, n.Right * sx - 1.5, n.Right * sx + 1.5);
        Assert.InRange(s.Top, n.Top * sy - 1.5, n.Top * sy + 1.5);
        Assert.InRange(s.Bottom, n.Bottom * sy - 1.5, n.Bottom * sy + 1.5);
    }

    [Fact]
    public void A_picture_filling_the_canvas_fills_the_output_with_no_border()
    {
        var full = new ResolvedLayer(Scene.Picture(Canvas, new FrameSize(160, 90), VisualProperties.Default), Scene.Solid(160, 90, 0, 255, 0));
        var target = new FrameSize(854, 480);   // 16:9 within the rounding of a side
        var image = Render.Export(CompositionDrawPlan.Build(Canvas, CompositionDrawPlan.Stretch(Canvas, target), new[] { full }) with { TargetSize = target });

        foreach (var (x, y) in new[] { (0, 0), (853, 0), (0, 479), (853, 479), (427, 240) })
        {
            var (_, g, _, a) = image.At(x, y);
            Assert.True(g > 240 && a == 255, $"({x},{y}) is not the picture");
        }
    }

    [Fact]
    public void Another_mapping_than_the_stretch_onto_the_target_is_refused()
    {
        var plan = CompositionDrawPlan.Build(Canvas, CompositionDrawPlan.Viewport(Canvas, 400, 400), new[] { RedQuarter() })
            with { TargetSize = new FrameSize(400, 400) };
        Assert.ThrowsAny<ArgumentException>(() => Render.Export(plan));
    }
}
