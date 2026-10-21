using System.Globalization;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// Draws a template's graphics (D045 §6) in canvas pixels, under the operation's transform already pushed — the look of the
/// Step 29.1 prototypes accepted by the product owner. Sizes are in 1080-units (× the canvas's short side / 1080). Only
/// immutable brushes and pens, <see cref="FormattedText"/> and rectangles / lines / ellipses — no geometry objects — so it
/// runs off the UI thread like the rest of <see cref="CompositionPainter"/>. Everything is a function of the operation.
/// </summary>
public static class TemplatePainter
{
    private const string Sport = "Bahnschrift";
    private const string Mono = "Consolas";
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static void Draw(DrawingContext context, TemplateDraw op)
    {
        switch (op.Kind)
        {
            case TemplateKind.Rec: Rec(context, op); break;
            case TemplateKind.Vhs: Vhs(context, op); break;
            case TemplateKind.Boulder: Boulder(context, op); break;
            case TemplateKind.Highlight: Highlight(context, op); break;
        }
    }

    private static double Unit(TemplateDraw op) => Math.Min(op.Canvas.Width, op.Canvas.Height) / 1080.0;

    private static IImmutableBrush Brush(string hex, double opacity = 1) => new ImmutableSolidColorBrush(Color.Parse(hex), opacity);

    private static FormattedText Text(string text, string family, FontWeight weight, double size, IBrush brush) =>
        new(text, Russian, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, brush);

    private static double OutCubic(double x) { x = Math.Clamp(x, 0, 1); return 1 - Math.Pow(1 - x, 3); }
    private static double InCubic(double x) { x = Math.Clamp(x, 0, 1); return x * x * x; }

    private static double OutBack(double x)
    {
        x = Math.Clamp(x, 0, 1);
        const double c = 1.4;
        return 1 + (c + 1) * Math.Pow(x - 1, 3) + c * Math.Pow(x - 1, 2);
    }

    /// <summary>
    /// The ink of laid-out text from the font's own glyph metrics, relative to the point <c>DrawText</c> is given: the left and
    /// right edges of the ink (the first glyph's left bearing, the last glyph's right bearing) and the band of the capitals —
    /// from the top of a capital (the cap height, the typeface's «H») to the baseline. Text is centred on that band
    /// (descenders hang below it, as in typesetting), never on the layout box, whose line gap and descent sit below the
    /// baseline and would lift the text (Phase 29 fix, 2026-10-21).
    /// </summary>
    internal readonly record struct TextInk(double Left, double Right, double CapTop, double Baseline)
    {
        public double CenterX => (Left + Right) / 2;
        public double CenterY => (CapTop + Baseline) / 2;

        /// <summary>Where to draw the text so that its ink is centred on <paramref name="centre"/>.</summary>
        public Point OriginFor(Point centre) => new(centre.X - CenterX, centre.Y - CenterY);
    }

    internal static TextInk InkOf(FormattedText text, string content, string family, FontWeight weight, double size)
    {
        var glyphs = new Typeface(family, FontStyle.Normal, weight).GlyphTypeface;
        var scale = size / glyphs.Metrics.DesignEmHeight;
        var cap = glyphs.TryGetGlyph('H', out var h) && glyphs.TryGetGlyphMetrics(h, out var hm) ? hm.YBearing * scale : 0.7 * size;
        double lead = 0, trail = 0;
        // The callers lay out trimmed text: the first character is the first glyph drawn.
        var visible = content;
        if (visible.Length > 0 && glyphs.TryGetGlyph(visible[0], out var g) && glyphs.TryGetGlyphMetrics(g, out var m))
            lead = m.XBearing * scale;
        if (visible.Length > 0 && glyphs.TryGetGlyph(visible[^1], out var g2) && glyphs.TryGetGlyphMetrics(g2, out var m2))
            trail = (glyphs.GetGlyphAdvance(g2) - m2.XBearing - m2.Width) * scale;
        return new TextInk(lead, text.Width - trail, text.Baseline - cap, text.Baseline);
    }

