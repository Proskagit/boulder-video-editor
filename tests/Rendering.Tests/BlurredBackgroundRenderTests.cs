using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// D057 §3.7: the pixels of a blurred background — a landscape picture fitted on a portrait canvas: with a black background
/// the bars stay black; with a blurred one no bar pixel is black, the bars carry the picture's colours, the picture itself is
/// drawn unchanged over it, and the Preview draws the same pixels as the export.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class BlurredBackgroundRenderTests
{
    private static readonly FrameSize Portrait = new(180, 320);
    private static readonly FrameSize Source = new(320, 180);

    /// <summary>Left half red, right half blue — never black.</summary>
    private static DecodedFrame TwoColours() =>
        Scene.Frame(Source.Width, Source.Height, (x, _) => x < Source.Width / 2 ? ((byte)220, (byte)40, (byte)40, (byte)255) : ((byte)40, (byte)40, (byte)220, (byte)255));

    private static (Image Export, Image Preview) Draw(ClipBackground background)
    {
        var visual = VisualProperties.Default with { Scale = CompositionMath.FitScale(Portrait, Source, VisualProperties.Default) };
        var picture = Scene.Picture(Portrait, Source, visual);
        picture = picture with { Span = picture.Span with { Background = background } };
        var layer = new ResolvedLayer(picture, TwoColours());
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
    public void The_blurred_copy_is_small_deterministic_and_cached()
    {
        var frame = TwoColours();
        var a = BackgroundBlur.Of(frame, Portrait);
        Assert.Same(a, BackgroundBlur.Of(frame, Portrait));
        Assert.Equal(BackgroundBlur.SizeFor(Portrait), (a.Width, a.Height));
        var b = BackgroundBlur.Of(TwoColours(), Portrait);
        Assert.True(a.Pixels.Span.SequenceEqual(b.Pixels.Span));
        Assert.Equal((109, 0, 101, 180), BackgroundBlur.FillRect(320, 180, Portrait));      // the centre, the canvas's aspect
    }
}
