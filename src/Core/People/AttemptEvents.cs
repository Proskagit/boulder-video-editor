using System.Collections.Immutable;
using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.People;

/// <summary>D078 §3: the events of a window of a video — every lift-off candidate and every fall found, in source time order.</summary>
public sealed record AttemptEventSet(ImmutableArray<MediaTime> LiftOffs, ImmutableArray<MediaTime> Falls)
{
    public static AttemptEventSet Empty { get; } = new(ImmutableArray<MediaTime>.Empty, ImmutableArray<MediaTime>.Empty);

    /// <summary>The event of <paramref name="events"/> nearest to <paramref name="time"/> within <paramref name="radius"/>; null
    /// when none is that near (the earlier on a tie).</summary>
    public static MediaTime? Nearest(ImmutableArray<MediaTime> events, MediaTime time, MediaTime radius)
    {
        MediaTime? best = null;
        foreach (var e in events)
        {
            var d = Math.Abs((e - time).Ticks);
            if (d <= radius.Ticks && (best is not { } b || d < Math.Abs((b - time).Ticks))) best = e;
        }
        return best;
    }
}

/// <summary>
/// D078 §3: the attempt's events from the climbers' skeletons (the research's rules «detect.py v3», frozen before the independent
/// check; `docs/ATTEMPT_EVENTS_RESEARCH.md`). Pure. Over the samples of <see cref="PersonTracks"/> in a source time window:
/// up to <see cref="MaxClimbers"/> climbers (the tracks whose box top rises most, each joined with the tracks continuing it
/// after a fast fall broke it); for each one
/// <list type="bullet">
/// <item>the <b>lift-off candidates</b>: a foot put on a hold (the higher ankle ≥ 0.2 body heights above the lower one for
/// ≥ 0.5 s) or a sit start (both feet up together by ≥ 0.06 body heights while crouched), each moved to where the lower foot
/// leaves its floor level — a foot's height in the frame alone cannot tell a low foothold from the floor;</item>
/// <item>the <b>falls</b>: around each highest point of an attempt, the feet back at the attempt's floor level after it (the
/// landing); the fall is where the hip's descent into that landing begins.</item>
/// </list>
/// </summary>
public static class AttemptEvents
{
    public const int MaxClimbers = 3;
    private const int MaxAttempts = 6;
    private const int MinTrackSamples = 30;

    public static AttemptEventSet Find(PersonTracks tracks, MediaTime from, MediaTime to)
    {
        if (tracks.Samples.IsDefaultOrEmpty || to <= from) return AttemptEventSet.Empty;
        var window = tracks.Samples.Where(s => s.Time >= from && s.Time <= to).ToList();
        if (window.Count < MinTrackSamples) return AttemptEventSet.Empty;
        var times = window.Select(s => s.Time).ToArray();

        var byTrack = new Dictionary<int, List<(int Index, PersonDetection Person)>>();
        for (var i = 0; i < window.Count; i++)
            for (var p = 0; p < window[i].People.Length; p++)
            {
                var id = window[i].TrackIdOf(p);
                if (id < 0) continue;
                if (!byTrack.TryGetValue(id, out var list)) byTrack[id] = list = new();
                list.Add((i, window[i].People[p]));
            }

        var lifts = new SortedSet<long>();
        var falls = new SortedSet<long>();
        foreach (var chain in Climbers(byTrack))
        {
            var s = Series.Of(chain, times.Length);
            foreach (var i in s.LiftOffCandidates()) lifts.Add(times[i].Ticks);
            foreach (var i in s.Falls()) falls.Add(times[i].Ticks);
        }
        return new AttemptEventSet(lifts.Select(t => new MediaTime(t)).ToImmutableArray(), falls.Select(t => new MediaTime(t)).ToImmutableArray());
    }

    // ---- The climbers -------------------------------------------------------------------------------------------------

    private static double Rise(List<(int Index, PersonDetection Person)> track)
    {
        if (track.Count < MinTrackSamples) return -1;
        var y0 = track.Select(p => p.Person.Box.Y0).ToArray();
        return (Percentile(y0, 95) - Percentile(y0, 5)) * Math.Min(1.0, track.Count / 150.0);
    }

