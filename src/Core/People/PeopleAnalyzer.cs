using System.Collections.Immutable;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
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

    /// <summary>The confident detections (≥ <see cref="PersonTracker.HighScore"/>) a track needs to be segmented.</summary>
    public const int MinConfident = 5;

    /// <summary>At most this many people are segmented at once (the largest first).</summary>
    public const int MaxPeople = 6;

    /// <summary>The video to analyse: its file, start time (origin of source time), HDR rule, nominal rate and duration.</summary>
    public sealed record Source(string FilePath, MediaTime FileStart, HdrConversion? Hdr, FrameRate? NominalRate, MediaTime Duration);

    /// <summary>D061 amendment 1: the analyses are made, cached and published in chunks of this much source time — only the
    /// parts of a video the clips need (the followed parts, the stretches where hold outlines are shown), in order from the
    /// part being followed, so Follow shows its result while the video plays.</summary>
    public static readonly MediaTime ChunkLength = MediaTime.FromSeconds(2);

    /// <summary>The chunk containing source time <paramref name="time"/>.</summary>
    public static int ChunkOf(MediaTime time) => (int)Math.Max(0, time.Ticks / ChunkLength.Ticks);

    public static MediaTime ChunkStart(int chunk) => new(chunk * ChunkLength.Ticks);

    /// <summary>The number of chunks of a video of <paramref name="duration"/>.</summary>
    public static int ChunkCount(MediaTime duration) => duration <= MediaTime.Zero ? 0 : ChunkOf(duration - new MediaTime(1)) + 1;

    /// <summary>Pass 1 over the whole video: the people and their tracks. <paramref name="progress"/> reports 0..1.</summary>
    public Task<PersonTracks> DetectAsync(Source source, IProgress<double>? progress, CancellationToken cancellationToken) =>
        DetectRangeAsync(source, MediaTime.Zero, source.Duration, null, progress, cancellationToken);

    /// <summary>
    /// Pass 1 over <c>[from, to)</c> of the video (decoded from <paramref name="from"/>): the people and their tracks.
    /// <paramref name="onChunk"/> receives each chunk's detections as soon as the chunk is done (the chunks of the range, an
    /// empty one past the file's end); <paramref name="progress"/> reports 0..1 over the range.
    /// </summary>
    public async Task<PersonTracks> DetectRangeAsync(Source source, MediaTime from, MediaTime to, Action<int, PersonTracks>? onChunk,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var tracker = new PersonTracker();
        var samples = ImmutableArray.CreateBuilder<PersonSample>();
        var chunk = new ChunkEmitter<PersonSample>(from, to,
            onChunk is null ? null : (k, items) => onChunk(k, PersonTracks.Of(DetectionInterval, items, vision.ModelId)));
        var picker = new IntervalPicker(DetectionInterval);
        await foreach (var (frame, time) in Frames(source, from, cancellationToken))
        {
            if (time < from) continue;
            if (time >= to) break;
            if (!picker.Take(time)) continue;
            var people = PersonTracker.Suppress(vision.Detect(frame, cancellationToken));
            var sample = new PersonSample(time, people, tracker.Add(time, people));
            samples.Add(sample);
            chunk.Add(time, sample);
            Report(progress, time - from, to - from);
        }
        chunk.Finish();
        progress?.Report(1);
        return PersonTracks.Of(DetectionInterval, samples.ToImmutable(), vision.ModelId);
    }

    /// <summary>Collects the items of a range chunk by chunk and hands each finished chunk on (empty chunks too).</summary>
    private sealed class ChunkEmitter<T>(MediaTime from, MediaTime to, Action<int, ImmutableArray<T>>? onChunk)
    {
        private int _chunk = ChunkOf(from);
        private readonly int _last = to > from ? ChunkOf(to - new MediaTime(1)) : ChunkOf(from) - 1;
        private readonly ImmutableArray<T>.Builder _items = ImmutableArray.CreateBuilder<T>();

        public void Add(MediaTime time, T item)
        {
            Advance(ChunkOf(time));
            _items.Add(item);
        }

        public void Finish() => Advance(_last + 1);

        private void Advance(int chunk)
        {
            while (_chunk < chunk && _chunk <= _last)
            {
                onChunk?.Invoke(_chunk, _items.ToImmutable());
                _items.Clear();
                _chunk++;
            }
        }
    }

    /// <summary>
    /// Pass 2: every person's mask, separately. One segmenter object per <b>person</b>, not per detector track: the tracks the
    /// re-identification of Follow (<see cref="FollowTarget"/>) joins into one person — the climber who left the frame and came
    /// back, a track the tracker split — share the object of the earliest one (<see cref="PersonRoots"/>), which is prompted
    /// again with the later track's box; so a person keeps one mask id over the video. A detection lying mostly inside the mask
    /// of another person (a cut-off part of them, or somebody behind them) never starts an object, and of two objects whose
    /// masks touch with one inside the other's extent the younger is stopped — one person is never split between two masks.
    /// <para>Two guards against a wrong object (found on the D053 project's wide clip, D061 amendment 1): a person first seen
    /// cut off — a climber walking in close to the camera, only the T-shirt in the frame — is started on that part, and the
    /// model's memory then keeps segmenting the part; so an object whose mask is clearly smaller than its person's confident
    /// detector box (<see cref="Partial"/>), or whose mask has lost the person's torso (<see cref="LostTorso"/> — a white
    /// T-shirt fading into a white wall), is <b>anchored again</b> on that box, at most every <see cref="ReanchorSteps"/>,
    /// never on a box shared with another person's mask. And an object with no detected person at its mask for <see cref="OrphanSteps"/> (a weak
    /// false box that started it, the model drifting onto a wall volume) is stopped.</para>
    /// </summary>
    public Task<PersonMaskTrack> SegmentAsync(Source source, PersonTracks tracks, IProgress<double>? progress, CancellationToken cancellationToken) =>
        SegmentRangeAsync(source, tracks, MediaTime.Zero, source.Duration, null, progress, cancellationToken);

    /// <summary>Pass 2 over <c>[from, to)</c> (decoded from <paramref name="from"/>; every person present is started there);
    /// <paramref name="onChunk"/> receives each chunk's masks as soon as the chunk is done.</summary>
    public async Task<PersonMaskTrack> SegmentRangeAsync(Source source, PersonTracks tracks, MediaTime from, MediaTime to,
        Action<int, PersonMaskTrack>? onChunk, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        int? gridWidth = null, gridHeight = null;
        var chunk = new ChunkEmitter<PersonMaskSample>(from, to,
            onChunk is null ? null : (k, items) => onChunk(k, new PersonMaskTrack(gridWidth ?? 1, gridHeight ?? 1, MaskInterval, items, vision.ModelId)));
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
        var supported = new Dictionary<int, long>();                    // object → the last step a detected person was at its mask
        var lastMasks = new Dictionary<int, byte[]>();                  // object → its visible mask, last step
        long step = 0;
        await foreach (var (frame, time) in Frames(source, from, cancellationToken))
        {
            if (time < from) continue;
            if (time >= to) break;
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
                else if (missing == 0 && seen.Score >= ReanchorScore && step - order[root] >= ReanchorSteps &&
                         extents.TryGetValue(root, out var extent) && !OverlapsAnother(seen.Box, extents, root) &&
                         (Partial(extent, seen.Box) ||
                          (seen.Torso is { } torso && lastMasks.TryGetValue(root, out var last) && LostTorso(last, torso, gridWidth ?? 1, gridHeight ?? 1))))
                    start[root] = seen.Box;                                        // the mask holds only a part of the person
            }
            // Supported: a detected person (any score, any track) at the object's mask — else, after OrphanSteps, it goes.
            if (tracks.SampleAt(time) is { } detected)
                foreach (var (id, extent) in extents)
                    if (detected.People.Any(p => p.Box.IoU(extent) >= 0.3 || extent.Contains(p.Box.Center))) supported[id] = step;
            var stop = active.Where(a => (a.Value >= 5 && time > lastSeenOf[a.Key] + MediaTime.FromSeconds(1)) ||
                                         step - supported.GetValueOrDefault(a.Key, step) >= OrphanSteps)
                .Select(a => a.Key).Where(id => !start.ContainsKey(id)).ToList();
            foreach (var id in stop) { active.Remove(id); extents.Remove(id); }
            foreach (var id in start.Keys) { active[id] = 0; order[id] = step; supported[id] = step; lastSeenOf[id] = trackEnd[id]; }

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
            var sample = new PersonMaskSample(time, entries.ToImmutable());
            samples.Add(sample);
            chunk.Add(time, sample);
            lastMasks = masks;
            Report(progress, time - from, to - from);
        }
        chunk.Finish();
        progress?.Report(1);
        return new PersonMaskTrack(gridWidth ?? 1, gridHeight ?? 1, MaskInterval, samples.ToImmutable(), vision.ModelId);
    }

    /// <summary>A person's detection at least this confident anchors its object again (<see cref="Partial"/>).</summary>
    public const float ReanchorScore = 0.6f;

    /// <summary>At most one anchoring of an object per this many mask steps (1.5 s at 15 Hz).</summary>
    public const int ReanchorSteps = 22;

    /// <summary>An object without a detected person at its mask for this many steps (3 s) is stopped.</summary>
    public const int OrphanSteps = 45;

    /// <summary>Whether a mask's <paramref name="extent"/> holds only a part of the person in <paramref name="box"/> (its
    /// confident, uncut detector box): clearly lower or narrower than the box, or hardly overlapping it. A whole person's mask
    /// spans the box (the box is the person's extent); a T-shirt alone is about half as high.</summary>
    public static bool Partial(NormalizedBox extent, NormalizedBox box) =>
        extent.Height < 0.7 * box.Height || extent.Width < 0.6 * box.Width || extent.IoU(box) < 0.5;

    /// <summary>Whether a person's <paramref name="mask"/> has lost its torso: under half of the grid cells within
    /// <see cref="TorsoReach"/> of the pose's torso centre <paramref name="torso"/> belong to the mask (a mask with a hole
    /// where the body is).</summary>
    public static bool LostTorso(byte[] mask, NormalizedPoint torso, int width, int height)
    {
        int cx = (int)(torso.X * width), cy = (int)(torso.Y * height), inside = 0, all = 0;
        for (var y = cy - TorsoReach; y <= cy + TorsoReach; y++)
            for (var x = cx - TorsoReach; x <= cx + TorsoReach; x++)
            {
                if (x < 0 || y < 0 || x >= width || y >= height) continue;
                all++;
                if (mask[y * width + x] >= 128) inside++;
            }
        return all > 0 && inside * 2 < all;
    }

    /// <summary>The half side (grid cells, of <see cref="MaskLongSide"/>) of the square around the torso centre.</summary>
    public const int TorsoReach = 3;

    /// <summary>Whether another object's mask covers a quarter of <paramref name="box"/> or more (then the box may hold that
    /// person too, and anchoring on it could take them in).</summary>
    public static bool OverlapsAnother(NormalizedBox box, IReadOnlyDictionary<int, NormalizedBox> extents, int self)
    {
        if (box.Area <= 0) return false;
        foreach (var (id, extent) in extents)
            if (id != self && Intersection(box, extent) >= 0.25 * box.Area) return true;
        return false;
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
        var confident = new Dictionary<int, int>();
        foreach (var sample in tracks.Samples)
        {
            for (var i = 0; i < sample.People.Length; i++)
            {
                var id = sample.TrackIdOf(i);
                if (id < 0) continue;
                var p = sample.People[i];
                var firstConfident = p.Score >= PersonTracker.HighScore ? sample.Time : (MediaTime?)null;
                if (p.Score >= PersonTracker.HighScore) confident[id] = confident.GetValueOrDefault(id) + 1;
                info[id] = info.TryGetValue(id, out var x)
                    ? (x.Start, sample.Time, Math.Max(x.Height, p.Box.Height), x.FirstConfident ?? firstConfident)
                    : (sample.Time, sample.Time, p.Box.Height, firstConfident);
            }
        }
        // A track confidently seen fewer than MinConfident times is a weak false box more often than a person.
        return info.Where(p => p.Value.Height >= MinPersonHeight && p.Value.FirstConfident is not null &&
                               confident.GetValueOrDefault(p.Key) >= MinConfident &&
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

    private async IAsyncEnumerable<(DecodedFrame Frame, MediaTime Time)> Frames(Source source, MediaTime from,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await decoder.OpenAsync(new VideoDecodeRequest
        {
            FilePath = source.FilePath,
            StartTime = source.FileStart,
            FirstSamplePoint = new SourceSamplePoint(Math.Max(0, from.Ticks), 1),
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
