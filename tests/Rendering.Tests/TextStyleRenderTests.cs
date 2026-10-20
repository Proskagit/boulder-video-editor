using System.Diagnostics;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Rendering;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Xunit;
using Xunit.Abstractions;
using TextAlignment = AiVideoEditor.Core.Entities.TextAlignment;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Phase 27 Step 27.4 (D043 §2–§3, §7; "Refined in Step 27.1", conditions 1–7): the outline, the background and bold in
/// the one painter of the Preview and the export — the export equal to the Preview pixel for pixel for each and together,
/// with a transform, a fade and the motion; a text without them drawn exactly as before D043; the glyph cache (a key part
/// changed → a new geometry, colours and thicknesses not; bounded; one geometry per consumer); the export's wait for the
/// UI thread ending on a cancel while the UI thread is busy, on the dispatcher's shutdown and on a timeout — never hanging;
/// the cost of an outlined text per frame.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class TextStyleRenderTests(ITestOutputHelper output)
{
    private static readonly FrameSize Canvas = new(640, 360);

    private static TextProperties Props(string text = "Маршрут 6B+", double size = 48) =>
        new(text, "Segoe UI", size, "#FFFFFF", TextAlignment.Center);

    private static ResolvedLayer Layer(TextProperties text, VisualProperties? visual = null) => new(Scene.Text(Canvas, text, visual), null);

    private static Image Export(params ResolvedLayer[] layers) => Render.Export(Scene.Plan(Canvas, layers));

    private static Image Preview(params ResolvedLayer[] layers) => Render.Preview(Canvas, Canvas.Width, Canvas.Height, layers.Select(Scene.AsPreview));

    private static readonly TextOutline Outline = new(true, "#000000", 0.08);
    private static readonly TextBackground Background = new(true, "#000000", 0.6);

    public static TheoryData<string, double, bool, bool, bool, double, double, double> Styles => new()
    {
        // text, size, bold, outline, background, rotation, scale, opacity
        { "Маршрут 6B+ / Route", 48, true, true, false, 0, 1, 1 },
        { "Маршрут 6B+ / Route", 48, false, false, true, 0, 1, 1 },
        { "Маршрут 6B+ / Route", 48, true, true, true, 0, 1, 1 },
        { "Жирный без рамки", 40, true, false, false, 0, 1, 1 },
        { "Мелкий 16 px", 16, true, true, false, 0, 1, 1 },
        { "Мелкий 16 px", 16, false, true, true, 0, 1, 1 },
        { "Две строки\nвторая, длиннее", 36, true, true, true, 20, 1.4, 0.7 },
        { "Крупно", 120, true, true, true, -10, 0.8, 1 },
    };

    [Theory]
    [MemberData(nameof(Styles))]
    public void The_export_draws_an_outline_and_a_background_exactly_like_the_preview(string text, double size, bool bold, bool outline,
        bool background, double rotation, double scale, double opacity)
    {
        var props = Props(text, size) with
        {
            Bold = bold, Outline = Outline with { IsEnabled = outline }, Background = Background with { IsEnabled = background }
        };
        var layer = Layer(props, VisualProperties.Default with { RotationDegrees = rotation, Scale = scale, Opacity = opacity });

        var export = Export(layer);
        var preview = Preview(layer);

        Assert.NotNull(export.InkBox(threshold: 60));
        Assert.Equal(preview.Pixels, export.Pixels);
    }

    /// <summary>Condition 1: a text without the D043 style is drawn exactly as the painter drew every text before
    /// (<c>DrawText</c> of the same layout at the box's origin) — a replica of that routine, pixel for pixel.</summary>
    [Theory]
    [InlineData("Plain title", 48, TextAlignment.Center)]
    [InlineData("Две строки\nтекста", 30, TextAlignment.Left)]
    [InlineData("16 px", 16, TextAlignment.Right)]
    public async Task A_text_without_the_new_style_is_drawn_as_before(string text, double size, TextAlignment alignment)
    {
        var props = new TextProperties(text, "Segoe UI", size, "#FFEE00", alignment) { MotionIn = new TextMotion(TextMotionKind.Zoom) };
        Assert.True(props.IsPlain);
        var now = Export(Layer(props));
        var before = await Task.Run(() =>
        {
            using var target = new RenderTargetBitmap(new PixelSize(Canvas.Width, Canvas.Height), new Vector(96, 96));
            using (var context = target.CreateDrawingContext())
            {
                context.FillRectangle(Brushes.Black, new Rect(0, 0, Canvas.Width, Canvas.Height));
                using (context.PushClip(new Rect(0, 0, Canvas.Width, Canvas.Height)))
                using (context.PushTransform(Matrix.CreateTranslation(Canvas.Width / 2.0, Canvas.Height / 2.0)))
                using (context.PushOpacity(1))
                {
                    var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
                        size, new ImmutableSolidColorBrush(Color.Parse("#FFEE00")));
                    formatted.MaxTextWidth = Math.Max(formatted.WidthIncludingTrailingWhitespace, 1);
                    formatted.TextAlignment = alignment switch
                    {
                        TextAlignment.Left => Avalonia.Media.TextAlignment.Left,
                        TextAlignment.Right => Avalonia.Media.TextAlignment.Right,
                        _ => Avalonia.Media.TextAlignment.Center
                    };
                    context.DrawText(formatted, new Point(-formatted.MaxTextWidth / 2, -formatted.Height / 2));
                }
            }
            var pixels = new byte[Canvas.Width * Canvas.Height * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { target.CopyPixels(new PixelRect(0, 0, Canvas.Width, Canvas.Height), handle.AddrOfPinnedObject(), pixels.Length, Canvas.Width * 4); }
            finally { handle.Free(); }
            return pixels;
        });

        Assert.Equal(before, now.Pixels);
    }

    [Fact]
    public void The_outline_is_drawn_around_the_glyphs_and_the_background_under_both()
    {
        var plain = Export(Layer(Props() with { Bold = true }));
        var outlined = Export(Layer(Props() with { Bold = true, Outline = new TextOutline(true, "#FF0000", 0.1) }));
        var boxed = Export(Layer(Props() with { Bold = true, Background = new TextBackground(true, "#0000FF", 1) }));

        var ink = plain.InkBox(threshold: 128)!.Value;
        static int Count(Image image, Func<(byte R, byte G, byte B, byte A), bool> test)
        {
            var n = 0;
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (test(image.At(x, y))) n++;
            return n;
        }
        static bool Red((byte R, byte G, byte B, byte A) p) => p.R > 200 && p.G < 40 && p.B < 40;
        static bool Blue((byte R, byte G, byte B, byte A) p) => p.B > 200 && p.R < 40 && p.G < 40;
        static bool White((byte R, byte G, byte B, byte A) p) => p.R > 240 && p.G > 240 && p.B > 240;

        Assert.Equal(0, Count(plain, Red));
        Assert.True(Count(outlined, Red) > 500);                                     // red around the letters
        Assert.True(Count(outlined, White) > Count(plain, White) / 2);               // the fill over it
        Assert.True(Count(boxed, Blue) > (ink.Right - ink.Left) * (ink.Bottom - ink.Top) / 2);   // the box under the text
        Assert.True(Count(boxed, White) > Count(plain, White) / 2);                  // the text over the box
        // The background reaches past the ink by its padding (25 % of the size, 12 px) on every side.
        Assert.True(Blue(boxed.At(ink.Left - 6, (ink.Top + ink.Bottom) / 2)));
        Assert.True(Blue(boxed.At(ink.Right + 6, (ink.Top + ink.Bottom) / 2)));
    }

    [Fact]
    public void A_moving_text_is_drawn_alike_on_every_frame_of_its_motion()
    {
        var project = new Project { Settings = { FrameRate = FrameRate.Fps25, FrameWidth = Canvas.Width, FrameHeight = Canvas.Height } };
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(track);
        track.Clips.Add(new TextClip
        {
            Text = "Выезд", Bold = true, Outline = Outline, Background = Background, TimelineStart = MediaTime.Zero,
            Duration = MediaTime.FromFrame(20, FrameRate.Fps25), FadeIn = MediaTime.FromFrame(6, FrameRate.Fps25),
            FadeOut = MediaTime.FromFrame(4, FrameRate.Fps25),
            MotionIn = new TextMotion(TextMotionKind.Slide, TextMotionSide.Bottom), MotionOut = new TextMotion(TextMotionKind.Zoom)
        });
        var snapshot = PlaybackSnapshotBuilder.Build(project, 1);
        foreach (var frame in new long[] { 0, 3, 6, 16, 19 })
        {
            var layers = snapshot.LayersAt(MediaTime.FromFrame(frame, FrameRate.Fps25)).Select(l => new ResolvedLayer(l, null)).ToArray();
            Assert.Equal(Preview(layers).Pixels, Export(layers).Pixels);
        }
    }

    // --- the glyph cache (conditions 2–4) ---------------------------------------------------------------------------

    private static Task<T> OnUi<T>(Func<T> f) => Dispatcher.UIThread.InvokeAsync(f).GetTask();

    [Fact]
    public async Task A_part_of_the_key_changed_builds_again_a_colour_or_a_thickness_does_not()
    {
        var cache = new TextGlyphCache();
        var text = Props() with { Outline = Outline };
        var first = await OnUi(() => cache.Get(text));
        Assert.Same(first, await OnUi(() => cache.Get(text with { ColorHex = "#123456", Outline = Outline with { ColorHex = "#FF0000", Thickness = 0.2 } })));
        Assert.Same(first, await OnUi(() => cache.Get(text with { Background = Background, MotionIn = new TextMotion(TextMotionKind.Zoom) })));
        Assert.Equal(1, cache.Builds);

        foreach (var changed in new[]
                 {
                     text with { Text = "Другой" }, text with { FontFamily = "Times New Roman" }, text with { FontSize = 49 },
                     text with { Bold = true }, text with { Alignment = TextAlignment.Left }
                 })
            Assert.NotSame(first, await OnUi(() => cache.Get(changed)));
        Assert.Equal(6, cache.Builds);
    }

    [Fact]
    public async Task The_cache_is_bounded_and_drops_the_least_recently_used()
    {
        var cache = new TextGlyphCache(capacity: 3);
        TextProperties T(int i) => Props($"Текст {i}") with { Outline = Outline };
        for (var i = 0; i < 3; i++) await OnUi(() => cache.Get(T(i)));
        await OnUi(() => cache.Get(T(0)));                  // 0 used again: 1 is now the least recent
        await OnUi(() => cache.Get(T(3)));
        Assert.Equal(3, cache.Count);
        var builds = cache.Builds;
        await OnUi(() => cache.Get(T(0)));
        Assert.Equal(builds, cache.Builds);                 // kept
        await OnUi(() => cache.Get(T(1)));
        Assert.Equal(builds + 1, cache.Builds);             // dropped, built again
    }

    [Fact]
    public async Task Off_the_UI_thread_the_geometry_comes_from_the_UI_thread_and_is_drawn_there()
    {
        var cache = new TextGlyphCache();
        var text = Props() with { Outline = Outline };
        var geometry = await Task.Run(() => cache.Get(text));
        Assert.NotNull(geometry);
        Assert.Equal(1, cache.Builds);
        Assert.Same(geometry, await Task.Run(() => cache.Get(text)));
    }

    [Fact]
    public async Task A_cancel_while_the_UI_thread_is_busy_ends_the_wait()
    {
        var cache = new TextGlyphCache();
        using var release = new ManualResetEventSlim();
        Dispatcher.UIThread.Post(() => release.Wait(TimeSpan.FromSeconds(30)));   // the UI thread busy
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var watch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => cache.Get(Props() with { Outline = Outline }, cts.Token)));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
            Assert.Equal(0, cache.Count);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task An_export_cancelled_while_the_UI_thread_is_busy_stops_its_render()
    {
        using var rasterizer = new AvaloniaCompositionRasterizer();
        var plan = Scene.Plan(Canvas, Layer(Props() with { Outline = Outline }));
        using var release = new ManualResetEventSlim();
        Dispatcher.UIThread.Post(() => release.Wait(TimeSpan.FromSeconds(30)));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
                rasterizer.Render(plan, new byte[Canvas.Width * Canvas.Height * 4], Canvas.Width * 4, cts.Token)));
        }
        finally
        {
            release.Set();
        }
    }

    private sealed class StuckUi(Task shutdown) : IUiThread
    {
        public bool CheckAccess() => false;
        public Task<Geometry?> InvokeAsync(Func<Geometry?> build, CancellationToken ct) => new TaskCompletionSource<Geometry?>().Task;   // never runs
        public Task Shutdown { get; } = shutdown;
    }

    [Fact]
    public async Task The_dispatcher_shutting_down_fails_the_wait_as_a_render_failure()
    {
        var cache = new TextGlyphCache(uiTimeout: TimeSpan.FromSeconds(30), ui: new StuckUi(Task.Delay(100)));
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ExportException>(() => Task.Run(() => cache.Get(Props() with { Outline = Outline })));
        Assert.Equal(ExportFailure.RenderFailed, error.Failure);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_UI_thread_that_never_answers_fails_the_wait_after_the_timeout()
    {
        var cache = new TextGlyphCache(uiTimeout: TimeSpan.FromMilliseconds(300), ui: new StuckUi(new TaskCompletionSource().Task));
        var error = await Assert.ThrowsAsync<ExportException>(() => Task.Run(() => cache.Get(Props() with { Outline = Outline })));
        Assert.Equal(ExportFailure.RenderFailed, error.Failure);
    }

    [Fact]
    public async Task An_outlined_text_without_a_cache_is_refused_rather_than_drawn_without_its_outline()
    {
        var plan = Scene.Plan(Canvas, Layer(Props() with { Outline = Outline }));
        var error = await Task.Run(() =>
        {
            using var target = new RenderTargetBitmap(new PixelSize(Canvas.Width, Canvas.Height), new Vector(96, 96));
            using var context = target.CreateDrawingContext();
            return Assert.Throws<InvalidOperationException>(() => CompositionPainter.Paint(context, plan, _ => null));
        });
        Assert.Contains("glyph cache", error.Message);
    }

    // --- cost (condition 6) -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1080, 1920)]
    [InlineData(1920, 1080)]
    public async Task The_cost_per_frame_of_an_outlined_text_is_reported(int width, int height)
    {
        var canvas = new FrameSize(width, height);
        var text = new TextProperties("Маршрут 6B+\nСкалодром", "Segoe UI", 72, "#FFFFFF", TextAlignment.Center) { Bold = true };
        double Measure(TextProperties t)
        {
            using var rasterizer = new AvaloniaCompositionRasterizer();
            var plan = CompositionDrawPlan.Build(canvas, Affine2D.Identity, new[] { new ResolvedLayer(Scene.Text(canvas, t), null) });
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < 5; i++) rasterizer.Render(plan, pixels, width * 4);
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 40; i++) rasterizer.Render(plan, pixels, width * 4);
            return watch.Elapsed.TotalMilliseconds / 40;
        }
        var (plain, styled) = await Task.Run(() => (Measure(text), Measure(text with { Outline = Outline, Background = Background })));
        output.WriteLine($"{width} × {height}: bold text {plain:F2} ms per frame; with outline and background {styled:F2} ms");
        Assert.True(styled < plain + 20, $"{styled:F2} vs {plain:F2}");
    }
}
