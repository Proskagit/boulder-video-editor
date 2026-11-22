using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.UI.Rendering;
using Xunit;
using TextAlignment = AiVideoEditor.Core.Entities.TextAlignment;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// D075 amendment 1: italic, «Тень», «Интервал» and «Ширина» in the one painter — the export equal to the Preview pixel for
/// pixel; each changes the picture; the block's width wraps the lines (the box as wide as set, taller); the line spacing makes
/// a two-line text taller; the shadow draws in its colour beside the text.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class TextStyle2RenderTests
{
    private static readonly FrameSize Canvas = new(640, 360);

    private static TextProperties Props(string text = "Трасса семь, шесть Б плюс") => new(text, "Segoe UI", 40, "#FFFFFF", TextAlignment.Center);

    private static ResolvedLayer Layer(TextProperties text) => new(Scene.Text(Canvas, text, null), null);

    private static Image Export(TextProperties text) => Render.Export(Scene.Plan(Canvas, new[] { Layer(text) }));

    private static Image Preview(TextProperties text) => Render.Preview(Canvas, Canvas.Width, Canvas.Height, new[] { Scene.AsPreview(Layer(text)) });

    public static TheoryData<string> Styles => new() { "italic", "shadow", "spacing", "width", "all" };

    private static TextProperties Styled(string style) => style switch
    {
        "italic" => Props() with { Italic = true },
        "shadow" => Props() with { Shadow = TextShadow.Default with { IsEnabled = true, ColorHex = "#FF0000", Opacity = 1 } },
        "spacing" => Props("Две строки\nвторая") with { LineSpacing = 1.8 },
        "width" => Props() with { BoxWidth = 220 },
        _ => Props() with { Italic = true, Bold = true, LineSpacing = 1.4, BoxWidth = 260, Shadow = TextShadow.Default with { IsEnabled = true },
            Outline = new TextOutline(true, "#000000", 0.08) }
    };

    [Theory]
    [MemberData(nameof(Styles))]
    public void The_export_draws_each_style_exactly_like_the_preview_and_unlike_the_plain_text(string style)
    {
        var text = Styled(style);
        var export = Export(text);
        Assert.Equal(Preview(text).Pixels, export.Pixels);
        var plain = style == "spacing" ? Props("Две строки\nвторая") : Props();
        Assert.NotEqual(Export(plain).Pixels, export.Pixels);
    }

    [Fact]
    public void The_width_wraps_the_lines_and_the_spacing_spreads_them()
    {
        var plain = CompositionPainter.DrawnBox(Props());
        var wrapped = CompositionPainter.DrawnBox(Props() with { BoxWidth = 220 });
        Assert.Equal(220, wrapped.Width, 3);
        Assert.True(wrapped.Height > plain.Height * 1.8);                  // two lines or more

        var two = CompositionPainter.DrawnBox(Props("Две строки\nвторая"));
        var spread = CompositionPainter.DrawnBox(Props("Две строки\nвторая") with { LineSpacing = 2 });
        Assert.True(spread.Height > two.Height * 1.5);
    }

    [Fact]
    public void The_shadow_is_drawn_in_its_colour_beside_the_text()
    {
        var image = Export(Props() with { Shadow = new TextShadow(true, "#FF0000", 1, 0, 0.1, 0.1) });
        var red = Enumerable.Range(0, image.Width * image.Height).Count(i =>
        {
            var (b, g, r) = (image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2]);
            return r > 200 && g < 60 && b < 60;
        });
        Assert.True(red > 200, $"{red} red pixels");
    }
}