    private static IEnumerable<List<(int Index, PersonDetection Person)>> Climbers(Dictionary<int, List<(int Index, PersonDetection Person)>> tracks)
    {
        var used = new HashSet<int>();
        var found = 0;
        foreach (var id in tracks.Keys.OrderByDescending(k => Rise(tracks[k])).ThenBy(k => k).ToList())
        {
            if (found >= MaxClimbers || Rise(tracks[id]) < 0.05) yield break;
            if (used.Contains(id)) continue;
            found++;
            yield return Chain(tracks, id, used);
        }
    }

    /// <summary>A climber's track joined with the tracks that continue it: one starting (or ending) within 1.5 s of the chain's
    /// end (start), near its box horizontally.</summary>
    private static List<(int Index, PersonDetection Person)> Chain(Dictionary<int, List<(int Index, PersonDetection Person)>> tracks, int first, HashSet<int> used)
    {
        var chain = new List<(int Index, PersonDetection Person)>(tracks[first]);
        used.Add(first);
        foreach (var forward in new[] { true, false })
        {
            while (true)
            {
                var (endIndex, endPerson) = forward ? chain[^1] : chain[0];
                var cx = endPerson.Box.Center.X;
                var w = endPerson.Box.Width;
                (int Gap, int Id)? best = null;
                foreach (var (id, track) in tracks)
                {
                    if (used.Contains(id) || track.Count < 3) continue;
                    var (index, person) = forward ? track[0] : track[^1];
                    var gap = forward ? index - endIndex : endIndex - index;
                    if (gap > 0 && gap <= 15 && Math.Abs(person.Box.Center.X - cx) < Math.Max(w, 0.1) * 1.5 &&
                        (best is null || gap < best.Value.Gap))
                        best = (gap, id);
                }
                if (best is not { } b) break;
                used.Add(b.Id);
                if (forward) chain.AddRange(tracks[b.Id]);
                else chain.InsertRange(0, tracks[b.Id]);
            }
        }
        return chain;
    }

    // ---- One climber's series --------------------------------------------------------------------------------------------

    private sealed class Series
    {
        private readonly int _n;
        private readonly double[] _hip, _lo, _hi, _boxH;
        private readonly double _h;

        private Series(int n, double[] hip, double[] lo, double[] hi, double[] boxH)
        {
            _n = n;
            _boxH = boxH;
            _hip = Smooth(hip);
            _lo = Smooth(lo);
            _hi = Smooth(hi);
            var heights = boxH.Where(v => !double.IsNaN(v)).ToArray();
            _h = heights.Length > 0 ? Percentile(heights, 75) : double.NaN;
        }

        /// <summary>The hip, the lower and the higher ankle (image y, down is larger) and the box height per window sample.</summary>
        public static Series Of(List<(int Index, PersonDetection Person)> chain, int n)
        {
            double[] hip = Nans(n), lo = Nans(n), hi = Nans(n), boxH = Nans(n), boxY1 = Nans(n);
            foreach (var (i, p) in chain)
            {
                if (!double.IsNaN(boxH[i])) continue;   // one person per sample (the first of a chain's tracks there)
                boxH[i] = p.Box.Height;
                boxY1[i] = p.Box.Y1;
                var hips = new[] { p.KeypointAt(CocoKeypoint.LeftHip), p.KeypointAt(CocoKeypoint.RightHip) }.Where(k => k is not null).ToList();
                if (hips.Count > 0) hip[i] = hips.Average(k => k!.Value.Y);
                var ankles = new[] { p.KeypointAt(CocoKeypoint.LeftAnkle), p.KeypointAt(CocoKeypoint.RightAnkle) }.Where(k => k is not null).ToList();
                if (ankles.Count > 0)
                {
                    lo[i] = ankles.Max(k => k!.Value.Y);
                    hi[i] = ankles.Min(k => k!.Value.Y);
                }
            }
            // The body's lowest point: the lower ankle, else the box's bottom.
            for (var i = 0; i < n; i++) if (double.IsNaN(lo[i])) lo[i] = boxY1[i];
            return new Series(n, hip, lo, hi, boxH);
        }

