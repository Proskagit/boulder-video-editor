using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;

namespace AiVideoEditor.Core.Composition;

/// <summary>One row of «Итоги» (D073 §2): a route — the attempts of one boulder by its number and grade — with the chosen
/// attempt's climb time and heart rate. Null values are drawn as «—».</summary>
public sealed record SummaryRow(string Route, string Grade, string AccentHex, BoulderStatus Result, int Attempts, MediaTime? ClimbTime,
    int? AverageHeartRate, int? MaxHeartRate);

/// <summary>The totals of «Итоги» (D073 §2, Q8): the rows, the tops (flashes included), the flashes, the climb time of every
/// attempt, the heart rate over every attempt's climb.</summary>
/// <summary>D074 amendment 1: a row of «Итоги» as its Inspector edits it — the route, its result, the attempts shown, whether a
/// plate states them, and the route's plates (each attempt's first part, in order; the last one takes a stated value).</summary>
public sealed record SummaryAttemptRow(string Route, string Grade, BoulderStatus Result, int Attempts, int Counted, bool Stated,
    IReadOnlyList<Guid> Plates);

public sealed record SummaryTotals(int Routes, int Tops, int Flashes, MediaTime? OnRoutes, int? AverageHeartRate, int? MaxHeartRate);

/// <summary>
/// D073: what «Итоги» shows — computed from the project when the playback snapshot is built (a pure function of the sequence
/// and the training, so the Preview and the export draw the same); nothing of it is stored in the template.
/// </summary>
public sealed record SummaryBoard(ImmutableArray<SummaryRow> Rows, SummaryTotals Totals)
{
    public static readonly SummaryBoard Empty = new(ImmutableArray<SummaryRow>.Empty, new SummaryTotals(0, 0, 0, null, null, null));

    /// <summary>Q7: the heart-rate columns are drawn only when a row has a heart rate.</summary>
    public bool ShowsHeartRate => Rows.Any(r => r.AverageHeartRate is not null || r.MaxHeartRate is not null);

    public bool Equals(SummaryBoard? other) => other is not null && Rows.SequenceEqual(other.Rows) && Totals == other.Totals;

    public override int GetHashCode() => HashCode.Combine(Rows.Length, Totals);

    /// <summary>
    /// The board of the «Итоги» starting at <paramref name="summaryStart"/>: the routes (the «Боулдер» areas) that started
    /// before it and not before the previous «Итоги»'s start, in the order they started; the attempts of one boulder (the same
    /// non-empty number and the same grade, trimmed, case ignored) one row. <paramref name="bindingOf"/> gives the heart-rate
    /// binding of a video asset (null: none).
    /// </summary>
    public static SummaryBoard Of(Sequence sequence, MediaTime summaryStart, FrameRate rate, Func<Guid, HeartRateBinding?> bindingOf)
    {
        var attempts = Attempts(sequence, summaryStart, rate, bindingOf);
        var rows = new List<SummaryRow>();
        foreach (var list in attempts.GroupBy(a => a.Key).Select(g => g.ToList()))
        {
            var (chosen, result, count) = Row(list);
            var bpm = chosen.HeartRate;
            rows.Add(new SummaryRow(chosen.Route, chosen.Grade, chosen.Accent, result, count, chosen.Time,
                bpm.Count > 0 ? (int)Math.Round(bpm.Average(), MidpointRounding.AwayFromZero) : null, bpm.Count > 0 ? bpm.Max() : null));
        }

        var times = attempts.Where(a => a.Time is not null).Select(a => a.Time!.Value.Ticks).ToList();
        var all = attempts.SelectMany(a => a.HeartRate).ToList();
        var totals = new SummaryTotals(rows.Count,
            rows.Count(r => r.Result is BoulderStatus.Top or BoulderStatus.Flash),
            rows.Count(r => r.Result == BoulderStatus.Flash),
            times.Count > 0 ? new MediaTime(times.Sum()) : null,
            all.Count > 0 ? (int)Math.Round(all.Average(), MidpointRounding.AwayFromZero) : null,
            all.Count > 0 ? all.Max() : null);
        return new SummaryBoard(rows.ToImmutableArray(), totals);
    }

