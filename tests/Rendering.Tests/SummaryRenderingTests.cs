using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Rendering;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// D073: «Итоги» drawn by the Preview's control and by the export's rasterizer gives the same bytes — landscape and portrait,
/// while the rows come in, while they scroll, in English, over the video and on its own background; its own background hides
/// the picture; the FLASH block is not TOP's yellow block; the rows' pace and scroll and the time format.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class SummaryRenderingTests
{
    private static SummaryBoard Board(int rows) => new(
        Enumerable.Range(0, rows).Select(i => new SummaryRow((i + 1).ToString(), "6B+", i % 2 == 0 ? "#FF1744" : "#2979FF",
            (BoulderStatus)(i % 4), 1 + i % 3, i % 5 == 4 ? null : MediaTime.FromSeconds(30 + i * 7), 130 + i, i % 3 == 0 ? null : 150 + i)).ToImmutableArray(),
        new SummaryTotals(rows, rows / 2, rows / 4, MediaTime.FromSeconds(412), 141, 172));

    private static TemplateLayer Summary(SummaryBoard board, double elapsed, bool own = false, TemplateLanguage language = TemplateLanguage.Russian)
    {
        var fields = TemplateRules.Defaults(TemplateKind.Summary) with { Title = "Итоги дня", Subtitle = "22.11.2026", OwnBackground = own, Language = language };
        var span = new TemplateSpan(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(10), TemplateKind.Summary, fields, MediaTime.Zero) { Summary = board };
        return new TemplateLayer(Guid.NewGuid(), span, new TemplateClock(elapsed, 10 - elapsed, 0.4, 0.4, elapsed, (long)(elapsed * 25)));
    }

    private static ResolvedLayer[] Scene(FrameSize canvas, TemplateLayer? template)
    {
        var frame = Rendering.Tests.Scene.Frame(canvas.Width, canvas.Height, (x, y) => ((byte)(x * 255 / canvas.Width), (byte)(y * 255 / canvas.Height), 128, 255));
        var layers = new List<ResolvedLayer> { new(Rendering.Tests.Scene.Picture(canvas, canvas, VisualProperties.Default), frame) };
        if (template is not null) layers.Add(new(template, null));
        return layers.ToArray();
    }

    private static ImmutableArray<LayerPicture> Preview(ResolvedLayer[] layers) => layers.Select(l => l.Layer is PictureLayer
        ? new LayerPicture(l.Layer, LayerPictureState.Frame, l.Frame) : new LayerPicture(l.Layer, LayerPictureState.Text)).ToImmutableArray();

    public static TheoryData<int, int, int, double, bool, TemplateLanguage> Cases() => new()
    {
        { 640, 360, 4, 1.2, false, TemplateLanguage.Russian },
        { 640, 360, 14, 6.5, false, TemplateLanguage.Russian },    // past ten rows: scrolled
        { 640, 360, 14, 9.9, true, TemplateLanguage.English },
        { 360, 640, 8, 5, false, TemplateLanguage.English },
        { 360, 640, 0, 3, true, TemplateLanguage.Russian },        // no routes: the header and «0»
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_preview_and_the_export_draw_the_board_byte_for_byte(int width, int height, int rows, double elapsed, bool own, TemplateLanguage language)
    {
        var canvas = new FrameSize(width, height);
        var layers = Scene(canvas, Summary(Board(rows), elapsed, own, language));
        var export = Render.Export(Rendering.Tests.Scene.Plan(canvas, layers));
        var preview = Render.Preview(canvas, width, height, Preview(layers));
        Assert.Equal(export.Pixels, preview.Pixels);
        Assert.NotEqual(Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, null))).Pixels, export.Pixels);
    }

    [Fact]
    public void Its_own_background_hides_the_picture_and_over_the_video_the_picture_shows_dimmed()
    {
        var canvas = new FrameSize(640, 360);
        var own = Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, Summary(Board(3), 5, own: true))));
        var over = Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, Summary(Board(3), 5))));
        // The top-left corner: navy on its own background; the picture's gradient (dimmed) over the video.
        Assert.Equal((0x2E, 0x1B, 0x0D), (own.Pixels[0], own.Pixels[1], own.Pixels[2]));
        Assert.NotEqual(own.Pixels[0], over.Pixels[0]);
    }

    [Fact]
    public void The_rows_scroll_once_past_ten_and_the_board_changes_while_they_scroll()
    {
        var pace = SummaryPainter.Pace(14, 10);
        Assert.Equal((0.5 * 10 - 0.45) / 14, pace, 9);
        Assert.Equal(5, SummaryPainter.FirstRow + 14 * pace, 9);   // D073 amendment 2: every row in by 5 s of 10, the totals held after
        Assert.Equal(SummaryPainter.FastestPace, SummaryPainter.Pace(40, 10));
        Assert.Equal(SummaryPainter.SlowestPace, SummaryPainter.Pace(2, 10));
        Assert.Equal(0, SummaryPainter.ScrollAt(SummaryPainter.FirstRow + 9.5 * pace, 14, pace));
        Assert.Equal(4, SummaryPainter.ScrollAt(10, 14, pace), 9);
        Assert.Equal(0, SummaryPainter.ScrollAt(10, 10, pace));

        var canvas = new FrameSize(640, 360);
        var before = Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, Summary(Board(14), SummaryPainter.FirstRow + 10 * pace))));
        var after = Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, Summary(Board(14), SummaryPainter.FirstRow + 10 * pace + 0.35))));
        Assert.NotEqual(before.Pixels, after.Pixels);
    }

    [Fact]
    public void A_flash_is_drawn_unlike_a_top_in_the_boulders_lilac()
    {
        var canvas = new FrameSize(640, 360);
        TemplateLayer Boulder(BoulderStatus status) => new(Guid.NewGuid(), new TemplateSpan(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(8),
            TemplateKind.Boulder, TemplateRules.Defaults(TemplateKind.Boulder) with { Route = "7", Status = status, ShowHeartRate = false }, MediaTime.Zero),
            new TemplateClock(2, 3, 0.35, 0.3, 2, 50));
        var top = Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, Boulder(BoulderStatus.Top))));
        var flash = Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, Boulder(BoulderStatus.Flash))));
        Assert.NotEqual(top.Pixels, flash.Pixels);
        int Orange(Image image) => Enumerable.Range(0, image.Width * image.Height).Count(i =>
        {
            var c = new[] { image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2] }.Order().ToArray();
            return c[2] > 240 && c[0] < 30 && c[1] is > 80 and < 140;   // #FF6D00 in either channel order; TOP's #FFD400 is not
        });
        Assert.True(Orange(flash) > Orange(top) + 200);   // D073 amendment 2: the FLASH block is orange and slanted, TOP's yellow
    }

    [Theory]
    [InlineData(42.4, "0:42")]
    [InlineData(42.5, "0:43")]
    [InlineData(78, "1:18")]
    [InlineData(3725, "1:02:05")]
    public void A_climb_time_is_minutes_and_seconds(double seconds, string text) =>
        Assert.Equal(text, SummaryPainter.Format(MediaTime.FromSeconds(seconds)));
}
