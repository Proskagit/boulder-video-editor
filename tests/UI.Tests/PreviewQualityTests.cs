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
/// D071 §2–§4 through the view models: the Preview's quality on its bar (saved in the settings, «Среднее» by default) bounds
/// every video's decoding; a 4K / HEVC video's proxy is made in the background and replaces the source in the Preview's
/// snapshot once ready; another quality drops the proxies and asks for its own; «Оригинал» decodes the source unbounded; with
/// «Создавать прокси» off nothing is made, a proxy already there is still used.
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

        _store.Release(_uhd, PreviewQuality.Medium);
        await _proxies!.IdleAsync();

        Assert.Equal(FakeStore.PathOf(_uhd, PreviewQuality.Medium), Decoding(_uhd).DecodePath);
        Assert.Null(Decoding(_hd).DecodePath);                            // a light 1080p H.264: no proxy
        Assert.DoesNotContain(_store.Made, m => m.Asset == _hd.Id);
    }

    [Fact]
    public async Task Another_quality_asks_for_its_own_proxy_and_the_original_decodes_the_source_unbounded()
    {
        var preview = Preview();
        _store.Release(_uhd, PreviewQuality.Medium);
        await _proxies!.IdleAsync();

        preview.QualityIndex = (int)PreviewQuality.Low;
        Assert.Equal(PreviewQuality.Low, _settings.Current.PreviewQuality);
        Assert.Equal(new(1280, 720), Decoding(_uhd).DecodeBound);
        Assert.Null(Decoding(_uhd).DecodePath);                           // the Medium proxy is never shown at Low
        _store.Release(_uhd, PreviewQuality.Low);
        await _proxies.IdleAsync();
        Assert.Equal(FakeStore.PathOf(_uhd, PreviewQuality.Low), Decoding(_uhd).DecodePath);

        preview.QualityIndex = (int)PreviewQuality.Original;
        await _proxies.IdleAsync();
        Assert.Equal(new(3840, 2160), Decoding(_uhd).DecodeBound);
        Assert.Null(Decoding(_uhd).DecodePath);
        Assert.DoesNotContain(_store.Made, m => m.Quality == PreviewQuality.Original);
    }

    [Fact]
    public async Task Without_making_proxies_none_is_made_but_one_already_there_is_used()
    {
        _settings.Update(s => s with { MakeProxies = false });
        _store.Existing.Add((_uhd.Id, PreviewQuality.Low));
        var preview = Preview();
        await _proxies!.IdleAsync();
        Assert.Null(Decoding(_uhd).DecodePath);
        Assert.Empty(_store.Made);

        preview.QualityIndex = (int)PreviewQuality.Low;
        await _proxies.IdleAsync();
        Assert.Equal(FakeStore.PathOf(_uhd, PreviewQuality.Low), Decoding(_uhd).DecodePath);
        Assert.Empty(_store.Made);
    }

    [Fact]
    public async Task A_video_whose_colour_is_found_later_gets_its_proxy_only_then_and_again_on_a_change()
    {
        _uhd.Metadata!.PixelFormat = null;                                 // D033 T3: colour not probed yet
        Preview();
        await _proxies!.IdleAsync();
        Assert.DoesNotContain(_store.Made, m => m.Asset == _uhd.Id);

        _uhd.Metadata.PixelFormat = "yuv420p";                             // the probe: SDR
        _projects.NotifyMediaAssetsChanged();
        await WaitAsync(() => _store.Made.Any(m => m.Asset == _uhd.Id));
        _store.Release(_uhd, PreviewQuality.Medium);
        await _proxies.IdleAsync();
        Assert.NotNull(Decoding(_uhd).DecodePath);

        var before = _store.Made.Count(m => m.Asset == _uhd.Id);
        _uhd.Metadata.PixelFormat = "yuv420p10le";                         // later found HLG (a relink / refresh)
        _uhd.Metadata.BitDepth = 10;
        _uhd.Metadata.ColorTransfer = "arib-std-b67";
        _uhd.Metadata.ColorPrimaries = "bt2020";
        _uhd.Metadata.ColorMatrix = "bt2020nc";
        _store.Forget(_uhd, PreviewQuality.Medium);                        // the store's key has the colour: no file for HLG
        _projects.NotifyMediaAssetsChanged();
        await WaitAsync(() => _store.Made.Count(m => m.Asset == _uhd.Id) > before);
    }

    [Fact]
    public async Task A_background_refresh_in_the_real_order_leaves_the_proxy_requested()
    {
        // MediaAnalysisCoordinator's order: the flag set, the metadata replaced, the event while the flag is still set, then the
        // flag cleared with no event (also when the probe fails).
        _uhd.Metadata!.PixelFormat = null;
        Preview();
        await _proxies!.IdleAsync();
        Assert.DoesNotContain(_store.Made, m => m.Asset == _uhd.Id);

        _uhd.IsMetadataRefreshing = true;
        _uhd.Metadata = new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate, Width = 3840, Height = 2160, DisplayWidth = 3840,
            DisplayHeight = 2160, VideoCodec = "hevc", PixelFormat = "yuv420p"
        };
        _projects.NotifyMediaAssetsChanged();
        _uhd.IsMetadataRefreshing = false;

        await WaitAsync(() => _store.Made.Any(m => m.Asset == _uhd.Id));
        _store.Release(_uhd, PreviewQuality.Medium);
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
        Assert.False(progress.IsBuilding);
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
        private readonly ConcurrentDictionary<(Guid, PreviewQuality), TaskCompletionSource> _gates = new();

        public ConcurrentBag<(Guid Asset, PreviewQuality Quality)> Made { get; } = new();
        public HashSet<(Guid, PreviewQuality)> Existing { get; } = new();

        public static string PathOf(MediaAsset asset, PreviewQuality quality) => $@"C:\proxies\{asset.Id:N}-{quality}.mp4";

        public string Folder => @"C:\proxies";

        public string? TryGet(MediaAsset asset, PreviewQuality quality)
        {
            lock (Existing) return Existing.Contains((asset.Id, quality)) ? PathOf(asset, quality) : null;
        }

        public async Task<string?> GetOrCreateAsync(MediaAsset asset, PreviewQuality quality, IProgress<double>? progress, CancellationToken ct)
        {
            Made.Add((asset.Id, quality));
            progress?.Report(0.3);
            await Gate(asset.Id, quality).Task.WaitAsync(ct);
            lock (Existing) Existing.Add((asset.Id, quality));
            return PathOf(asset, quality);
        }

        public void Release(MediaAsset asset, PreviewQuality quality) => Gate(asset.Id, quality).TrySetResult();

        public void Forget(MediaAsset asset, PreviewQuality quality)
        {
            lock (Existing) Existing.Remove((asset.Id, quality));
            _gates.TryRemove((asset.Id, quality), out _);
        }

        public void ReleaseAll()
        {
            foreach (var gate in _gates.Values) gate.TrySetResult();
        }

        private TaskCompletionSource Gate(Guid id, PreviewQuality quality) =>
            _gates.GetOrAdd((id, quality), _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

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
