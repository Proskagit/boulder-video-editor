using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// D057 §3.7, §7.12 (the owner's reference of 2026-11-10): the pixels of a blurred background — a landscape picture fitted
/// on a portrait canvas: with a black background the bars stay black; with a blurred one no bar pixel is black, the bars
/// show the picture itself enlarged to fill the canvas and softly blurred, the picture is drawn unchanged over it, and the
/// Preview draws the same pixels as the export.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class BlurredBackgroundRenderTests
{
    private static readonly FrameSize Portrait = new(180, 320);
    private static readonly FrameSize Source = new(320, 180);

    /// <summary>Left half red, right half blue — never black.</summary>
    private static DecodedFrame TwoColours() =>
        Scene.Frame(Source.Width, Source.Height, (x, _) => x < Source.Width / 2 ? ((byte)220, (byte)40, (byte)40, (byte)255) : ((byte)40, (byte)40, (byte)220, (byte)255));

    /// <summary>A green top edge, a magenta bottom edge, a grey middle with a white subject in the centre.</summary>
    private static DecodedFrame Subject() => Scene.Frame(Source.Width, Source.Height, (x, y) =>
        y < 30 ? ((byte)40, (byte)200, (byte)40, (byte)255)
        : y >= 150 ? ((byte)200, (byte)40, (byte)200, (byte)255)
        : Math.Abs(x - 160) < 40 && Math.Abs(y - 90) < 40 ? ((byte)255, (byte)255, (byte)255, (byte)255)
        : ((byte)100, (byte)100, (byte)100, (byte)255));

    private static ResolvedLayer Layer(ClipBackground background, DecodedFrame frame)
    {
        var visual = VisualProperties.Default with { Scale = CompositionMath.FitScale(Portrait, Source, VisualProperties.Default) };
        var picture = Scene.Picture(Portrait, Source, visual);
        picture = picture with { Span = picture.Span with { Background = background } };
        return new ResolvedLayer(picture, frame);
    }

    private static (Image Export, Image Preview) Draw(ClipBackground background, DecodedFrame? frame = null)
    {
        var layer = Layer(background, frame ?? TwoColours());
        return (Render.Export(Scene.Plan(Portrait, layer)), Render.Preview(Portrait, Portrait.Width, Portrait.Height, new[] { Scene.AsPreview(layer) }));
    }

    private static bool Black((byte R, byte G, byte B, byte A) p) => p.R < 16 && p.G < 16 && p.B < 16;

    [Fact]
    public void A_black_background_keeps_the_bars_black()
    {
        var (export, _) = Draw(ClipBackground.Black);
        Assert.True(Black(export.At(90, 10)));                              // the bar above the fitted picture
        Assert.True(Black(export.At(90, 310)));
    }

    [Fact]
    public void A_blurred_background_fills_the_bars_with_the_pictures_colours_and_the_preview_matches_the_export()
    {
        var (export, preview) = Draw(ClipBackground.Blur);
        for (var y = 0; y < Portrait.Height; y += 7)
            for (var x = 0; x < Portrait.Width; x += 7)
                Assert.False(Black(export.At(x, y)), $"black at ({x},{y})");
        var top = export.At(90, 10);
        Assert.True(top.R > 60 || top.B > 60);                               // the picture's colours, blurred
        var picture = export.At(20, 160);                                    // inside the fitted picture: unchanged red
        Assert.True(picture.R > 200 && picture.B < 60, picture.ToString());

        for (var y = 0; y < Portrait.Height; y += 5)
            for (var x = 0; x < Portrait.Width; x += 5)
            {
                var (p, q) = (export.At(x, y), preview.At(x, y));
                Assert.True(Math.Abs(p.R - q.R) <= 2 && Math.Abs(p.G - q.G) <= 2 && Math.Abs(p.B - q.B) <= 2, $"({x},{y}): {p} vs {q}");
            }
    }

    [Fact]
    public void The_bars_show_the_picture_enlarged_to_fill_the_canvas_and_blurred()
    {
        // The owner's reference (D057 §7.12): the scene itself, enlarged and softly blurred — the top bar shows the picture's
        // top (green), the bottom bar its bottom (magenta), and near the picture its centre (the white subject) enlarged.
        var (export, preview) = Draw(ClipBackground.Blur, Subject());
        var top = export.At(90, 6);
        Assert.True(top.G > top.R + 40 && top.G > top.B + 40, $"top: {top}");
        var bottom = export.At(90, Portrait.Height - 6);
        Assert.True(bottom.R > bottom.G + 40 && bottom.B > bottom.G + 40, $"bottom: {bottom}");
        var subject = export.At(90, 96);                                   // above the fitted picture (y ≈ 109 … 211)
        Assert.True(subject.R > 150 && subject.G > 150 && subject.B > 150, $"the enlarged subject: {subject}");
        for (var y = 0; y < Portrait.Height; y += 4)
            for (var x = 0; x < Portrait.Width; x += 4)
            {
                var (p, q) = (export.At(x, y), preview.At(x, y));
                Assert.True(Math.Abs(p.R - q.R) <= 2 && Math.Abs(p.G - q.G) <= 2 && Math.Abs(p.B - q.B) <= 2, $"({x},{y}): {p} vs {q}");
            }
    }

    [Fact]
    public void The_copy_is_small_deterministic_cached_and_the_same_from_a_frame_decoded_smaller()
    {
        var layer = Layer(ClipBackground.Blur, Subject());
        var a = CompositionDrawPlan.BlurredBackground(Portrait, Affine2D.Identity, layer)!.Frame;
        Assert.Same(a, CompositionDrawPlan.BlurredBackground(Portrait, Affine2D.Identity, layer)!.Frame);
        Assert.Equal(BackgroundBlur.SizeFor(Portrait), (a.Width, a.Height));
        var b = CompositionDrawPlan.BlurredBackground(Portrait, Affine2D.Identity, Layer(ClipBackground.Blur, Subject()))!.Frame;
        Assert.True(a.Pixels.Span.SequenceEqual(b.Pixels.Span));

        // The Preview decodes at a reduced size: the same picture at half the size gives (nearly) the same background.
        var half = Scene.Frame(Source.Width / 2, Source.Height / 2, (x, y) =>
        {
            var (bx, by) = (x * 2 + 1, y * 2 + 1);
            return by < 30 ? ((byte)40, (byte)200, (byte)40, (byte)255)
                : by >= 150 ? ((byte)200, (byte)40, (byte)200, (byte)255)
                : Math.Abs(bx - 160) < 40 && Math.Abs(by - 90) < 40 ? ((byte)255, (byte)255, (byte)255, (byte)255)
                : ((byte)100, (byte)100, (byte)100, (byte)255);
        });
        var c = CompositionDrawPlan.BlurredBackground(Portrait, Affine2D.Identity, Layer(ClipBackground.Blur, half))!.Frame;
        Assert.Equal((a.Width, a.Height), (c.Width, c.Height));
        var pa = a.Pixels.Span;
        var pc = c.Pixels.Span;
        // A hard synthetic edge moves by one source pixel at half the size: on average the same, nowhere far apart.
        long total = 0;
        var worst = 0;
        for (var i = 0; i < pa.Length; i++)
        {
            var d = Math.Abs(pa[i] - pc[i]);
            total += d;
            worst = Math.Max(worst, d);
        }
        Assert.True(total / (double)pa.Length <= 2, $"mean {total / (double)pa.Length:0.##}");
        Assert.True(worst <= 16, $"worst {worst}");
    }
}
