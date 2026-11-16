using System.Collections.Immutable;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.People;

/// <summary>
/// The people analysis of one video (D061 §2): two passes over the whole file, each decoding it once at
/// <see cref="AnalysisLongSide"/> px with the asset's HDR rule (D033) — the picture the user sees.
/// <list type="number">
/// <item><b>Detection</b> every <see cref="DetectionInterval"/>: the people, their torso centres and appearances, linked
/// into tracks by <see cref="PersonTracker"/> → <see cref="PersonTracks"/> (Follow, and the prompts of pass 2).</item>
/// <item><b>Segmentation</b> every <see cref="MaskInterval"/>: every tracked person large enough to stand in front of a
/// hold (<see cref="MinPersonHeight"/>) is started in the video segmenter at its first confident detection, re-prompted
/// with its detector box when the segmenter lost it while the detector still sees it, and stopped once its track is
/// over and it is gone → <see cref="PersonMaskTrack"/> (Occlusion).</item>
/// </list>
/// Deterministic for a given decoder and model; cancellable between frames (and inside a model run).
/// </summary>
public sealed class PeopleAnalyzer(IVideoDecoder decoder, IPeopleVision vision)
{
    public const int AnalysisLongSide = 1024;
    public const int MaskLongSide = 640;
    public static readonly MediaTime DetectionInterval = MediaTime.FromSeconds(0.1);
    public static readonly MediaTime MaskInterval = new(TimeSpan.TicksPerSecond / 15);

    /// <summary>The smallest person segmented: a box at least this share of the frame's height (a person far behind the
    /// filmed wall is never in front of its holds; each segmented person costs a model pass per frame).</summary>
    public const double MinPersonHeight = 0.12;

    /// <summary>The longest loss one mask id spans (<see cref="PersonRoots"/>).</summary>
    public static readonly MediaTime MaxJoinGap = MediaTime.FromSeconds(5);

    /// <summary>At most this many people are segmented at once (the largest first).</summary>
    public const int MaxPeople = 6;

    /// <summary>The video to analyse: its file, start time (origin of source time), HDR rule, nominal rate and duration.</summary>
    public sealed record Source(string FilePath, MediaTime FileStart, HdrConversion? Hdr, FrameRate? NominalRate, MediaTime Duration);

    /// <summary>Pass 1: the people and their tracks. <paramref name="progress"/> reports 0..1 by source time.</summary>
    public async Task<PersonTracks> DetectAsync(Source source, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var tracker = new PersonTracker();
        var samples = ImmutableArray.CreateBuilder<PersonSample>();
        var picker = new IntervalPicker(DetectionInterval);
        await foreach (var (frame, time) in Frames(source, cancellationToken))
        {
            if (!picker.Take(time)) continue;
            var people = PersonTracker.Suppress(vision.Detect(frame, cancellationToken));
            samples.Add(new PersonSample(time, people, tracker.Add(time, people)));
            Report(progress, time, source.Duration);
        }
        progress?.Report(1);
        return PersonTracks.Of(DetectionInterval, samples.ToImmutable(), vision.ModelId);
    }