    /// <summary>Draws <paramref name="text"/> with its ink centred on <paramref name="centre"/>.</summary>
    private static void Centred(DrawingContext context, FormattedText text, TextInk ink, Point centre) =>
        context.DrawText(text, ink.OriginFor(centre));

    /// <summary>Text with a soft shadow offset by <paramref name="offset"/> (down-right).</summary>
    private static void Shadowed(DrawingContext context, FormattedText text, FormattedText shadow, Point at, double offset)
    {
        context.DrawText(shadow, new Point(at.X + offset, at.Y + offset));
        context.DrawText(text, at);
    }

    // --- REC / Camcorder -----------------------------------------------------------------------------------------------

    /// <summary>Corner marks, a blinking red dot with REC (on 0.6 s of every second of the counter), a battery, the
    /// timecode bottom left, the date bottom right (when given); fading in and out over the clip's fades.</summary>
    private static void Rec(DrawingContext context, TemplateDraw op)
    {
        var (w, h, u) = (op.Canvas.Width, op.Canvas.Height, Unit(op));
        var clock = op.Clock;
        using var fade = context.PushOpacity(Math.Min(clock.In, clock.Out));
        IImmutableBrush white = Brushes.White;
        var shadow = Brush("#000000", 0.45);

        var inset = 54 * u;
        var arm = 86 * u;
        foreach (var (brush, o) in new[] { (shadow, 2 * u), (white, 0.0) })
        {
            var pen = new ImmutablePen(brush, 5 * u, lineCap: PenLineCap.Square);
            void Corner(double x, double y, double dx, double dy)
            {
                context.DrawLine(pen, new Point(x + o, y + o), new Point(x + dx * arm + o, y + o));
                context.DrawLine(pen, new Point(x + o, y + o), new Point(x + o, y + dy * arm + o));
            }
            Corner(inset, inset, 1, 1);
            Corner(w - inset, inset, -1, 1);
            Corner(inset, h - inset, 1, -1);
            Corner(w - inset, h - inset, -1, -1);
        }

        var pad = inset + 34 * u;
        var rec = Text("REC", Sport, FontWeight.SemiBold, 44 * u, white);
        var dot = 13 * u;
        var cy = pad + rec.Height / 2;
        if (clock.Counter % 1.0 < 0.6)
        {
            context.DrawEllipse(shadow, null, new Point(pad + dot + 2 * u, cy + 2 * u), dot, dot);
            context.DrawEllipse(Brush("#FF2D2D"), null, new Point(pad + dot, cy), dot, dot);
        }
        Shadowed(context, rec, Text("REC", Sport, FontWeight.SemiBold, 44 * u, shadow), new Point(pad + 2 * dot + 14 * u, pad), 2 * u);

        var (bw, bh) = (64 * u, 30 * u);
        var (bx, by) = (w - pad - bw, cy - bh / 2);
        context.DrawRectangle(null, new ImmutablePen(white, 3 * u), new Rect(bx, by, bw, bh), 4 * u, 4 * u);
        context.DrawRectangle(white, null, new Rect(bx + bw, by + bh * 0.3, 5 * u, bh * 0.4));
        for (var i = 0; i < 3; i++) context.DrawRectangle(white, null, new Rect(bx + 6 * u + i * 19 * u, by + 6 * u, 14 * u, bh - 12 * u));

        var seconds = Math.Max(0, clock.Counter);
        var tc = TimeSpan.FromSeconds(Math.Floor(seconds));
        var frames = (int)((seconds - Math.Floor(seconds)) * 30);
        var code = $"{(int)tc.TotalHours:00}:{tc.Minutes:00}:{tc.Seconds:00}:{frames:00}";
        var size = 38 * u;
        var code1 = Text(code, Sport, FontWeight.Normal, size, white);
        var bottom = h - pad - code1.Height;
        Shadowed(context, code1, Text(code, Sport, FontWeight.Normal, size, shadow), new Point(pad, bottom), 2 * u);
        if (op.Fields.Date.Length > 0)
        {
            var date = Text(op.Fields.Date, Sport, FontWeight.Normal, size, white);
            Shadowed(context, date, Text(op.Fields.Date, Sport, FontWeight.Normal, size, shadow), new Point(w - pad - date.Width, bottom), 2 * u);
        }
    }

