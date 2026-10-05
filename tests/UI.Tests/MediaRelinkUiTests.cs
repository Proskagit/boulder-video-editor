using System.Collections.Concurrent;
using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D026 §3 (Step 11.4): what the editor shows after a relink, its Undo and Redo — the old file's thumbnail and waveform
/// dropped at once and the new file's made, work for the old file never published, the timeline's waveform gone for a
/// file without sound, the Preview decoding the new file, the export preflight passing, an unprobed relink left
/// unanalysed, a later incompatibility in the status bar. Real project, timeline and relink services over a fake file
/// system and a fake probe.
/// </summary>
public sealed class MediaRelinkUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly ConcurrentDictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MediaMetadata> _probed = new(StringComparer.OrdinalIgnoreCase);
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly MediaRelinkService _relink;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aive-relink-ui", Guid.NewGuid().ToString("N"));
    private PlaybackService? _playback;
    private bool _probeUnavailable;

    public MediaRelinkUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance, new ProjectFileStore(), path => _files.ContainsKey(path));
        _projects.Current.Settings.FrameRate = Rate;
        _projects.Current.Settings.IsFrameRateLocked = true;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _relink = new MediaRelinkService(_projects, _undo, new Probe(this), NullLogger<MediaRelinkService>.Instance,
            path => _files.TryGetValue(path, out var size) ? size : null);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_playback is not null) await _playback.DisposeAsync();
    }

    private sealed class Probe(MediaRelinkUiTests owner) : IMediaAnalysisService
    {
        public int Calls;

        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (owner._probeUnavailable)
                return Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "ffprobe could not be found."));
            return Task.FromResult(owner._probed.TryGetValue(filePath, out var m)
                ? MediaAnalysisResult.Success(m)
                : MediaAnalysisResult.Failure(MediaAnalysisOutcome.FileNotFound, "not found"));
        }
    }

    private static MediaMetadata Meta(double seconds, string? audio = "aac") => new()
    {
        Duration = MediaTime.FromSeconds(seconds), Width = 64, Height = 36, DisplayWidth = 64, DisplayHeight = 36, DisplayRotation = 0,
        FrameRate = Rate, AvgFrameRate = Rate, StartTime = MediaTime.Zero, VideoCodec = "h264",
        AudioCodec = audio, AudioSampleRate = audio is null ? null : 48000, AudioChannels = audio is null ? null : 2
    };

    /// <summary>A video with a clip on V1 (0–4 s) whose file is gone now (offline after the re-check).</summary>
    private async Task<(MediaAsset Asset, Clip Clip)> OfflineVideoOnTimeline(string name = "old.mp4", double at = 0)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(_root, name), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed, Metadata = Meta(10)
        };
        _files[asset.FilePath] = 100;
        _projects.AddMediaAssets(new[] { asset });
        var add = _edit.AddClip(asset.Id, null, MediaTime.FromSeconds(at));
        Assert.True(add.Success, add.Message);
        var clip = _projects.Current.Timeline.VideoTracks[0].Clips.Single(c => c.Id == add.ClipIds[0]);
        Assert.True(_edit.TrimClip(clip.Id, ClipEdge.End, MediaTime.FromSeconds(at + 4)).Success);
        _files.TryRemove(asset.FilePath, out _);
        await _projects.RecheckMediaAsync();
        Assert.True(asset.IsMissing);
        return (asset, clip);
    }

    private string NewFile(string name, MediaMetadata? metadata, long size = 200)
    {
        var path = Path.Combine(_root, name);
        _files[path] = size;
        if (metadata is not null) _probed[path] = metadata;
        return path;
    }

    private async Task Relink(MediaAsset asset, string path)
    {
        var check = await _relink.CheckAsync(asset.Id, path);
        Assert.True(check.CanApply, check.Message);
        var result = await _relink.ApplyAsync(check);
        Assert.True(result.Applied, result.Message);
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

    // --- thumbnails and waveforms -----------------------------------------------------------------------------------

    private sealed class Thumbnails : IThumbnailService
    {
        private int _made;
        public ConcurrentDictionary<Guid, Thumbnail> Cached { get; } = new();
        public ConcurrentQueue<string> Makes { get; } = new();
        public volatile TaskCompletionSource? Gate;

        public Thumbnail? TryGetCached(MediaAsset asset, string cacheFolder) => Cached.TryGetValue(asset.Id, out var t) ? t : null;

        public async Task<Thumbnail?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default)
        {
            Makes.Enqueue(asset.FilePath);
            var number = Interlocked.Increment(ref _made);
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            await Task.Yield();
            return new Thumbnail(1, 1, new byte[] { (byte)number, 0, 0, 255 });
        }
    }

    private sealed class Waveforms : IWaveformService
    {
        public ConcurrentQueue<string> Makes { get; } = new();
        public Waveform? TryGetCached(MediaAsset asset, string cacheFolder) => null;
        public Task<Waveform?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default)
        {
            Makes.Enqueue(asset.FilePath);
            return Task.FromResult<Waveform?>(new Waveform(256, 256 * 4, new byte[] { 100, 100, 100, 100 }));
        }
    }

    private sealed class Location : IThumbnailCacheLocation, IWaveformCacheLocation
    {
        public string CurrentFolder => "cache";
        public event EventHandler? Changed { add { } remove { } }
        public Task CleanUpUnsavedAsync() => Task.CompletedTask;
    }

    private static int Number(Thumbnail? t) => t is null ? 0 : t.Pixels.Span[0];

    [Fact]
    public async Task A_relinked_asset_loses_the_old_thumbnail_at_once_and_gets_the_new_files()
    {
        var service = new Thumbnails();
        var (asset, _) = await OfflineVideoOnTimeline();
        service.Cached[asset.Id] = new Thumbnail(1, 1, new byte[] { 99, 0, 0, 255 });   // the old file's, cached
        var coordinator = new ThumbnailCoordinator(service, _projects, new Location(), NullLogger<ThumbnailCoordinator>.Instance);
        await coordinator.IdleAsync();
        Assert.Equal(99, Number(coordinator.Get(asset.Id)));
        service.Cached.Clear();

        service.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Relink(asset, NewFile("new.mp4", Meta(10)));
        Assert.Null(coordinator.Get(asset.Id));                             // dropped at once: another file
        await Eventually(() => service.Makes.Count == 1, "no thumbnail made for the new file");
        Assert.Equal(asset.FilePath, Assert.Single(service.Makes));

        service.Gate.SetResult();
        await coordinator.IdleAsync();
        Assert.Equal(1, Number(coordinator.Get(asset.Id)));

        _undo.Undo();                                                       // the old (offline) file again: what it showed
        await coordinator.IdleAsync();
        Assert.Equal(99, Number(coordinator.Get(asset.Id)));               // its own cached thumbnail, as before (D2)
        Assert.Single(service.Makes);                                       // nothing made or read for it
    }

    [Fact]
    public async Task A_batch_relink_refreshes_every_relinked_assets_thumbnail_and_one_undo_takes_them_all()
    {
        var service = new Thumbnails();
        var (first, _) = await OfflineVideoOnTimeline("one.mp4");
        var (second, _) = await OfflineVideoOnTimeline("two.mp4", at: 5);
        var coordinator = new ThumbnailCoordinator(service, _projects, new Location(), NullLogger<ThumbnailCoordinator>.Instance);
        await coordinator.IdleAsync();

        var checks = new[]
        {
            await _relink.CheckAsync(first.Id, NewFile("one-new.mp4", Meta(10))),
            await _relink.CheckAsync(second.Id, NewFile("two-new.mp4", Meta(10)))
        };
        var result = await _relink.ApplyAllAsync(checks);
        await coordinator.IdleAsync();

        Assert.Equal(2, result.AppliedCount);
        Assert.NotNull(coordinator.Get(first.Id));
        Assert.NotNull(coordinator.Get(second.Id));
        Assert.Equal(2, service.Makes.Count);

        _undo.Undo();
        await coordinator.IdleAsync();
        Assert.True(first.IsMissing && second.IsMissing);
        Assert.Null(coordinator.Get(first.Id));
        Assert.Null(coordinator.Get(second.Id));
    }

    [Fact]
    public async Task Work_for_the_file_before_an_undo_publishes_nothing()
    {
        var service = new Thumbnails();
        var coordinator = new ThumbnailCoordinator(service, _projects, new Location(), NullLogger<ThumbnailCoordinator>.Instance);
        var (asset, _) = await OfflineVideoOnTimeline();                   // made once while it was online
        await coordinator.IdleAsync();
        var made = service.Makes.Count;
        var ownBefore = coordinator.Get(asset.Id);                          // the old file's, shown while offline (D024)
        service.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Relink(asset, NewFile("new.mp4", Meta(10)));
        await Eventually(() => service.Makes.Count == made + 1, "no thumbnail make");

        _undo.Undo();                                                       // while the new file's thumbnail is made
        service.Gate.SetResult();
        await coordinator.IdleAsync();

        Assert.Same(ownBefore, coordinator.Get(asset.Id));                  // the undone file's thumbnail is not shown
    }

    [Fact]
    public async Task The_timeline_drops_the_waveform_of_a_clip_relinked_to_a_file_without_sound()
    {
        var waveforms = new WaveformCoordinator(new Waveforms(), _projects, new Location(), NullLogger<WaveformCoordinator>.Instance);
        var timeline = new TimelineViewModel(_projects, _edit, new StatusService(), NullLogger<TimelineViewModel>.Instance, waveforms: waveforms);
        var (asset, clip) = await OfflineVideoOnTimeline();
        ClipWaveform? Shown() => timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Clip.Id == clip.Id).Waveform;
        await Relink(asset, NewFile("sound.mp4", Meta(10)));
        await waveforms.IdleAsync();
        await Eventually(() => Shown() is not null, "no waveform for the relinked file with sound");

        _undo.Undo();
        await Relink(asset, NewFile("silent.mp4", Meta(10, audio: null)));
        await waveforms.IdleAsync();

        Assert.Null(waveforms.Get(asset.Id));
        Assert.Null(Shown());                                               // not the previous file's waveform
    }

    // --- Preview, export, analysis, status ---------------------------------------------------------------------------

    [Fact]
    public async Task The_preview_decodes_the_relinked_file()
    {
        var decoder = new FakeVideoDecoder();
        _playback = new PlaybackService(decoder, new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4 });
        var preview = new PreviewViewModel(new StatusService(), _playback, _projects, NullLogger<PreviewViewModel>.Instance);
        var (asset, _) = await OfflineVideoOnTimeline();
        var path = NewFile("new.mp4", Meta(10));
        decoder.Add(path, new FakeSource(Rate, 250));

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

        await TickUntil(LayerPictureState.Offline);
        await Relink(asset, path);
        await TickUntil(LayerPictureState.Frame);
        Assert.Contains(decoder.Requests, r => r.FilePath == path);
        _undo.Undo();
        await TickUntil(LayerPictureState.Offline);
    }

    [Fact]
    public async Task The_export_preflight_passes_for_the_relinked_media()
    {
        var (asset, _) = await OfflineVideoOnTimeline();
        var environment = new ExportPreflightEnvironment(true, _ => true, path => _files.ContainsKey(path), Directory.Exists);
        Assert.Contains(ExportPreflight.Check(_projects.Current, null, environment).Errors, e => e.Kind == ExportIssueKind.MediaOffline);

        await Relink(asset, NewFile("new.mp4", Meta(10)));

        Assert.DoesNotContain(ExportPreflight.Check(_projects.Current, null, environment).Errors, e => e.Kind == ExportIssueKind.MediaOffline);
    }

    [Fact]
    public async Task A_relink_without_ffprobe_leaves_the_media_unanalysed_and_the_analysis_coordinator_alone()
    {
        var analysis = new Probe(this);
        var coordinator = new MediaAnalysisCoordinator(analysis, _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var (asset, _) = await OfflineVideoOnTimeline();
        _probeUnavailable = true;

        await Relink(asset, NewFile("new.mp4", null));
        await coordinator.IdleAsync();

        Assert.Equal(MediaAnalysisStatus.Pending, asset.AnalysisStatus);
        Assert.Null(asset.Metadata);
        Assert.Equal(0, analysis.Calls);                                    // the ffprobe location is fixed for the run
        var environment = new ExportPreflightEnvironment(true, _ => true, path => _files.ContainsKey(path), Directory.Exists);
        Assert.Contains(ExportPreflight.Check(_projects.Current, null, environment).Errors, e => e.Kind == ExportIssueKind.MediaNotAnalyzed);
    }

    [Fact]
    public async Task A_later_incompatibility_is_shown_in_the_status_bar()
    {
        var status = new StatusService();
        var analysis = new MediaAnalysisCoordinator(new Probe(this), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, status, NullLogger<MediaImportWorkflow>.Instance);
        var projectFiles = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), status,
            NullLogger<ProjectFileWorkflow>.Instance);
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance);
        _ = new MainWindowViewModel(
            new ToolbarViewModel(_undo, projectFiles, import, status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, status),
            new TimelineViewModel(_projects, _edit, status, NullLogger<TimelineViewModel>.Instance),
            status, projectFiles, _projects, NullLogger<MainWindowViewModel>.Instance, relink: _relink);
        var (asset, _) = await OfflineVideoOnTimeline();
        _probeUnavailable = true;
        await Relink(asset, NewFile("new.mp4", null));

        asset.Metadata = Meta(2);                                           // the analysis at a later Open: 2 s < 4 s
        asset.AnalysisStatus = MediaAnalysisStatus.Completed;
        _projects.NotifyMediaAssetsChanged();

        Assert.Contains("new.mp4", status.Message);
        Assert.Contains("Undo", status.Message);
        Assert.Equal(Path.Combine(_root, "new.mp4"), asset.FilePath);      // nothing undone by itself
    }

    private sealed class NoImport : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromResult(new MediaImportBatchResult());
    }
}
