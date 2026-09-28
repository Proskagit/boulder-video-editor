using System.Collections.Concurrent;
using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D024 Step 9.5c: <see cref="WaveformCoordinator"/> over a fake waveform service that counts the waveforms being made
/// at the same moment and can hold them — only media on the timeline (PO-W3), at most two at once with slots of its
/// own, never held up by the thumbnails (PO-W4), offline media from the cache only (PO-W5), generations, shutdown and
/// the window's close. Real project service; no ffmpeg.
/// </summary>
public sealed class WaveformCoordinatorTests : IDisposable
{
    private const int Limit = 2; // PO-W4 — the product decision, not the constant under test

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly FakeWaveforms _waveforms = new();
    private readonly FakeLocation _location = new() { CurrentFolder = "folder-1" };
    private readonly WaveformCoordinator _coordinator;
    private readonly ConcurrentQueue<Guid> _ready = new();
    private readonly List<TaskCompletionSource> _gates = new();

    public WaveformCoordinatorTests()
    {
        _coordinator = new WaveformCoordinator(_waveforms, _projects, _location, NullLogger<WaveformCoordinator>.Instance);
        _coordinator.WaveformReady += (_, id) => _ready.Enqueue(id);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // --- fakes and helpers ------------------------------------------------------------------------------------------

    /// <summary>Counts makes running at once; makes wait at a gate the test opens.</summary>
    private sealed class Gated
    {
        private int _active;
        private int _maxActive;
        private volatile TaskCompletionSource _gate = Opened();

        public ConcurrentDictionary<Guid, byte> Cached { get; } = new();
        public ConcurrentDictionary<Guid, Exception> Failures { get; } = new();
        public ConcurrentQueue<(Guid Asset, string Folder)> CacheReads { get; } = new();
        public ConcurrentQueue<(Guid Asset, string Folder, CancellationToken Token)> Makes { get; } = new();
        public bool IgnoreCancellation { get; set; }
        public int Active => Volatile.Read(ref _active);
        public int MaxActive => Volatile.Read(ref _maxActive);

        private static TaskCompletionSource Opened()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.SetResult();
            return gate;
        }

        public TaskCompletionSource Close() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open() => _gate = Opened();

        public bool ReadCache(MediaAsset asset, string folder)
        {
            CacheReads.Enqueue((asset.Id, folder));
            return Cached.ContainsKey(asset.Id);
        }

        public async Task MakeAsync(MediaAsset asset, string folder, CancellationToken ct)
        {
            Makes.Enqueue((asset.Id, folder, ct));
            var now = Interlocked.Increment(ref _active);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, now, seen) != seen) { }
            try
            {
                var gate = _gate.Task;
                if (IgnoreCancellation) await gate;
                else await gate.WaitAsync(ct);
                await Task.Yield();
                if (Failures.TryGetValue(asset.Id, out var failure)) throw failure;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public int MakesOf(Guid asset) => Makes.Count(m => m.Asset == asset);
    }

    private sealed class FakeWaveforms : IWaveformService
    {
        public Gated Work { get; } = new();

        public Waveform? TryGetCached(MediaAsset asset, string cacheFolder) =>
            Work.ReadCache(asset, cacheFolder) ? Peaks(asset.Id) : null;

