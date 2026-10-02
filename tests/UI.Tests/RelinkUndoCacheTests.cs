using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Step 11.6 acceptance defect D2: after an Undo of a relink an offline asset showed the thumbnail (or waveform) of the
/// file it had been relinked to — the cache keeps one file per asset and offline media takes the last one (D024). Now
/// what was shown for each file comes back with Undo / Redo, for single and batch relinks and repeated steps, while media
/// that is offline for any other reason still gets its last cached result. The fake services behave like the real cache:
/// one stored result per asset (the last one made), offline lookups by asset id only.
/// </summary>
public sealed class RelinkUndoCacheTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "aive-relink-undo-cache");

    private readonly ConcurrentDictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly MediaRelinkService _relink;

    public RelinkUndoCacheTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance, new ProjectFileStore(), path => _files.ContainsKey(path));
        _projects.Current.Settings.FrameRate = Rate;
        _projects.Current.Settings.IsFrameRateLocked = true;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _relink = new MediaRelinkService(_projects, _undo, new Probe(), NullLogger<MediaRelinkService>.Instance,
            path => _files.TryGetValue(path, out var size) ? size : null);
    }

    private sealed class Probe : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Success(Path.GetExtension(filePath) == ".wav" ? AudioMeta() : VideoMeta()));
    }

    private static MediaMetadata VideoMeta() => new()
    {
        Duration = MediaTime.FromSeconds(10), Width = 64, Height = 36, DisplayWidth = 64, DisplayHeight = 36, DisplayRotation = 0,
        FrameRate = Rate, AvgFrameRate = Rate, StartTime = MediaTime.Zero, VideoCodec = "h264", AudioCodec = "aac",
        AudioSampleRate = 48000, AudioChannels = 2
    };

    private static MediaMetadata AudioMeta() => new()
    {
        Duration = MediaTime.FromSeconds(10), StartTime = MediaTime.Zero, AudioCodec = "pcm_s16le", AudioSampleRate = 48000, AudioChannels = 2
    };

    /// <summary>One stored result per asset — the last one made, with the file it was made from (as <c>SourceFileCache</c>:
    /// an online lookup matches the file, an offline one takes whatever is stored, D024); a made result is numbered.</summary>
    private sealed class Store<T>(Func<int, T> make) where T : class
    {
        private int _made;
        private readonly ConcurrentDictionary<Guid, string> _madeFrom = new();
        public ConcurrentDictionary<Guid, T> Cached { get; } = new();
        public ConcurrentQueue<string> Makes { get; } = new();
        public volatile TaskCompletionSource? Gate;

        public T? TryGetCached(MediaAsset asset) =>
            Cached.TryGetValue(asset.Id, out var t) &&
            (asset.IsMissing || !_madeFrom.TryGetValue(asset.Id, out var from) || string.Equals(from, asset.FilePath, StringComparison.OrdinalIgnoreCase))
                ? t : null;

        public async Task<T?> MakeAsync(MediaAsset asset, CancellationToken ct)
        {
            Makes.Enqueue(asset.FilePath);
            var result = make(Interlocked.Increment(ref _made));
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            await Task.Yield();
            Cached[asset.Id] = result;                                       // the asset's older file is replaced
            _madeFrom[asset.Id] = asset.FilePath;
            return result;
        }
    }

    private sealed class Thumbnails(Store<Thumbnail> store) : IThumbnailService
    {
        public Thumbnail? TryGetCached(MediaAsset asset, string cacheFolder) => store.TryGetCached(asset);
        public Task<Thumbnail?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default) => store.MakeAsync(asset, ct);
    }

    private sealed class Waveforms(Store<Waveform> store) : IWaveformService
    {
        public Waveform? TryGetCached(MediaAsset asset, string cacheFolder) => store.TryGetCached(asset);
        public Task<Waveform?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default) => store.MakeAsync(asset, ct);
    }

    private sealed class Location : IThumbnailCacheLocation, IWaveformCacheLocation
    {
        public string CurrentFolder => "cache";
        public event EventHandler? Changed { add { } remove { } }
        public Task CleanUpUnsavedAsync() => Task.CompletedTask;
    }

    private static Thumbnail Picture(int number) => new(1, 1, new byte[] { (byte)number, 0, 0, 255 });
    private static int Number(Thumbnail? t) => t is null ? 0 : t.Pixels.Span[0];

    private (Store<Thumbnail> Store, ThumbnailCoordinator Coordinator) ThumbnailSetup(Store<Thumbnail>? store = null)
    {
        store ??= new Store<Thumbnail>(Picture);
        return (store, new ThumbnailCoordinator(new Thumbnails(store), _projects, new Location(), NullLogger<ThumbnailCoordinator>.Instance));
    }

    /// <summary>An analysed media item with a 4 s clip; <paramref name="present"/> decides whether its file is there.</summary>
    private async Task<MediaAsset> Media(string name, MediaKind kind = MediaKind.Video, double at = 0, bool present = false)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Root, "old", name), Kind = kind, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = kind == MediaKind.Audio ? AudioMeta() : VideoMeta()
        };
        _files[asset.FilePath] = 100;
        _projects.AddMediaAssets(new[] { asset });
        var add = _edit.AddClip(asset.Id, null, MediaTime.FromSeconds(at));
        Assert.True(add.Success, add.Message);
        Assert.True(_edit.TrimClip(add.ClipIds[0], ClipEdge.End, MediaTime.FromSeconds(at + 4)).Success);
        if (!present) _files.TryRemove(asset.FilePath, out _);
        await _projects.RecheckMediaAsync();
        return asset;
    }

    private string NewFile(string name)
    {
        var path = Path.Combine(Root, "new", name);
        _files[path] = 200;
        return path;
    }

    private async Task RelinkAll(params (MediaAsset Asset, string Path)[] items)
    {
        var checks = new List<RelinkCheck>();
        foreach (var (asset, path) in items) checks.Add(await _relink.CheckAsync(asset.Id, path));
        Assert.Equal(items.Length, (await _relink.ApplyAllAsync(checks)).AppliedCount);
    }

    [Fact]
    public async Task An_offline_item_without_a_thumbnail_has_none_again_after_undo_and_its_new_one_after_redo()
    {
        var asset = await Media("a.mp4");                                   // offline when the project is shown, nothing cached
        var (store, coordinator) = ThumbnailSetup();
        await coordinator.IdleAsync();
        Assert.Null(coordinator.Get(asset.Id));

        await RelinkAll((asset, NewFile("a.mp4")));
        await coordinator.IdleAsync();
        var relinked = coordinator.Get(asset.Id);
        Assert.Equal(1, Number(relinked));
        Assert.NotNull(store.TryGetCached(asset));                          // the cache now holds the relinked file's

        for (var round = 0; round < 3; round++)
        {
            _undo.Undo();
            await coordinator.IdleAsync();
            Assert.True(asset.IsMissing);
            Assert.Null(coordinator.Get(asset.Id));                         // not the relinked file's thumbnail (D2)

            _undo.Redo();
            await coordinator.IdleAsync();
            Assert.Same(relinked, coordinator.Get(asset.Id));
        }
        Assert.Single(store.Makes);                                         // restored, never made again
    }

    [Fact]
    public async Task A_batch_undo_and_redo_give_every_item_back_what_it_showed()
    {
        var without = await Media("none.mp4", at: 5);                       // offline, nothing cached
        var withOwn = await Media("own.mp4", at: 20, present: true);      // online …
        var (store, coordinator) = ThumbnailSetup();
        await coordinator.IdleAsync();                                      // … its thumbnail made
        var own = coordinator.Get(withOwn.Id);
        Assert.NotNull(own);
        _files.TryRemove(withOwn.FilePath, out _);
        await _projects.RecheckMediaAsync();                                // … then offline: still shown (D024)
        await coordinator.IdleAsync();
        Assert.Same(own, coordinator.Get(withOwn.Id));
        Assert.Null(coordinator.Get(without.Id));

        await RelinkAll((withOwn, NewFile("own.mp4")), (without, NewFile("none.mp4")));
        await coordinator.IdleAsync();
        var newOwn = coordinator.Get(withOwn.Id);
        var newNone = coordinator.Get(without.Id);
        Assert.NotNull(newOwn);
        Assert.NotNull(newNone);

        _undo.Undo();                                                       // one step for the batch
        await coordinator.IdleAsync();
        Assert.Same(own, coordinator.Get(withOwn.Id));
        Assert.Null(coordinator.Get(without.Id));

        _undo.Redo();
        await coordinator.IdleAsync();
        Assert.Same(newOwn, coordinator.Get(withOwn.Id));
        Assert.Same(newNone, coordinator.Get(without.Id));
    }

    [Fact]
    public async Task Consecutive_relinks_and_undos_each_come_back_to_their_file()
    {
        var asset = await Media("a.mp4");
        var (_, coordinator) = ThumbnailSetup();
        await coordinator.IdleAsync();
        await RelinkAll((asset, NewFile("first.mp4")));
        await coordinator.IdleAsync();
        var first = coordinator.Get(asset.Id);
        _files.TryRemove(asset.FilePath, out _);
        await _projects.RecheckMediaAsync();                                // the first file goes too
        await RelinkAll((asset, NewFile("second.mp4")));
        await coordinator.IdleAsync();
        var second = coordinator.Get(asset.Id);
        Assert.NotSame(first, second);

        _undo.Undo();                                                       // back to the first file (offline now)
        await coordinator.IdleAsync();
        Assert.Same(first, coordinator.Get(asset.Id));
        _undo.Undo();                                                       // back to the original: nothing
        await coordinator.IdleAsync();
        Assert.Null(coordinator.Get(asset.Id));
        _undo.Redo();
        _undo.Redo();
        await coordinator.IdleAsync();
        Assert.Same(second, coordinator.Get(asset.Id));
    }

    [Fact]
    public async Task A_thumbnail_still_being_made_when_undone_is_never_published()
    {
        var asset = await Media("a.mp4");
        var (store, coordinator) = ThumbnailSetup();
        await coordinator.IdleAsync();
        store.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await RelinkAll((asset, NewFile("a.mp4")));
        var until = DateTime.UtcNow.AddSeconds(5);
        while (store.Makes.IsEmpty && DateTime.UtcNow < until) await Task.Delay(2);

        _undo.Undo();                                                       // while the new file's thumbnail is made
        store.Gate.SetResult();
        await coordinator.IdleAsync();

        Assert.Null(coordinator.Get(asset.Id));
    }

    [Fact]
    public async Task Media_offline_for_any_other_reason_still_shows_its_last_cached_thumbnail()
    {
        var asset = await Media("a.mp4");
        var store = new Store<Thumbnail>(Picture);
        store.Cached[asset.Id] = Picture(42);                              // cached by an earlier session
        var (_, coordinator) = ThumbnailSetup(store);                       // the project as it is opened

        await coordinator.IdleAsync();

        Assert.Equal(42, Number(coordinator.Get(asset.Id)));               // D024 unchanged
    }

    [Fact]
    public async Task An_offline_audio_clip_gets_no_waveform_of_an_undone_relink()
    {
        var asset = await Media("a.wav", MediaKind.Audio);
        var store = new Store<Waveform>(n => new Waveform(256, 256 * n, new byte[n]));
        var coordinator = new WaveformCoordinator(new Waveforms(store), _projects, new Location(), NullLogger<WaveformCoordinator>.Instance);
        await coordinator.IdleAsync();
        Assert.Null(coordinator.Get(asset.Id));

        await RelinkAll((asset, NewFile("a.wav")));
        await coordinator.IdleAsync();
        var relinked = coordinator.Get(asset.Id);
        Assert.NotNull(relinked);

        _undo.Undo();
        await coordinator.IdleAsync();
        Assert.Null(coordinator.Get(asset.Id));                             // the same defect as D2, fixed alike
        _undo.Redo();
        await coordinator.IdleAsync();
        Assert.Same(relinked, coordinator.Get(asset.Id));
    }
}
