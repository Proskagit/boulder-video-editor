using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests.People;

/// <summary>
/// D061 §2: the two passes over a video with fakes for the decoder and the models — the detection frames are taken every
/// 0.1 s and linked into tracks; the segmentation starts every person at its first confident detection, keeps separate
/// masks, re-prompts a person the segmenter lost while the detector sees it, skips people too small to stand in front of a
/// hold, and stops when cancelled.
/// </summary>
public class PeopleAnalyzerTests
{
    /// <summary>30 fps frames for <paramref name="seconds"/>, 64 × 36 px.</summary>
    internal sealed class FakeDecoder(double seconds) : IVideoDecoder
    {
        public int Opened { get; private set; }

        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            Opened++;
            return Task.FromResult<IVideoFrameStream>(new Stream((int)(seconds * 30)));
        }

        private sealed class Stream(int count) : IVideoFrameStream
        {
            private int _next;

            public ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (_next >= count) return ValueTask.FromResult<DecodedFrame?>(null);
                var frame = new DecodedFrame(64, 36, 64 * 4, new byte[64 * 36 * 4], new SourceTimestamp(_next++, new TimeBase(1, 30)));
                return ValueTask.FromResult<DecodedFrame?>(frame);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A climber walking right (always), a passer-by from 1 s to 2 s; a tiny far person always. The segmenter
    /// paints each followed person's box; it "loses" the climber from 1.0 s to 1.5 s and sees the passer-by leave at 2 s.</summary>
    internal sealed class FakeVision : IPeopleVision
    {
        public List<(double Time, int Id)> Started { get; } = new();
        public List<int> Stopped { get; } = new();
        public int DetectCalls { get; private set; }
        public Action? OnDetect { get; init; }

        public string ModelId => "fake";
        public InferenceDevice DetectorDevice => InferenceDevice.Cpu;

        public static double Seconds(DecodedFrame frame) => frame.Timestamp.Pts / 30.0;

        public IReadOnlyList<PersonDetection> Detect(DecodedFrame frame, CancellationToken cancellationToken)
        {
            DetectCalls++;
            OnDetect?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var t = Seconds(frame);
            var people = new List<PersonDetection>
            {
                PeopleTestData.Person(0.2 + t * 0.05, 0.5, colour: 0),
                PeopleTestData.Person(0.9, 0.1, w: 0.02, h: 0.05, colour: 5)    // far away: never segmented
            };
            if (t is >= 1 and < 2) people.Add(PeopleTestData.Person(0.8 - (t - 1) * 0.2, 0.55, colour: 12));
            return people;
        }

        public IPersonSegmenter CreateSegmenter(CancellationToken cancellationToken) => new Segmenter(this);

        private sealed class Segmenter(FakeVision owner) : IPersonSegmenter
        {
            private readonly Dictionary<int, NormalizedBox> _boxes = new();

            public IReadOnlyList<SegmentedPerson> Step(DecodedFrame frame, IReadOnlyList<SegmenterPrompt> start, IReadOnlyCollection<int> stop,
                int gridWidth, int gridHeight, CancellationToken cancellationToken)
            {
                var t = Seconds(frame);
                foreach (var id in stop) { _boxes.Remove(id); owner.Stopped.Add(id); }
                foreach (var p in start) { _boxes[p.PersonId] = p.Box; owner.Started.Add((t, p.PersonId)); }
                return _boxes.Select(b =>
                {
                    // The climber is lost by the model for half a second; the passer-by (right half) leaves at 2 s.
                    var lost = (b.Key == 0 && t is >= 1.0 and < 1.5) || (b.Value.Center.X > 0.5 && t >= 2.0);
                    var grid = new byte[gridWidth * gridHeight];
                    if (!lost)
                        for (var y = (int)(b.Value.Y0 * gridHeight); y < (int)(b.Value.Y1 * gridHeight); y++)
                            for (var x = (int)(b.Value.X0 * gridWidth); x < (int)(b.Value.X1 * gridWidth); x++) grid[y * gridWidth + x] = 255;
                    return new SegmentedPerson(b.Key, !lost, grid);
                }).ToList();
            }

