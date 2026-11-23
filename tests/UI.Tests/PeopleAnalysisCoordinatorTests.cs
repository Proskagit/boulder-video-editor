using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D061 §5: the people analyses run in the background for the videos whose clips use Follow (tracks) or hide their outlines
/// behind people (tracks, then masks); the results reach the snapshot and the export; the user cancels and retries; a
/// missing model fails with its message, never silently; a cached analysis is never redone, a changed file is; switching
/// Follow off stops the analysis nobody needs; nothing is an undo step.
/// </summary>
public sealed class PeopleAnalysisCoordinatorTests : IDisposable
{
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TestUiThread _ui = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-pac-" + Guid.NewGuid().ToString("N"));

    public PeopleAnalysisCoordinatorTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        _ui.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private VideoClip AddClip(FollowSettings? follow, bool occlusion = false, string? path = null, double seconds = 1, bool routes = true)
    {
        path ??= Path.Combine(_folder, $"{Guid.NewGuid():N}.mp4");
        if (!File.Exists(path)) File.WriteAllBytes(path, new byte[100]);
        var asset = new MediaAsset
        {
            FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(seconds), Width = 64, Height = 36, DisplayWidth = 64, DisplayHeight = 36,
                FrameRate = FrameRate.Fps30, StartTime = MediaTime.Zero
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        var clip = new VideoClip { MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(seconds), Follow = follow, HidesOutlinesBehindPeople = occlusion };
        clip.SourceOut = clip.Duration;
        if (routes)
        {
            // A route with one hold and no animation: its outline is shown over the whole clip (the masks are needed there).
            var square = ImmutableArray.Create(new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.6, 0.4), new NormalizedPoint(0.6, 0.6), new NormalizedPoint(0.4, 0.6));
            clip.Routes = ImmutableArray.Create(new HoldMarkup
            {
                Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(), square, default, default, MediaTime.Zero))
            });
        }
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        return clip;
    }

    // D078 amendment 1: the toolbar's badge reads what runs now.

    [Fact]
    public Task The_activity_names_the_running_analysis_and_its_share_and_ends_with_it() => _ui.Run(async () =>
    {
        var clip = AddClip(FollowSettings.Whole(), seconds: 6);
        AddClip(FollowSettings.Whole(), seconds: 4);                                 // two videos: one is done before the other
        using var coordinator = new PeopleAnalysisCoordinator(_projects, new Decoder(6), new Vision { Delay = TimeSpan.FromMilliseconds(5) });
        var seen = new List<BackgroundActivity>();
        coordinator.StatusChanged += (_, _) => { if (coordinator.Activity is { } a) seen.Add(a); };
        await coordinator.IdleAsync();
        Assert.NotEmpty(seen);
        Assert.All(seen, a => Assert.Equal(AiVideoEditor.UI.Localization.Strings.Activity_PeopleTracks, a.Label));
        Assert.All(seen, a => Assert.InRange(a.Progress!.Value, 0, 1));
        Assert.Contains(seen, a => a.Progress > 0);
        // Amendment 3: each run's own share — it grows within a run and starts again at the next one.
        Assert.Null(coordinator.Activity);
        var badge = new AiVideoEditor.UI.ViewModels.BackgroundActivityViewModel(coordinator);
        Assert.False(badge.IsBusy);
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
    });

    // D078 §3: the attempt's events' windows — tracks after the clips' own needs, on a GPU only, never in the export's wait.

    [Fact]
    public Task The_events_windows_are_analysed_after_the_clips_needs_and_are_not_the_exports() => _ui.Run(async () =>
    {
        var followed = AddClip(FollowSettings.Of(new FollowPart(MediaTime.FromSeconds(4), MediaTime.FromSeconds(5))), seconds: 20);
        var other = AddClip(null, seconds: 20, routes: false);
        var decoder = new Decoder(20);
        using var coordinator = new PeopleAnalysisCoordinator(_projects, decoder, new Vision())
        {
            EventSpans = _ => new[] { (other.MediaAssetId, MediaTime.FromSeconds(10), MediaTime.FromSeconds(13)) }
        };
        coordinator.RefreshNeeds();
        await coordinator.IdleAsync();
        // The followed part's chunks first (from 2 s), then the window's [10, 14) of the other video.
        Assert.Equal(new[] { MediaTime.FromSeconds(2), MediaTime.FromSeconds(10) }, decoder.Starts);
        Assert.True(coordinator.HasTracks(other.MediaAssetId, MediaTime.FromSeconds(10), MediaTime.FromSeconds(13)));
        Assert.False(coordinator.HasTracks(other.MediaAssetId, MediaTime.FromSeconds(0), MediaTime.FromSeconds(13)));
        Assert.True(coordinator.Analyses.Tracks.ContainsKey(other.MediaAssetId));
        Assert.Equal(PeopleAnalysisState.None, coordinator.StatusOf(other.MediaAssetId, PeopleAnalysisKind.Masks).State);
        Assert.DoesNotContain(other.MediaAssetId, coordinator.ForExport().Pending);
        Assert.False(coordinator.EventsNeedGpu);
    });

    [Fact]
    public Task Without_a_GPU_the_events_windows_are_not_analysed_and_the_video_does_not_fail() => _ui.Run(async () =>
    {
        var other = AddClip(null, seconds: 20, routes: false);
        var decoder = new Decoder(20);
        using var coordinator = new PeopleAnalysisCoordinator(_projects, decoder, new Vision { DetectorOnCpu = true })
        {
            EventSpans = _ => new[] { (other.MediaAssetId, MediaTime.FromSeconds(10), MediaTime.FromSeconds(13)) }
        };
        coordinator.RefreshNeeds();
        await coordinator.IdleAsync();
        Assert.Empty(decoder.Starts);
        Assert.True(coordinator.EventsNeedGpu);
        Assert.NotEqual(PeopleAnalysisState.Failed, coordinator.StatusOf(other.MediaAssetId, PeopleAnalysisKind.Tracks).State);
    });

    // D061 amendment 1: only what the clips need, in chunks of 2 s.

    [Fact]
    public Task A_followed_part_analyses_only_the_chunks_around_it_as_one_track() => _ui.Run(async () =>
    {
        var clip = AddClip(FollowSettings.Of(new FollowPart(MediaTime.FromSeconds(4), MediaTime.FromSeconds(5))), seconds: 20);
        var decoder = new Decoder(20);
        var vision = new Vision();
        using var coordinator = new PeopleAnalysisCoordinator(_projects, decoder, vision);
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        // [4, 5) widened by 1 s: chunks [2, 4) and [4, 6) — 4 s of the 20 s file, one decoding from 2 s.
        Assert.Equal(new[] { MediaTime.FromSeconds(2) }, decoder.Starts);
        Assert.Equal(40, vision.Detections);
        var tracks = coordinator.Analyses.Tracks[clip.MediaAssetId];
        Assert.Equal(MediaTime.FromSeconds(2), tracks.Samples[0].Time);
        Assert.Single(tracks.Tracks);                                        // the person crosses the chunk boundary in one track

        // The part made longer: only the new chunks are analysed.
        clip.Follow = FollowSettings.Of(new FollowPart(MediaTime.FromSeconds(4), MediaTime.FromSeconds(9)));
        _projects.NotifyTimelineChanged();
        await coordinator.IdleAsync();
        Assert.Equal(new[] { MediaTime.FromSeconds(2), MediaTime.FromSeconds(6) }, decoder.Starts);
        Assert.Equal(80, vision.Detections);
        Assert.Single(coordinator.Analyses.Tracks[clip.MediaAssetId].Tracks);
    });

    [Fact]
    public Task Stop_before_the_analysis_reaches_the_parts_end_never_analyses_past_it() => _ui.Run(async () =>
    {
        // «Следить отсюда» at 4 s of a 20 s file (the part open to the end), «Остановить» at 6 s while the analysis runs.
        var clip = AddClip(FollowSettings.Of(new FollowPart(MediaTime.FromSeconds(4))), seconds: 20, routes: false);
        var vision = new Vision { Delay = TimeSpan.FromMilliseconds(3) };
        using var coordinator = new PeopleAnalysisCoordinator(_projects, new Decoder(20), vision);
        var stopped = false;
        coordinator.AnalysesChanged += (_, _) =>
        {
            if (stopped) return;
            stopped = true;
            clip.Follow = FollowSettings.Of(new FollowPart(MediaTime.FromSeconds(4), MediaTime.FromSeconds(6)));
            _projects.NotifyTimelineChanged();
        };
        await coordinator.IdleAsync();
        Assert.True(stopped);
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        // [4, 6) widened: [3, 7) → chunks [2, 4), [4, 6), [6, 8): 60 detections, at most one chunk more in flight.
        Assert.InRange(vision.Detections, 60, 80);
    });

    [Fact]
    public Task A_clip_without_outlines_needs_no_masks_and_the_masks_follow_the_outlines() => _ui.Run(async () =>
    {
        var clip = AddClip(null, occlusion: true, seconds: 6, routes: false);
        var vision = new Vision();
        using var coordinator = new PeopleAnalysisCoordinator(_projects, new Decoder(6), vision);
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks).State);
        Assert.Equal(0, vision.Detections);                                  // nothing to hide: nothing analysed
        Assert.Equal(0, vision.Segmenters);
        Assert.Empty(coordinator.Analyses.Masks);
        Assert.Empty(coordinator.ForExport().Pending);
    });

    [Fact]
    public Task Several_parts_of_one_clip_share_one_analysis_of_their_chunks() => _ui.Run(async () =>
    {
        var clip = AddClip(new FollowSettings(ImmutableArray.Create(
            new FollowPart(MediaTime.FromSeconds(2), MediaTime.FromSeconds(4)),
            new FollowPart(MediaTime.FromSeconds(12), MediaTime.FromSeconds(13)))), seconds: 20);
        var decoder = new Decoder(20);
        using var coordinator = new PeopleAnalysisCoordinator(_projects, decoder, new Vision());
        await coordinator.IdleAsync();
        Assert.Equal(new[] { MediaTime.FromSeconds(0), MediaTime.FromSeconds(10) }, decoder.Starts);   // [1, 5) and [11, 14)
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
    });

    [Fact]
    public Task A_clip_with_Follow_gets_its_videos_tracks_and_no_masks() => _ui.Run(async () =>
    {
        var clip = AddClip(FollowSettings.Whole());
        var decoder = new Decoder();
        using var coordinator = new PeopleAnalysisCoordinator(_projects, decoder, new Vision());
        Assert.True(coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).IsBusy);
        Assert.Contains(clip.MediaAssetId, coordinator.ForExport().Pending);   // the export waits

        await coordinator.IdleAsync();

        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        Assert.Equal(PeopleAnalysisState.None, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks).State);
        Assert.Equal(10, coordinator.Analyses.Tracks[clip.MediaAssetId].Samples.Length);
        Assert.Empty(coordinator.Analyses.Masks);
        Assert.Empty(coordinator.ForExport().Pending);
        Assert.Same(coordinator.Analyses, coordinator.ForExport().Analyses);
        Assert.False(_undo.CanUndo);
    });

    [Fact]
    public Task Occlusion_analyses_the_tracks_then_the_masks() => _ui.Run(async () =>
    {
        var clip = AddClip(null, occlusion: true);
        using var coordinator = new PeopleAnalysisCoordinator(_projects, new Decoder(), new Vision());
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks).State);
        var masks = coordinator.Analyses.Masks[clip.MediaAssetId];
        Assert.Equal(15, masks.Samples.Length);
        Assert.NotEmpty(masks.PersonIds);
    });

    // D061 amendment 1 §3: Follow and Occlusion share one analysis of the video — one detection pass (the tracks) and one
    // segmentation pass (the masks), whichever was switched on first; the decoder opens the file once per pass.

    [Fact]
    public Task A_Follow_that_switched_Occlusion_on_analyses_the_video_once() => _ui.Run(async () =>
    {
        var clip = AddClip(FollowSettings.Whole(), occlusion: true);          // scenario A: one step switched both on
        var decoder = new Decoder();
        var vision = new Vision();
        using var coordinator = new PeopleAnalysisCoordinator(_projects, decoder, vision);
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks).State);
        Assert.Equal(2, decoder.Opened);                                     // the tracks, then the masks
        Assert.Equal(10, vision.Detections);                                 // one detection pass: every 0.1 s of one second
        Assert.Equal(1, vision.Segmenters);

        // A second clip of the same file (a split half, a copy) with Follow and Occlusion analyses nothing.
        AddClip(FollowSettings.Whole(), occlusion: true, path: _projects.Current.MediaAssets.Single().FilePath);
        _projects.NotifyTimelineChanged();
        await coordinator.IdleAsync();
        Assert.Equal(2, decoder.Opened);
        Assert.Equal(1, vision.Segmenters);
    });

    [Fact]
    public Task Follow_switched_on_after_Occlusion_reuses_its_analysis() => _ui.Run(async () =>
    {
        var clip = AddClip(null, occlusion: true);                           // scenario B
        var decoder = new Decoder();
        var vision = new Vision();
        using var coordinator = new PeopleAnalysisCoordinator(_projects, decoder, vision);
        await coordinator.IdleAsync();
        Assert.Equal(2, decoder.Opened);

        clip.Follow = FollowSettings.Whole();
        _projects.NotifyTimelineChanged();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        await coordinator.IdleAsync();
        Assert.Equal(2, decoder.Opened);
        Assert.Equal(1, vision.Segmenters);
    });

    [Fact]
    public Task Occlusion_switched_on_while_Follows_analysis_runs_never_starts_it_again() => _ui.Run(async () =>
    {
        var clip = AddClip(FollowSettings.Whole());                            // scenario C: one after the other, at once
        var decoder = new Decoder();
        var vision = new Vision { Delay = TimeSpan.FromMilliseconds(5) };
        using var coordinator = new PeopleAnalysisCoordinator(_projects, decoder, vision);
        Assert.Equal(PeopleAnalysisState.Analysing, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        clip.HidesOutlinesBehindPeople = true;
        _projects.NotifyTimelineChanged();
        Assert.Equal(PeopleAnalysisState.Analysing, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        Assert.Equal(PeopleAnalysisState.Waiting, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks).State);
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks).State);
        Assert.Equal(2, decoder.Opened);
        Assert.Equal(10, vision.Detections);
        Assert.Equal(1, vision.Segmenters);
    });

    [Fact]
    public Task The_user_cancels_and_retries() => _ui.Run(async () =>
    {
        var clip = AddClip(FollowSettings.Whole());
        var vision = new Vision { Delay = TimeSpan.FromMilliseconds(30) };
        using var coordinator = new PeopleAnalysisCoordinator(_projects, new Decoder(), vision);
        coordinator.Cancel(clip.MediaAssetId);
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Cancelled, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        Assert.Contains(clip.MediaAssetId, coordinator.ForExport().Pending);   // a stopped analysis still blocks the export
        Assert.Empty(coordinator.Analyses.Tracks);

        coordinator.Retry(clip.MediaAssetId);
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
    });

    [Fact]
    public Task A_missing_model_fails_with_its_message() => _ui.Run(async () =>
    {
        var clip = AddClip(FollowSettings.Whole(), occlusion: true);
        using var coordinator = new PeopleAnalysisCoordinator(_projects, new Decoder(), new Vision { Missing = "no people models here" });
        await coordinator.IdleAsync();
        var status = coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks);
        Assert.Equal(PeopleAnalysisState.Failed, status.State);
        Assert.Equal("no people models here", status.Message);
        Assert.Equal(PeopleAnalysisState.Failed, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks).State);
        var export = coordinator.ForExport();
        Assert.Empty(export.Pending);                                      // a failure never blocks: the export warns
        Assert.Equal("no people models here", export.FailedTracks[clip.MediaAssetId]);
    });

    [Fact]
    public Task Without_a_GPU_the_masks_fail_but_Follow_works() => _ui.Run(async () =>
    {
        var clip = AddClip(FollowSettings.Whole(), occlusion: true);
        using var coordinator = new PeopleAnalysisCoordinator(_projects, new Decoder(), new Vision { NoGpu = true });
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        Assert.Equal(PeopleAnalysisState.Failed, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks).State);
        Assert.Contains(clip.MediaAssetId, coordinator.ForExport().FailedMasks.Keys);
    });

    [Fact]
    public Task A_cached_analysis_is_not_redone_and_a_changed_file_is() => _ui.Run(async () =>
    {
        var clip = AddClip(FollowSettings.Whole());
        var path = _projects.Current.MediaAssets.Single().FilePath;
        var cache = new MemoryCache();
        using (var first = new PeopleAnalysisCoordinator(_projects, new Decoder(), new Vision(), cache))
            await first.IdleAsync();

        var decoder = new Decoder();
        using (var second = new PeopleAnalysisCoordinator(_projects, decoder, new Vision(), cache))
        {
            await second.IdleAsync();
            Assert.Equal(0, decoder.Opened);
            Assert.Equal(PeopleAnalysisState.Ready, second.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        }

        File.WriteAllBytes(path, new byte[200]);   // the video was replaced on disk
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var again = new Decoder();
        using (var third = new PeopleAnalysisCoordinator(_projects, again, new Vision(), cache))
        {
            await third.IdleAsync();
            Assert.Equal(1, again.Opened);
        }
    });

    [Fact]
    public Task Switching_Follow_off_stops_the_analysis() => _ui.Run(async () =>
    {
        var clip = AddClip(FollowSettings.Whole());
        using var coordinator = new PeopleAnalysisCoordinator(_projects, new Decoder(), new Vision { Delay = TimeSpan.FromMilliseconds(50) });
        clip.Follow = null;
        _projects.NotifyTimelineChanged();
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.None, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        Assert.Empty(coordinator.ForExport().Pending);
    });

    [Fact]
    public Task A_project_change_during_an_analysis_publishes_nothing_stale() => _ui.Run(async () =>
    {
        AddClip(FollowSettings.Whole());
        using var coordinator = new PeopleAnalysisCoordinator(_projects, new Decoder(), new Vision { Delay = TimeSpan.FromMilliseconds(20) });
        Assert.NotEmpty(coordinator.ForExport().Pending);
        _projects.CreateNew("Другой");                       // while the first video is analysed
        await coordinator.IdleAsync();
        Assert.Empty(coordinator.Analyses.Tracks);             // the old project's result never arrives
        Assert.Empty(coordinator.ForExport().Pending);

        var clip = AddClip(FollowSettings.Whole());              // the new project's own video is analysed
        _projects.NotifyTimelineChanged();
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        Assert.Single(coordinator.Analyses.Tracks);
    });

    /// <summary>30 fps frames of 64 × 36 px for <c>seconds</c> (one by default), from the requested start.</summary>
    private sealed class Decoder(int seconds = 1) : IVideoDecoder
    {
        public int Opened { get; private set; }
        public List<MediaTime> Starts { get; } = new();

        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            Opened++;
            var start = new MediaTime((long)(request.FirstSamplePoint.TicksNumerator / request.FirstSamplePoint.TicksDenominator));
            Starts.Add(start);
            return Task.FromResult<IVideoFrameStream>(new Stream((int)(start.Ticks * 30 / TimeSpan.TicksPerSecond), seconds * 30));
        }

        private sealed class Stream(int first, int end) : IVideoFrameStream
        {
            private int _next = first;

            public ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult<DecodedFrame?>(_next >= end ? null
                    : new DecodedFrame(64, 36, 256, new byte[64 * 36 * 4], new SourceTimestamp(_next++, new TimeBase(1, 30))));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>One person in the middle; the segmenter paints its box.</summary>
    private sealed class Vision : IPeopleVision
    {
        public string? Missing { get; init; }
        public bool NoGpu { get; init; }
        public TimeSpan Delay { get; init; }
        public int Detections;
        public int Segmenters;
        public string ModelId => "fake";
        public bool DetectorOnCpu { get; init; }
        public InferenceDevice DetectorDevice => DetectorOnCpu ? InferenceDevice.Cpu : InferenceDevice.Gpu;

        public InferenceDevice PrepareDetector(CancellationToken cancellationToken)
        {
            if (Missing is not null) throw new PeopleModelException(Missing);
            return DetectorDevice;
        }

        public IReadOnlyList<PersonDetection> Detect(DecodedFrame frame, CancellationToken cancellationToken)
        {
            if (Missing is not null) throw new PeopleModelException(Missing);
            if (Delay > TimeSpan.Zero) Task.Delay(Delay, cancellationToken).GetAwaiter().GetResult();
            Interlocked.Increment(ref Detections);
            var h = new float[PersonAppearance.Length];
            h[0] = 1;
            return new[] { new PersonDetection(new NormalizedBox(0.4, 0.2, 0.6, 0.8), 0.9f, null, ImmutableArray.Create(h)) };
        }

        public IPersonSegmenter CreateSegmenter(CancellationToken cancellationToken)
        {
            if (NoGpu) throw new PeopleModelException("needs a GPU", requiresGpu: true);
            Interlocked.Increment(ref Segmenters);
            return new Segmenter();
        }

        private sealed class Segmenter : IPersonSegmenter
        {
            private readonly HashSet<int> _people = new();

            public IReadOnlyList<SegmentedPerson> Step(DecodedFrame frame, IReadOnlyList<SegmenterPrompt> start, IReadOnlyCollection<int> stop,
                int gridWidth, int gridHeight, CancellationToken cancellationToken)
            {
                foreach (var p in start) _people.Add(p.PersonId);
                foreach (var id in stop) _people.Remove(id);
                return _people.Select(id =>
                {
                    var grid = new byte[gridWidth * gridHeight];
                    for (var y = gridHeight / 4; y < gridHeight * 3 / 4; y++)
                        for (var x = gridWidth * 2 / 5; x < gridWidth * 3 / 5; x++) grid[y * gridWidth + x] = 255;
                    return new SegmentedPerson(id, true, grid);
                }).ToList();
            }

            public void Dispose() { }
        }
    }

    /// <summary>The cache in memory, with a fingerprint from the file's size and write time.</summary>
    private sealed class MemoryCache : IPeopleAnalysisCache
    {
        private readonly Dictionary<string, object> _entries = new();

        public string? Fingerprint(string filePath)
        {
            var info = new FileInfo(filePath);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : null;
        }

        public PersonTracks? TryLoadTracks(string key) => _entries.GetValueOrDefault(key) as PersonTracks;
        public void SaveTracks(string key, PersonTracks tracks) => _entries[key] = tracks;
        public PersonMaskTrack? TryLoadMasks(string key) => _entries.GetValueOrDefault(key) as PersonMaskTrack;
        public void SaveMasks(string key, PersonMaskTrack masks) => _entries[key] = masks;
    }
}
