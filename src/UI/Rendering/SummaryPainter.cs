using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using static AiVideoEditor.UI.Rendering.TemplatePainter;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// D073: «Итоги» — the summary board after «Боулдер»'s look (navy blocks, the yellow accent, the route's colour as a row's left
/// edge, TOP / ПОПЫТКА chips, the FLASH block): a header (the title upper-case, the date), a table with column captions and a
/// row per route, the totals as a row of cards at the bottom (its own, lighter background). Over the dimmed video, or on its
/// own full-frame background (<see cref="TemplateProperties.OwnBackground"/>). Sizes in 1080-units of the canvas's short side,
/// the whole board scaled down to fit a narrow (portrait) canvas. Everything is a function of the operation (its clock and
/// board), so the Preview and the export draw the same; rectangles, lines, text and clips only, as the rest of the painter.
/// </summary>
internal static class SummaryPainter
{
    /// <summary>The rows visible at once (Q5); the next one scrolls the list up under the header.</summary>
    public const int VisibleRows = 10;

    /// <summary>The rows' timing (seconds): the first row's start, the slowest and the fastest pace, one row's entrance, the
    /// share of the length by which the last row is in.</summary>
    public const double FirstRow = 0.45, SlowestPace = 0.6, FastestPace = 0.25, RowIn = 0.3, LastRowShare = 0.7;

    /// <summary>The board's width in 1080-units: wide on a landscape canvas, narrower (denser columns) on a portrait one.</summary>
    private const double LandscapeWidth = 1560, PortraitWidth = 1180;

    private const double HeaderHeight = 110, Gap = 22, CaptionHeight = 44, RowHeight = 56, RowGap = 6, CardHeight = 118;

    private const string Grey = "#A9BACB", RowA = "#0F1F33", RowB = "#16283F", Card = "#1B3150", NoResult = "#2A3B52";

    /// <summary>The seconds between two rows' entrances for <paramref name="rows"/> rows in a template of
    /// <paramref name="length"/> seconds: the last row in by 70 % of the length, within 0.25 … 0.6 s.</summary>
    public static double Pace(int rows, double length) =>
        rows <= 0 ? SlowestPace : Math.Clamp((LastRowShare * length - FirstRow) / rows, FastestPace, SlowestPace);

    /// <summary>The rows scrolled up at <paramref name="t"/> seconds (fractional while a row comes in): one per row past
    /// <see cref="VisibleRows"/> that has started coming in.</summary>
    public static double ScrollAt(double t, int rows, double pace)
    {
        var offset = 0.0;
        for (var i = VisibleRows; i < rows; i++) offset += OutCubic((t - (FirstRow + i * pace)) / RowIn);
        return offset;
    }

