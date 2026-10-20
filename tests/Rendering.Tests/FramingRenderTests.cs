using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 26 (D041 §2): the pixels of «Заполнить кадр» / «Вписать в кадр». A portrait picture on a landscape canvas and a
/// landscape one on a portrait canvas: Fill leaves no black pixel on the canvas, Fit leaves the bars and shows every edge of
/// the picture; the Preview draws the same pixels as the export.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class FramingRenderTests
{
    private static readonly FrameSize Landscape = new(320, 180);
    private static readonly FrameSize Portrait = new(180, 320);

    /// <summary>Never black: every channel at least 64, so a bar (black) is told apart from the picture.</summary>
    private static DecodedFrame Bright(int w, int h) =>
        Scene.Frame(w, h, (x, y) => ((byte)(64 + x * 191 / (w - 1)), (byte)(64 + y * 191 / (h - 1)), 160, 255));

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { 320, 180, 180, 320, 0.0 },   // a portrait clip on a landscape canvas
        new object[] { 180, 320, 320, 180, 0.0 },   // a landscape clip on a portrait canvas
        new object[] { 180, 320, 640, 360, 0.0 },
        new object[] { 320, 180, 320, 180, 90.0 },  // a landscape clip turned a quarter on a landscape canvas
    };

    private static (Image Export, Image Preview) Draw(FrameSize canvas, FrameSize source, VisualProperties visual)
    {
        var layer = new ResolvedLayer(Scene.Picture(canvas, source, visual), Bright(source.Width, source.Height));
        return (Render.Export(Scene.Plan(canvas, layer)), Render.Preview(canvas, canvas.Width, canvas.Height, new[] { Scene.AsPreview(layer) }));
    }

    private static int Dark(Image image, FrameSize canvas)
    {
        var n = 0;
        for (var y = 0; y < canvas.Height; y++)
        for (var x = 0; x < canvas.Width; x++)
        {
            var p = image.At(x, y);
            if (p.R < 32 && p.G < 32 && p.B < 32) n++;
        }
        return n;
    }

    private static void AssertSame(Image a, Image b, FrameSize canvas, int tolerance)
    {
        for (var y = 0; y < canvas.Height; y++)
        for (var x = 0; x < canvas.Width; x++)
        {
            var p = a.At(x, y);
            var q = b.At(x, y);
            Assert.True(Math.Abs(p.R - q.R) <= tolerance && Math.Abs(p.G - q.G) <= tolerance && Math.Abs(p.B - q.B) <= tolerance,
                $"({x},{y}): {p} vs {q}");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fill_leaves_no_bar_and_the_Preview_matches_the_export(int cw, int ch, int sw, int sh, double rotation)
    {
        var canvas = new FrameSize(cw, ch);
        var source = new FrameSize(sw, sh);
        var visual = VisualProperties.Default with { RotationDegrees = rotation };
        visual = visual with { Scale = CompositionMath.FillScale(canvas, source, visual) };

        var (export, preview) = Draw(canvas, source, visual);

        Assert.Equal(0, Dark(export, canvas));
        Assert.Equal(0, Dark(preview, canvas));
        AssertSame(export, preview, canvas, tolerance: 2);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fit_shows_the_whole_picture_with_bars_and_the_Preview_matches_the_export(int cw, int ch, int sw, int sh, double rotation)
    {
        var canvas = new FrameSize(cw, ch);
        var source = new FrameSize(sw, sh);
        var visual = VisualProperties.Default with { RotationDegrees = rotation };
        visual = visual with { Scale = CompositionMath.FitScale(canvas, source, visual) };

        var (export, preview) = Draw(canvas, source, visual);
        var bounds = CompositionMath.Layout(canvas, source, visual).Bounds;

        Assert.True(bounds.X >= -1e-9 && bounds.Y >= -1e-9 && bounds.Right <= cw + 1e-9 && bounds.Bottom <= ch + 1e-9);
        var picturePixels = (int)Math.Round(bounds.Width) * (int)Math.Round(bounds.Height);
        Assert.Equal(cw * ch - picturePixels, Dark(export, canvas), (double)Math.Max(cw, ch) * 2);   // the bars, ± the edge rows
        AssertSame(export, preview, canvas, tolerance: 2);
    }
}