    // --- VHS (the treatment is TemplateTreatment.Vhs) ------------------------------------------------------------------

    /// <summary>The deck's on-screen display: PLAY ▶ for the first 2.5 s of the counter, the tape counter bottom left, the date
    /// bottom right (when given), in a monospaced face with a soft shadow.</summary>
    private static void Vhs(DrawingContext context, TemplateDraw op)
    {
        var (w, h, u) = (op.Canvas.Width, op.Canvas.Height, Unit(op));
        var white = Brush("#F4F4F4", 0.92);
        var shadow = Brush("#000000", 0.55);
        var size = 46 * u;
        var pad = 70 * u;
        void Osd(string text, double x, double y, bool right = false)
        {
            var t = Text(text, Mono, FontWeight.Bold, size, white);
            Shadowed(context, t, Text(text, Mono, FontWeight.Bold, size, shadow), new Point(right ? x - t.Width : x, y), 3 * u);
        }

        if (op.Clock.Counter < 2.5)
        {
            Osd("PLAY", pad, pad);
            // ▶ as a triangle of lines (the face's glyph would vary; no geometry objects off the UI thread).
            var height = 30 * u;
            var x = pad + Text("PLAY ", Mono, FontWeight.Bold, size, white).WidthIncludingTrailingWhitespace;
            var y = pad + 12 * u;
            foreach (var (brush, o) in new[] { (shadow, 3 * u), (white, 0.0) })
                for (var i = 0.0; i <= height; i += 1)
                {
                    // The row's width: 0.9 × the height at the apex row, 0 at the top and the bottom.
                    var width = 1.8 * Math.Min(i, height - i);
                    context.DrawRectangle(brush, null, new Rect(x + o, y + i + o, Math.Max(0, width) + 1, 1));
                }
        }

        var counter = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, op.Clock.Counter)));
        var bottom = h - pad - 54 * u;
        Osd($"{(int)counter.TotalHours}:{counter.Minutes:00}:{counter.Seconds:00}", pad, bottom);
        if (op.Fields.Date.Length > 0) Osd(op.Fields.Date, w - pad, bottom, right: true);
    }

    // --- «Боулдер» -----------------------------------------------------------------------------------------------------

    /// <summary>A laid-out text with its ink (<see cref="InkOf"/>).</summary>
    private readonly record struct Inked(FormattedText Text, TextInk Ink)
    {
        public double InkWidth => Ink.Right - Ink.Left;
    }

    private static Inked Lay(string content, FontWeight weight, double size, IImmutableBrush brush)
    {
        var text = Text(content, Sport, weight, size, brush);
        return new Inked(text, InkOf(text, content, Sport, weight, size));
    }

    /// <summary>
    /// «Боулдер» (the product owner's corrections of 2026-10-21): two separate blocks bottom left (above the phone zone on a
    /// vertical canvas). The route block — navy, the route's colour as its left edge, «ТРАССА 7 · 7A» in white (each part
    /// only when given) — slides in and fades over the clip's fades. The status block beside it, after a gap — TOP yellow
    /// with navy text, ПОПЫТКА muted blue with white text — appears at the moment the user set (<see cref="StatusAppear"/>)
    /// with a short fade and a small slide (no overshoot) and stays to the end. Every text is centred on its block by its ink
    /// (<see cref="InkOf"/>).
    /// </summary>
    private static void Boulder(DrawingContext context, TemplateDraw op)
    {
        var (w, h, u) = (op.Canvas.Width, op.Canvas.Height, Unit(op));
        var clock = op.Clock;
        var appear = OutCubic(clock.In);
        var k = Math.Min(appear, OutCubic(clock.Out));
        if (k <= 0) return;

        var fields = op.Fields;
        var vertical = h > w;
        var height = 62 * u;
        var x0 = (vertical ? 48 : 60) * u;
        var y0 = h - (vertical ? 260 : 64) * u - height;
        var centreY = y0 + height / 2;
        var padX = 20 * u;
        var gap = 10 * u;
        var edge = 6 * u;
        var grey = Brush("#A9BACB");

        var parts = new List<Inked>();
        var route = fields.Route.Trim();
        var grade = fields.Grade.Trim();
        if (route.Length > 0)
        {
            parts.Add(Lay(TemplateRules.RouteWord, FontWeight.SemiBold, 20 * u, grey));
            parts.Add(Lay(route, FontWeight.Bold, 36 * u, Brushes.White));
        }
        if (grade.Length > 0)
        {
            if (route.Length > 0) parts.Add(Lay("·", FontWeight.Bold, 36 * u, grey));
            parts.Add(Lay(grade, FontWeight.Bold, 36 * u, Brushes.White));
        }
        var textWidth = parts.Sum(p => p.InkWidth) + Math.Max(0, parts.Count - 1) * gap;
        var plate = new Rect(x0, y0, edge + padX + textWidth + padX, height);

        using var slide = context.PushTransform(Matrix.CreateTranslation(-(1 - appear) * 40 * u, 0));
        using var alpha = context.PushOpacity(k);
        if (parts.Count > 0)
        {
            context.DrawRectangle(Brush(TemplateRules.PlateHex, 0.92), null, plate);
            context.DrawRectangle(Brush(fields.AccentHex), null, new Rect(plate.X, plate.Y, edge, plate.Height));
            var x = plate.X + edge + padX;
            foreach (var part in parts)
            {
                Centred(context, part.Text, part.Ink, new Point(x + part.InkWidth / 2, centreY));
                x += part.InkWidth + gap;
            }
        }

        if (fields.Status == BoulderStatus.None) return;
        var shown = StatusAppear(clock);
        if (shown <= 0) return;
        var top = fields.Status == BoulderStatus.Top;
        var label = Lay(top ? TemplateRules.TopWord : TemplateRules.AttemptWord, FontWeight.Bold, 32 * u,
            top ? Brush(TemplateRules.PlateHex) : Brushes.White);
        var block = new Rect(parts.Count > 0 ? plate.Right + 12 * u : x0, y0, label.InkWidth + 2 * padX, height);
        using var fade = context.PushOpacity(shown);
        using var rise = context.PushTransform(Matrix.CreateTranslation(-(1 - shown) * 16 * u, 0));
        context.DrawRectangle(Brush(top ? TemplateRules.TopHex : TemplateRules.AttemptHex, top ? 1 : 0.95), null, block);
        Centred(context, label.Text, label.Ink, block.Center);
    }

    /// <summary>0 → 1 over the status block's appearance: <see cref="TemplateRules.StatusAppearSeconds"/> from the status
    /// moment the user set (<see cref="TemplateClock.SinceStatus"/>, D045 §17); 0 before it, 1 after it to the end.</summary>
    private static double StatusAppear(TemplateClock clock) =>
        clock.SinceStatus < 0 ? 0 : OutCubic(clock.SinceStatus / TemplateRules.StatusAppearSeconds);

    // --- «Хайлайт» -----------------------------------------------------------------------------------------------------

    /// <summary>A slanted yellow bar wipes in from the left with a light edge ahead of it, the upper-case title rises inside it,
    /// the subtitle (when given) fades in below on a dark slanted strip; at the end the bar wipes out to the right. The bar
    /// is a rectangle under a skew transform (no geometry); the title is drawn unskewed inside its clip. The title and the
    /// subtitle are centred on their bands by their ink (<see cref="InkOf"/>): the bands are sized from the cap height, not
    /// from the layout box (Phase 29 fix, 2026-10-21).</summary>
    private static void Highlight(DrawingContext context, TemplateDraw op)
    {
        var (w, h, u) = (op.Canvas.Width, op.Canvas.Height, Unit(op));
        var clock = op.Clock;
        var vertical = h > w;
        var ink = Brush("#101010");
        var title = op.Fields.Title.Trim().ToUpper(Russian);
        var size = (vertical ? 92 : 104) * u;
        var ft = Lay(title, FontWeight.Bold, size, ink);
        if (ft.InkWidth > w - 160 * u && ft.InkWidth > 0)
        {
            size *= (w - 160 * u) / ft.InkWidth;
            ft = Lay(title, FontWeight.Bold, size, ink);
        }
        const double skew = 0.21;   // ≈ 12°
        var padX = 36 * u;
        var capHeight = ft.Ink.Baseline - ft.Ink.CapTop;
        var barH = capHeight + 2 * 0.34 * capHeight;    // a third of the cap height above and below it
        var barW = ft.InkWidth + 2 * padX;
        var x0 = vertical ? (w - barW) / 2 : 90 * u;
        var y0 = (vertical ? 0.60 : 0.62) * h;

        var grow = clock.Intro > 0 ? OutCubic(clock.Elapsed / 0.28) : 1;
        var shrink = clock.Outro > 0 ? InCubic((0.3 - clock.Remaining) / 0.3) : 0;
        if (grow <= 0 || shrink >= 1) return;
        var left = x0 + barW * shrink;
        var right = x0 + barW * grow;

        // x' = x − (y − y0)·skew: the bar leans right as it rises; at the bar's middle row its centre is shifted by that much.
        var skewMatrix = new Matrix(1, 0, -skew, 1, skew * y0, 0);
        var unskew = new Matrix(1, 0, skew, 1, -skew * y0, 0);
        var centre = new Point(x0 + barW / 2 - barH / 2 * skew, y0 + barH / 2);
        using (context.PushTransform(skewMatrix))
        {
            context.DrawRectangle(Brush("#000000", 0.35), null, new Rect(left + 8 * u, y0 + 8 * u, right - left, barH));
            var bar = new Rect(left, y0, right - left, barH);
            context.DrawRectangle(Brush(TemplateRules.HighlightAccentHex), null, bar);
            if (grow < 1) context.DrawRectangle(Brushes.White, null, new Rect(right, y0, 10 * u, barH));
            using (context.PushClip(bar))
            using (context.PushTransform(unskew))
            {
                var rise = clock.Intro > 0 ? (1 - OutCubic((clock.Elapsed - 0.12) / 0.3)) * barH * 0.8 : 0;
                Centred(context, ft.Text, ft.Ink, new Point(centre.X, centre.Y + rise));
            }
        }

        var subtitle = op.Fields.Subtitle.Trim();
        if (subtitle.Length == 0) return;
        var sub = Lay(subtitle, FontWeight.SemiBold, 40 * u, Brushes.White);
        var show = (clock.Intro > 0 ? OutCubic((clock.Elapsed - 0.3) / 0.25) : 1) * (1 - shrink);
        if (show <= 0) return;
        var subCap = sub.Ink.Baseline - sub.Ink.CapTop;
        var sy = y0 + barH + 8 * u;
        var sh = subCap + 2 * 0.45 * subCap;
        var sw = sub.InkWidth + 36 * u;
        var sx = x0 + 40 * u;
        using var fade = context.PushOpacity(show);
        using (context.PushTransform(new Matrix(1, 0, -skew, 1, skew * sy, 0)))
            context.DrawRectangle(Brush("#101010", 0.85), null, new Rect(sx, sy, sw, sh));
        Centred(context, sub.Text, sub.Ink, new Point(sx + sw / 2 - sh / 2 * skew, sy + sh / 2));
    }
}