    /// <summary>D074 amendment 1: the rows of the «Итоги» at <paramref name="summaryStart"/> for its Inspector — the same routes
    /// and attempts as <see cref="Of"/>, with the plates a stated number of attempts is written to.</summary>
    public static IReadOnlyList<SummaryAttemptRow> AttemptRows(Sequence sequence, MediaTime summaryStart, FrameRate rate) =>
        Attempts(sequence, summaryStart, rate, _ => null).GroupBy(a => a.Key).Select(g => g.ToList()).Select(list =>
        {
            var (chosen, result, count) = Row(list);
            return new SummaryAttemptRow(chosen.Route, chosen.Grade, result, count, list.Count, list.Any(a => a.Stated is not null),
                list.Select(a => a.Plate).ToList());
        }).ToList();

    /// <summary>The attempts the board lists, in the order they started.</summary>
    private static List<Attempt> Attempts(Sequence sequence, MediaTime summaryStart, FrameRate rate, Func<Guid, HeartRateBinding?> bindingOf)
    {
        var clips = sequence.VideoTracks.SelectMany(t => t.Clips).ToList();
        var previous = clips.OfType<TemplateClip>()
            .Where(t => t.Kind == TemplateKind.Summary && t.TimelineStart < summaryStart)
            .Select(t => (MediaTime?)t.TimelineStart).Max() ?? new MediaTime(long.MinValue);
        var videos = clips.OfType<VideoClip>().ToDictionary(c => c.Id);
        return Routes(clips, videos)
            .Where(parts => parts[0].TimelineStart < summaryStart && parts[0].TimelineStart >= previous)
            .OrderBy(parts => parts[0].TimelineStart)
            .Select(parts => AttemptOf(parts, videos, rate, bindingOf))
            .ToList();
    }

    /// <summary>A route's row from its attempts: the chosen attempt (the first top / flash, else the last), the best result and
    /// the attempts (a flash 1, else the last stated value, else the attempts counted).</summary>
    private static (Attempt Chosen, BoulderStatus Result, int Count) Row(List<Attempt> list)
    {
        var chosen = list.FirstOrDefault(a => a.Status is BoulderStatus.Top or BoulderStatus.Flash) ?? list[^1];
        var result = chosen.Status is BoulderStatus.Top or BoulderStatus.Flash ? chosen.Status
            : list.Any(a => a.Status == BoulderStatus.Attempt) ? BoulderStatus.Attempt
            : BoulderStatus.None;
        var count = result == BoulderStatus.Flash ? 1 : list.LastOrDefault(a => a.Stated is not null)?.Stated ?? list.Count;
        return (chosen, result, count);
    }

    /// <summary>One attempt: a route area's fields, its result, its climb time, the heart-rate samples of the climb, and its
    /// plate (the area's first part — where its stated attempts live).</summary>
    private sealed record Attempt(object Key, string Route, string Grade, string Accent, BoulderStatus Status, int? Stated, MediaTime? Time,
        IReadOnlyList<int> HeartRate, Guid Plate);

    /// <summary>
    /// The routes of the «Боулдер» areas, each its parts by start. An anchored area's route is its route id (the stored
    /// <see cref="TemplateClip.AnchorRouteId"/>, else its clip's single markup); a split — of the area or of its video clip —
    /// leaves parts with the same id whose template clock continues (the right part's <see cref="TemplateClip.CounterStart"/>
    /// &gt; 0, at or after the left part's clock end), so such a part joins the route that started before it; a part whose clock
    /// starts at 0 (a new area, a copy or a paste of a whole clip — the route id is kept on a copy) is a route of its own. A free
    /// area is a route by its part whose clock starts at 0 (the YouTube timestamps' grouping, D057 §3.9).
    /// </summary>
    private static IEnumerable<List<TemplateClip>> Routes(IEnumerable<Clip> clips, IReadOnlyDictionary<Guid, VideoClip> videos)
    {
        var areas = clips.OfType<TemplateClip>().Where(t => t.Kind == TemplateKind.Boulder).ToList();
        Guid? RouteOf(TemplateClip t) => t.AnchorRouteId ??
            (t.AnchorClipId is { } c && videos.TryGetValue(c, out var clip) ? RouteOverlays.RouteIdOf(t, clip) : null);

        foreach (var group in areas.Where(a => a.AnchorClipId is not null && RouteOf(a) is not null).GroupBy(a => RouteOf(a)!.Value))
        {
            var chains = new List<List<TemplateClip>>();
            foreach (var part in group.OrderBy(a => a.TimelineStart))
            {
                var chain = part.CounterStart > MediaTime.Zero
                    ? chains.LastOrDefault(c => c[^1].CounterStart + c[^1].Duration <= part.CounterStart + MediaTime.FromSeconds(0.5))
                    : null;
                if (chain is null) chains.Add(new List<TemplateClip> { part });
                else chain.Add(part);
            }
            foreach (var chain in chains) yield return chain;
        }
        foreach (var area in areas.Where(a => (a.AnchorClipId is null || RouteOf(a) is null) && a.CounterStart == MediaTime.Zero))
            yield return new List<TemplateClip> { area };
    }