        private bool Ok => !double.IsNaN(_h) && _hip.Any(v => !double.IsNaN(v));

        /// <summary>The lift-off candidates (indices): a foot on a hold, a sit start — each moved to its lift-off.</summary>
        public IEnumerable<int> LiftOffCandidates()
        {
            if (!Ok) return Array.Empty<int>();
            var candidates = new List<int>();
            var raised = Raised();
            for (var i = 1; i < _n; i++)
                if (raised[i] && !raised[i - 1] && Enumerable.Range(i, 5).All(k => k < _n && raised[k])) candidates.Add(i);
            candidates.AddRange(SitStarts(10, _n));
            var result = new SortedSet<int>();
            foreach (var c in candidates)
                if (FloorBefore(c) is { } f) result.Add(LiftOff(c, f, Math.Min(_n, c + 30)));
            return result;
        }

        /// <summary>The falls (indices): per attempt around its highest point, where the hip's descent into the landing begins.</summary>
        public IEnumerable<int> Falls()
        {
            var result = new List<int>();
            if (!Ok) return result;
            var rest = Percentile(_hip.Where(v => !double.IsNaN(v)).ToArray(), 85);   // the standing hip level
            var masked = new bool[_n];
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var p = -1;
                for (var i = 0; i < _n; i++)
                    if (!masked[i] && !double.IsNaN(_hip[i]) && (p < 0 || _hip[i] < _hip[p])) p = i;
                if (p < 0 || _hip[p] > rest - 0.25 * _h) break;   // nothing high enough left
                int lower = p, upper = p;
                while (lower > 0 && !masked[lower - 1]) lower--;
                while (upper < _n - 1 && !masked[upper + 1]) upper++;
                if (Attempt(p, lower, upper) is { } a)
                {
                    if (a.Fall is { } fall) result.Add(fall);
                    var end = a.Landing ?? Math.Min(_n - 1, p + 50);
                    for (var i = Math.Max(0, a.Start - 5); i <= Math.Min(_n - 1, end + 9); i++) masked[i] = true;
                }
                else
                {
                    for (var i = Math.Max(0, p - 30); i < Math.Min(_n, p + 30); i++) masked[i] = true;
                }
            }
            return result;
        }

        private (int Start, int? Landing, int? Fall)? Attempt(int p, int lower, int upper)
        {
            var raised = Raised();
            var candidates = new List<int>();
            for (var i = lower; i <= p; i++)
            {
                if (!raised[i] || (i > lower && raised[i - 1])) continue;
                var j = i;
                while (j <= p && raised[j]) j++;
                if (j - i >= 5) candidates.Add(i);
            }
            candidates.AddRange(SitStarts(Math.Max(10, lower + 10), p));
            int? start = null;
            double floor = 0;
            foreach (var c in candidates.Distinct().OrderBy(c => c))   // the earliest with no standing again before p
            {
                if (FloorBefore(c) is not { } f || StandsAgain(c, p, f)) continue;
                start = LiftOff(c, f, Math.Min(p, c + 30));
                floor = f;
                break;
            }
            if (start is not { } s) return null;
            int? landing = null;
            for (var i = p + 1; i <= upper; i++)
                if (!double.IsNaN(_lo[i]) && _lo[i] >= floor - 0.03 * _h) { landing = i; break; }
            if (landing is not { } l) return (s, null, null);
            var r = l;   // back from the landing while the hip descends
            while (r - 1 > s && !double.IsNaN(_hip[r - 1]) && _hip[r] - _hip[r - 1] > 0.002 * _h) r--;
            return (s, l, r);
        }

        private bool[] Raised()
        {
            var raised = new bool[_n];
            for (var i = 0; i < _n; i++) raised[i] = !double.IsNaN(_lo[i]) && !double.IsNaN(_hi[i]) && _lo[i] - _hi[i] >= 0.2 * _h;
            return raised;
        }