            public void Dispose() { }
        }
    }

    private static readonly PeopleAnalyzer.Source Video = new(@"C:\v.mp4", MediaTime.Zero, null, null, MediaTime.FromSeconds(3));

    [Fact]
    public async Task The_detection_pass_samples_every_tenth_of_a_second_and_links_tracks()
    {
        var vision = new FakeVision();
        var progress = new List<double>();
        var tracks = await new PeopleAnalyzer(new FakeDecoder(3), vision).DetectAsync(Video, new SyncProgress(progress.Add), CancellationToken.None);
        Assert.Equal(30, tracks.Samples.Length);
        Assert.Equal(30, vision.DetectCalls);
        Assert.Equal("fake", tracks.ModelId);
        Assert.Equal(3, tracks.Tracks.Length);   // the climber, the far person, the passer-by
        Assert.Equal(1, progress[^1]);
        Assert.True(progress.Zip(progress.Skip(1)).All(p => p.Second >= p.First));
    }

    [Fact]
    public async Task The_segmentation_keeps_separate_masks_and_re_prompts_a_lost_person()
    {
        var vision = new FakeVision();
        var analyzer = new PeopleAnalyzer(new FakeDecoder(3), vision);
        var tracks = await analyzer.DetectAsync(Video, null, CancellationToken.None);
        var masks = await analyzer.SegmentAsync(Video, tracks, null, CancellationToken.None);

        Assert.Equal(45, masks.Samples.Length);   // 15 per second
        Assert.Equal((640, 360), (masks.Width, masks.Height));   // the long side 640, the frame's aspect ratio
        var climber = tracks.Samples[0].TrackIds[0];
        var passer = tracks.Samples[15].TrackIds[2];
        // The far person is never segmented; the climber at once; the passer-by when it appears.
        Assert.DoesNotContain(vision.Started, s => s.Id == tracks.Samples[0].TrackIds[1]);
        Assert.Contains(vision.Started, s => s.Id == climber && s.Time == 0);
        Assert.Contains(vision.Started, s => s.Id == passer && s.Time is >= 1 and < 1.2);
        // Lost by the segmenter at 1.0 s while the detector sees it: prompted again after three missing steps.
        Assert.Contains(vision.Started, s => s.Id == climber && s.Time is > 1.0 and < 1.5);
        // Separate masks: at 1.3 s the passer-by has its own.
        var people = masks.PeopleAt(MediaTime.FromSeconds(1.6));
        Assert.Equal(new[] { climber, passer }.OrderBy(i => i), people.Select(p => p.PersonId).OrderBy(i => i));
        // The passer-by's track ended at 2 s: it is stopped about a second later.
        Assert.Contains(passer, vision.Stopped);
    }

    [Fact]
    public void A_detection_inside_another_persons_mask_starts_nothing()
    {
        var extents = new Dictionary<int, NormalizedBox> { [1] = new(0.2, 0.2, 0.6, 0.8) };
        Assert.True(PeopleAnalyzer.InsideAnother(new NormalizedBox(0.25, 0.5, 0.5, 0.8), extents, self: 2));   // the climber's legs
        Assert.False(PeopleAnalyzer.InsideAnother(new NormalizedBox(0.25, 0.5, 0.5, 0.8), extents, self: 1));  // its own
        Assert.False(PeopleAnalyzer.InsideAnother(new NormalizedBox(0.55, 0.2, 0.95, 0.8), extents, self: 2)); // somebody beside
    }

