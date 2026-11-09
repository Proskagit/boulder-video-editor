using System.Globalization;
using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Composition;

/// <summary>
/// D057 §3.9: the YouTube description's timestamps — one line per route, at the <b>left edge of its green area</b> (a
/// «Боулдер», anchored or free) in the exported video; never <c>T</c> or the orange's start; no end times.
/// <list type="bullet">
/// <item>The first line is always at <c>00:00</c>: <c>00:00 Intro</c> when no route starts at 0.</item>
/// <item>A line: <c>Трасса {номер} · {категория}</c> — a missing part drops with its « · » (<c>Трасса 7</c>,
/// <c>Трасса · 7A</c>, <c>Трасса</c>); Top adds <c> 🟢</c>, Attempt <c> 🔴</c>, no status nothing.</item>
/// <item>The parts of a split area are one route (its earliest part): an anchored area by its route, a free one by the part
/// whose template clock starts at 0. Different routes are different lines, even at the same time or with the same name.</item>
/// <item>With an exported range, the times are from its start; areas starting outside it are left out.</item>
/// <item><c>mm:ss</c>, from an hour <c>h:mm:ss</c> (whole seconds, rounded down); the lines by time, then by track and place.</item>
/// </list>
/// </summary>
public static class YouTubeTimestamps
{
    public const string RouteWord = "Трасса";
    public const string Intro = "Intro";
    public const string Top = "🟢";
    public const string Attempt = "🔴";

    public static IReadOnlyList<(MediaTime Time, string Title)> Lines(Sequence sequence, FrameRate rate, MediaTime? rangeStart = null, MediaTime? rangeEnd = null)
    {
        var start = rangeStart ?? MediaTime.Zero;
        var areas = sequence.VideoTracks
            .SelectMany((track, index) => track.Clips.OfType<TemplateClip>().Where(t => t.Kind == TemplateKind.Boulder).Select(t => (Template: t, Track: index)))
            .ToList();

        var routes = new List<(TemplateClip Template, int Track)>();
        foreach (var group in areas.Where(a => a.Template.AnchorRouteId is not null).GroupBy(a => a.Template.AnchorRouteId!.Value))
            routes.Add(group.OrderBy(a => a.Template.TimelineStart).First());
        routes.AddRange(areas.Where(a => a.Template.AnchorRouteId is null && a.Template.CounterStart == MediaTime.Zero));

        var lines = routes
            .Where(r => r.Template.TimelineStart >= start && (rangeEnd is not { } end || r.Template.TimelineStart < end))
            .OrderBy(r => r.Template.TimelineStart).ThenBy(r => r.Track)
            .Select(r => (Time: r.Template.TimelineStart - start, Title: Title(r.Template)))
            .ToList();
        if (lines.Count == 0 || lines[0].Time.Ticks >= TimeSpan.TicksPerSecond)
            lines.Insert(0, (MediaTime.Zero, Intro));
        return lines;
    }

    /// <summary>The text to copy: one line each, <c>{time} {title}</c>.</summary>
    public static string Text(Sequence sequence, FrameRate rate, MediaTime? rangeStart = null, MediaTime? rangeEnd = null)
    {
        var text = new StringBuilder();
        foreach (var (time, title) in Lines(sequence, rate, rangeStart, rangeEnd))
            text.Append(Format(time)).Append(' ').Append(title).Append('\n');
        return text.ToString().TrimEnd('\n');
    }

    /// <summary><c>mm:ss</c>, from an hour <c>h:mm:ss</c>, rounded down to the second.</summary>
    public static string Format(MediaTime time)
    {
        var seconds = Math.Max(0, time.Ticks / TimeSpan.TicksPerSecond);
        long h = seconds / 3600, m = seconds / 60 % 60, s = seconds % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", m, s);
    }

    /// <summary><c>Трасса {номер} · {категория}</c> and the status mark.</summary>
    public static string Title(TemplateClip area)
    {
        var (route, grade) = (area.Route.Trim(), area.Grade.Trim());
        var title = new StringBuilder(RouteWord);
        if (route.Length > 0) title.Append(' ').Append(route);
        if (grade.Length > 0) title.Append(" · ").Append(grade);
        if (area.Status == BoulderStatus.Top) title.Append(' ').Append(Top);
        else if (area.Status == BoulderStatus.Attempt) title.Append(' ').Append(Attempt);
        return title.ToString();
    }
}
