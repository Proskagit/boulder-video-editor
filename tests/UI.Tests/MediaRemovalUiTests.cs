using System.Collections.Concurrent;
using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 12 Step 12.4 (D027 §4): Remove in the Media Browser. An unused asset goes at once; a used one only after the
/// user confirms ("Remove" / "Cancel", naming the clips and that the file stays on disk); a removal the edit service
/// would refuse is not asked about; nothing during an export or a relink. Thumbnails stay consistent: the removed row
/// is gone, Undo shows the asset's thumbnail again without making it anew, and a thumbnail or an analysis still
/// running when the asset is removed ends normally and is there after Undo (no asset stuck "Analyzing").
/// </summary>
public sealed class MediaRemovalUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly CountingThumbnails _thumbnailService = new();
    private readonly ThumbnailCoordinator _thumbnails;

    public MediaRemovalUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _thumbnails = new ThumbnailCoordinator(_thumbnailService, _projects, new FixedLocation(), NullLogger<ThumbnailCoordinator>.Instance);
    }

    private MediaBrowserViewModel Browser(ScriptedDialogs? dialogs = null, MediaAnalysisCoordinator? analysis = null)
    {
        analysis ??= new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status,
            NullLogger<MediaImportWorkflow>.Instance);
        return new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance, _lock, _thumbnails,
            edit: _edit, dialogs: dialogs ?? new ScriptedDialogs(), status: _status);
    }

    private static MediaAsset Asset(string name, MediaKind kind = MediaKind.Video) => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), "aive-removal", Guid.NewGuid().ToString("N"), name),
        Kind = kind, AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), Width = 64, Height = 36, FrameRate = Rate }
    };

    private MediaAsset Import(string name, MediaKind kind = MediaKind.Video)
    {
        var asset = Asset(name, kind);
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private static void Select(MediaBrowserViewModel browser, MediaAsset asset) =>
        browser.SelectedItem = browser.Items.Single(i => i.Asset.Id == asset.Id);

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
    public async Task An_unused_asset_is_removed_without_asking()
    {
        var keep = Import("keep.mp4");
        var unused = Import("unused.mp4");
        var dialogs = new ScriptedDialogs();
        var browser = Browser(dialogs);
        Select(browser, unused);
        MediaAsset? inspected = unused;
        browser.SelectionChanged += (_, a) => inspected = a;

        await browser.RemoveCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.Asked);
        Assert.Equal(new[] { keep }, browser.Items.Select(i => i.Asset));
        Assert.Null(browser.SelectedItem);
        Assert.Null(inspected);                                          // the Inspector is told: nothing selected
        Assert.Equal("Removed unused.mp4 from the project.", _status.Message);

        _undo.Undo();
        Assert.Equal(new[] { keep, unused }, browser.Items.Select(i => i.Asset));
    }

    [Fact]
    public async Task A_used_asset_is_removed_with_its_clips_only_after_the_user_confirms()
    {
        var used = Import("used.mp4");
        Assert.True(_edit.AddClip(used.Id).Success);
        Assert.True(_edit.AddClip(used.Id).Success);
        var dialogs = new ScriptedDialogs(1, null, 0);                   // Cancel, closed, then Remove
        var browser = Browser(dialogs);
        Select(browser, used);

        await browser.RemoveCommand.ExecuteAsync(null);
        await browser.RemoveCommand.ExecuteAsync(null);
        Assert.Contains(used, _projects.Current.MediaAssets);            // neither Cancel nor closing removes
        Assert.Equal(2, _projects.Current.Timeline.VideoTracks[0].Clips.Count);

        await browser.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(3, dialogs.Asked.Count);
        var question = dialogs.Asked[0];
        Assert.Equal("Remove Media", question.Title);
        Assert.Contains("used.mp4 is used by 2 clips on the timeline", question.Message);
        Assert.Contains("The file on disk is not deleted.", question.Message);
        Assert.Equal(new[] { "Remove", "Cancel" }, question.Buttons);
        Assert.DoesNotContain(used, _projects.Current.MediaAssets);
        Assert.Empty(_projects.Current.Timeline.VideoTracks[0].Clips);
        Assert.Equal("Removed used.mp4 and its 2 clips from the project.", _status.Message);

        _undo.Undo();                                                    // one step: the asset and both clips
        Assert.Contains(browser.Items, i => i.Asset == used);
        Assert.Equal(2, _projects.Current.Timeline.VideoTracks[0].Clips.Count);
    }

    [Fact]
    public async Task A_removal_the_service_refuses_is_not_asked_about()
    {
        var music = Import("music.mp3", MediaKind.Audio);
        Assert.True(_edit.AddClip(music.Id).Success);
        _projects.Current.Timeline.AudioTracks[0].IsLocked = true;
        var dialogs = new ScriptedDialogs(0);
        var browser = Browser(dialogs);
        Select(browser, music);

        await browser.RemoveCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.Asked);
        Assert.Equal("music.mp3 is used on track A1, which is locked.", _status.Message);
        Assert.Contains(music, _projects.Current.MediaAssets);
    }

    [Fact]
    public async Task An_export_started_while_the_question_is_open_cancels_the_removal()
    {
        var used = Import("used.mp4");
        Assert.True(_edit.AddClip(used.Id).Success);
        IDisposable? held = null;
        var dialogs = new ScriptedDialogs(0) { OnAsk = () => held = _lock.Acquire() };
        var browser = Browser(dialogs);
        Select(browser, used);

        await browser.RemoveCommand.ExecuteAsync(null);

        Assert.Contains(used, _projects.Current.MediaAssets);
        held!.Dispose();
    }

    [Fact]
    public void Remove_needs_a_selection_and_is_disabled_during_an_export()
    {
        var asset = Import("a.mp4");
        var browser = Browser();
        Assert.False(browser.RemoveCommand.CanExecute(null));            // nothing selected

        Select(browser, asset);
        Assert.True(browser.RemoveCommand.CanExecute(null));
        using (_lock.Acquire())
            Assert.False(browser.RemoveCommand.CanExecute(null));
        Assert.True(browser.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public void Without_the_edit_service_there_is_no_remove()
    {
        var asset = Import("a.mp4");
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status,
            NullLogger<MediaImportWorkflow>.Instance);
        var browser = new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance);
        Select(browser, asset);

        Assert.False(browser.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Undo_shows_the_assets_thumbnail_again_without_making_it_anew()
    {
        var asset = Import("thumb.mp4");
        var browser = Browser();
        await Eventually(() => browser.Items.Single().HasThumbnail, "the thumbnail");
        var thumbnail = browser.Items.Single().Thumbnail;
        Select(browser, asset);

        await browser.RemoveCommand.ExecuteAsync(null);
        Assert.Empty(browser.Items);
        _undo.Undo();

        var row = browser.Items.Single();
        Assert.Same(thumbnail, row.Thumbnail);                           // shown again at once
        await _thumbnails.IdleAsync();
        Assert.Equal(1, _thumbnailService.Made(asset.Id));                // made once in all
    }

    [Fact]
    public async Task A_thumbnail_still_being_made_when_the_asset_is_removed_is_there_after_undo()
    {
        var gate = _thumbnailService.HoldNext();
        var asset = Import("slow.mp4");
        var browser = Browser();
        Select(browser, asset);

        await browser.RemoveCommand.ExecuteAsync(null);
        gate.SetResult();                                                // the work ends while the asset is removed
        await _thumbnails.IdleAsync();
        Assert.Empty(browser.Items);

        _undo.Undo();

        Assert.True(browser.Items.Single().HasThumbnail);
        Assert.Equal(1, _thumbnailService.Made(asset.Id));
    }

    [Fact]
    public async Task An_analysis_still_running_when_the_asset_is_removed_completes_and_the_asset_comes_back_analysed()
    {
        var probe = new GatedAnalysis();
        var analysis = new MediaAnalysisCoordinator(probe, _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var asset = new MediaAsset { FilePath = Path.Combine(Path.GetTempPath(), "aive-removal", "probing.mp4"), Kind = MediaKind.Video };
        _projects.AddMediaAssets(new[] { asset });
        var browser = Browser(analysis: analysis);
        analysis.QueueAnalysis(asset);
        Assert.Equal(MediaAnalysisStatus.Analyzing, asset.AnalysisStatus);
        Select(browser, asset);

        await browser.RemoveCommand.ExecuteAsync(null);
        probe.Gate.SetResult();
        await analysis.IdleAsync();

        _undo.Undo();
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);  // never left "Analyzing"
        Assert.Equal(MediaTime.FromSeconds(7), asset.Metadata!.Duration);
        Assert.Contains(browser.Items, i => i.Asset == asset);
    }

    // --- fakes ------------------------------------------------------------------------------------------------------------

    private sealed class CountingThumbnails : IThumbnailService
    {
        private readonly ConcurrentDictionary<Guid, int> _made = new();
        private TaskCompletionSource? _hold;

        public int Made(Guid id) => _made.GetValueOrDefault(id);

        /// <summary>Holds the next thumbnail being made until the returned source is completed.</summary>
        public TaskCompletionSource HoldNext() =>
            _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public Thumbnail? TryGetCached(MediaAsset asset, string cacheFolder) => null;

        public async Task<Thumbnail?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default)
        {
            var hold = Interlocked.Exchange(ref _hold, null);
            if (hold is not null) await hold.Task.WaitAsync(ct);
            else await Task.Yield();
            _made.AddOrUpdate(asset.Id, 1, (_, n) => n + 1);
            return new Thumbnail(1, 1, new byte[] { 1, 2, 3, 255 });
        }
    }

    private sealed class FixedLocation : IThumbnailCacheLocation
    {
        public string CurrentFolder => "cache";
        public event EventHandler? Changed { add { } remove { } }
        public Task CleanUpUnsavedAsync() => Task.CompletedTask;
    }

    private sealed class GatedAnalysis : IMediaAnalysisService
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            await Gate.Task.WaitAsync(ct);
            return MediaAnalysisResult.Success(new MediaMetadata { Duration = MediaTime.FromSeconds(7) });
        }
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
