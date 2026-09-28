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
/// D024 Step 9.4c: <see cref="ThumbnailCoordinator"/> over a fake thumbnail service that counts the thumbnails being made
/// at the same moment and can hold them — so the limit, the dedup, the cache-hit path, the generations and the
/// cancellation are measured, not inferred. Real project service; no ffmpeg.
/// </summary>
public sealed class ThumbnailCoordinatorTests : IDisposable
{
    private const int Limit = ThumbnailCoordinator.MaxConcurrentGenerations;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly FakeThumbnails _thumbnails = new();
    private readonly FakeLocation _location = new() { CurrentFolder = "folder-1" };
    private readonly ThumbnailCoordinator _coordinator;
    private readonly ConcurrentQueue<Guid> _ready = new();

    public ThumbnailCoordinatorTests()
    {
        _coordinator = new ThumbnailCoordinator(_thumbnails, _projects, _location, NullLogger<ThumbnailCoordinator>.Instance);
        _coordinator.ThumbnailReady += (_, id) => _ready.Enqueue(id);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // --- fakes and helpers ------------------------------------------------------------------------------------------

    private sealed class FakeThumbnails : IThumbnailService
    {
        private int _active;
        private int _maxActive;
        private volatile TaskCompletionSource _gate = Opened();

        public ConcurrentDictionary<Guid, Thumbnail> Cached { get; } = new();
        public ConcurrentDictionary<Guid, Exception> Failures { get; } = new();
        public ConcurrentDictionary<Guid, byte> NoneFor { get; } = new();
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

        /// <summary>Makes from now on wait at a new gate (makes already waiting keep theirs).</summary>
        public TaskCompletionSource Close() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open() => _gate = Opened();

        public Thumbnail? TryGetCached(MediaAsset asset, string cacheFolder)
        {
            CacheReads.Enqueue((asset.Id, cacheFolder));
            return Cached.TryGetValue(asset.Id, out var t) ? t : null;
        }

        public async Task<Thumbnail?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default)
        {
            Makes.Enqueue((asset.Id, cacheFolder, ct));
            var now = Interlocked.Increment(ref _active);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, now, seen) != seen) { }
            try
            {
                var gate = _gate.Task;
                if (IgnoreCancellation) await gate;
                else await gate.WaitAsync(ct);            // the real service: cancellation throws
                await Task.Yield();
                if (Failures.TryGetValue(asset.Id, out var failure)) throw failure;
                return NoneFor.ContainsKey(asset.Id) ? null : Picture(asset.Id);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public int MakesOf(Guid asset) => Makes.Count(m => m.Asset == asset);
    }

    private sealed class FakeLocation : IThumbnailCacheLocation
    {
        public string CurrentFolder { get; set; } = "";
        public event EventHandler? Changed;
        public void Move(string folder) { CurrentFolder = folder; Changed?.Invoke(this, EventArgs.Empty); }
        public Task CleanUpUnsavedAsync() => Task.CompletedTask;
    }

    /// <summary>A 1 × 1 thumbnail whose pixel says which asset it belongs to.</summary>
    private static Thumbnail Picture(Guid asset) => new(1, 1, asset.ToByteArray()[..4]);

