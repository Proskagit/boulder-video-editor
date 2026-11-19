using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D071 §2–§4 and amendment 2 through the view models: the Preview's quality (saved in the settings, «Среднее» by default) changes
/// the decoding on the fly; each 4K / HEVC video gets one 1080p proxy in the background, used at «Среднее» and «Минимум» once
/// ready, never made again for another quality; «Высокое» and «Оригинал» decode the source; with «Создавать прокси» off nothing is
/// made, a proxy already there is still used.
/// </summary>
public sealed class PreviewQualityTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly RecordingPlayback _playback = new();
    private readonly InMemoryUserSettings _settings = new();
    private readonly FakeStore _store = new();
    private readonly MediaAsset _uhd;
    private readonly MediaAsset _hd;
    private ProxyCoordinator? _proxies;
    private PreviewViewModel? _preview;

    public PreviewQualityTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _uhd = Video("climb-4k.mp4", 3840, 2160, "hevc");
        _hd = Video("phone-hd.mp4", 1920, 1080, "h264");
        _projects.AddMediaAssets(new[] { _uhd, _hd });
        Assert.True(_edit.AddClip(_uhd.Id).Success);
        Assert.True(_edit.AddClip(_hd.Id).Success);
    }

    private static MediaAsset Video(string name, int w, int h, string codec) => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), "aive-quality", name), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate, Width = w, Height = h, DisplayWidth = w, DisplayHeight = h,
            VideoCodec = codec, PixelFormat = "yuv420p"
        }
    };

    private PreviewViewModel Preview()
    {
        _proxies = new ProxyCoordinator(_store, _projects, _settings, NullLogger<ProxyCoordinator>.Instance);
        return _preview = new PreviewViewModel(new StatusService(), _playback, _projects, NullLogger<PreviewViewModel>.Instance,
            settings: _settings, proxies: _proxies);
    }

    private PlaybackAsset Decoding(MediaAsset asset) => _playback.Last!.Assets[asset.Id];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _store.ReleaseAll();
        if (_proxies is not null) await _proxies.ShutdownAsync();
        if (_preview is not null) await _preview.ReleasePlaybackAsync();
    }

    [Fact]
    public async Task Medium_bounds_the_decoding_and_the_ready_proxy_replaces_the_4K_source()
    {
        var preview = Preview();
        Assert.Equal((int)PreviewQuality.Medium, preview.QualityIndex);
        Assert.Equal(new(1920, 1080), Decoding(_uhd).DecodeBound);
        Assert.Equal(new(1920, 1080), Decoding(_hd).DecodeBound);
        Assert.Null(Decoding(_uhd).DecodePath);                           // the source until the proxy is ready

        _store.Release(_uhd);
        await _proxies!.IdleAsync();

        Assert.Equal(FakeStore.PathOf(_uhd), Decoding(_uhd).DecodePath);
        Assert.Null(Decoding(_hd).DecodePath);                            // a light 1080p H.264: no proxy
        Assert.DoesNotContain(_hd.Id, _store.Made);
    }

    [Fact]
    public async Task The_quality_changes_on_the_fly_and_never_asks_for_another_proxy()
    {
        // D071 amendment 2: one 1080p proxy per file; a quality only sets the Preview's bound and whether it takes the proxy.
        var preview = Preview();
        _store.Release(_uhd);
        await _proxies!.IdleAsync();
        var made = _store.Made.Count;

        Assert.Equal("1080p", preview.QualityShort);                        // the compact button
        preview.SetQualityCommand.Execute("3");                             // its menu: «Минимум (720p)»
        Assert.Equal("720p", preview.QualityShort);
        Assert.Equal(PreviewQuality.Low, _settings.Current.PreviewQuality);
        Assert.Equal(new(1280, 720), Decoding(_uhd).DecodeBound);           // the 1080p proxy scaled down on the fly
        Assert.Equal(FakeStore.PathOf(_uhd), Decoding(_uhd).DecodePath);

        preview.QualityIndex = (int)PreviewQuality.High;
        Assert.Equal(new(2560, 1440), Decoding(_uhd).DecodeBound);
        Assert.Null(Decoding(_uhd).DecodePath);                             // above the proxy: the source

        preview.QualityIndex = (int)PreviewQuality.Original;
        Assert.Equal(new(3840, 2160), Decoding(_uhd).DecodeBound);
        Assert.Null(Decoding(_uhd).DecodePath);

        preview.QualityIndex = (int)PreviewQuality.Medium;
        Assert.Equal(FakeStore.PathOf(_uhd), Decoding(_uhd).DecodePath);
        await _proxies.IdleAsync();
        Assert.Equal(made, _store.Made.Count);                              // nothing made again
    }

    [Fact]
    public async Task Without_making_proxies_none_is_made_but_one_already_there_is_used()
    {
        _settings.Update(s => s with { MakeProxies = false });
        _store.Existing.Add(_uhd.Id);
        Preview();
        await _proxies!.IdleAsync();
        Assert.Equal(FakeStore.PathOf(_uhd), Decoding(_uhd).DecodePath);
        Assert.Empty(_store.Made);
    }

    [Fact]
    public async Task A_video_whose_colour_is_found_later_gets_its_proxy_only_then_and_again_on_a_change()
    {
        _uhd.Metadata!.PixelFormat = null;                                 // D033 T3: colour not probed yet
        Preview();
        await _proxies!.IdleAsync();
        Assert.DoesNotContain(_uhd.Id, _store.Made);

        _uhd.Metadata.PixelFormat = "yuv420p";                             // the probe: SDR
        _projects.NotifyMediaAssetsChanged();
        await WaitAsync(() => _store.Made.Contains(_uhd.Id));
        _store.Release(_uhd);
        await _proxies.IdleAsync();
        Assert.NotNull(Decoding(_uhd).DecodePath);

        var before = _store.Made.Count(m => m == _uhd.Id);
        _uhd.Metadata.PixelFormat = "yuv420p10le";                         // later found HLG (a relink / refresh)
        _uhd.Metadata.BitDepth = 10;
        _uhd.Metadata.ColorTransfer = "arib-std-b67";
        _uhd.Metadata.ColorPrimaries = "bt2020";
        _uhd.Metadata.ColorMatrix = "bt2020nc";
        _store.Forget(_uhd);                        // the store's key has the colour: no file for HLG
        _projects.NotifyMediaAssetsChanged();
        await WaitAsync(() => _store.Made.Count(m => m == _uhd.Id) > before);
    }

    [Fact]
    public async Task A_background_refresh_in_the_real_order_leaves_the_proxy_requested()
    {
        // MediaAnalysisCoordinator's order: the flag set, the metadata replaced, the event while the flag is still set, then the
        // flag cleared with no event (also when the probe fails).
        _uhd.Metadata!.PixelFormat = null;
        Preview();
        await _proxies!.IdleAsync();
        Assert.DoesNotContain(_uhd.Id, _store.Made);

        _uhd.IsMetadataRefreshing = true;
        _uhd.Metadata = new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate, Width = 3840, Height = 2160, DisplayWidth = 3840,
            DisplayHeight = 2160, VideoCodec = "hevc", PixelFormat = "yuv420p"
        };
        _projects.NotifyMediaAssetsChanged();
        _uhd.IsMetadataRefreshing = false;

        await WaitAsync(() => _store.Made.Contains(_uhd.Id));
        _store.Release(_uhd);
        await _proxies.IdleAsync();
        Assert.NotNull(Decoding(_uhd).DecodePath);
    }

    [Fact]
    public async Task The_status_bar_shows_the_proxy_being_made_and_its_cancel_gives_it_up()
    {
        Preview();
        var progress = _proxies!.Progress;
        await WaitAsync(() => progress.IsBuilding);
        Assert.Equal("climb-4k.mp4", progress.FileName);

        progress.CancelCommand.Execute(null);
        await _proxies.IdleAsync();
        await WaitAsync(() => !progress.IsBuilding);                       // the indicator's end is posted to the UI context
        Assert.Null(Decoding(_uhd).DecodePath);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    /// <summary>A store whose proxies are made when the test releases them.</summary>
    private sealed class FakeStore : IProxyStore
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _gates = new();

        public ConcurrentBag<Guid> Made { get; } = new();
        public HashSet<Guid> Existing { get; } = new();

        public static string PathOf(MediaAsset asset) => $@"C:\proxies\{asset.Id:N}-1080p.mp4";

        public string Folder => @"C:\proxies";

        public string? TryGet(MediaAsset asset)
        {
            lock (Existing) return Existing.Contains(asset.Id) ? PathOf(asset) : null;
        }

        public async Task<string?> GetOrCreateAsync(MediaAsset asset, IProgress<double>? progress, CancellationToken ct)
        {
            Made.Add(asset.Id);
            progress?.Report(0.3);
            await Gate(asset.Id).Task.WaitAsync(ct);
            lock (Existing) Existing.Add(asset.Id);
            return PathOf(asset);
        }

        public void Release(MediaAsset asset) => Gate(asset.Id).TrySetResult();

        public void Forget(MediaAsset asset)
        {
            lock (Existing) Existing.Remove(asset.Id);
            _gates.TryRemove(asset.Id, out _);
        }

        public void ReleaseAll()
        {
            foreach (var gate in _gates.Values) gate.TrySetResult();
        }

        private TaskCompletionSource Gate(Guid id) =>
            _gates.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public long SizeBytes() => 0;
        public long Clear() => 0;
    }

    private sealed class RecordingPlayback : IPlaybackService
    {
        public PlaybackSnapshot? Last { get; private set; }
        public PlaybackState State => PlaybackState.Paused;
        public bool IsBuffering => false;
        public bool IsAvailable => true;
        public bool IsAudioAvailable => true;
        public MediaTime Position => MediaTime.Zero;
        public MediaTime Duration => MediaTime.FromSeconds(60);
        public MediaTime PlaybackEnd => Duration;
        public event EventHandler? StateChanged { add { } remove { } }
        public void UpdateSnapshot(PlaybackSnapshot snapshot) => Last = snapshot;
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void SetPlaybackRange(PlaybackRange? range) { }
        public Task<bool> SeekAsync(MediaTime position, CancellationToken ct = default) => Task.FromResult(true);
        public PlaybackFrame Update() => new(Position, 0, State, false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