    private static Attempt AttemptOf(List<TemplateClip> parts, IReadOnlyDictionary<Guid, VideoClip> videos, FrameRate rate,
        Func<Guid, HeartRateBinding?> bindingOf)
    {
        var first = parts[0];
        var (route, grade) = (first.Route.Trim(), first.Grade.Trim());
        object key = route.Length > 0 ? (route.ToUpperInvariant(), grade.ToUpperInvariant()) : new object();

        // S: the lilac's start — the status moment on the template's clock, inside the part it falls in (that part's clip
        // turns it into source time).
        long? s = null;
        TemplateClip? sPart = null;
        var status = parts.Select(p => p.Status).FirstOrDefault(st => st != BoulderStatus.None);
        foreach (var part in parts.Where(p => p.Status != BoulderStatus.None))
        {
            long ps = part.TimelineStart.ToNearestFrame(rate), pe = part.TimelineEnd.ToNearestFrame(rate);
            var at = ps + part.StatusAt.ToNearestFrame(rate) - part.CounterStart.ToNearestFrame(rate);
            if (at < ps || at >= pe) continue;
            (s, status, sPart) = (at, part.Status, part);
            break;
        }

        MediaTime? time = null;
        var heartRate = (IReadOnlyList<int>)Array.Empty<int>();
        if (s is { } sFrame && sPart?.AnchorClipId is { } clipId && videos.TryGetValue(clipId, out var clip) && Ae(parts, videos, rate) is { } ae)
        {
            var sSource = HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, sFrame, rate);
            if (sSource > ae)
            {
                time = sSource - ae;
                if (bindingOf(clip.MediaAssetId) is { } binding) heartRate = Samples(binding, ae, sSource);
            }
        }
        return new Attempt(key, route, grade, first.AccentHex, status, first.Attempts, time, heartRate, first.Id);
    }

    /// <summary><c>Ae</c>: the route's orange end on whichever part's clip holds the orange (a split leaves it on one side; the
    /// parts' clips are pieces of one source), else the route's stored <c>T</c>; null without either.</summary>
    private static MediaTime? Ae(List<TemplateClip> parts, IReadOnlyDictionary<Guid, VideoClip> videos, FrameRate rate)
    {
        foreach (var part in parts)
            if (part.AnchorClipId is { } id && videos.TryGetValue(id, out var clip) && RouteOverlays.RouteIdOf(part, clip) is { } routeId &&
                RouteOverlays.OrangeEndOf(clip, routeId, rate) is { } end)
                return end;
        return parts.Select(p => p.RouteStart).FirstOrDefault(t => t is not null);
    }

    /// <summary>The FIT's samples in the watch-time window of the source range <c>[from, to)</c> with the display offset (as
    /// the «Боулдер» plate shows them, D057 §3.8), inside the recording and outside the timer's pauses — never interpolated.</summary>
    internal static IReadOnlyList<int> Samples(HeartRateBinding binding, MediaTime from, MediaTime to)
    {
        var workout = binding.Workout;
        var start = TrainingAlignment.WatchTime(binding.Zero, new MediaTime(from.Ticks - binding.DisplayOffset.Ticks), binding.ClockCorrection);
        var end = TrainingAlignment.WatchTime(binding.Zero, new MediaTime(to.Ticks - binding.DisplayOffset.Ticks), binding.ClockCorrection);
        return workout.HeartRate
            .Where(h => h.Utc >= start && h.Utc < end && workout.Recording.Contains(h.Utc) && !workout.Pauses.Any(p => p.Contains(h.Utc)))
            .Select(h => h.Bpm)
            .ToList();
    }
}