    /// <summary>
    /// Pass 2: every person's mask, separately. One segmenter object per <b>person</b>, not per detector track: the tracks the
    /// re-identification of Follow (<see cref="FollowTarget"/>) joins into one person — the climber who left the frame and came
    /// back, a track the tracker split — share the object of the earliest one (<see cref="PersonRoots"/>), which is prompted
    /// again with the later track's box; so a person keeps one mask id over the video. A detection lying mostly inside the mask
    /// of another person (a cut-off part of them, or somebody behind them) never starts an object, and of two objects whose
    /// masks touch with one inside the other's extent the younger is stopped — one person is never split between two masks.
    /// </summary>
    public async Task<PersonMaskTrack> SegmentAsync(Source source, PersonTracks tracks, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var segmenter = vision.CreateSegmenter(cancellationToken);
        var people = SegmentedTracks(tracks);
        var roots = PersonRoots(tracks, people.Select(p => p.Id));
        var trackEnd = people.GroupBy(p => roots[p.Id]).ToDictionary(g => g.Key, g => g.Max(p => p.End));
        var lastSeenOf = new Dictionary<int, MediaTime>(trackEnd);   // a duplicate's is cleared, so it is stopped
        var samples = ImmutableArray.CreateBuilder<PersonMaskSample>();
        var picker = new IntervalPicker(MaskInterval);
        var active = new Dictionary<int, int>();                       // object (root) → steps not visible
        var order = new Dictionary<int, long>();                        // object → the step it was (re)started at
        var extents = new Dictionary<int, NormalizedBox>();             // object → its visible mask's extent, last step
        long step = 0;
        int? gridWidth = null, gridHeight = null;
        await foreach (var (frame, time) in Frames(source, cancellationToken))
        {
            if (!picker.Take(time)) continue;
            step++;
            if (gridWidth is null)
            {
                var scale = (double)MaskLongSide / Math.Max(frame.Width, frame.Height);
                gridWidth = Math.Max(1, (int)Math.Round(frame.Width * scale));
                gridHeight = Math.Max(1, (int)Math.Round(frame.Height * scale));
            }

            var start = new Dictionary<int, NormalizedBox>();
            foreach (var person in people)
            {
                var root = roots[person.Id];
                if (start.ContainsKey(root) || time < person.Start) continue;
                if (DetectionNear(tracks, person.Id, time) is not { Score: >= PersonTracker.HighScore } seen) continue;
                if (InsideAnother(seen.Box, extents, root)) continue;
                if (!active.TryGetValue(root, out var missing))
                {
                    if (active.Count < MaxPeople) start[root] = seen.Box;          // a new person, or a person coming back
                }
                else if (missing >= 3) start[root] = seen.Box;                     // lost by the segmenter, seen by the detector
            }
            var stop = active.Where(a => a.Value >= 5 && time > lastSeenOf[a.Key] + MediaTime.FromSeconds(1)).Select(a => a.Key).ToList();
            foreach (var id in stop) { active.Remove(id); extents.Remove(id); }
            foreach (var id in start.Keys) { active[id] = 0; order[id] = step; lastSeenOf[id] = trackEnd[id]; }

            var result = segmenter.Step(frame, start.Select(p => new SegmenterPrompt(p.Key, p.Value)).ToList(), stop, gridWidth ?? 1,
                gridHeight ?? 1, cancellationToken);
            extents.Clear();
            var masks = new Dictionary<int, byte[]>();
            foreach (var person in result)
            {
                if (active.ContainsKey(person.PersonId))
                    active[person.PersonId] = person.Visible ? 0 : active[person.PersonId] + 1;
                if (!person.Visible || Extent(person.Coverage, gridWidth ?? 1, gridHeight ?? 1) is not { } extent) continue;
                extents[person.PersonId] = extent;
                masks[person.PersonId] = person.Coverage;
            }
            // Two objects on one person: the younger goes (left out now, stopped at the next step).
            foreach (var duplicate in Duplicates(masks, extents, order, gridWidth ?? 1, gridHeight ?? 1).ToList())
            {
                masks.Remove(duplicate);
                extents.Remove(duplicate);
                active[duplicate] = 5;
                lastSeenOf[duplicate] = MediaTime.Zero;
            }
            var entries = ImmutableArray.CreateBuilder<PersonMaskEntry>();
            foreach (var (id, coverage) in masks.OrderBy(m => m.Key))
                if (MaskCodec.Entry(id, coverage, gridWidth ?? 1, gridHeight ?? 1) is { } entry) entries.Add(entry);
            samples.Add(new PersonMaskSample(time, entries.ToImmutable()));
            Report(progress, time, source.Duration);
        }
        progress?.Report(1);
        return new PersonMaskTrack(gridWidth ?? 1, gridHeight ?? 1, MaskInterval, samples.ToImmutable(), vision.ModelId);
    }