    public static void Draw(DrawingContext context, TemplateDraw op)
    {
        var board = op.Summary ?? SummaryBoard.Empty;
        var clock = op.Clock;
        var k = Math.Min(OutCubic(clock.In), OutCubic(clock.Out));
        if (k <= 0) return;
        var (w, h) = (op.Canvas.Width, op.Canvas.Height);
        var fields = op.Fields;
        var words = TemplateWords.Of(fields.Language);
        var t = clock.Elapsed;
        var length = clock.Elapsed + clock.Remaining;

        using var alpha = context.PushOpacity(k);
        if (fields.OwnBackground) Background(context, w, h, Unit(w, h));
        else context.DrawRectangle(Brush("#050A12", 0.45), null, new Rect(0, 0, w, h));

        var showsHeart = board.ShowsHeartRate;
        var u0 = Unit(w, h);
        var boardWidth = w >= h ? LandscapeWidth : PortraitWidth;
        var v = u0 * Math.Min(1, 0.92 * w / (boardWidth * u0));    // the board's unit: 1080-units, scaled to fit a narrow canvas
        var width = boardWidth * v;
        var x0 = (w - width) / 2;
        var visible = Math.Min(Math.Max(board.Rows.Length, 1), VisibleRows);
        var step = (RowHeight + RowGap) * v;
        var tableHeight = (CaptionHeight + 12) * v + visible * step + 12 * v;
        var total = HeaderHeight * v + Gap * v + tableHeight + Gap * v + CardHeight * v;
        var y0 = Math.Max(24 * v, (h - total) / 2);

        // The table: the panel unrolls downwards under the header, then the rows come in one by one.
        var tableTop = y0 + (HeaderHeight + Gap) * v;
        var unroll = OutCubic((t - 0.3) / 0.35);
        if (unroll > 0)
        {
            var panel = new Rect(x0, tableTop, width, tableHeight * unroll);
            context.DrawRectangle(Brush(TemplateRules.PlateHex, fields.OwnBackground ? 0.75 : 0.9), null, panel, 14 * v, 14 * v);
            using var panelClip = context.PushClip(panel);
            var columns = Columns(x0 + 24 * v, width - 48 * v, showsHeart);
            var captions = Captions(words, showsHeart);
            for (var c = 0; c < columns.Length; c++)
            {
                var caption = Lay(captions[c], FontWeight.SemiBold, 22 * v, Brush(Grey));
                Centred(context, caption.Text, caption.Ink, new Point(columns[c].Centre, tableTop + (12 + CaptionHeight / 2) * v));
            }

            var rowsTop = tableTop + (CaptionHeight + 12) * v;
            var pace = Pace(board.Rows.Length, length);
            var scroll = ScrollAt(t, board.Rows.Length, pace);
            using var rowsClip = context.PushClip(new Rect(x0, rowsTop, width, visible * step));
            for (var i = 0; i < board.Rows.Length; i++)
            {
                var appear = OutCubic((t - (FirstRow + i * pace)) / RowIn);
                if (appear <= 0) break;
                var y = rowsTop + (i - scroll) * step;
                if (y + step < rowsTop || y > rowsTop + visible * step) continue;
                using var fade = context.PushOpacity(appear);
                using var slide = context.PushTransform(Matrix.CreateTranslation(-40 * v * (1 - appear), 0));
                Row(context, board.Rows[i], i, new Rect(x0 + 12 * v, y, width - 24 * v, RowHeight * v), columns, words, showsHeart, v, t - (FirstRow + i * pace), t);
            }
        }

        // The header over the rows that scroll under it.
        var header = OutCubic(t / 0.45);
        using (context.PushOpacity(header))
        using (context.PushTransform(Matrix.CreateTranslation(0, -30 * v * (1 - header))))
            Header(context, fields, new Rect(x0, y0, width, HeaderHeight * v), v);

        // The totals after the last row.
        var totalsAt = FirstRow + board.Rows.Length * Pace(board.Rows.Length, length);
        var totals = OutCubic((t - totalsAt) / 0.35);
        if (totals > 0)
        {
            using var fade = context.PushOpacity(totals);
            using var rise = context.PushTransform(Matrix.CreateTranslation(0, 24 * v * (1 - totals)));
            Totals(context, board.Totals, words, showsHeart, new Rect(x0, tableTop + tableHeight + Gap * v, width, CardHeight * v), v);
        }
    }

    private static double Unit(double w, double h) => Math.Min(w, h) / 1080.0;

    /// <summary>The own background: navy over the whole frame, a lighter slanted band and three yellow stripes.</summary>
    private static void Background(DrawingContext context, double w, double h, double u)
    {
        context.DrawRectangle(Brush(TemplateRules.PlateHex), null, new Rect(0, 0, w, h));
        using (context.PushTransform(new Matrix(1, 0, -0.36, 1, 0, 0)))
        {
            context.DrawRectangle(Brush("#132842"), null, new Rect(w * 0.72, 0, w * 0.42, h));
            for (var i = 0; i < 3; i++)
                context.DrawRectangle(Brush(TemplateRules.TopHex, 0.9), null, new Rect(w - 210 * u + i * 46 * u + h * 0.36, h - 120 * u, 26 * u, 70 * u));
        }
    }

    private static void Header(DrawingContext context, TemplateProperties fields, Rect area, double v)
    {
        context.DrawRectangle(Brush(TemplateRules.TopHex), null, new Rect(area.X, area.Y + 10 * v, 10 * v, area.Height - 20 * v));
        var title = Lay(fields.Title.Trim().ToUpper(CultureInfo.GetCultureInfo("ru-RU")), FontWeight.Bold, 64 * v, Brushes.White);
        var date = fields.Subtitle.Trim();
        var titleY = date.Length > 0 ? area.Y + 42 * v : area.Center.Y;
        var x = area.X + 34 * v;
        Centred(context, title.Text, title.Ink, new Point(x + title.InkWidth / 2, titleY));
        if (date.Length > 0)
        {
            var line = Lay(date, FontWeight.SemiBold, 26 * v, Brush(Grey));
            Centred(context, line.Text, line.Ink, new Point(x + line.InkWidth / 2, area.Y + 92 * v));
        }

        // A yellow line from the title to the right edge, ending in three slanted stripes.
        var lineStart = x + title.InkWidth + 40 * v;
        var stripes = area.Right - 120 * v;
        if (stripes - lineStart > 40 * v)
            context.DrawRectangle(Brush(TemplateRules.TopHex), null, new Rect(lineStart, titleY + 30 * v, stripes - lineStart, 3 * v));
        using (context.PushTransform(Matrix.CreateTranslation(-stripes, -titleY) * new Matrix(1, 0, -0.45, 1, 0, 0) * Matrix.CreateTranslation(stripes, titleY)))
            for (var i = 0; i < 3; i++)
                context.DrawRectangle(Brush(TemplateRules.TopHex), null, new Rect(stripes + 10 * v + i * 36 * v, titleY - 6 * v, 22 * v, 40 * v));
    }

