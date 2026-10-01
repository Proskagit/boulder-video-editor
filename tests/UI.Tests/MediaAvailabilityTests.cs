using System.Collections.Concurrent;
using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D026 §2 (Step 11.3): what the rest of the editor does when a media file comes back or goes during the session — the
/// analysis that is still needed, a thumbnail / waveform made in the same project generation, the Preview's snapshot
/// rebuilt, the Media Browser's row, an analysis that ran while its asset changed dropped. A real project service over a
/// fake file system (<see cref="Files"/>): files come and go without touching the disk.
/// </summary>
public sealed class MediaAvailabilityTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly ConcurrentDictionary<string, byte> _present = new(StringComparer.OrdinalIgnoreCase);
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly GatedAnalysis _analysis = new();
    private readonly MediaAnalysisCoordinator _analyses;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aive-availability", Guid.NewGuid().ToString("N"));
    private PlaybackService? _playback;

    public MediaAvailabilityTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance, new ProjectFileStore(), Files);
        _analyses = new MediaAnalysisCoordinator(_analysis, _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_playback is not null) await _playback.DisposeAsync();
    }

    private bool Files(string path) => _present.ContainsKey(path);
    private void Restore(MediaAsset asset) => _present[asset.FilePath] = 0;
    private void Remove(MediaAsset asset) => _present.TryRemove(asset.FilePath, out _);

    private string PathOf(string name) => Path.Combine(_root, name);

    private MediaAsset Asset(string name, MediaKind kind, MediaAnalysisStatus status = MediaAnalysisStatus.Completed,
        bool present = true, bool missing = false)
    {
        var asset = new MediaAsset
        {
            FilePath = PathOf(name), Kind = kind, AnalysisStatus = status, IsMissing = missing,
            Metadata = status == MediaAnalysisStatus.Completed
                ? new MediaMetadata
                {
                    Duration = MediaTime.FromFrame(250, Rate), FrameRate = Rate, AvgFrameRate = Rate,
                    Width = kind == MediaKind.Audio ? null : 64, Height = kind == MediaKind.Audio ? null : 36,
                    DisplayWidth = kind == MediaKind.Audio ? null : 64, DisplayHeight = kind == MediaKind.Audio ? null : 36,
                    DisplayRotation = kind == MediaKind.Audio ? null : 0,
                    VideoCodec = kind == MediaKind.Audio ? null : "h264", AudioCodec = kind == MediaKind.Image ? null : "aac",
                    AudioSampleRate = 48000, AudioChannels = 2
                }
                : null
        };
        if (present) _present[asset.FilePath] = 0;
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(2);
        }
    }

    // --- analysis ----------------------------------------------------------------------------------------------------

    /// <summary>Success for every file (a duration in seconds = the number of calls for it); a call can be held.</summary>
    private sealed class GatedAnalysis : IMediaAnalysisService
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
        public ConcurrentQueue<string> Calls { get; } = new();
        public int CallsFor(string path) => Calls.Count(c => c == path);

        public TaskCompletionSource HoldNext(string path) =>
            _gates[path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            Calls.Enqueue(filePath);
            var number = CallsFor(filePath);
            if (_gates.TryRemove(filePath, out var gate)) await gate.Task.WaitAsync(ct);
            return MediaAnalysisResult.Success(new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(number), AudioCodec = "aac", AudioSampleRate = 48000, AudioChannels = 2
            });
        }
    }

    [Fact]
    public async Task A_returned_file_without_metadata_is_analysed()
    {
        var asset = Asset("a.wav", MediaKind.Audio, MediaAnalysisStatus.Pending, present: false, missing: true);

        Restore(asset);
        await _projects.RecheckMediaAsync();
        await _analyses.IdleAsync();

        Assert.Equal(1, _analysis.CallsFor(asset.FilePath));
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.NotNull(asset.Metadata);
    }

    [Fact]
    public async Task A_returned_file_whose_analysis_failed_is_analysed_again()
    {
        var asset = Asset("a.wav", MediaKind.Audio, MediaAnalysisStatus.Failed, present: false, missing: true);
        asset.AnalysisError = "The file was not found.";

        Restore(asset);
        await _projects.RecheckMediaAsync();
        await _analyses.IdleAsync();

        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.Null(asset.AnalysisError);
    }

    [Fact]
    public async Task A_returned_file_with_saved_metadata_is_not_probed_again()
    {
        var asset = Asset("a.wav", MediaKind.Audio, present: false, missing: true);
        var metadata = asset.Metadata;

        Restore(asset);
        await _projects.RecheckMediaAsync();
        await _analyses.IdleAsync();

        Assert.Empty(_analysis.Calls);
        Assert.Same(metadata, asset.Metadata);
    }

    [Fact]
    public async Task A_returned_file_with_metadata_from_before_orientation_is_refreshed()
    {
        var asset = Asset("v.mp4", MediaKind.Video, present: false, missing: true);
        asset.Metadata!.DisplayWidth = null;
        asset.Metadata.DisplayHeight = null;                                  // saved before Phase 7

        Restore(asset);
        await _projects.RecheckMediaAsync();
        await _analyses.IdleAsync();

        Assert.Equal(1, _analysis.CallsFor(asset.FilePath));
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
    }

    [Fact]
    public async Task An_analysis_whose_file_went_missing_meanwhile_writes_nothing_and_runs_again_when_it_returns()
    {
        var asset = Asset("a.wav", MediaKind.Audio, MediaAnalysisStatus.Pending);
        var gate = _analysis.HoldNext(asset.FilePath);
        _analyses.QueueAnalysis(asset);
        Assert.Equal(MediaAnalysisStatus.Analyzing, asset.AnalysisStatus);

        Remove(asset);
        await _projects.RecheckMediaAsync();                                   // gone while being probed
        gate.SetResult();
        await _analyses.IdleAsync();

        Assert.True(asset.IsMissing);
        Assert.Equal(MediaAnalysisStatus.Pending, asset.AnalysisStatus);      // as before it was queued
        Assert.Null(asset.Metadata);

        Restore(asset);
        await _projects.RecheckMediaAsync();
        await _analyses.IdleAsync();

        Assert.Equal(2, _analysis.CallsFor(asset.FilePath));
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.Equal(MediaTime.FromSeconds(2), asset.Metadata!.Duration);    // the second probe's result
    }

    [Fact]
    public async Task An_analysis_of_an_asset_given_another_path_meanwhile_writes_nothing()
    {
        var asset = Asset("a.wav", MediaKind.Audio, MediaAnalysisStatus.Pending);
        var gate = _analysis.HoldNext(asset.FilePath);
        _analyses.QueueAnalysis(asset);

        asset.FilePath = PathOf("other.wav");                                  // as a relink would (Step 11.4)
        asset.AnalysisStatus = MediaAnalysisStatus.Pending;
        gate.SetResult();
        await _analyses.IdleAsync();

        Assert.Null(asset.Metadata);
        Assert.Equal(MediaAnalysisStatus.Pending, asset.AnalysisStatus);      // left to whoever changed it
    }

    // --- thumbnails and waveforms -----------------------------------------------------------------------------------

    /// <summary>Nothing cached unless put there; every make returns a new numbered result and can be held.</summary>
    private sealed class Results<T> where T : class
    {
        private readonly Func<int, T> _make;
        private int _made;
        private volatile TaskCompletionSource? _gate;

        public Results(Func<int, T> make) => _make = make;

        public ConcurrentDictionary<Guid, T> Cached { get; } = new();
        public ConcurrentQueue<Guid> Makes { get; } = new();
        public TaskCompletionSource Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open() => _gate = null;

        public T? TryGetCached(MediaAsset asset) => Cached.TryGetValue(asset.Id, out var t) ? t : null;

        public async Task<T?> MakeAsync(MediaAsset asset, CancellationToken ct)
        {
            Makes.Enqueue(asset.Id);
            var number = Interlocked.Increment(ref _made);
            if (_gate is { } gate) await gate.Task.WaitAsync(ct);
            await Task.Yield();
            return _make(number);
        }
    }

    private sealed class FakeThumbnails(Results<Thumbnail> results) : IThumbnailService
    {
        public Thumbnail? TryGetCached(MediaAsset asset, string cacheFolder) => results.TryGetCached(asset);
        public Task<Thumbnail?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default) =>
            results.MakeAsync(asset, ct);
    }

    private sealed class FakeWaveforms(Results<Waveform> results) : IWaveformService
    {
        public Waveform? TryGetCached(MediaAsset asset, string cacheFolder) => results.TryGetCached(asset);
        public Task<Waveform?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default) =>
            results.MakeAsync(asset, ct);
    }

    private sealed class FakeLocation : IThumbnailCacheLocation, IWaveformCacheLocation
    {
        public string CurrentFolder => "cache";
        public event EventHandler? Changed { add { } remove { } }
        public Task CleanUpUnsavedAsync() => Task.CompletedTask;
    }

    private static Thumbnail Picture(int number) => new(1, 1, new byte[] { (byte)number, 0, 0, 255 });
    private static int NumberOf(Thumbnail? thumbnail) => thumbnail is null ? 0 : thumbnail.Pixels.Span[0];

    private ThumbnailCoordinator Thumbnails(Results<Thumbnail> results) =>
        new(new FakeThumbnails(results), _projects, new FakeLocation(), NullLogger<ThumbnailCoordinator>.Instance);

    [Fact]
    public async Task An_offline_video_gets_its_thumbnail_made_once_its_file_returns()
    {
        var results = new Results<Thumbnail>(Picture);
        var coordinator = Thumbnails(results);
        var ready = new ConcurrentQueue<Guid>();
        coordinator.ThumbnailReady += (_, id) => ready.Enqueue(id);
        var asset = Asset("v.mp4", MediaKind.Video, present: false, missing: true);
        await coordinator.IdleAsync();
        Assert.Empty(results.Makes);                                           // offline: never decoded
        Assert.Null(coordinator.Get(asset.Id));

        Restore(asset);
        await _projects.RecheckMediaAsync();
        await coordinator.IdleAsync();

        Assert.Single(results.Makes);
        Assert.Equal(1, NumberOf(coordinator.Get(asset.Id)));
        Assert.Contains(asset.Id, ready);
    }

    [Fact]
    public async Task A_returned_file_replaces_the_cached_thumbnail_shown_while_it_was_offline()
    {
        var results = new Results<Thumbnail>(Picture);
        var stale = Picture(99);
        var asset = Asset("v.mp4", MediaKind.Video, present: false, missing: true);
        results.Cached[asset.Id] = stale;
        var coordinator = Thumbnails(results);
        await coordinator.IdleAsync();
        Assert.Same(stale, coordinator.Get(asset.Id));                         // the last cached one (D024 9.4)

        results.Cached.Clear();                                                // the returned file is not in the cache
        var gate = results.Hold();
        Restore(asset);
        await _projects.RecheckMediaAsync();
        await Eventually(() => results.Makes.Count == 1, "no make after the file returned");
        Assert.Same(stale, coordinator.Get(asset.Id));                         // kept until the new one is ready

        gate.SetResult();
        await coordinator.IdleAsync();
        Assert.Equal(1, NumberOf(coordinator.Get(asset.Id)));
    }

    [Fact]
    public async Task Work_started_before_a_restart_publishes_nothing()
    {
        var results = new Results<Thumbnail>(Picture);
        var coordinator = Thumbnails(results);
        var first = results.Hold();
        var asset = Asset("v.mp4", MediaKind.Video);                           // online: make 1 is held
        await Eventually(() => results.Makes.Count == 1, "no first make");

        Remove(asset);
        await _projects.RecheckMediaAsync();                                   // gone …
        results.Open();
        Restore(asset);
        await _projects.RecheckMediaAsync();                                   // … and back: make 2
        await Eventually(() => results.Makes.Count == 2, "no make after the file returned");
        await Eventually(() => NumberOf(coordinator.Get(asset.Id)) == 2, "the new thumbnail was not published");

        first.SetResult();                                                     // the held make 1 ends last
        await coordinator.IdleAsync();
        Assert.Equal(2, NumberOf(coordinator.Get(asset.Id)));
    }

    [Fact]
    public async Task A_thumbnail_is_not_made_again_for_a_file_that_was_there_all_along()
    {
        var results = new Results<Thumbnail>(Picture);
        var coordinator = Thumbnails(results);
        var asset = Asset("v.mp4", MediaKind.Video);
        var other = Asset("gone.mp4", MediaKind.Video);
        await coordinator.IdleAsync();
        Assert.Equal(2, results.Makes.Count);

        Remove(other);
        await _projects.RecheckMediaAsync();
        Restore(other);
        await _projects.RecheckMediaAsync();
        await coordinator.IdleAsync();

        Assert.Equal(3, results.Makes.Count);                                  // only the returned one again
        Assert.Equal(1, results.Makes.Count(id => id == asset.Id));
    }

    [Fact]
    public async Task An_offline_audio_clip_gets_its_waveform_made_once_its_file_returns()
    {
        var results = new Results<Waveform>(n => new Waveform(256, 256 * n, new byte[n]));
        var asset = Asset("a.wav", MediaKind.Audio, present: false);           // the clip is added before it is checked
        var edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        Assert.True(edit.AddClip(asset.Id, null, MediaTime.Zero).Success);
        await _projects.RecheckMediaAsync();                                   // … and found missing
        Assert.True(asset.IsMissing);
        var coordinator = new WaveformCoordinator(new FakeWaveforms(results), _projects, new FakeLocation(),
            NullLogger<WaveformCoordinator>.Instance);
        await coordinator.IdleAsync();
        Assert.Empty(results.Makes);

        Restore(asset);
        await _projects.RecheckMediaAsync();
        await coordinator.IdleAsync();

        Assert.Single(results.Makes);
        Assert.NotNull(coordinator.Get(asset.Id));
    }

    // --- Preview, Media Browser -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_preview_shows_a_returned_file_and_the_placeholder_for_a_gone_one()
    {
        var decoder = new FakeVideoDecoder();
        _playback = new PlaybackService(decoder, new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4 });
        var preview = new PreviewViewModel(new StatusService(), _playback, _projects, NullLogger<PreviewViewModel>.Instance);
        var edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        var asset = Asset("v.mp4", MediaKind.Video);
        decoder.Add(asset.FilePath, new FakeSource(Rate, 250));
        Assert.True(edit.AddClip(asset.Id, null, MediaTime.Zero).Success);

        LayerPictureState? Top() => preview.Layers.LastOrDefault(l => l.Layer is Core.Composition.PictureLayer)?.State;
        async Task TickUntil(LayerPictureState state)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                preview.Tick();
                if (Top() == state) return;
                if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException($"{state} not shown (now {Top()})");
                await Task.Delay(1);
            }
        }

        await TickUntil(LayerPictureState.Frame);
        Remove(asset);
        await _projects.RecheckMediaAsync();
        await TickUntil(LayerPictureState.Offline);

        Restore(asset);
        await _projects.RecheckMediaAsync();
        await TickUntil(LayerPictureState.Frame);                              // no reopening of the project
    }

    [Fact]
    public async Task The_media_browser_row_follows_the_file()
    {
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, _analyses, new StatusService(),
            NullLogger<MediaImportWorkflow>.Instance);
        var browser = new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance);
        var asset = Asset("a.wav", MediaKind.Audio);
        string? Summary() => browser.Items.Single(i => i.Asset.Id == asset.Id).TechnicalSummary;
        Assert.NotEqual("Media offline", Summary());

        Remove(asset);
        await _projects.RecheckMediaAsync();
        Assert.Equal("Media offline", Summary());

        Restore(asset);
        await _projects.RecheckMediaAsync();
        Assert.NotEqual("Media offline", Summary());
    }

    private sealed class NoImport : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromResult(new MediaImportBatchResult());
    }
}