    /// <summary>Each track → the person it belongs to: the earliest track of its Follow chain
    /// (<see cref="FollowTarget.Resolve(PersonTracks, int)"/>), the tracks taken in the order of their start.</summary>
    public static Dictionary<int, int> PersonRoots(PersonTracks tracks, IEnumerable<int> ids)
    {
        var roots = new Dictionary<int, int>();
        var starts = tracks.Tracks.ToDictionary(t => t.Id, t => t.Start);
        var ends = tracks.Tracks.ToDictionary(t => t.Id, t => t.End);
        foreach (var id in ids.OrderBy(i => starts.GetValueOrDefault(i)).ThenBy(i => i))
        {
            if (roots.ContainsKey(id)) continue;
            // Only forward from the person's earliest track, exactly as Follow joins them: a later track whose own chain
            // reaches back to an earlier person is not that person unless that person's chain reaches it.
            // A mask id spans only short losses (MaxJoinGap): after a long absence the "same" appearance is as likely somebody
            // else (an operator's hand, another visitor), and a new id is the honest answer.
            roots[id] = id;
            var reached = ends.GetValueOrDefault(id);
            foreach (var member in FollowTarget.Resolve(tracks, id).TrackIds.OrderBy(m => starts.GetValueOrDefault(m)))
            {
                if (member == id || starts.GetValueOrDefault(member) < starts.GetValueOrDefault(id)) continue;
                if (starts.GetValueOrDefault(member) - reached > MaxJoinGap) break;
                if (!FollowTarget.Resolve(tracks, member).TrackIds.Contains(id)) break;   // both ways: one person from either end
                roots.TryAdd(member, id);
                if (ends.GetValueOrDefault(member) > reached) reached = ends.GetValueOrDefault(member);
            }
        }
        return roots;
    }

    /// <summary>Whether <paramref name="box"/> lies mostly (≥ 60 %) inside the mask extent of another person than
    /// <paramref name="self"/>: a cut-off part of that person, or somebody hidden behind them.</summary>
    public static bool InsideAnother(NormalizedBox box, IReadOnlyDictionary<int, NormalizedBox> extents, int self)
    {
        if (box.Area <= 0) return false;
        foreach (var (id, extent) in extents)
            if (id != self && Intersection(box, extent) >= 0.6 * box.Area) return true;
        return false;
    }

    /// <summary>
    /// The younger of every pair of objects on one person: their masks touch along at least 20 cells and the smaller mask's
    /// extent lies at least 80 % inside the larger's (slightly enlarged) — two people standing apart, or one passing in front of
    /// the other, have disjoint or only partly overlapping extents.
    /// </summary>
    public static IEnumerable<int> Duplicates(IReadOnlyDictionary<int, byte[]> masks, IReadOnlyDictionary<int, NormalizedBox> extents,
        IReadOnlyDictionary<int, long> order, int width, int height)
    {
        var gone = new HashSet<int>();
        var ids = masks.Keys.OrderBy(id => order.GetValueOrDefault(id)).ThenBy(id => id).ToList();
        for (var i = 0; i < ids.Count; i++)
            for (var j = i + 1; j < ids.Count; j++)
            {
                var (older, younger) = (ids[i], ids[j]);
                if (gone.Contains(older) || gone.Contains(younger)) continue;
                var (a, b) = (extents[older], extents[younger]);
                var (small, large) = a.Area <= b.Area ? (a, b) : (b, a);
                var grown = new NormalizedBox(large.X0 - 0.03, large.Y0 - 0.03, large.X1 + 0.03, large.Y1 + 0.03);
                if (Intersection(small, grown) < 0.8 * small.Area) continue;
                if (Touching(masks[older], masks[younger], width, height) >= 20) gone.Add(younger);
            }
        return gone;
    }