    /// <summary>A column: its centre and width.</summary>
    private readonly record struct Column(double Centre, double Width);

    private static Column[] Columns(double x, double width, bool heart)
    {
        double[] shares = heart ? [0.06, 0.14, 0.18, 0.13, 0.13, 0.12, 0.12, 0.12] : [0.08, 0.19, 0.22, 0.17, 0.17, 0.17];
        var columns = new Column[shares.Length];
        var at = x;
        for (var i = 0; i < shares.Length; i++)
        {
            columns[i] = new Column(at + shares[i] * width / 2, shares[i] * width);
            at += shares[i] * width;
        }
        return columns;
    }

    private static string[] Captions(TemplateWords words, bool heart) => heart
        ? [words.Number, words.RouteColumn, words.Result, words.AttemptsColumn, words.Time, words.Grade, words.AverageHr, words.MaxHr]
        : [words.Number, words.RouteColumn, words.Result, words.AttemptsColumn, words.Time, words.Grade];

    private static void Row(DrawingContext context, SummaryRow row, int index, Rect area, Column[] columns, TemplateWords words, bool heart,
        double v, double since, double t)
    {
        context.DrawRectangle(Brush(index % 2 == 0 ? RowB : RowA, 0.95), null, area, 6 * v, 6 * v);
        context.DrawRectangle(Brush(row.AccentHex), null, new Rect(area.X, area.Y, 8 * v, area.Height));
        var y = area.Center.Y;
        Cell(context, (index + 1).ToString(CultureInfo.InvariantCulture), columns[0], y, v, Grey);
        Cell(context, Dash(row.Route), columns[1], y, v);
        Chip(context, row.Result, words, new Point(columns[2].Centre, y), v, since, t);
        Cell(context, row.Attempts.ToString(CultureInfo.InvariantCulture), columns[3], y, v);
        Cell(context, row.ClimbTime is { } time ? Format(time) : "—", columns[4], y, v);
        Cell(context, Dash(row.Grade), columns[5], y, v);
        if (!heart) return;
        Cell(context, Number(row.AverageHeartRate), columns[6], y, v);
        Cell(context, Number(row.MaxHeartRate), columns[7], y, v);
    }

    private static void Cell(DrawingContext context, string text, Column column, double y, double v, string hex = "#FFFFFF")
    {
        var laid = Lay(text, FontWeight.Bold, 32 * v, Brush(hex));
        Centred(context, laid.Text, laid.Ink, new Point(column.Centre, y));
    }

    /// <summary>The result chip: TOP yellow, ПОПЫТКА blue, FLASH the FLASH block, none a grey «—».</summary>
    private static void Chip(DrawingContext context, BoulderStatus result, TemplateWords words, Point centre, double v, double since, double t)
    {
        var flash = result == BoulderStatus.Flash;
        var (fill, ink) = result switch
        {
            BoulderStatus.Top => (TemplateRules.TopHex, TemplateRules.PlateHex),
            BoulderStatus.Attempt => (TemplateRules.AttemptHex, "#FFFFFF"),
            BoulderStatus.Flash => (TemplateRules.FlashHex, TemplateRules.PlateHex),
            _ => (NoResult, Grey)
        };
        var label = Lay(result == BoulderStatus.None ? "—" : words.StatusWord(result), FontWeight.Bold, 26 * v, Brush(ink));
        var chipWidth = Math.Max(160 * v, label.InkWidth + 36 * v + (flash ? 34 * v : 0));
        var chip = new Rect(centre.X - chipWidth / 2, centre.Y - 21 * v, chipWidth, 42 * v);
        if (flash)
        {
            FlashBlock(context, chip, label, v, since, t);
            return;
        }
        context.DrawRectangle(Brush(fill), null, chip, 6 * v, 6 * v);
        Centred(context, label.Text, label.Ink, chip.Center);
    }

