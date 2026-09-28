using System.Collections.Concurrent;
using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D024 Step 9.4d: the Media Browser rows show the thumbnails the coordinator makes — a row gets its thumbnail when it
/// becomes ready and keeps it (the same instance) when the rows are rebuilt; audio and offline media without a cached
/// thumbnail keep their colour tile; another project starts with none.
/// </summary>
public sealed class MediaBrowserThumbnailTests
{
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly FakeThumbnails _service = new();
    private readonly MediaBrowserViewModel _browser;

    public MediaBrowserThumbnailTests()
    {
        var coordinator = new ThumbnailCoordinator(_service, _projects, new FixedLocation(), NullLogger<ThumbnailCoordinator>.Instance);
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, new StatusService(),
            NullLogger<MediaImportWorkflow>.Instance);
        _browser = new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance, thumbnails: coordinator);
    }

    private sealed class FakeThumbnails : IThumbnailService
    {
        public ConcurrentDictionary<Guid, Thumbnail> Cached { get; } = new();
        public Thumbnail? TryGetCached(MediaAsset asset, string cacheFolder) => Cached.TryGetValue(asset.Id, out var t) ? t : null;
        public async Task<Thumbnail?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default)
        {
            await Task.Yield();
            return new Thumbnail(1, 1, new byte[] { 1, 2, 3, 255 });
        }
    }

    private sealed class FixedLocation : IThumbnailCacheLocation
    {
        public string CurrentFolder => "cache";
        public event EventHandler? Changed { add { } remove { } }
        public Task CleanUpUnsavedAsync() => Task.CompletedTask;
    }

    private static MediaAsset Asset(string name, MediaKind kind = MediaKind.Video) => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), "aive-browser-thumbs", Guid.NewGuid().ToString("N"), name),
        Kind = kind, AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), Width = 64, Height = 36 }
    };

    private MediaBrowserItemViewModel Row(MediaAsset asset) => _browser.Items.Single(i => i.Asset.Id == asset.Id);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task A_row_gets_its_thumbnail_when_it_is_ready_and_keeps_it_when_the_rows_are_rebuilt()
    {
        var video = Asset("clip.mp4");
        _projects.AddMediaAssets(new[] { video });
        var row = Row(video);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(row.HasThumbnail);                                   // the colour tile until then

        await Eventually(() => row.HasThumbnail, "the row got no thumbnail");
        Assert.Contains(nameof(MediaBrowserItemViewModel.Thumbnail), changed);
        Assert.Contains(nameof(MediaBrowserItemViewModel.HasThumbnail), changed);
        var thumbnail = row.Thumbnail;

        _projects.NotifyMediaAssetsChanged();                             // the rows are rebuilt (new view models)
        Assert.NotSame(row, Row(video));
        Assert.Same(thumbnail, Row(video).Thumbnail);                     // same thumbnail: no new bitmap for the view
    }

    [Fact]
    public async Task Audio_and_offline_media_without_a_cached_thumbnail_keep_their_colour_tile()
    {
        var audio = Asset("a.mp3", MediaKind.Audio);
        var offline = Asset("o.mp4");
        offline.IsMissing = true;
        var offlineCached = Asset("oc.mp4");
        offlineCached.IsMissing = true;
        _service.Cached[offlineCached.Id] = new Thumbnail(1, 1, new byte[4]);
        var video = Asset("v.mp4");

        _projects.AddMediaAssets(new[] { audio, offline, offlineCached, video });
        await Eventually(() => Row(video).HasThumbnail && Row(offlineCached).HasThumbnail, "thumbnails not shown");

        Assert.False(Row(audio).HasThumbnail);
        Assert.False(Row(offline).HasThumbnail);
        Assert.Equal("#3A784F", Row(audio).ThumbnailColorHex);
    }

    [Fact]
    public async Task Another_project_starts_without_the_previous_thumbnails()
    {
        var video = Asset("clip.mp4");
        _projects.AddMediaAssets(new[] { video });
        await Eventually(() => Row(video).HasThumbnail, "the row got no thumbnail");

        _projects.CreateNew("Other");
        var next = Asset("next.mp4");
        _projects.AddMediaAssets(new[] { next });

        Assert.DoesNotContain(_browser.Items, i => i.Asset.Id == video.Id);
        await Eventually(() => Row(next).HasThumbnail, "the new project's row got no thumbnail");
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
