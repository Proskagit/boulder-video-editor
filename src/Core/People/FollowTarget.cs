using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.People;

/// <summary>Where the followed person is on one analysed frame: its anchor and box when seen, nothing when lost.</summary>
public readonly record struct FollowPoint(MediaTime Time, NormalizedPoint? Anchor, NormalizedBox? Box)
{
    public bool IsSeen => Anchor is not null;
}

/// <summary>A stretch of source time where the followed person is not seen.</summary>
public readonly record struct FollowGap(MediaTime Start, MediaTime End)
{
    public MediaTime Duration => End - Start;
}

/// <summary>
/// The followed person over the whole analysed video (D061 §3): one point per analysed frame, seen or lost. Built by
/// <see cref="FollowTarget.Resolve(PersonTracks, int)"/> from the tracks the person was recognised in — never another
/// person's.
/// </summary>
public sealed record FollowTrajectory(ImmutableArray<FollowPoint> Points, ImmutableArray<int> TrackIds)
{
    /// <summary>The share of analysed frames in <c>[from, to]</c> where the person is seen (1 for an empty range).</summary>
    public double SeenFraction(MediaTime from, MediaTime to)
    {
        var inRange = Points.Where(p => p.Time >= from && p.Time <= to).ToList();
        return inRange.Count == 0 ? 1 : inRange.Count(p => p.IsSeen) / (double)inRange.Count;
    }

    /// <summary>The stretches of <c>[from, to]</c> where the person is not seen for at least <paramref name="minimum"/>.</summary>
    public ImmutableArray<FollowGap> Gaps(MediaTime from, MediaTime to, MediaTime minimum)
    {
        var gaps = ImmutableArray.CreateBuilder<FollowGap>();
        MediaTime? start = null;
        MediaTime last = from;
        foreach (var p in Points)
        {
            if (p.Time < from) continue;
            if (p.Time > to) break;
            if (!p.IsSeen) start ??= p.Time;
            else if (start is { } s)
            {
                if (p.Time - s >= minimum) gaps.Add(new FollowGap(s, p.Time));
                start = null;
            }
            last = p.Time;
        }
        if (start is { } open && last - open >= minimum) gaps.Add(new FollowGap(open, last));
        return gaps.ToImmutable();
    }

    /// <summary>Whether the person is seen at <paramref name="time"/> (the nearest analysed frame).</summary>
    public bool IsSeenAt(MediaTime time)
    {
        if (Points.IsDefaultOrEmpty) return false;
        int lo = 0, hi = Points.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Points[mid].Time < time) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && (time - Points[lo - 1].Time).Ticks <= (Points[lo].Time - time).Ticks) lo--;
        return Points[lo].IsSeen;
    }
}

/// <summary>
/// Which person Follow follows, and where it is on every analysed frame (D061 §3.4). The user picks a person on one frame
/// (or the only person of the clip is taken); its track is extended across losses — the person left the frame, was hidden,
/// the detector missed it for longer than a track bridges — by later (and earlier) tracks that are the same person:
/// <list type="bullet">
/// <item>a candidate never coexists with the person (a track seen at the same time is somebody else);</item>
/// <item>its appearance must match the person's (<see cref="MinSimilarity"/>), and it must start near where the person was
/// last seen — the allowed distance grows with the length of the loss (a hand-held camera may have turned meanwhile), but
/// after a long loss only a clearly matching appearance (<see cref="StrongSimilarity"/>) is accepted;</item>
/// <item>when two candidates are about equally likely the person stays lost rather than guessing — Follow never switches to
/// another person silently; the camera holds and the loss is reported.</item>
/// </list>
/// Pure and deterministic.
/// </summary>
public static class FollowTarget
{
    public const double MinSimilarity = 0.62;
    public const double StrongSimilarity = 0.8;
    public const double AmbiguityMargin = 0.06;
    public static readonly MediaTime Overlap = MediaTime.FromSeconds(0.3);
    public static readonly MediaTime MinCandidate = MediaTime.FromSeconds(0.3);

