using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 29 fixes of the product owner's manual check (2026-10-21), read off the rendered pixels at 1080p: the text of
/// «Боулдер» (the route block, TOP, ПОПЫТКА) and of «Хайлайт» (the title, the subtitle) is centred on its block — the
/// centre of its ink within 1 px of the block's centre, vertically and horizontally — for short and long, Russian and English
/// text (capitals and digits without diacritics or descending letters — Й, Ё, Д, Ц, Щ reach out of the band of the capitals,
/// which the text is centred on by design), on a horizontal and a vertical canvas; the route block and the status block are two blocks with a gap between them,
/// TOP yellow, ПОПЫТКА muted blue.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class TemplateTextAlignmentTests(ITestOutputHelper output)
{
    private const byte Grey = 128;

    private static Image Draw(FrameSize canvas, TemplateKind kind, TemplateProperties fields, double elapsed = 3)
    {
        var picture = Scene.Picture(canvas, canvas, VisualProperties.Default);
        var frame = Scene.Solid(canvas.Width, canvas.Height, Grey, Grey, Grey);
        var (intro, outro) = TemplateRules.Animation(kind);
        var span = new TemplateSpan(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(8), kind, fields, MediaTime.Zero);
        var template = new TemplateLayer(Guid.NewGuid(), span, new TemplateClock(elapsed, 5, intro, outro, elapsed, 75));
        return Render.Export(Scene.Plan(canvas, new ResolvedLayer(picture, frame), new ResolvedLayer(template, null)));
    }

    private readonly record struct Box(int Left, int Top, int Right, int Bottom)
    {
        public double CentreX => (Left + Right) / 2.0;
        public double CentreY => (Top + Bottom) / 2.0;
    }

    /// <summary>The bounding box of the pixels <paramref name="match"/> accepts inside <paramref name="within"/> (inclusive).</summary>
    private static Box? Find(Image image, Func<byte, byte, byte, bool> match, Box? within = null)
    {
        var w = within ?? new Box(0, 0, image.Width - 1, image.Height - 1);
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (var y = w.Top; y <= w.Bottom; y++)
        for (var x = w.Left; x <= w.Right; x++)
        {
            var (red, green, blue, _) = image.At(x, y);
            if (!match(red, green, blue)) continue;
            l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y);
        }
        return r < 0 ? null : new Box(l, t, r, b);
    }

    private static bool Near(byte r, byte g, byte b, string hex, int tolerance = 12)
    {
        var c = Avalonia.Media.Color.Parse(hex);
        return Math.Abs(r - c.R) <= tolerance && Math.Abs(g - c.G) <= tolerance && Math.Abs(b - c.B) <= tolerance;
    }

    private void AssertCentred(string what, Box block, Box ink)
    {
        output.WriteLine($"{what}: block {block}, ink {ink}, Δx {ink.CentreX - block.CentreX:0.0}, Δy {ink.CentreY - block.CentreY:0.0}");
        Assert.InRange(ink.CentreY - block.CentreY, -1.0, 1.0);
        Assert.InRange(ink.CentreX - block.CentreX, -1.0, 1.0);
    }

    public static TheoryData<int, int, string, string> Plates() => new()
    {
        { 1920, 1080, "3", "6B+" }, { 1920, 1080, "12", "7A" }, { 1080, 1920, "7", "7A" }, { 1080, 1920, "123", "6C+/7A" }
    };

    [Theory]
    [MemberData(nameof(Plates))]
    public void The_status_blocks_text_is_centred_and_the_blocks_are_separate(int width, int height, string route, string grade)
    {
        var canvas = new FrameSize(width, height);
        var fields = TemplateRules.Defaults(TemplateKind.Boulder) with { Route = route, Grade = grade };

        var top = Draw(canvas, TemplateKind.Boulder, fields with { Status = BoulderStatus.Top });
        var topBlock = Find(top, (r, g, b) => Near(r, g, b, TemplateRules.TopHex))!.Value;
        var topInk = Find(top, (r, g, b) => r < 100 && b < 100, topBlock)!.Value;            // navy text on yellow
        AssertCentred("TOP", topBlock, topInk);

        var attempt = Draw(canvas, TemplateKind.Boulder, fields with { Status = BoulderStatus.Attempt });
        var attemptBlock = Find(attempt, (r, g, b) => Near(r, g, b, TemplateRules.AttemptHex, 16))!.Value;
        var attemptInk = Find(attempt, (r, g, b) => r > 200 && g > 200 && b > 200, attemptBlock)!.Value;
        AssertCentred("ПОПЫТКА", attemptBlock, attemptInk);

        // The route block: navy left of the status block, a gap of the background between them.
        var plate = Find(attempt, (r, g, b) => b > r + 15 && r < 40, new Box(0, attemptBlock.Top, attemptBlock.Left - 1, attemptBlock.Bottom))!.Value;
        Assert.True(attemptBlock.Left - plate.Right >= 8, $"a gap of {attemptBlock.Left - plate.Right - 1} px between the blocks");
        var gapPixel = attempt.At((plate.Right + attemptBlock.Left) / 2, (int)attemptBlock.CentreY);
        Assert.Equal((Grey, Grey, Grey), (gapPixel.R, gapPixel.G, gapPixel.B));
        var textBox = new Box(plate.Left + (int)(16 * Math.Min(width, height) / 1080.0), plate.Top, plate.Right, plate.Bottom);
        var plateInk = Find(attempt, (r, g, b) => r > 150 && g > 150 && b > 150, textBox)!.Value;     // the white and grey text
        output.WriteLine($"plate: {plate}, text {plateInk}, Δy {plateInk.CentreY - plate.CentreY:0.0}");
        Assert.InRange(plateInk.CentreY - plate.CentreY, -1.0, 1.0);
        Assert.Equal(attemptBlock.Top, plate.Top);
        Assert.Equal(attemptBlock.Bottom, plate.Bottom);
    }

    [Theory]
    [InlineData(1920, 1080, "ЛУЧШАЯ ПОПЫТКА", "ТРАССА 3 · 6B+")]
    [InlineData(1920, 1080, "BEST ATTEMPT", "ROUTE 3 · 6B+")]
    [InlineData(1920, 1080, "ТОП", "")]
    [InlineData(1080, 1920, "ПЕРВАЯ ВЕРШИНА", "ТРАССА 7 · 7A")]
    [InlineData(1080, 1920, "САМАЯ ТЯЖЕЛАЯ ПОПЫТКА ЗА ВЕСЬ СЕЗОН", "ШЕСТАЯ ПОПЫТКА")]
    public void The_highlight_title_and_subtitle_are_centred_on_their_bands(int width, int height, string title, string subtitle)
    {
        var canvas = new FrameSize(width, height);
        var image = Draw(canvas, TemplateKind.Highlight, TemplateRules.Defaults(TemplateKind.Highlight) with { Title = title, Subtitle = subtitle });

        var bar = Find(image, (r, g, b) => Near(r, g, b, TemplateRules.HighlightAccentHex))!.Value;
        var ink = Find(image, (r, g, b) => r < 60 && g < 60 && b < 60, bar)!.Value;            // dark title on yellow
        output.WriteLine($"title «{title}»: bar rows {bar.Top}..{bar.Bottom}, ink rows {ink.Top}..{ink.Bottom}, Δy {ink.CentreY - bar.CentreY:0.0}");
        Assert.InRange(ink.CentreY - bar.CentreY, -1.0, 1.0);
        // Horizontally on the slanted bar: its yellow span on the ink's middle row.
        var row = (int)Math.Round(ink.CentreY);
        var span = Find(image, (r, g, b) => Near(r, g, b, TemplateRules.HighlightAccentHex), new Box(0, row, width - 1, row))!.Value;
        var leftGap = ink.Left - span.Left;
        var rightGap = span.Right - ink.Right;
        output.WriteLine($"  row {row}: yellow {span.Left}..{span.Right}, ink {ink.Left}..{ink.Right}, gaps {leftGap} / {rightGap}");
        Assert.InRange(leftGap - rightGap, -3, 3);         // the slant moves the edges by a pixel or two over the ink's height

        if (subtitle.Length == 0) return;
        var strip = Find(image, (r, g, b) => r < 40 && g < 40 && b < 40, new Box(0, bar.Bottom + 4, width - 1, height - 1))!.Value;
        var subInk = Find(image, (r, g, b) => r > 200 && g > 200 && b > 200, strip)!.Value;
        // Capitals and digits: their ink is the band of the capitals, centred on the strip (the strip is slanted: vertically only).
        output.WriteLine($"  subtitle: strip rows {strip.Top}..{strip.Bottom}, ink rows {subInk.Top}..{subInk.Bottom}, Δy {subInk.CentreY - strip.CentreY:0.0}");
        Assert.InRange(subInk.CentreY - strip.CentreY, -1.0, 1.0);
    }
}