    private static double Intersection(NormalizedBox a, NormalizedBox b)
    {
        var w = Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0);
        var h = Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0);
        return w > 0 && h > 0 ? w * h : 0;
    }

    /// <summary>The cells of <paramref name="a"/> on or next to a cell of <paramref name="b"/> (both at least half covered).</summary>
    private static int Touching(byte[] a, byte[] b, int width, int height)
    {
        var count = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (a[y * width + x] < 128) continue;
                if (b[y * width + x] >= 128 || (x > 0 && b[y * width + x - 1] >= 128) || (x + 1 < width && b[y * width + x + 1] >= 128) ||
                    (y > 0 && b[(y - 1) * width + x] >= 128) || (y + 1 < height && b[(y + 1) * width + x] >= 128))
                    count++;
            }
        return count;
    }

    /// <summary>The extent of a coverage's half-covered cells, normalized; null when there are none.</summary>
    private static NormalizedBox? Extent(byte[] coverage, int width, int height)
    {
        int x0 = width, y0 = height, x1 = -1, y1 = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (coverage[y * width + x] < 128) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        return x1 < 0 ? null : new NormalizedBox((double)x0 / width, (double)y0 / height, (x1 + 1.0) / width, (y1 + 1.0) / height);
    }

    /// <summary>The tracks worth segmenting: lasting at least <see cref="FollowTarget.MinCandidate"/> with a box at least
    /// <see cref="MinPersonHeight"/> high somewhere, the largest first; with their first confident sighting.</summary>
    internal static List<(int Id, MediaTime Start, MediaTime End)> SegmentedTracks(PersonTracks tracks)
    {
        var info = new Dictionary<int, (MediaTime Start, MediaTime End, double Height, MediaTime? FirstConfident)>();
        foreach (var sample in tracks.Samples)
        {
            for (var i = 0; i < sample.People.Length; i++)
            {
                var id = sample.TrackIdOf(i);
                if (id < 0) continue;
                var p = sample.People[i];
                var confident = p.Score >= PersonTracker.HighScore ? sample.Time : (MediaTime?)null;
                info[id] = info.TryGetValue(id, out var x)
                    ? (x.Start, sample.Time, Math.Max(x.Height, p.Box.Height), x.FirstConfident ?? confident)
                    : (sample.Time, sample.Time, p.Box.Height, confident);
            }
        }
        return info.Where(p => p.Value.Height >= MinPersonHeight && p.Value.FirstConfident is not null &&
                               (p.Value.End - p.Value.Start >= FollowTarget.MinCandidate))
            .OrderByDescending(p => p.Value.Height).ThenBy(p => p.Key)
            .Select(p => (p.Key, p.Value.FirstConfident!.Value, p.Value.End)).ToList();
    }

    /// <summary>The detection of track <paramref name="id"/> on the detection frame nearest to <paramref name="time"/> (within
    /// one detection interval), or null.</summary>
    internal static PersonDetection? DetectionNear(PersonTracks tracks, int id, MediaTime time)
    {
        if (tracks.SampleAt(time) is not { } sample) return null;
        for (var i = 0; i < sample.People.Length; i++)
            if (sample.TrackIdOf(i) == id) return sample.People[i];
        return null;
    }

    private async IAsyncEnumerable<(DecodedFrame Frame, MediaTime Time)> Frames(Source source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await decoder.OpenAsync(new VideoDecodeRequest
        {
            FilePath = source.FilePath,
            StartTime = source.FileStart,
            FirstSamplePoint = new SourceSamplePoint(0, 1),
            NominalFrameRate = source.NominalRate,
            MaxWidth = AnalysisLongSide,
            MaxHeight = AnalysisLongSide,
            Hardware = HardwareDecoding.Auto,   // NVDEC / D3D11VA where available (5.6× faster than software on 4K HEVC), else software
            Hdr = source.Hdr
        }, cancellationToken);
        while (await stream.ReadFrameAsync(cancellationToken) is { } frame)
            yield return (frame, CameraMotionMath.SourceTimeOf(frame.Timestamp, source.FileStart));
    }

    private static void Report(IProgress<double>? progress, MediaTime time, MediaTime duration)
    {
        if (progress is not null && duration > MediaTime.Zero) progress.Report(Math.Clamp(time.TotalSeconds / duration.TotalSeconds, 0, 0.999));
    }

    /// <summary>Takes the first frame at or after each multiple of the interval (a frame within half a millisecond counts).</summary>
    public sealed class IntervalPicker(MediaTime interval)
    {
        private long _next;

        public bool Take(MediaTime time)
        {
            if (time.Ticks + 5_000 < _next) return false;
            _next = (time.Ticks + 5_000) / interval.Ticks * interval.Ticks + interval.Ticks;
            return true;
        }
    }
}
