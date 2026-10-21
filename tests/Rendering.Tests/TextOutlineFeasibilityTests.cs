using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Step 27.1 (D043 "Refined in Step 27.1"): the feasibility of a text outline drawn from the glyph geometry, stroked with a
/// round-join pen under the fill. No product code: the candidate routine lives here and draws the way
/// <c>CompositionPainter</c> draws a text (the same layout rule, the box centred on the local origin). Kept as the guard of
/// the chosen variant B against a change of Avalonia's behaviour (condition 5): D043's candidate (A,
/// <see cref="FormattedText.BuildGeometry"/> on the drawing thread) still fails off the UI thread; B — the geometry built
/// and first drawn on the UI thread, then drawn on any thread — gives the export's path (a render target on a thread-pool
/// thread) the Preview's pixels (a control rendered on the UI thread), also for parallel exports and for one shared
/// geometry drawn by several threads; the geometry lies on the glyphs <c>DrawText</c> draws; the cost per frame is
/// reported. With <c>AIVE_OUTLINE_SPIKE_OUT</c> set to a folder, a gallery of PNGs is written for the visual check;
/// <c>AIVE_OUTLINE_FILL</c> compares the fills (the geometry by default — the chosen one).
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class TextOutlineFeasibilityTests(ITestOutputHelper output)
{
    private sealed record Style(string Text, string Family, double Size, bool Bold, double OutlinePercent,
        string Fill = "#FFFFFF", string Outline = "#000000", double Rotation = 0, double Scale = 1);

    private static readonly string? GalleryDir = Environment.GetEnvironmentVariable("AIVE_OUTLINE_SPIKE_OUT");

    /// <summary>The fill over an outline: the hinted <c>DrawText</c> (ClearType in a render target), the same with
    /// grayscale anti-aliasing, or the glyph geometry itself (the outline's own shape). <c>AIVE_OUTLINE_FILL</c>.</summary>
    public enum Fill { Text, GrayText, Geometry }

    private static readonly Fill FillWay = Enum.TryParse<Fill>(Environment.GetEnvironmentVariable("AIVE_OUTLINE_FILL"), out var f) ? f : Fill.Geometry;

    // --- the candidate routine ---------------------------------------------------------------------------

    private static FormattedText Layout(Style s)
    {
        var typeface = new Typeface(new FontFamily(s.Family), FontStyle.Normal, s.Bold ? FontWeight.Bold : FontWeight.Normal);
        var formatted = new FormattedText(s.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, s.Size,
            new ImmutableSolidColorBrush(Color.Parse(s.Fill)));
        formatted.MaxTextWidth = Math.Max(formatted.WidthIncludingTrailingWhitespace, 1);
        formatted.TextAlignment = TextAlignment.Center;
        return formatted;
    }

    /// <summary>How the outline's glyph geometry is obtained: A — <see cref="FormattedText.BuildGeometry"/> on the
    /// drawing thread (D043's candidate); B — the same geometry built on the UI thread (a dispatcher call from the export's
    /// thread) and drawn on the drawing thread. (C — the glyph runs turned into platform geometry by the render interface
    /// on any thread — needs <c>AvaloniaLocator.Current</c> and <c>Geometry.PlatformImpl</c>, which the public reference
    /// assemblies of Avalonia 11.1.3 do not expose; not reachable without private API.)</summary>
    public enum Mode { A, B }

    private sealed class Prepared
    {
        public required FormattedText Text { get; init; }
        public required Point Origin { get; init; }
        public Geometry? Glyphs { get; init; }
        public IPen? Pen { get; init; }
    }

    private static Prepared Prepare(Style s, Mode mode = Mode.A)
    {
        var text = Layout(s);
        var origin = new Point(-text.MaxTextWidth / 2, -text.Height / 2);
        if (s.OutlinePercent <= 0) return new Prepared { Text = text, Origin = origin };
        // The visible part outside the glyph is half the pen; the inner half is covered by the fill drawn over it.
        var width = 2 * s.OutlinePercent / 100 * s.Size;
        return new Prepared
        {
            Text = text, Origin = origin, Glyphs = Glyphs(s, text, origin, mode),
            Pen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse(s.Outline)), width, lineCap: PenLineCap.Round,
                lineJoin: PenLineJoin.Round)
        };
    }

    private static Geometry? Glyphs(Style s, FormattedText text, Point origin, Mode mode)
    {
        if (mode == Mode.A || Dispatcher.UIThread.CheckAccess()) return text.BuildGeometry(origin);
        return Dispatcher.UIThread.Invoke(() =>
        {
            // Draw it once on the UI thread so its platform geometry is created there (lazily cached by Avalonia).
            var geometry = Layout(s).BuildGeometry(origin);
            using var warm = new RenderTargetBitmap(new PixelSize(1, 1), new Vector(96, 96));
            using (var context = warm.CreateDrawingContext())
                if (geometry is not null) context.DrawGeometry(Brushes.White, null, geometry);
            return geometry;
        });
    }

    /// <summary>A "wall" (coloured blocks), then the text at the canvas centre with its transform.</summary>
    private static void Paint(DrawingContext context, int width, int height, Style s, Prepared p)
    {
        Wall(context, width, height);
        var m = Matrix.CreateScale(s.Scale, s.Scale) * Matrix.CreateRotation(s.Rotation * Math.PI / 180) *
                Matrix.CreateTranslation(width / 2.0, height / 2.0);
        using (context.PushTransform(m))
        {
            if (p.Glyphs is not { } glyphs)
            {
                context.DrawText(p.Text, p.Origin);
                return;
            }
            context.DrawGeometry(null, p.Pen, glyphs);
            switch (FillWay)
            {
                case Fill.Text:
                    context.DrawText(p.Text, p.Origin);
                    break;
                case Fill.GrayText:
                    using (context.PushRenderOptions(new RenderOptions { TextRenderingMode = TextRenderingMode.Antialias }))
                        context.DrawText(p.Text, p.Origin);
                    break;
                default:
                    context.DrawGeometry(new ImmutableSolidColorBrush(Color.Parse(s.Fill)), null, glyphs);
                    break;
            }
        }
    }

    private static readonly IBrush[] WallColours =
    [
        new ImmutableSolidColorBrush(Color.Parse("#D9C7A7")), new ImmutableSolidColorBrush(Color.Parse("#F2E94E")),
        new ImmutableSolidColorBrush(Color.Parse("#E4572E")), new ImmutableSolidColorBrush(Color.Parse("#76B041")),
        new ImmutableSolidColorBrush(Color.Parse("#2E86AB")), new ImmutableSolidColorBrush(Color.Parse("#F5F5F5")),
        new ImmutableSolidColorBrush(Color.Parse("#A23B72")), new ImmutableSolidColorBrush(Color.Parse("#3B3B3B")),
    ];

    private static void Wall(DrawingContext context, int width, int height)
    {
        const int block = 37;
        var i = 0;
        for (var y = 0; y < height; y += block)
        for (var x = 0; x < width; x += block)
            context.FillRectangle(WallColours[(i++ * 7 + y / block) % WallColours.Length], new Rect(x, y, block, block));
    }

    // --- the two paths --------------------------------------------------------------------------------------

    /// <summary>The export's way: a render target painted on a thread-pool thread, its pixels copied out.</summary>
    private static Image Export(int width, int height, Style s, Mode mode, string? save = null) => Task.Run(() =>
    {
        using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        var prepared = Prepare(s, mode);
        using (var context = target.CreateDrawingContext())
            Paint(context, width, height, s, prepared);
        if (save is not null) target.Save(save);
        return Copy(target, width, height);
    }).GetAwaiter().GetResult();

    private sealed class SpikeView(int width, int height, Style s, Mode mode) : Control
    {
        public override void Render(DrawingContext context) => Paint(context, width, height, s, Prepare(s, mode));
    }

    /// <summary>The Preview's way: a control laid out and rendered on the UI thread.</summary>
    private static Image Preview(int width, int height, Style s, Mode mode) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var view = new SpikeView(width, height, s, mode);
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        target.Render(view);
        return Copy(target, width, height);
    }).GetTask().GetAwaiter().GetResult();

    private static Image Copy(RenderTargetBitmap target, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { target.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4); }
        finally { handle.Free(); }
        return new Image(width, height, pixels);
    }

    // --- the cases ------------------------------------------------------------------------------------------

    private static readonly (string, string, double, bool, double, double, double)[] Base =
    {
        // text, family, size, bold, outline %, rotation, scale
        ( "Маршрут 6B+ / Route", "Segoe UI", 48, true, 8, 0, 1 ),
        ( "Маршрут 6B+ / Route", "Segoe UI", 48, false, 8, 0, 1 ),
        ( "Мелкий текст 16 px", "Segoe UI", 16, true, 8, 0, 1 ),
        ( "Мелкий текст 16 px", "Segoe UI", 16, false, 2, 0, 1 ),
        ( "Крупно", "Segoe UI", 160, true, 20, 0, 1 ),
        ( "Тонкая обводка", "Segoe UI", 72, true, 2, 0, 1 ),
        ( "Times / Шрифт", "Times New Roman", 64, false, 8, 0, 1 ),
        ( "Consolas 0123", "Consolas", 56, true, 12, 0, 1 ),
        ( "Поворот и\nмасштаб", "Segoe UI", 48, true, 8, 25, 1.7 ),
        ( "Нет шрифта", "Nowhere Sans 123", 48, true, 8, 0, 1 ),
        ( "Без обводки", "Segoe UI", 48, true, 0, 0, 1 ),
    };

    public static TheoryData<Mode, string, string, double, bool, double, double, double> Cases()
    {
        var data = new TheoryData<Mode, string, string, double, bool, double, double, double>();
        foreach (var mode in new[] { Mode.A, Mode.B })
        foreach (var (t, f, z, b, o, r, k) in Base) data.Add(mode, t, f, z, b, o, r, k);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_outline_draws_off_the_UI_thread_exactly_like_the_preview(Mode mode, string text, string family, double size, bool bold,
        double outline, double rotation, double scale)
    {
        var s = new Style(text, family, size, bold, outline, Rotation: rotation, Scale: scale);
        string? save = null;
        if (GalleryDir is not null)
        {
            Directory.CreateDirectory(GalleryDir);
            save = Path.Combine(GalleryDir, $"{mode}{FillWay}_{string.Join("_", text.Split(Path.GetInvalidFileNameChars().Append(' ').Append('\n').ToArray()))}_{size}_{(bold ? "b" : "r")}_{outline}.png");
        }

        if (mode == Mode.A && outline > 0)
        {
            // D043's candidate as written: the glyph geometry is an AvaloniaObject, created only on the UI thread.
            var error = Assert.Throws<InvalidOperationException>(() => Export(900, 360, s, mode, save));
            Assert.Contains("invalid thread", error.Message);
            return;
        }

        var export = Export(900, 360, s, mode, save);
        var preview = Preview(900, 360, s, mode);

        Assert.Equal(preview.Pixels, export.Pixels);
    }

    [Theory]
    [InlineData(Mode.A)]
    [InlineData(Mode.B)]
    public void Several_exports_at_once_draw_outlines_without_errors_and_identically(Mode mode)
    {
        var s = new Style("Параллельно / Parallel", "Segoe UI", 64, true, 8);
        if (mode == Mode.A)
        {
            Assert.Throws<InvalidOperationException>(() => Export(640, 200, s, mode));
            return;
        }
        var reference = Export(640, 200, s, mode);
        var results = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(4).Select(_ => Export(640, 200, s, mode)).ToList();
        Assert.All(results, r => Assert.Equal(reference.Pixels, r.Pixels));
    }

    /// <summary>Condition 3: one geometry prepared once (as a cache entry) and drawn by several threads at once gives the
    /// same pixels as on one thread.</summary>
    [Fact]
    public async Task One_prepared_geometry_drawn_by_several_threads_at_once_gives_identical_pixels()
    {
        var s = new Style("Общая геометрия / Shared", "Segoe UI", 56, true, 10);
        var shared = await Task.Run(() => Prepare(s, Mode.B));
        Image Draw()
        {
            using var target = new RenderTargetBitmap(new PixelSize(700, 220), new Vector(96, 96));
            using (var context = target.CreateDrawingContext())
                Paint(context, 700, 220, s, shared);
            return Copy(target, 700, 220);
        }

        var reference = await Task.Run(Draw);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(Draw)));
        Assert.All(results, r => Assert.Equal(reference.Pixels, r.Pixels));
    }

    /// <summary>The geometry is where the glyphs are: its fill and <c>DrawText</c>'s fill cover the same pixels up to
    /// anti-aliasing (hinting may differ), so the outline sits on the letters and not beside them.</summary>
    [Theory]
    [InlineData(Mode.B, "Маршрут 6B+ / Route", 48, true)]
    [InlineData(Mode.B, "Мелкий текст 16 px", 16, false)]
    [InlineData(Mode.B, "Мелкий текст 16 px", 16, true)]
    [InlineData(Mode.B, "Крупно", 160, true)]
    [InlineData(Mode.B, "Две строки\nвторая, длиннее", 40, true)]
    public void The_geometry_lies_on_the_glyphs_drawn_by_DrawText(Mode mode, string text, double size, bool bold)
    {
        const int w = 900, h = 300;
        var s = new Style(text, "Segoe UI", size, bold, 0);
        Image Draw(bool geometry) => Task.Run(() =>
        {
            using var target = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            var p = Prepare(s);
            using (var context = target.CreateDrawingContext())
            {
                context.FillRectangle(Brushes.Black, new Rect(0, 0, w, h));
                using (context.PushTransform(Matrix.CreateTranslation(w / 2.0, h / 2.0)))
                {
                    if (geometry) context.DrawGeometry(Brushes.White, null, Glyphs(s, p.Text, p.Origin, mode)!);
                    else context.DrawText(p.Text, p.Origin);
                }
            }
            return Copy(target, w, h);
        }).GetAwaiter().GetResult();

        var byText = Draw(false);
        var byGeometry = Draw(true);
        var a = byText.InkBox(threshold: 128)!.Value;
        var b = byGeometry.InkBox(threshold: 128)!.Value;
        long diff = 0, ink = 0;
        var big = 0;
        for (var i = 1; i < byText.Pixels.Length; i += 4)
        {
            var d = Math.Abs(byText.Pixels[i] - byGeometry.Pixels[i]);
            diff += d;
            ink += byText.Pixels[i];
            if (d > 128) big++;
        }
        var share = (double)diff / Math.Max(ink, 1);
        output.WriteLine($"{mode} {text.Replace('\n', '|')} {size} {(bold ? "bold" : "regular")}: ink box text {a} geometry {b}; |Δ| / ink = {share:P2}; pixels off by > 50 %: {big}");

        Assert.InRange(Math.Abs(a.Left - b.Left), 0, 1);
        Assert.InRange(Math.Abs(a.Top - b.Top), 0, 1);
        Assert.InRange(Math.Abs(a.Right - b.Right), 0, 1);
        Assert.InRange(Math.Abs(a.Bottom - b.Bottom), 0, 1);
    }

    /// <summary>The cost per frame at the export's sizes: the text alone (today), the outline built every frame, the
    /// outline's geometry cached per text and style. Reported; asserted only against an absurd bound.</summary>
    [Theory]
    [InlineData(Mode.B, 1080, 1920)]
    [InlineData(Mode.B, 1920, 1080)]
    public async Task The_cost_per_frame_is_measured(Mode mode, int width, int height)
    {
        var s = new Style("Маршрут 6B+\nСкалодром «Тест»", "Segoe UI", 48 * Math.Min(width, height) / 1080.0 * 1.5, true, 8);
        var plain = s with { OutlinePercent = 0 };

        var result = await Task.Run(() =>
        {
            using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            var pixels = new byte[width * height * 4];
            double Run(Func<Prepared> prepare, int frames)
            {
                var sw = Stopwatch.StartNew();
                for (var f = 0; f < frames; f++)
                {
                    var p = prepare();
                    using (var context = target.CreateDrawingContext())
                        Paint(context, width, height, s, p);
                    var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    try { target.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4); }
                    finally { handle.Free(); }
                }
                return sw.Elapsed.TotalMilliseconds / frames;
            }

            var cached = Prepare(s, mode);
            var cachedPlain = Prepare(plain);
            Run(() => cached, 10);                           // warm-up
            var wallOnly = Run(() => new Prepared { Text = cachedPlain.Text, Origin = new Point(1e6, 1e6) }, 60);
            var textOnly = Run(() => cachedPlain, 60);
            var rebuilt = Run(() => Prepare(s, mode), 60);
            var reused = Run(() => cached, 60);
            return (wallOnly, textOnly, rebuilt, reused);
        });

        output.WriteLine($"{mode} {width} × {height}, ms per frame (wall of {width / 37 * (height / 37)} rects + copy): " +
                         $"wall only {result.wallOnly:F2}; text without outline {result.textOnly:F2}; " +
                         $"outline rebuilt every frame {result.rebuilt:F2}; outline cached {result.reused:F2}");
        Assert.True(result.reused < 1000, "absurd");
    }
}