    private static MediaAsset Video(string name = "clip.mp4", MediaKind kind = MediaKind.Video,
        MediaAnalysisStatus status = MediaAnalysisStatus.Completed) => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), "aive-thumb-coordinator", Guid.NewGuid().ToString("N"), name),
        Kind = kind, AnalysisStatus = status,
        Metadata = status == MediaAnalysisStatus.Completed ? new MediaMetadata { Duration = MediaTime.FromSeconds(10), Width = 64, Height = 36 } : null
    };

    private List<MediaAsset> Import(int count)
    {
        var assets = Enumerable.Range(0, count).Select(i => Video($"clip{i}.mp4")).ToList();
        _projects.AddMediaAssets(assets);                                    // raises MediaAssetsChanged
        return assets;
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

    private void AssertReady(MediaAsset asset) =>
        Assert.Equal(Picture(asset.Id).Pixels.ToArray(), _coordinator.Get(asset.Id)?.Pixels.ToArray());

    // --- limit, completion, dedup, cache hits -----------------------------------------------------------------------

    [Fact]
    public async Task At_most_two_thumbnails_are_made_at_once_and_every_asset_gets_its_own()
    {
        _gates.Add(_thumbnails.Close());
        var assets = Import(7);

        await Eventually(() => _thumbnails.Active == Limit, "the first thumbnails did not start");
        await Task.Delay(100);                                                // nothing more starts while the slots are taken
        Assert.Equal((Limit, Limit), (_thumbnails.Active, _thumbnails.Makes.Count));

        await ReleaseAll();

        Assert.Equal(Limit, _thumbnails.MaxActive);
        Assert.All(assets, AssertReady);
        Assert.Equal(assets.Select(a => a.Id).Order(), _ready.Order());      // one event per asset
    }

    /// <summary>Opens every gate handed out so far and waits until nothing is left.</summary>
    private async Task ReleaseAll()
    {
        _gates.ForEach(g => g.TrySetResult());
        _thumbnails.Open();
        await Settle();
    }

    private readonly List<TaskCompletionSource> _gates = new();

    [Fact]
    public async Task An_asset_is_handled_once_however_often_the_media_change()
    {
        _gates.Add(_thumbnails.Close());
        var asset = Import(1)[0];
        for (var i = 0; i < 5; i++) _projects.NotifyMediaAssetsChanged();   // e.g. other assets' analyses finishing

        await Eventually(() => _thumbnails.Active == 1, "the thumbnail did not start");
        Assert.Equal((1, 1), (_thumbnails.CacheReads.Count, _thumbnails.Makes.Count));
        await ReleaseAll();

        for (var i = 0; i < 5; i++) _projects.NotifyMediaAssetsChanged();   // and after it is ready
        await Settle();
        Assert.Equal((1, 1), (_thumbnails.CacheReads.Count, _thumbnails.Makes.Count));
        AssertReady(asset);
        Assert.Single(_ready);
    }

    [Fact]
    public async Task A_cached_thumbnail_takes_no_slot_and_is_never_made()
    {
        _gates.Add(_thumbnails.Close());
        Import(Limit);                                                        // both slots taken
        await Eventually(() => _thumbnails.Active == Limit, "slots not taken");

        var cached = Video("cached.mp4");
        _thumbnails.Cached[cached.Id] = Picture(cached.Id);
        _projects.AddMediaAssets(new[] { cached });

        await Eventually(() => _coordinator.Get(cached.Id) is not null, "the cached thumbnail waited for a slot");
        Assert.Equal(0, _thumbnails.MakesOf(cached.Id));
        Assert.Equal(Limit, _thumbnails.Active);
        await ReleaseAll();
    }

    // --- which assets --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_analysed_video_and_images_are_made_offline_media_only_shows_a_cached_one()
    {
        var audio = Video("a.mp3", MediaKind.Audio);
        var pending = Video("p.mp4", status: MediaAnalysisStatus.Pending);
        var failed = Video("f.mp4", status: MediaAnalysisStatus.Failed);
        var image = Video("i.png", MediaKind.Image);
        var offlineCached = Video("oc.mp4");
        offlineCached.IsMissing = true;
        _thumbnails.Cached[offlineCached.Id] = Picture(offlineCached.Id);
        var offlineBare = Video("ob.mp4");
        offlineBare.IsMissing = true;

        _projects.AddMediaAssets(new[] { audio, pending, failed, image, offlineCached, offlineBare });
        await Settle();

        Assert.Equal(new[] { image.Id }, _thumbnails.Makes.Select(m => m.Asset));                  // only one made
        Assert.Equal(new[] { image.Id, offlineCached.Id, offlineBare.Id }.Order(), _thumbnails.CacheReads.Select(r => r.Asset).Order());
        AssertReady(image);
        AssertReady(offlineCached);
        Assert.Null(_coordinator.Get(offlineBare.Id));
        Assert.Null(_coordinator.Get(audio.Id));

        pending.Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10) };            // its analysis completes
        pending.AnalysisStatus = MediaAnalysisStatus.Completed;
        _projects.NotifyMediaAssetsChanged();
        await Settle();
        AssertReady(pending);
    }

    [Fact]
    public async Task A_failing_asset_doesn_t_stop_the_others_and_isn_t_tried_again_in_the_same_project()
    {
        var assets = Import(0);
        var broken = Video("broken.mp4");
        var undecodable = Video("undecodable.mp4");
        _thumbnails.Failures[broken.Id] = new IOException("disk gone");
        _thumbnails.NoneFor[undecodable.Id] = 0;
        _projects.AddMediaAssets(new[] { broken, undecodable, Video("a.mp4"), Video("b.mp4"), Video("c.mp4") });
        await Settle();

        var fine = _projects.Current.MediaAssets.Where(a => a != broken && a != undecodable).ToList();
        Assert.All(fine, AssertReady);
        Assert.Null(_coordinator.Get(broken.Id));
        Assert.Null(_coordinator.Get(undecodable.Id));
        Assert.Equal(3, _ready.Count);

        _projects.NotifyMediaAssetsChanged();
        await Settle();
        Assert.Equal((1, 1), (_thumbnails.MakesOf(broken.Id), _thumbnails.MakesOf(undecodable.Id)));
    }

    // --- generations and cancellation -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)] // the service notices the cancellation (ffmpeg is ended)
    [InlineData(true)]  // it finishes anyway after the switch: the result must still be dropped
    public async Task A_new_project_cancels_the_old_work_and_the_old_results_are_never_published(bool ignoresCancellation)
    {
        _thumbnails.IgnoreCancellation = ignoresCancellation;
        _gates.Add(_thumbnails.Close());
        var old = Import(5);
        await Eventually(() => _thumbnails.Active == Limit, "old work did not start");

        _projects.CreateNew("Other");
        Assert.All(_thumbnails.Makes, m => Assert.True(m.Token.IsCancellationRequested));
        await ReleaseAll();

        Assert.Empty(_ready);                                                                    // no event for the old project
        Assert.All(old, a => Assert.Null(_coordinator.Get(a.Id)));
        Assert.Equal(Limit, _thumbnails.Makes.Count);                                           // its waiting assets never started
    }

    [Fact]
    public async Task A_slot_freed_while_the_old_project_is_cancelled_starts_no_work_for_it()
    {
        // Two makes run, four requests wait; then the first two end and two of the waiting ones start making — after
        // the last two had begun waiting. Cancelling runs the callbacks newest first, so those makes end (and free their
        // slots) before the two remaining waits are cancelled: a wait may get a slot of the cancelled project.
        var first = _thumbnails.Close();
        Import(2);
        await Eventually(() => _thumbnails.Active == Limit, "the first makes did not start");
        _gates.Add(_thumbnails.Close());
        Import(4);
        await Eventually(() => _coordinator.RunningCount == 6, "the waits did not begin");
        first.SetResult();
        await Eventually(() => _thumbnails.Makes.Count == 4 && _thumbnails.Active == Limit, "the next makes did not start");

        _projects.CreateNew("Other");
        await Settle();

        Assert.Equal(4, _thumbnails.Makes.Count);            // the last two never made anything
        Assert.Equal(2, _ready.Count);                        // only the two finished before the switch
        await ReleaseAll();
    }

    [Fact]
    public async Task Waits_of_an_old_project_end_at_once_the_new_project_is_handled_and_no_slot_is_lost()
    {
        _thumbnails.IgnoreCancellation = true;                                                  // the old makes hold their slots for a while
        _gates.Add(_thumbnails.Close());
        Import(6);
        await Eventually(() => _thumbnails.Active == Limit, "old work did not start");

        _projects.CreateNew("Other");
        await Eventually(() => _coordinator.RunningCount == Limit, "the 4 waiting requests did not end with their project");
        _thumbnails.Open();
        var fresh = Import(Limit * 2);
        await Task.Delay(50);
        Assert.All(fresh, a => Assert.Null(_coordinator.Get(a.Id)));                           // slots still held by the old makes

        _gates.ForEach(g => g.TrySetResult());                                                  // the old makes end → slots free
        await Settle();
        Assert.All(fresh, AssertReady);
        Assert.Equal(fresh.Select(a => a.Id).Order(), _ready.Order());

        _gates.Add(_thumbnails.Close());                                                        // every slot still exists
        Import(Limit + 1);
        await Eventually(() => _thumbnails.Active == Limit, "slots were lost");
        await Task.Delay(50);
        Assert.Equal(Limit, _thumbnails.Active);
        await ReleaseAll();
    }

    [Fact]
    public async Task Reopening_a_project_handles_its_assets_again_in_the_new_generation()
    {
        var asset = Import(1)[0];
        await Settle();
        var folder = Path.Combine(_root, "Film");
        await _projects.SaveAsAsync(folder);
        _thumbnails.Cached[asset.Id] = Picture(asset.Id);

        await _projects.OpenAsync(folder);                                                     // same asset ids, new objects
        await Settle();

        Assert.NotNull(_coordinator.Get(asset.Id));
        Assert.Equal(2, _thumbnails.CacheReads.Count(r => r.Asset == asset.Id));
        Assert.Equal(1, _thumbnails.MakesOf(asset.Id));                                       // the reopen was a cache hit
    }

    [Fact]
    public async Task Shutdown_cancels_everything_waits_for_it_and_publishes_nothing()
    {
        _gates.Add(_thumbnails.Close());
        var assets = Import(5);
        await Eventually(() => _thumbnails.Active == Limit, "work did not start");

        await _coordinator.ShutdownAsync();

        Assert.Equal(0, _coordinator.RunningCount);
        Assert.Equal(0, _thumbnails.Active);
        Assert.All(_thumbnails.Makes, m => Assert.True(m.Token.IsCancellationRequested));
        Assert.Empty(_ready);
        Assert.All(assets, a => Assert.Null(_coordinator.Get(a.Id)));

        _thumbnails.Open();
        Import(1);                                                                             // nothing starts any more
        await Task.Delay(50);
        Assert.Equal(Limit, _thumbnails.Makes.Count);
    }

    [Fact]
    public async Task Closing_the_main_window_shuts_the_thumbnail_work_down()
    {
        _gates.Add(_thumbnails.Close());
        Import(3);
        await Eventually(() => _thumbnails.Active == Limit, "work did not start");
        var vm = MainWindow();

        Assert.True(await vm.PrepareToCloseAsync());                                          // a new project: nothing to ask

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
            status, files, _projects, NullLogger<MainWindowViewModel>.Instance, _coordinator);
    }

    // --- the cache folder ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_cache_folder_is_used_for_later_requests_without_starting_over()
    {
        var first = Import(1)[0];
        await Settle();
        Assert.Equal("folder-1", _thumbnails.Makes.Single().Folder);

        _location.Move("folder-2");                                                           // Save / Save As: same project
        _projects.NotifyMediaAssetsChanged();
        var second = Import(1)[0];
        await Settle();

        AssertReady(first);                                                                   // kept: no new generation
        Assert.Equal(1, _thumbnails.MakesOf(first.Id));                                      // not made again
        Assert.Equal("folder-2", _thumbnails.Makes.Single(m => m.Asset == second.Id).Folder);
        Assert.Equal("folder-2", _thumbnails.CacheReads.Single(r => r.Asset == second.Id).Folder);
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