        /// <summary>Crouched (the lower ankle close below the hip) when both feet step up together by ≥ 0.06 h.</summary>
        private IEnumerable<int> SitStarts(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                if (double.IsNaN(_lo[i]) || double.IsNaN(_hip[i])) continue;
                var before = Values(_lo, i - 10, i - 4);
                if (before.Length > 0 && Median(before) - _lo[i] >= 0.06 * _h && _lo[i] - _hip[i] < 0.3 * _h) yield return i;
            }
        }

        /// <summary>The standing foot's floor level just before <paramref name="c"/>.</summary>
        private double? FloorBefore(int c)
        {
            var pre = Values(_lo, Math.Max(0, c - 10), Math.Max(1, c - 1));
            return pre.Length > 0 ? pre.Max() : null;
        }

        /// <summary>Where the lower foot leaves the floor level <paramref name="floor"/> near <paramref name="c"/>.</summary>
        private int LiftOff(int c, double floor, int limit)
        {
            for (var i = Math.Max(0, c - 5); i < limit; i++)
                if (!double.IsNaN(_lo[i]) && _lo[i] < floor - 0.03 * _h) return i;
            return c;
        }

        /// <summary>Standing on both feet at the floor level again (both ankles near it) for ≥ 1 s within [a, b).</summary>
        private bool StandsAgain(int a, int b, double floor)
        {
            var run = 0;
            for (var i = a; i < b; i++)
            {
                var both = !double.IsNaN(_lo[i]) && !double.IsNaN(_hi[i]) && _lo[i] >= floor - 0.04 * _h && _hi[i] >= floor - 0.06 * _h;
                run = both ? run + 1 : 0;
                if (run >= 10) return true;
            }
            return false;
        }
    }

    // ---- Numerics (as numpy's) -------------------------------------------------------------------------------------------

    private static double[] Nans(int n) => Enumerable.Repeat(double.NaN, n).ToArray();

    private static double[] Values(double[] a, int from, int to) =>
        a.Skip(Math.Max(0, from)).Take(Math.Max(0, Math.Min(a.Length, to) - Math.Max(0, from))).Where(v => !double.IsNaN(v)).ToArray();

    /// <summary>A median over 5 samples; gaps up to 10 samples interpolated first, longer ones stay unknown.</summary>
    internal static double[] Smooth(double[] a, int k = 5)
    {
        var n = a.Length;
        var known = Enumerable.Range(0, n).Where(i => !double.IsNaN(a[i])).ToArray();
        if (known.Length < 2) return (double[])a.Clone();
        var filled = new double[n];
        for (var i = 0; i < n; i++)
        {
            var pos = Array.BinarySearch(known, i);
            if (pos >= 0) { filled[i] = a[i]; continue; }
            pos = ~pos;
            filled[i] = pos == 0 ? a[known[0]] : pos == known.Length ? a[known[^1]]
                : a[known[pos - 1]] + (a[known[pos]] - a[known[pos - 1]]) * (i - known[pos - 1]) / (double)(known[pos] - known[pos - 1]);
        }
        var run = 0;
        for (var i = 0; i < n; i++)
        {
            run = double.IsNaN(a[i]) ? run + 1 : 0;
            if (run > 10) for (var j = i - run + 1; j <= i; j++) filled[j] = double.NaN;
        }
        var result = Nans(n);
        var half = k / 2;
        for (var i = 0; i < n; i++)
        {
            var w = Values(filled, i - half, i + half + 1);
            if (w.Length > 0) result[i] = Median(w);
        }
        return result;
    }

    internal static double Median(double[] values)
    {
        var s = values.OrderBy(v => v).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// <summary>The linear-interpolation percentile (numpy's default).</summary>
    internal static double Percentile(double[] values, double q)
    {
        var s = values.OrderBy(v => v).ToArray();
        if (s.Length == 0) return double.NaN;
        var pos = (s.Length - 1) * q / 100;
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(s.Length - 1, lo + 1);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }
}