    private static void Totals(DrawingContext context, SummaryTotals totals, TemplateWords words, bool heart, Rect area, double v)
    {
        var cards = new List<(string Label, string? Lead, string Value, string? Unit, bool Bolt, bool Accent)>
        {
            (words.Tops, totals.Tops.ToString(CultureInfo.InvariantCulture), $"/ {totals.Routes}", null, false, false),
            (words.Flashes, null, totals.Flashes.ToString(CultureInfo.InvariantCulture), null, true, false)
        };
        if (heart)
        {
            cards.Add((words.AverageHr, null, Number(totals.AverageHeartRate), totals.AverageHeartRate is null ? null : words.Bpm, false, false));
            cards.Add((words.MaxHr, null, Number(totals.MaxHeartRate), totals.MaxHeartRate is null ? null : words.Bpm, false, false));
        }
        cards.Add((words.OnRoutes, null, totals.OnRoutes is { } time ? Format(time) : "—", null, false, true));

        var gap = 16 * v;
        var cardWidth = (area.Width - gap * (cards.Count - 1)) / cards.Count;
        for (var i = 0; i < cards.Count; i++)
        {
            var (label, lead, value, unit, bolt, accent) = cards[i];
            var card = new Rect(area.X + i * (cardWidth + gap), area.Y, cardWidth, area.Height);
            context.DrawRectangle(Brush(Card, 0.96), null, card, 12 * v, 12 * v);
            if (accent)
                context.DrawRectangle(null, new ImmutablePen(Brush(TemplateRules.TopHex), 3 * v), card.Deflate(1.5 * v), 12 * v, 12 * v);
            var caption = Lay(label, FontWeight.SemiBold, 22 * v, Brush(Grey));
            Centred(context, caption.Text, caption.Ink, new Point(card.X + 22 * v + caption.InkWidth / 2, card.Y + 30 * v));

            // The figure: [bolt] [lead in yellow] value [unit, smaller] — shrunk to the card's width when it would not fit.
            double Width(double s) => (bolt ? 42 * s : 0) + (lead is null ? 0 : Lay(lead, FontWeight.Bold, 50 * s, Brushes.White).InkWidth + 12 * s) +
                                      Lay(value, FontWeight.Bold, 50 * s, Brushes.White).InkWidth +
                                      (unit is null ? 0 : 8 * s + Lay(unit, FontWeight.SemiBold, 24 * s, Brushes.White).InkWidth);
            var room = card.Width - 44 * v;
            var f = Math.Min(v, v * room / Math.Max(1e-6, Width(v)));
            var x = card.X + 22 * v;
            var y = card.Y + 80 * v;
            if (bolt)
            {
                Bolt(context, new Rect(x, y - 24 * f, 30 * f, 46 * f), TemplateRules.TopHex, TemplateRules.PlateHex, f);
                x += 42 * f;
            }
            if (lead is not null)
            {
                var laid = Lay(lead, FontWeight.Bold, 50 * f, Brush(TemplateRules.TopHex));
                Centred(context, laid.Text, laid.Ink, new Point(x + laid.InkWidth / 2, y));
                x += laid.InkWidth + 12 * f;
            }
            var big = Lay(value, FontWeight.Bold, 50 * f, Brushes.White);
            Centred(context, big.Text, big.Ink, new Point(x + big.InkWidth / 2, y));
            if (unit is null) continue;
            x += big.InkWidth + 8 * f;
            var small = Lay(unit, FontWeight.SemiBold, 24 * f, Brush("#D6E0EA"));
            // On the figure's baseline.
            context.DrawText(small.Text, new Point(x - small.Ink.Left, y + (big.Ink.Baseline - big.Ink.CapTop) / 2 - small.Ink.Baseline));
        }
    }

    private static string Dash(string text) => text.Trim().Length > 0 ? text.Trim() : "—";

    private static string Number(int? value) => value is { } n ? n.ToString(CultureInfo.InvariantCulture) : "—";

    /// <summary>A climb time: <c>m:ss</c>, from an hour <c>h:mm:ss</c>, rounded to the nearest second.</summary>
    public static string Format(MediaTime time)
    {
        var seconds = (time.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond;   // to the nearest second, on the ticks
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }
}
