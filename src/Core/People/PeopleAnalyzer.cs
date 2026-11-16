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

    /// <summary>Pass 2: every person's mask, separately.</summary>
    public async Task<PersonMaskTrack> SegmentAsync(Source source, PersonTracks tracks, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var segmenter = vision.CreateSegmenter(cancellationToken);
        var people = SegmentedTracks(tracks);
        var samples = ImmutableArray.CreateBuilder<PersonMaskSample>();
        var picker = new IntervalPicker(MaskInterval);
        var active = new Dictionary<int, int>();          // track id → steps not visible
        var started = new HashSet<int>();
        byte[]? segmented = null;                          // the last step's union of the visible people
        int? gridWidth = null, gridHeight = null;
        await foreach (var (frame, time) in Frames(source, cancellationToken))
        {
            if (!picker.Take(time)) continue;
            if (gridWidth is null)
            {
                var scale = (double)MaskLongSide / Math.Max(frame.Width, frame.Height);
                gridWidth = Math.Max(1, (int)Math.Round(frame.Width * scale));
                gridHeight = Math.Max(1, (int)Math.Round(frame.Height * scale));
            }

            var start = new List<SegmenterPrompt>();
            var stop = new List<int>();
            foreach (var person in people)
            {
                var seen = DetectionNear(tracks, person.Id, time);
                if (!started.Contains(person.Id))
                {
                    // Started at its first confident detection (the first mask step at or after it).
                    if (seen is { Score: >= PersonTracker.HighScore } d && time >= person.Start && active.Count < MaxPeople &&
                        !AlreadySegmented(segmented, gridWidth.Value, gridHeight!.Value, d.Box))
                    {
                        start.Add(new SegmenterPrompt(person.Id, d.Box));
                        started.Add(person.Id);
                        active[person.Id] = 0;
                    }
                    continue;
                }
                if (!active.TryGetValue(person.Id, out var missing)) continue;
                if (missing >= 3 && seen is { Score: >= PersonTracker.HighScore } again &&
                    !AlreadySegmented(segmented, gridWidth.Value, gridHeight!.Value, again.Box))
                {
                    start.Add(new SegmenterPrompt(person.Id, again.Box));   // lost by the segmenter, seen by the detector
                    active[person.Id] = 0;
                }
                else if (time > person.End + MediaTime.FromSeconds(1) && missing >= 5)
                {
                    stop.Add(person.Id);
                    active.Remove(person.Id);
                }
            }

            var result = segmenter.Step(frame, start, stop, gridWidth ?? 1, gridHeight ?? 1, cancellationToken);
            var entries = ImmutableArray.CreateBuilder<PersonMaskEntry>();
            segmented = new byte[(gridWidth ?? 1) * (gridHeight ?? 1)];
            foreach (var person in result)
            {
                if (person.Visible)
                    for (var i = 0; i < segmented.Length; i++) segmented[i] = Math.Max(segmented[i], person.Coverage[i]);
                if (active.ContainsKey(person.PersonId))
                    active[person.PersonId] = person.Visible ? 0 : active[person.PersonId] + 1;
                if (person.Visible && MaskCodec.Entry(person.PersonId, person.Coverage, gridWidth ?? 1, gridHeight ?? 1) is { } entry)
                    entries.Add(entry);
            }
            samples.Add(new PersonMaskSample(time, entries.ToImmutable()));
            Report(progress, time, source.Duration);
        }
        progress?.Report(1);
        return new PersonMaskTrack(gridWidth ?? 1, gridHeight ?? 1, MaskInterval, samples.ToImmutable(), vision.ModelId);
    }

    /// <summary>
    /// Whether at least half of <paramref name="box"/> is already covered by a segmented person: then the detection is that
    /// person (a track the tracker split — a cut-off body, a turn) or somebody behind them, and starting (or re-prompting) a
    /// second object there would split one person between two masks. It is started later, once it stands apart.
    /// </summary>
    public static bool AlreadySegmented(byte[]? segmented, int width, int height, NormalizedBox box)
    {
        if (segmented is null) return false;
        int x0 = Math.Clamp((int)(box.X0 * width), 0, width - 1), x1 = Math.Clamp((int)Math.Ceiling(box.X1 * width), x0 + 1, width);
        int y0 = Math.Clamp((int)(box.Y0 * height), 0, height - 1), y1 = Math.Clamp((int)Math.Ceiling(box.Y1 * height), y0 + 1, height);
        int covered = 0, total = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                total++;
                if (segmented[y * width + x] >= 128) covered++;
            }
        return total > 0 && covered * 2 >= total;
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
