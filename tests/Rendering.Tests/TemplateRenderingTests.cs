using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 29 (D045 §3, criterion 2): every template drawn by the Preview's control and by the export's rasterizer (off the UI
/// thread) gives the same bytes at the canvas size — horizontal and vertical, over a picture and a text, during its
/// appearance and in its middle; its graphics change the picture; VHS treats what is below it (the text under it loses its
/// pure colour) and nothing above it (a text over it keeps it).
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class TemplateRenderingTests
{
    private static readonly TextProperties Red = new("ТЕКСТ", "Segoe UI", 64, "#FF0000", TextAlignment.Center) { Bold = true };

    private static TemplateLayer Template(TemplateKind kind, TemplateClock clock)
    {
        var fields = TemplateRules.Defaults(kind) with
        {
            Date = "27.09.2026", Route = "7", Grade = "7A", Status = BoulderStatus.Top, Subtitle = "Трасса 7 · 7A"
        };
        var span = new TemplateSpan(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(8), kind, fields, MediaTime.Zero);
        return new TemplateLayer(Guid.NewGuid(), span, clock);
    }

    private static ImmutableArray<LayerPicture> Preview(params ResolvedLayer[] layers) => layers.Select(l => l.Layer switch
    {
        PictureLayer => new LayerPicture(l.Layer, LayerPictureState.Frame, l.Frame),
        _ => new LayerPicture(l.Layer, LayerPictureState.Text)
    }).ToImmutableArray();

    private static ResolvedLayer[] Scene(FrameSize canvas, TemplateLayer? template, bool textAbove = false)
    {
        var frame = Rendering.Tests.Scene.Frame(canvas.Width, canvas.Height, (x, y) => ((byte)(x * 255 / canvas.Width), (byte)(y * 255 / canvas.Height), 128, 255));
        var picture = Rendering.Tests.Scene.Picture(canvas, new FrameSize(canvas.Width, canvas.Height), VisualProperties.Default);
        var layers = new List<ResolvedLayer> { new(picture, frame) };
        if (!textAbove) layers.Add(new(Rendering.Tests.Scene.Text(canvas, Red), null));
        if (template is not null) layers.Add(new(template, null));
        if (textAbove) layers.Add(new(Rendering.Tests.Scene.Text(canvas, Red), null));
        return layers.ToArray();
    }

    public static TheoryData<TemplateKind, int, int, double> Cases()
    {
        var data = new TheoryData<TemplateKind, int, int, double>();
        foreach (var kind in Enum.GetValues<TemplateKind>())
        foreach (var (w, h) in new[] { (640, 360), (360, 640) })
        foreach (var elapsed in new[] { 0.12, 1.5 })
            data.Add(kind, w, h, elapsed);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_preview_and_the_export_draw_every_template_byte_for_byte(TemplateKind kind, int width, int height, double elapsed)
    {
        var canvas = new FrameSize(width, height);
        var (intro, outro) = TemplateRules.Animation(kind);
        var clock = new TemplateClock(elapsed, 3, intro, outro, 3.8 + elapsed, (long)(elapsed * 25));
        var layers = Scene(canvas, Template(kind, clock));

        var export = Render.Export(Rendering.Tests.Scene.Plan(canvas, layers));
        var preview = Render.Preview(canvas, width, height, Preview(layers));
        var without = Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, null)));

        Assert.Equal(export.Pixels, preview.Pixels);
        Assert.NotEqual(without.Pixels, export.Pixels);                           // the template drew something
    }

    private static bool HasPureRed(Image image) =>
        Enumerable.Range(0, image.Width * image.Height).Any(i => image.Pixels[i * 4 + 2] == 255 && image.Pixels[i * 4 + 1] == 0 && image.Pixels[i * 4] == 0);

    [Fact]
    public void Vhs_treats_what_is_below_it_and_nothing_above_it()
    {
        var canvas = new FrameSize(640, 360);
        var vhs = Template(TemplateKind.Vhs, new TemplateClock(1, 3, 0, 0, 1, 25));

        Assert.True(HasPureRed(Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, null)))));
        Assert.False(HasPureRed(Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, vhs)))));
        Assert.True(HasPureRed(Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, vhs, textAbove: true)))));
    }

    [Fact]
    public void A_plan_without_a_template_is_drawn_exactly_as_before_and_the_rasterizer_is_reused()
    {
        var canvas = new FrameSize(320, 180);
        using var rasterizer = new UI.Rendering.AvaloniaCompositionRasterizer();
        var plain = Rendering.Tests.Scene.Plan(canvas, Scene(canvas, null));
        var vhs = Rendering.Tests.Scene.Plan(canvas, Scene(canvas, Template(TemplateKind.Vhs, new TemplateClock(1, 3, 0, 0, 1, 25))));

        var first = Render.Export(plain, rasterizer);
        var treated = Render.Export(vhs, rasterizer);
        var again = Render.Export(vhs, rasterizer);
        var plainAgain = Render.Export(plain, rasterizer);

        Assert.Equal(first.Pixels, plainAgain.Pixels);
        Assert.Equal(treated.Pixels, again.Pixels);
        Assert.Equal(Render.Export(plain).Pixels, first.Pixels);
    }

    [Fact]
    public void An_empty_boulder_plate_draws_nothing_and_a_status_alone_draws_its_chip()
    {
        var canvas = new FrameSize(640, 360);
        var clock = new TemplateClock(2, 3, 0.35, 0.3, 2, 50);
        TemplateLayer Boulder(TemplateProperties fields) =>
            new(Guid.NewGuid(), new TemplateSpan(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(8), TemplateKind.Boulder, fields, MediaTime.Zero), clock);
        var empty = TemplateRules.Defaults(TemplateKind.Boulder);
        var without = Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, null)));

        Assert.Equal(without.Pixels, Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, Boulder(empty)))).Pixels);
        Assert.NotEqual(without.Pixels, Render.Export(Rendering.Tests.Scene.Plan(canvas, Scene(canvas, Boulder(empty with { Status = BoulderStatus.Top })))).Pixels);
    }

    [Theory]
    [InlineData(BoulderStatus.Top, 640, 360)]
    [InlineData(BoulderStatus.Attempt, 640, 360)]
    [InlineData(BoulderStatus.Attempt, 360, 640)]
    public void The_status_shows_from_its_moment_with_its_appearance_the_same_in_the_preview_and_the_export(BoulderStatus status, int width, int height)
    {
        // D045 §17: before the moment only the route block; on its frame the appearance starts (nothing of it yet); during
        // 0.25 s it fades and slides in; then it stays as it is to the end.
        var canvas = new FrameSize(width, height);
        var fields = TemplateRules.Defaults(TemplateKind.Boulder) with { Route = "7", Grade = "7A", Status = status };
        Image Draw(TemplateProperties f, double sinceStatus, bool preview = false)
        {
            var clock = new TemplateClock(2, 3, 0.35, 0.3, 2, 50, sinceStatus);
            var span = new TemplateSpan(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(8), TemplateKind.Boulder, f, MediaTime.Zero);
            var layers = Scene(canvas, new TemplateLayer(Guid.NewGuid(), span, clock));
            return preview ? Render.Preview(canvas, width, height, Preview(layers)) : Render.Export(Rendering.Tests.Scene.Plan(canvas, layers));
        }

        var plateOnly = Draw(fields with { Status = BoulderStatus.None }, double.PositiveInfinity).Pixels;
        var shown = Draw(fields, double.PositiveInfinity).Pixels;
        Assert.NotEqual(plateOnly, shown);

        Assert.Equal(plateOnly, Draw(fields, -1.0).Pixels);
        Assert.Equal(plateOnly, Draw(fields, -0.04).Pixels);
        Assert.Equal(plateOnly, Draw(fields, 0).Pixels);                       // the frame the appearance starts on
        var during = Draw(fields, 0.12).Pixels;
        Assert.NotEqual(plateOnly, during);
        Assert.NotEqual(shown, during);
        Assert.Equal(shown, Draw(fields, TemplateRules.StatusAppearSeconds).Pixels);
        Assert.Equal(shown, Draw(fields, 4.0).Pixels);

        foreach (var since in new[] { -0.04, 0, 0.04, 0.12, 0.24, 0.25, 4.0 })
            Assert.Equal(Draw(fields, since).Pixels, Draw(fields, since, preview: true).Pixels);
    }
}