    [Fact]
    public void The_younger_of_two_objects_on_one_person_is_stopped_but_two_people_side_by_side_stay()
    {
        const int w = 100, h = 100;
        byte[] Rect(int x0, int y0, int x1, int y1)
        {
            var g = new byte[w * h];
            for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++) g[y * w + x] = 255;
            return g;
        }
        NormalizedBox Box(int x0, int y0, int x1, int y1) => new(x0 / 100.0, y0 / 100.0, x1 / 100.0, y1 / 100.0);
        // One person: the torso (object 1, older) and the legs (object 2) as two touching masks inside one extent.
        var masks = new Dictionary<int, byte[]> { [1] = Rect(30, 20, 60, 90), [2] = Rect(32, 55, 58, 90) };
        var extents = new Dictionary<int, NormalizedBox> { [1] = Box(30, 20, 60, 90), [2] = Box(32, 55, 58, 90) };
        Assert.Equal(new[] { 2 }, PeopleAnalyzer.Duplicates(masks, extents, new Dictionary<int, long> { [1] = 1, [2] = 5 }, w, h));
        // Two people touching side by side: neither extent inside the other.
        masks = new Dictionary<int, byte[]> { [1] = Rect(20, 20, 50, 90), [2] = Rect(50, 20, 80, 90) };
        extents = new Dictionary<int, NormalizedBox> { [1] = Box(20, 20, 50, 90), [2] = Box(50, 20, 80, 90) };
        Assert.Empty(PeopleAnalyzer.Duplicates(masks, extents, new Dictionary<int, long> { [1] = 1, [2] = 5 }, w, h));
    }

    [Fact]
    public void Tracks_of_one_person_share_one_object()
    {
        // The red climber leaves for 3 s and comes back: two tracks, one person; the blue person is somebody else.
        var frames = Enumerable.Range(0, 20).Select(_ => new[] { PeopleTestData.Person(0.3, 0.5, colour: 0), PeopleTestData.Person(0.8, 0.5, colour: 12) })
            .Concat(Enumerable.Range(0, 30).Select(_ => new[] { PeopleTestData.Person(0.8, 0.5, colour: 12) }))
            .Concat(Enumerable.Range(0, 20).Select(_ => new[] { PeopleTestData.Person(0.35, 0.5, colour: 0), PeopleTestData.Person(0.8, 0.5, colour: 12) }))
            .ToArray();
        var tracks = PeopleTestData.Tracks(frames);
        var roots = PeopleAnalyzer.PersonRoots(tracks, tracks.Tracks.Select(t => t.Id));
        var red1 = PeopleTestData.IdAt(tracks, 0, 0.3);
        var red2 = PeopleTestData.IdAt(tracks, 60, 0.35);
        var blue = PeopleTestData.IdAt(tracks, 0, 0.8);
        Assert.NotEqual(red1, red2);
        Assert.Equal(roots[red1], roots[red2]);
        Assert.NotEqual(roots[red1], roots[blue]);
    }

    [Fact]
    public void A_person_back_after_a_long_absence_gets_a_new_mask_id()
    {
        // The red person leaves for 8 s (more than MaxJoinGap): another red person then is not taken for them.
        var frames = Enumerable.Range(0, 20).Select(_ => new[] { PeopleTestData.Person(0.3, 0.5, colour: 0) })
            .Concat(Enumerable.Range(0, 80).Select(_ => Array.Empty<PersonDetection>()))
            .Concat(Enumerable.Range(0, 20).Select(_ => new[] { PeopleTestData.Person(0.32, 0.5, colour: 0) })).ToArray();
        var tracks = PeopleTestData.Tracks(frames);
        var roots = PeopleAnalyzer.PersonRoots(tracks, tracks.Tracks.Select(t => t.Id));
        Assert.NotEqual(roots[PeopleTestData.IdAt(tracks, 0, 0.3)], roots[PeopleTestData.IdAt(tracks, 110, 0.32)]);
    }

    [Fact]
    public async Task A_cancelled_analysis_stops()
    {
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        var vision = new FakeVision { OnDetect = () => { if (++calls == 5) cancel.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PeopleAnalyzer(new FakeDecoder(3), vision).DetectAsync(Video, null, cancel.Token));
        Assert.True(vision.DetectCalls <= 6);
    }

    [Fact]
    public void The_interval_picker_takes_the_first_frame_of_each_interval()
    {
        var picker = new PeopleAnalyzer.IntervalPicker(MediaTime.FromSeconds(0.1));
        var taken = Enumerable.Range(0, 60).Where(i => picker.Take(new MediaTime((long)Math.Round(i * TimeSpan.TicksPerSecond / 60.0)))).ToList();
        Assert.Equal(10, taken.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i * 6), taken);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