    /// <summary>The trajectory of the person at <paramref name="point"/> on the frame nearest to <paramref name="time"/>;
    /// null when nobody is there.</summary>
    public static FollowTrajectory? Resolve(PersonTracks tracks, MediaTime time, NormalizedPoint point) =>
        tracks.TrackAt(time, point) is { } id ? Resolve(tracks, id) : null;

    /// <summary>The only person of <c>[from, to]</c> worth following — the one track lasting at least a third of the range
    /// (and 1 s) while every other person of a comparable size (a median box at least half as high — a spectator far behind
    /// does not count) is seen at most a fifth as long; null when there is none or several (the user picks).</summary>
    public static int? AutoTrack(PersonTracks tracks, MediaTime from, MediaTime to)
    {
        var range = Math.Max(1, (to - from).Ticks);
        var heights = tracks.Samples.Where(s => s.Time >= from && s.Time <= to)
            .SelectMany(s => s.People.Select((p, i) => (Id: s.TrackIdOf(i), p.Box.Height)))
            .Where(x => x.Id >= 0).GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Height).OrderBy(h => h).ElementAt(g.Count() / 2));
        var spans = tracks.TracksIn(from, to, MinCandidate)
            .Select(t => (t.Id, Ticks: Math.Min(t.End.Ticks, to.Ticks) - Math.Max(t.Start.Ticks, from.Ticks), Height: heights.GetValueOrDefault(t.Id)))
            .OrderByDescending(t => t.Ticks).ToList();
        if (spans.Count == 0) return null;
        var best = spans[0];
        if (best.Ticks < range / 3 || best.Ticks < MediaTime.FromSeconds(1).Ticks) return null;
        return spans.Skip(1).All(s => s.Ticks <= best.Ticks / 5 || s.Height < best.Height / 2) ? best.Id : null;
    }

    private sealed record TrackData(int Id, ImmutableArray<(MediaTime Time, PersonDetection Person)> Seen)
    {
        public MediaTime Start => Seen[0].Time;
        public MediaTime End => Seen[^1].Time;
    }

    public static FollowTrajectory Resolve(PersonTracks tracks, int seedTrack)
    {
        var data = tracks.Samples
            .SelectMany(s => s.People.Select((p, i) => (Id: s.TrackIdOf(i), s.Time, Person: p)))
            .Where(x => x.Id >= 0)
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => new TrackData(g.Key, g.OrderBy(x => x.Time).Select(x => (x.Time, x.Person)).ToImmutableArray()));
        if (!data.TryGetValue(seedTrack, out var seed))
            return new FollowTrajectory(tracks.Samples.Select(s => new FollowPoint(s.Time, null, null)).ToImmutableArray(), ImmutableArray<int>.Empty);

        var chain = new List<TrackData> { seed };
        Extend(chain, data, forward: true);
        Extend(chain, data, forward: false);

        var byTime = new Dictionary<long, PersonDetection>();
        foreach (var track in chain)
            foreach (var (time, person) in track.Seen)
                byTime[time.Ticks] = person;
        var points = tracks.Samples.Select(s => byTime.TryGetValue(s.Time.Ticks, out var p)
            ? new FollowPoint(s.Time, p.Anchor, p.Box)
            : new FollowPoint(s.Time, null, null)).ToImmutableArray();
        return new FollowTrajectory(points, chain.OrderBy(t => t.Start).Select(t => t.Id).ToImmutableArray());
    }

    private static void Extend(List<TrackData> chain, Dictionary<int, TrackData> all, bool forward)
    {
        var excluded = new HashSet<int>();
        while (true)
        {
            var edge = forward ? chain.MaxBy(t => t.End)! : chain.MinBy(t => t.Start)!;
            var edgeTime = forward ? edge.End : edge.Start;
            var (lastTime, last) = forward ? edge.Seen[^1] : edge.Seen[0];
            var model = AppearanceOf(chain, forward);

            var candidates = new List<(TrackData Track, double Score, double Similarity)>();
            foreach (var track in all.Values)
            {
                if (excluded.Contains(track.Id) || chain.Any(c => c.Id == track.Id)) continue;
                if (track.End - track.Start < MinCandidate && track.Seen.Length < 3) continue;
                if (forward ? track.Start <= edgeTime - Overlap : track.End >= edgeTime + Overlap) continue;
                if (chain.Any(c => Coexist(c, track))) continue;
                var (firstTime, first) = forward ? track.Seen[0] : track.Seen[^1];
                var gap = Math.Abs((firstTime - lastTime).TotalSeconds);
                var similarity = PersonAppearance.Similarity(model, AppearanceOf(track, forward));
                if (similarity < MinSimilarity) continue;
                var (a, b) = (last.Anchor, first.Anchor);
                var distance = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
                var radius = 0.12 + Math.Max(last.Box.Size, first.Box.Size) * 0.5 + 0.35 * gap;
                var near = distance <= radius;
                if (!near && !(similarity >= StrongSimilarity && gap >= 1.0)) continue;
                if (gap > 2.0 && similarity < StrongSimilarity && distance > 0.25) continue;
                candidates.Add((track, similarity - 0.25 * Math.Min(1, distance / Math.Max(radius, 1e-6)) - 0.02 * Math.Min(gap, 10), similarity));
            }
            if (candidates.Count == 0) return;

            // The earliest plausible candidate wins over a better one much later (the person comes back once); among
            // candidates starting about together the best one — unless two are equally likely.
            var ordered = candidates.OrderBy(c => forward ? c.Track.Start.Ticks : -c.Track.End.Ticks).ToList();
            var firstStart = forward ? ordered[0].Track.Start : ordered[0].Track.End;
            var group = ordered.Where(c => Math.Abs(((forward ? c.Track.Start : c.Track.End) - firstStart).TotalSeconds) <= 1.0)
                .OrderByDescending(c => c.Score).ToList();
            if (group.Count > 1 && group[0].Score - group[1].Score < AmbiguityMargin && Coexist(group[0].Track, group[1].Track))
            {
                foreach (var c in group) excluded.Add(c.Track.Id);   // two people about equally likely: stay lost there
                continue;
            }
            chain.Add(group[0].Track);
        }
    }

    /// <summary>Whether two tracks are seen at the same time for longer than <see cref="Overlap"/> — then they are two
    /// people.</summary>
    private static bool Coexist(TrackData a, TrackData b)
    {
        var start = a.Start > b.Start ? a.Start : b.Start;
        var end = a.End < b.End ? a.End : b.End;
        return end - start > Overlap;
    }

    /// <summary>The appearance of the chain at its growing end: the mean of its last confident, uncut detections.</summary>
    private static ImmutableArray<float> AppearanceOf(List<TrackData> chain, bool forward)
    {
        var seen = chain.SelectMany(t => t.Seen).OrderBy(s => s.Time).ToList();
        if (!forward) seen.Reverse();
        var picked = seen.AsEnumerable().Reverse().Where(s => !s.Person.Box.TouchesEdge() && s.Person.Score >= PersonTracker.HighScore).Take(15).ToList();
        if (picked.Count == 0) picked = seen.AsEnumerable().Reverse().Take(15).ToList();
        return Mean(picked.Select(s => s.Person.Appearance));
    }

    /// <summary>The appearance of a candidate at the end where it would join: the mean of its first (last) detections.</summary>
    private static ImmutableArray<float> AppearanceOf(TrackData track, bool forward)
    {
        var seen = forward ? track.Seen.AsEnumerable() : track.Seen.Reverse();
        var picked = seen.Where(s => !s.Person.Box.TouchesEdge()).Take(8).ToList();
        if (picked.Count == 0) picked = seen.Take(8).ToList();
        return Mean(picked.Select(s => s.Person.Appearance));
    }

    private static ImmutableArray<float> Mean(IEnumerable<ImmutableArray<float>> histograms)
    {
        float[]? sum = null;
        var n = 0;
        foreach (var h in histograms)
        {
            if (h.IsDefaultOrEmpty) continue;
            sum ??= new float[h.Length];
            if (h.Length != sum.Length) continue;
            for (var i = 0; i < h.Length; i++) sum[i] += h[i];
            n++;
        }
        if (sum is null || n == 0) return ImmutableArray<float>.Empty;
        for (var i = 0; i < sum.Length; i++) sum[i] /= n;
        return ImmutableArray.Create(sum);
    }
}
