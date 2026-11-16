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

    private VideoClip AddClip(FollowSettings? follow, bool occlusion = false, string? path = null)
    {
        path ??= Path.Combine(_folder, $"{Guid.NewGuid():N}.mp4");
        if (!File.Exists(path)) File.WriteAllBytes(path, new byte[100]);
        var asset = new MediaAsset
        {
            FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(1), Width = 64, Height = 36, DisplayWidth = 64, DisplayHeight = 36,
                FrameRate = FrameRate.Fps30, StartTime = MediaTime.Zero
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        var clip = new VideoClip { MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(1), Follow = follow, HidesOutlinesBehindPeople = occlusion };
        clip.SourceOut = clip.Duration;
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        return clip;
    }

    [Fact]
    public Task A_clip_with_Follow_gets_its_videos_tracks_and_no_masks() => _ui.Run(async () =>
    {
        var clip = AddClip(new FollowSettings());
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
        var clip = AddClip(new FollowSettings(), occlusion: true);          // scenario A: one step switched both on
        var decoder = new Decoder();
        var vision = new Vision();
        using var coordinator = new PeopleAnalysisCoordinator(_projects, decoder, vision);
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks).State);
        Assert.Equal(2, decoder.Opened);                                     // the tracks, then the masks
        Assert.Equal(10, vision.Detections);                                 // one detection pass: every 0.1 s of one second
        Assert.Equal(1, vision.Segmenters);

        // A second clip of the same file (a split half, a copy) with Follow and Occlusion analyses nothing.
        AddClip(new FollowSettings(), occlusion: true, path: _projects.Current.MediaAssets.Single().FilePath);
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

        clip.Follow = new FollowSettings();
        _projects.NotifyTimelineChanged();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        await coordinator.IdleAsync();
        Assert.Equal(2, decoder.Opened);
        Assert.Equal(1, vision.Segmenters);
    });

    [Fact]
    public Task Occlusion_switched_on_while_Follows_analysis_runs_never_starts_it_again() => _ui.Run(async () =>
    {
        var clip = AddClip(new FollowSettings());                            // scenario C: one after the other, at once
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
        var clip = AddClip(new FollowSettings());
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
        var clip = AddClip(new FollowSettings(), occlusion: true);
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
        var clip = AddClip(new FollowSettings(), occlusion: true);
        using var coordinator = new PeopleAnalysisCoordinator(_projects, new Decoder(), new Vision { NoGpu = true });
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        Assert.Equal(PeopleAnalysisState.Failed, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks).State);
        Assert.Contains(clip.MediaAssetId, coordinator.ForExport().FailedMasks.Keys);
    });

    [Fact]
    public Task A_cached_analysis_is_not_redone_and_a_changed_file_is() => _ui.Run(async () =>
    {
        var clip = AddClip(new FollowSettings());
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
        var clip = AddClip(new FollowSettings());
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
        AddClip(new FollowSettings());
        using var coordinator = new PeopleAnalysisCoordinator(_projects, new Decoder(), new Vision { Delay = TimeSpan.FromMilliseconds(20) });
        Assert.NotEmpty(coordinator.ForExport().Pending);
        _projects.CreateNew("Другой");                       // while the first video is analysed
        await coordinator.IdleAsync();
        Assert.Empty(coordinator.Analyses.Tracks);             // the old project's result never arrives
        Assert.Empty(coordinator.ForExport().Pending);

        var clip = AddClip(new FollowSettings());              // the new project's own video is analysed
        _projects.NotifyTimelineChanged();
        await coordinator.IdleAsync();
        Assert.Equal(PeopleAnalysisState.Ready, coordinator.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks).State);
        Assert.Single(coordinator.Analyses.Tracks);
    });

    /// <summary>30 fps frames for one second, 64 × 36 px.</summary>
    private sealed class Decoder : IVideoDecoder
    {
        public int Opened { get; private set; }

        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            Opened++;
            return Task.FromResult<IVideoFrameStream>(new Stream());
        }

        private sealed class Stream : IVideoFrameStream
        {
            private int _next;

            public ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult<DecodedFrame?>(_next >= 30 ? null
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
        public InferenceDevice DetectorDevice => InferenceDevice.Gpu;

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