        public async Task<Waveform?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default)
        {
            await Work.MakeAsync(asset, cacheFolder, ct);
            return Peaks(asset.Id);
        }
    }

    private sealed class FakeThumbnails : IThumbnailService
    {
        public Gated Work { get; } = new();
        public Thumbnail? TryGetCached(MediaAsset asset, string cacheFolder) => null;

        public async Task<Thumbnail?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default)
        {
            await Work.MakeAsync(asset, cacheFolder, ct);
            return new Thumbnail(1, 1, new byte[4]);
        }
    }

    private sealed class FakeLocation : IWaveformCacheLocation, IThumbnailCacheLocation
    {
        public string CurrentFolder { get; set; } = "";
        public event EventHandler? Changed;
        public void Move(string folder) { CurrentFolder = folder; Changed?.Invoke(this, EventArgs.Empty); }
        public Task CleanUpUnsavedAsync() => Task.CompletedTask;
    }

    /// <summary>A waveform whose first peaks say which asset it belongs to.</summary>
    private static Waveform Peaks(Guid asset) => new(1, 4, asset.ToByteArray()[..4]);

    private static MediaAsset Media(string name = "clip.wav", MediaKind kind = MediaKind.Audio, string? audioCodec = "aac",
        MediaAnalysisStatus status = MediaAnalysisStatus.Completed) => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), "aive-waveform-coordinator", Guid.NewGuid().ToString("N"), name),
        Kind = kind, AnalysisStatus = status,
        Metadata = status == MediaAnalysisStatus.Completed
            ? new MediaMetadata { Duration = MediaTime.FromSeconds(10), AudioCodec = audioCodec }
            : null
    };

    /// <summary>Imports <paramref name="assets"/> and puts a clip of each on the timeline (audio on A1, the rest on V1),
    /// as one timeline change.</summary>
    private void OnTimeline(params MediaAsset[] assets)
    {
        var added = assets.Where(a => !_projects.Current.MediaAssets.Contains(a)).ToList();
        if (added.Count > 0) _projects.AddMediaAssets(added);
        foreach (var asset in assets)
        {
            var timeline = _projects.Current.Timeline;
            var track = asset.Kind == MediaKind.Audio ? timeline.AudioTracks[0] : timeline.VideoTracks[0];
            MediaBackedClip clip = asset.Kind switch
            {
                MediaKind.Audio => new AudioClip { MediaAssetId = asset.Id },
                MediaKind.Image => new ImageClip { MediaAssetId = asset.Id },
                _ => new VideoClip { MediaAssetId = asset.Id }
            };
            clip.Duration = MediaTime.FromSeconds(1);
            clip.SourceOut = clip.Duration;
            clip.TimelineStart = MediaTime.FromSeconds(track.Clips.Count);
            track.Clips.Add(clip);
        }
        _projects.NotifyTimelineChanged();
    }

    private List<MediaAsset> OnTimeline(int count)
    {
        var assets = Enumerable.Range(0, count).Select(i => Media($"clip{i}.wav")).ToArray();
        OnTimeline(assets);
        return assets.ToList();
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(5);
        }
    }

    private Task Settle() => _coordinator.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

    private async Task ReleaseAll()
    {
        _gates.ForEach(g => g.TrySetResult());
        _waveforms.Work.Open();
        await Settle();
    }

    private void AssertReady(MediaAsset asset) =>
        Assert.Equal(Peaks(asset.Id).Peaks.ToArray(), _coordinator.Get(asset.Id)?.Peaks.ToArray());

    // --- only media on the timeline ---------------------------------------------------------------------------------

    [Fact]
    public async Task Only_media_used_by_a_timeline_clip_get_a_waveform_nothing_at_import()
    {
        var onTimeline = Media("used.wav");
        var video = Media("used.mp4", MediaKind.Video);
        var importedOnly = Media("unused.wav");
        _projects.AddMediaAssets(new[] { onTimeline, video, importedOnly });
        await Settle();
        Assert.Empty(_waveforms.Work.CacheReads);                                   // import alone: nothing

        OnTimeline(onTimeline, video);
        await Settle();

        Assert.Equal(new[] { onTimeline.Id, video.Id }.Order(), _waveforms.Work.Makes.Select(m => m.Asset).Order());
        AssertReady(onTimeline);
        AssertReady(video);
        Assert.Null(_coordinator.Get(importedOnly.Id));
        Assert.Equal(new[] { onTimeline.Id, video.Id }.Order(), _ready.Order());
    }

    [Fact]
    public async Task Media_already_on_the_timeline_of_an_opened_project_are_handled()
    {
        var asset = OnTimeline(1)[0];
        await Settle();
        var folder = Path.Combine(_root, "Film");
        await _projects.SaveAsAsync(folder);
        _waveforms.Work.Cached[asset.Id] = 0;

        await _projects.OpenAsync(folder);                                         // new generation, same ids
        await Settle();

        Assert.NotNull(_coordinator.Get(asset.Id));
        Assert.Equal(1, _waveforms.Work.MakesOf(asset.Id));                        // the reopen was a cache hit
    }

    [Fact]
    public async Task A_clip_removed_from_the_timeline_keeps_its_waveform_for_an_undo()
    {
        var asset = OnTimeline(1)[0];
        await Settle();

        _projects.Current.Timeline.AudioTracks[0].Clips.Clear();
        _projects.NotifyTimelineChanged();
        await Settle();

        AssertReady(asset);
        Assert.Equal(1, _waveforms.Work.MakesOf(asset.Id));
    }

    // --- limit, dedup, cache hits, independence from thumbnails -------------------------------------------------------

    [Fact]
    public async Task At_most_two_waveforms_are_made_at_once_and_every_asset_gets_its_own()
    {
        _gates.Add(_waveforms.Work.Close());
        var assets = OnTimeline(7);

        await Eventually(() => _waveforms.Work.Active == Limit, "the first waveforms did not start");
        await Task.Delay(100);
        Assert.Equal((Limit, Limit), (_waveforms.Work.Active, _waveforms.Work.Makes.Count));

        await ReleaseAll();

        Assert.Equal(Limit, _waveforms.Work.MaxActive);
        Assert.All(assets, AssertReady);
        Assert.Equal(assets.Select(a => a.Id).Order(), _ready.Order());
    }

    [Fact]
    public async Task Busy_thumbnail_slots_never_hold_up_the_waveforms()
    {
        var thumbnailService = new FakeThumbnails();
        var thumbnailGate = thumbnailService.Work.Close();
        var thumbnails = new ThumbnailCoordinator(thumbnailService, _projects, _location, NullLogger<ThumbnailCoordinator>.Instance);
        _gates.Add(_waveforms.Work.Close());

        var videos = Enumerable.Range(0, 4).Select(i => Media($"v{i}.mp4", MediaKind.Video)).ToArray();
        OnTimeline(videos);                                                        // thumbnails and waveforms for each

        await Eventually(() => thumbnailService.Work.Active == ThumbnailCoordinator.MaxConcurrentGenerations, "thumbnails did not start");
        await Eventually(() => _waveforms.Work.Active == Limit, "waveforms waited for the thumbnails' slots");

        thumbnailGate.SetResult();
        thumbnailService.Work.Open();
        await thumbnails.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Limit, _waveforms.Work.Active);                               // and the thumbnails freed none of ours
        await ReleaseAll();
        Assert.All(videos, AssertReady);
    }

    [Fact]
    public async Task An_asset_is_handled_once_however_often_the_timeline_or_the_media_change()
    {
        _gates.Add(_waveforms.Work.Close());
        var asset = OnTimeline(1)[0];
        for (var i = 0; i < 5; i++)
        {
            _projects.NotifyTimelineChanged();
            _projects.NotifyMediaAssetsChanged();
        }

        await Eventually(() => _waveforms.Work.Active == 1, "the waveform did not start");
        await ReleaseAll();
        OnTimeline(asset);                                                         // a second clip of the same media
        await Settle();

        Assert.Equal((1, 1), (_waveforms.Work.CacheReads.Count, _waveforms.Work.Makes.Count));
        Assert.Single(_ready);
    }

    [Fact]
    public async Task A_cached_waveform_takes_no_slot_and_is_never_made()
    {
        _gates.Add(_waveforms.Work.Close());
        OnTimeline(Limit);
        await Eventually(() => _waveforms.Work.Active == Limit, "slots not taken");

        var cached = Media("cached.wav");
        _waveforms.Work.Cached[cached.Id] = 0;
        OnTimeline(cached);

        await Eventually(() => _coordinator.Get(cached.Id) is not null, "the cached waveform waited for a slot");
        Assert.Equal(0, _waveforms.Work.MakesOf(cached.Id));
        await ReleaseAll();
    }

    // --- which media --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Media_without_sound_or_analysis_get_none_and_an_analysis_completing_later_is_picked_up()
    {
        var silentVideo = Media("s.mp4", MediaKind.Video, audioCodec: null);
        var image = Media("i.png", MediaKind.Image);
        var pending = Media("p.wav", status: MediaAnalysisStatus.Pending);
        var failed = Media("f.wav", status: MediaAnalysisStatus.Failed);

        OnTimeline(silentVideo, image, pending, failed);
        await Settle();
        Assert.Empty(_waveforms.Work.CacheReads);

        pending.Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), AudioCodec = "pcm_s16le" };
        pending.AnalysisStatus = MediaAnalysisStatus.Completed;
        _projects.NotifyMediaAssetsChanged();
        await Settle();

        Assert.Equal(new[] { pending.Id }, _waveforms.Work.Makes.Select(m => m.Asset));
        AssertReady(pending);
    }

    [Fact]
    public async Task Offline_media_shows_only_a_cached_waveform_and_is_never_made()
    {
        var cached = Media("oc.wav");
        cached.IsMissing = true;
        _waveforms.Work.Cached[cached.Id] = 0;
        var bare = Media("ob.wav");
        bare.IsMissing = true;
        var unknown = Media("ou.mp4", MediaKind.Video, status: MediaAnalysisStatus.Pending); // no metadata saved
        unknown.IsMissing = true;
        var silent = Media("os.mp4", MediaKind.Video, audioCodec: null);                    // known to have no sound
        silent.IsMissing = true;

        OnTimeline(cached, bare, unknown, silent);
        await Settle();

        Assert.Empty(_waveforms.Work.Makes);
        Assert.Equal(new[] { cached.Id, bare.Id, unknown.Id }.Order(), _waveforms.Work.CacheReads.Select(r => r.Asset).Order());
        AssertReady(cached);
        Assert.Null(_coordinator.Get(bare.Id));
        Assert.Equal(new[] { cached.Id }, _ready);
    }

    [Fact]
    public async Task A_failing_asset_doesn_t_stop_the_others_and_isn_t_tried_again_in_the_same_project()
    {
        var broken = Media("broken.wav");
        _waveforms.Work.Failures[broken.Id] = new IOException("disk gone");
        var fine = new[] { Media("a.wav"), Media("b.wav"), Media("c.wav") };
        OnTimeline(fine.Prepend(broken).ToArray());
        await Settle();

        Assert.All(fine, AssertReady);
        Assert.Null(_coordinator.Get(broken.Id));
        _projects.NotifyTimelineChanged();
        await Settle();
        Assert.Equal(1, _waveforms.Work.MakesOf(broken.Id));
    }

    // --- generations, shutdown, close -------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_project_cancels_the_old_work_and_the_old_results_are_never_published(bool ignoresCancellation)
    {
        _waveforms.Work.IgnoreCancellation = ignoresCancellation;
        _gates.Add(_waveforms.Work.Close());
        var old = OnTimeline(5);
        await Eventually(() => _waveforms.Work.Active == Limit, "old work did not start");

        _projects.CreateNew("Other");
        Assert.All(_waveforms.Work.Makes, m => Assert.True(m.Token.IsCancellationRequested));
        await ReleaseAll();

        Assert.Empty(_ready);
        Assert.All(old, a => Assert.Null(_coordinator.Get(a.Id)));
        Assert.Equal(Limit, _waveforms.Work.Makes.Count);                         // the waiting ones never started

        var fresh = OnTimeline(1)[0];                                              // the new project is handled
        await Settle();
        AssertReady(fresh);
    }

    [Fact]
    public async Task Shutdown_cancels_everything_waits_for_it_and_publishes_nothing()
    {
        _gates.Add(_waveforms.Work.Close());
        var assets = OnTimeline(5);
        await Eventually(() => _waveforms.Work.Active == Limit, "work did not start");

        await _coordinator.ShutdownAsync();

        Assert.Equal(0, _coordinator.RunningCount);
        Assert.Equal(0, _waveforms.Work.Active);
        Assert.Empty(_ready);
        Assert.All(assets, a => Assert.Null(_coordinator.Get(a.Id)));

        _waveforms.Work.Open();
        OnTimeline(1);
        await Task.Delay(50);
        Assert.Equal(Limit, _waveforms.Work.Makes.Count);
    }

    [Fact]
    public async Task Closing_the_main_window_shuts_the_waveform_work_down()
    {
        _gates.Add(_waveforms.Work.Close());
        OnTimeline(3);
        await Eventually(() => _waveforms.Work.Active == Limit, "work did not start");

        Assert.True(await MainWindow().PrepareToCloseAsync());

        Assert.Equal(0, _coordinator.RunningCount);
        Assert.Empty(_ready);
    }

    private MainWindowViewModel MainWindow()
    {
        var undo = new UndoRedoService();
        var edit = new TimelineEditService(_projects, undo, NullLogger<TimelineEditService>.Instance);
        var status = new StatusService();
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var picker = new ScriptedPicker();
        var import = new MediaImportWorkflow(picker, new NoImport(), _projects, analysis, status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(1 /* Don't Save */), picker, status,
            NullLogger<ProjectFileWorkflow>.Instance);
        var playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance);
        return new MainWindowViewModel(
            new ToolbarViewModel(undo, files, import, status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(status, playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(edit, status),
            new TimelineViewModel(_projects, edit, status, NullLogger<TimelineViewModel>.Instance),
            status, files, _projects, NullLogger<MainWindowViewModel>.Instance, waveforms: _coordinator);
    }

    [Fact]
    public async Task A_new_cache_folder_is_used_for_later_requests_without_starting_over()
    {
        var first = OnTimeline(1)[0];
        await Settle();

        _location.Move("folder-2");                                                // Save / Save As: same project
        _projects.NotifyTimelineChanged();
        var second = OnTimeline(1)[0];
        await Settle();

        AssertReady(first);
        Assert.Equal(1, _waveforms.Work.MakesOf(first.Id));
        Assert.Equal("folder-2", _waveforms.Work.Makes.Single(m => m.Asset == second.Id).Folder);
    }

    private sealed class NoAnalysis : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "test"));
    }

    private sealed class NoImport : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromResult(new MediaImportBatchResult());
    }
}
