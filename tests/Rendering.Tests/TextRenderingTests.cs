using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Rendering;
using Avalonia.Media;
using Xunit;
using TextAlignment = AiVideoEditor.Core.Entities.TextAlignment;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 8 Step 3 (D023): text. The export draws text with the Preview's own routine
/// (<see cref="CompositionPainter"/>, Avalonia <see cref="FormattedText"/>), so the Preview's control and
/// the export rasterizer must produce the same pixels for the same canvas-size composition — for one or
/// several lines, every alignment, sizes, families (also one that isn't installed), rotation, scale,
/// opacity and clipping. Geometry of the text box rule (<see cref="TextDraw"/>) is also measured on the ink:
/// centring, alignment of lines, size and scale, rotation — within 1 px (2 px where glyph shapes differ).
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class TextRenderingTests
{
    private static readonly FrameSize Canvas = new(640, 360);

    private static TextProperties Props(string text, string family = "Segoe UI", double size = 48,
        TextAlignment alignment = TextAlignment.Center, string color = "#FFFFFF") => new(text, family, size, color, alignment);

    private static ResolvedLayer Layer(TextProperties text, VisualProperties? visual = null) => new(Scene.Text(Canvas, text, visual), null);

    private static Image Export(params ResolvedLayer[] layers) => Render.Export(Scene.Plan(Canvas, layers));

    private static Image Preview(params ResolvedLayer[] layers) => Render.Preview(Canvas, Canvas.Width, Canvas.Height, layers.Select(Scene.AsPreview));

    // --- Preview == export ------------------------------------------------------------------------------

    public static TheoryData<string, string, double, TextAlignment, double, double, double, double, double> Cases => new()
    {
        // text, family, size, alignment, rotation, scale, opacity, x, y
        { "Single line", "Segoe UI", 48, TextAlignment.Center, 0, 1, 1, 0, 0 },
        { "First line\nSecond, longer line\nx", "Segoe UI", 40, TextAlignment.Left, 0, 1, 1, 0, 0 },
        { "First line\nSecond, longer line\nx", "Segoe UI", 40, TextAlignment.Center, 0, 1, 1, 0, 0 },
        { "First line\nSecond, longer line\nx", "Segoe UI", 40, TextAlignment.Right, 0, 1, 1, 0, 0 },
        { "Small text 12", "Segoe UI", 12, TextAlignment.Center, 0, 1, 1, 0, 0 },
        { "Big", "Segoe UI", 200, TextAlignment.Center, 0, 1, 1, 0, 0 },
        { "Monospace\nfont", "Consolas", 44, TextAlignment.Right, 0, 1, 1, 0, 0 },
        { "Serif font", "Times New Roman", 60, TextAlignment.Center, 0, 1, 1, 0, 0 },
        { "Missing font", "Nowhere Sans 123", 48, TextAlignment.Left, 0, 1, 1, 0, 0 },
        { "Rotated\ntext", "Segoe UI", 48, TextAlignment.Center, 30, 1, 1, 0, 0 },
        { "Scaled", "Segoe UI", 30, TextAlignment.Center, 0, 2.5, 1, 0, 0 },
        { "Half opaque", "Segoe UI", 64, TextAlignment.Center, 0, 1, 0.5, 0, 0 },
        { "Clipped at the edge", "Segoe UI", 64, TextAlignment.Left, -15, 1.2, 1, 250, -150 },
        { "Trailing spaces   \nLine", "Segoe UI", 40, TextAlignment.Right, 0, 1, 1, 0, 0 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_export_draws_text_exactly_like_the_preview(string text, string family, double size, TextAlignment alignment,
        double rotation, double scale, double opacity, double x, double y)
    {
        var layer = Layer(Props(text, family, size, alignment),
            VisualProperties.Default with { RotationDegrees = rotation, Scale = scale, Opacity = opacity, PositionX = x, PositionY = y });

        var export = Export(layer);
        var preview = Preview(layer);

        Assert.NotNull(export.InkBox(threshold: 60));
        Assert.Equal(preview.Pixels, export.Pixels);
    }

    [Fact]
    public void A_font_that_is_not_installed_falls_back_to_the_default_family_as_in_the_preview()
    {
        var fallback = FontManager.Current.DefaultFontFamily.Name;

        var missing = Export(Layer(Props("Fallback Hg", "Nowhere Sans 123")));
        var byDefault = Export(Layer(Props("Fallback Hg", fallback)));

        Assert.Equal(byDefault.Pixels, missing.Pixels);
    }

    [Fact]
    public void Another_family_is_really_used()
    {
        var segoe = Export(Layer(Props("iiiiiiiiii")))!.InkBox()!.Value;
        var consolas = Export(Layer(Props("iiiiiiiiii", "Consolas")))!.InkBox()!.Value;

        Assert.True(Math.Abs((segoe.Right - segoe.Left) - (consolas.Right - consolas.Left)) > 20, $"{segoe} vs {consolas}");
    }

    // --- the text box rule, measured ------------------------------------------------------------------------

    [Fact]
    public void The_box_is_centred_on_the_layer_position()
    {
        var text = Props("HHHHHH");
        var layout = CompositionPainter.Layout(text);

        foreach (var (x, y) in new[] { (0.0, 0.0), (-150.5, 60.0) })
        {
            var ink = Export(Layer(text, VisualProperties.Default with { PositionX = x, PositionY = y })).InkBox()!.Value;
            var centreX = 320 + x;
            var centreY = 180 + y;

            Assert.InRange((ink.Left + ink.Right) / 2.0, centreX - 1, centreX + 1);                       // symmetric glyphs
            Assert.InRange(ink.Left, centreX - layout.MaxTextWidth / 2 - 1, centreX);                    // inside the box
            Assert.InRange(ink.Top, centreY - layout.Height / 2 - 1, centreY);
            Assert.InRange(ink.Bottom, centreY, centreY + layout.Height / 2 + 1);
        }
    }

    [Theory]
    [InlineData(TextAlignment.Left)]
    [InlineData(TextAlignment.Center)]
    [InlineData(TextAlignment.Right)]
    public void Lines_are_aligned_inside_the_box(TextAlignment alignment)
    {
        var text = Props("HHHHHHHHHH\nHHH", size: 40, alignment: alignment);
        var layout = CompositionPainter.Layout(text);
        var image = Export(Layer(text));

        var all = image.InkBox()!.Value;
        var middle = (all.Top + all.Bottom) / 2;
        var first = image.InkBox(bottom: middle)!.Value;
        var second = image.InkBox(top: middle)!.Value;
        var boxLeft = 320 - layout.MaxTextWidth / 2;
        var boxRight = 320 + layout.MaxTextWidth / 2;

        Assert.InRange(first.Left, boxLeft - 1, boxLeft + 6);         // the longest line spans the box (glyph side bearing)
        Assert.InRange(first.Right, boxRight - 6, boxRight + 1);
        switch (alignment)
        {
            case TextAlignment.Left: Assert.InRange(second.Left - first.Left, -1, 1); break;
            case TextAlignment.Right: Assert.InRange(second.Right - first.Right, -1, 1); break;
            default: Assert.InRange((second.Left + second.Right) - (first.Left + first.Right), -2, 2); break;
        }
        Assert.True(second.Right - second.Left < first.Right - first.Left);
    }

    [Fact]
    public void Font_size_and_scale_grow_the_text_proportionally()
    {
        static int Width((int Left, int Top, int Right, int Bottom) box) => box.Right - box.Left;
        var small = Export(Layer(Props("HHHHHH", size: 30))).InkBox()!.Value;
        var bySize = Export(Layer(Props("HHHHHH", size: 60))).InkBox()!.Value;
        var byScale = Export(Layer(Props("HHHHHH", size: 30), VisualProperties.Default with { Scale = 2 })).InkBox()!.Value;

        Assert.InRange(Width(bySize), 2 * Width(small) - 3, 2 * Width(small) + 3);
        Assert.InRange(Width(byScale), 2 * Width(small) - 3, 2 * Width(small) + 3);
    }

    [Fact]
    public void Rotation_turns_the_box_clockwise_around_its_centre()
    {
        var upright = Export(Layer(Props("HHHHHHHH"))).InkBox()!.Value;
        var turned = Export(Layer(Props("HHHHHHHH"), VisualProperties.Default with { RotationDegrees = 90 })).InkBox()!.Value;

        Assert.InRange(turned.Right - turned.Left, upright.Bottom - upright.Top - 2, upright.Bottom - upright.Top + 2);
        Assert.InRange(turned.Bottom - turned.Top, upright.Right - upright.Left - 2, upright.Right - upright.Left + 2);
        Assert.InRange((turned.Top + turned.Bottom) / 2.0, 180 - 2, 180 + 2);
    }

    [Fact]
    public void Colour_and_opacity_apply_to_the_glyphs()
    {
        var orange = Export(Layer(Props("█████", size: 80, color: "#FF8800")));
        var half = Export(Layer(Props("█████", size: 80), VisualProperties.Default with { Opacity = 0.5 }));

        var (r, g, b, _) = orange.At(320, 180);
        Assert.Equal((255, 136, 0), (r, g, b));
        Assert.InRange(half.At(320, 180).G, 125, 131);
    }

    [Fact]
    public void Text_beyond_the_canvas_is_clipped_in_the_preview_letterbox()
    {
        var layer = Layer(Props("Clipped text beyond the edge", size: 60, alignment: TextAlignment.Left),
            VisualProperties.Default with { PositionX = -300 });

        var image = Render.Preview(Canvas, 800, 360, new[] { Scene.AsPreview(layer) });   // canvas at x ∈ [80, 720)

        for (var y = 0; y < 360; y++)
        for (var x = 0; x < 78; x++)
            Assert.Equal(0, image.At(x, y).A);
        Assert.NotNull(image.InkBox());
    }

    [Fact]
    public void The_preview_at_another_size_shows_the_same_text_geometry_scaled()
    {
        var layer = Layer(Props("Scaled preview\nsecond line", size: 48, alignment: TextAlignment.Right),
            VisualProperties.Default with { PositionX = 80, PositionY = -40 });

        var export = Export(layer).InkBox()!.Value;
        var preview = Render.Preview(Canvas, 320, 180, new[] { Scene.AsPreview(layer) }).InkBox()!.Value;   // viewport 0.5

        Assert.InRange(preview.Left, export.Left / 2.0 - 2, export.Left / 2.0 + 2);
        Assert.InRange(preview.Right, export.Right / 2.0 - 2, export.Right / 2.0 + 2);
        Assert.InRange(preview.Top, export.Top / 2.0 - 2, export.Top / 2.0 + 2);
        Assert.InRange(preview.Bottom, export.Bottom / 2.0 - 2, export.Bottom / 2.0 + 2);
    }
}
