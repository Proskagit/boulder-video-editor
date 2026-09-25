using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// The real shell wiring (MainWindowViewModel) over real project/timeline services and the real
/// PlaybackService, with a fake decoder and a manual reference clock. UI ticks are driven by the
/// test exactly as the view's timer would call <see cref="PreviewViewModel.Tick"/>.
/// </summary>
public sealed class PlaybackUiIntegrationTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undoRedo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _playback;
    private readonly MainWindowViewModel _vm;
    private int _seekRequests;

    public PlaybackUiIntegrationTests()
    {
        _projects = new ProjectService(_undoRedo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undoRedo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });
        var status = new StatusService();
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var workflow = new MediaImportWorkflow(new NoPicker(), new NoImport(), _projects, analysis, status, NullLogger<MediaImportWorkflow>.Instance);
        var projectFiles = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new NoPicker(), status, NullLogger<ProjectFileWorkflow>.Instance);

        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undoRedo, projectFiles, workflow, status),
            new MediaBrowserViewModel(_projects, workflow, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, status),
            new TimelineViewModel(_projects, _edit, status, NullLogger<TimelineViewModel>.Instance),
            status,
            projectFiles,
            _projects,
            NullLogger<MainWindowViewModel>.Instance);
        _vm.Timeline.SeekRequested += (_, _) => _seekRequests++;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private PreviewViewModel Preview => _vm.Preview;
    private TimelineViewModel Timeline => _vm.Timeline;
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private MediaAsset Video(string name, int frames)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-ui-tests", Guid.NewGuid().ToString("N"), name),
            Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = F(frames), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36 }
        };
        _decoder.Add(asset.FilePath, new FakeSource(Rate, frames));
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private Clip AddClip(MediaAsset asset, Guid? trackId = null, long? startFrame = null)
    {
        var result = _edit.AddClip(asset.Id, trackId, startFrame is { } s ? F(s) : null);
        Assert.True(result.Success, result.Message);
        return _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).Single(c => c.Id == result.ClipIds[0]);
    }

    /// <summary>Ticks like the view's timer until <paramref name="condition"/> holds.</summary>
    private async Task TickUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Preview.Tick();
            if (condition()) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"{what}; playhead {Timeline.Playhead}, frame {ShownNumber()}, playing {Preview.IsPlaying}");
            await Task.Delay(1);
        }
    }

    /// <summary>Frame number of the topmost picture layer, if it shows a frame.</summary>
    private int? ShownNumber() =>
        TopPicture() is { State: LayerPictureState.Frame, Frame: { } frame } ? FakeVideoDecoder.Number(frame) : null;

    private LayerPicture? TopPicture() => Preview.Layers.LastOrDefault(l => l.Layer is PictureLayer);

    /// <summary>What the renderer draws for the topmost layer's placeholder.</summary>
    private string? TopLabel() => (PreviewDrawPlan.Build(Preview.Canvas, 960, 540, Preview.Layers).Operations.LastOrDefault() as PlaceholderDraw)?.Label;

    private Task Settled(long frame) =>
        TickUntil(() => Timeline.Playhead == F(frame) && Preview.AreLayersCurrent && !Preview.IsBuffering && ShownNumber() == frame,
            $"frame {frame} not shown");

    private void AdvanceTo(long fromFrame, long toFrame) => _clock.Advance(F(toFrame) - F(fromFrame));

    // --- Transport -------------------------------------------------------------------------------

    [Fact]
    public async Task Play_UiTicks_AdvanceThePlayhead_AndShowDecodedFrames()
    {
        AddClip(Video("a.mp4", 250));
        await Settled(0);

        Preview.PlayPauseCommand.Execute(null);
        Assert.True(Preview.IsPlaying);
        Assert.Equal("Pause", Preview.PlayPauseLabel);

        for (var frame = 1; frame <= 25; frame++)
        {
            AdvanceTo(frame - 1, frame);
            await Settled(frame);
        }
        Assert.Equal(TimeFormat(F(25)), Preview.CurrentTimeDisplay);
    }

    [Fact]
    public async Task Pause_StopsThePlayhead_PlayAgainContinues()
    {
        AddClip(Video("a.mp4", 250));
        Preview.PlayPauseCommand.Execute(null);
        AdvanceTo(0, 10);
        await Settled(10);

        Preview.PlayPauseCommand.Execute(null); // pause
        _clock.Advance(3.0);
        for (var i = 0; i < 20; i++) Preview.Tick();
        Assert.False(Preview.IsPlaying);
        Assert.Equal(F(10), Timeline.Playhead);

        Preview.PlayPauseCommand.Execute(null); // play again from the middle
        AdvanceTo(10, 20);
        await Settled(20);
    }

    [Fact]
    public async Task Stop_ReturnsThePlayheadToZero()
    {
        AddClip(Video("a.mp4", 250));
        Preview.PlayPauseCommand.Execute(null);
        AdvanceTo(0, 30);
        await Settled(30);

        Preview.StopCommand.Execute(null);
        await Settled(0);
        Assert.False(Preview.IsPlaying);
        Assert.Equal(PlaybackState.Paused, _playback.State);
    }

    [Fact]
    public async Task ReachingTheEnd_Pauses_PlayAtTheEndRestartsFromZero()
    {
        AddClip(Video("a.mp4", 50));
        Preview.PlayPauseCommand.Execute(null);
        _clock.Advance(3.0);

        await TickUntil(() => !Preview.IsPlaying && Timeline.Playhead == F(50) && ShownNumber() == 49, "end not reached");

        Preview.PlayPauseCommand.Execute(null);
        Assert.True(Preview.IsPlaying);
        await Settled(0);
        AdvanceTo(0, 5);
        await Settled(5);
    }

    // --- Playhead ↔ seek -------------------------------------------------------------------------

    [Fact]
    public async Task PlayheadClickOrDrag_WhilePaused_Seeks()
    {
        AddClip(Video("a.mp4", 250));
        await Settled(0);

        Timeline.SetPlayhead(F(40));           // click
        await Settled(40);
        foreach (var frame in new[] { 41, 45, 60, 52 }) // drag
            Timeline.SetPlayhead(F(frame));
        await Settled(52);

        Assert.Equal(F(52), _playback.Position);
        Assert.Equal(PlaybackState.Paused, _playback.State);
    }

    [Fact]
    public async Task PlayheadClick_WhilePlaying_ContinuesFromTheRequestedPosition()
    {
        AddClip(Video("a.mp4", 250));
        Preview.PlayPauseCommand.Execute(null);
        AdvanceTo(0, 10);
        await Settled(10);

        Timeline.SetPlayhead(F(100));
        _clock.Advance(F(10)); // decoding time passes; the timeline keeps moving from 100
        await Settled(110);
        Assert.True(Preview.IsPlaying);
    }

    [Fact]
    public async Task PlaybackDrivenPlayheadUpdates_NeverTriggerASeek()
    {
        var asset = Video("a.mp4", 250);
        AddClip(asset);
        await Settled(0);
        var opensBefore = _decoder.OpenCount(asset.FilePath);

        Preview.PlayPauseCommand.Execute(null);
        for (var frame = 1; frame <= 50; frame++)
        {
            AdvanceTo(frame - 1, frame);
            await Settled(frame);
        }

        Assert.Equal(0, _seekRequests);
        Assert.Equal(opensBefore, _decoder.OpenCount(asset.FilePath)); // no seek → no new decoder
    }

    // --- Snapshot lifecycle ------------------------------------------------------------------------

    [Fact]
    public async Task TimelineChange_WhilePlaying_Resyncs_AndKeepsPlaying()
    {
        AddClip(Video("a.mp4", 250));
        var clip = _projects.Current.Timeline.VideoTracks[0].Clips[0];
        Preview.PlayPauseCommand.Execute(null);
        AdvanceTo(0, 25);
        await Settled(25);

        Assert.True(_edit.MoveClips(new[] { clip.Id }, 10).Success); // raises TimelineChanged

        await TickUntil(() => ShownNumber() == 15 && Preview.AreLayersCurrent, "moved clip not shown");
        Assert.True(Preview.IsPlaying);
        Assert.Equal(F(25), Timeline.Playhead);
        AdvanceTo(25, 30);
        await TickUntil(() => Timeline.Playhead == F(30) && ShownNumber() == 20, "playback did not continue");
    }

    [Fact]
    public async Task TimelineChange_WhilePaused_RebuildsWithoutStartingPlayback()
    {
        AddClip(Video("a.mp4", 250));
        var clip = _projects.Current.Timeline.VideoTracks[0].Clips[0];
        Timeline.SetPlayhead(F(40));
        await Settled(40);

        Assert.True(_edit.MoveClips(new[] { clip.Id }, 10).Success);
        await TickUntil(() => ShownNumber() == 30, "rebuilt snapshot not shown");

        _clock.Advance(2.0);
        for (var i = 0; i < 20; i++) Preview.Tick();
        Assert.False(Preview.IsPlaying);
        Assert.Equal(F(40), Timeline.Playhead);
        Assert.Equal(30, ShownNumber());
    }

    [Fact]
    public async Task MediaStateChange_AndNewProject_RebuildTheSnapshot()
    {
        var asset = Video("a.mp4", 250);
        AddClip(asset);
        await Settled(0);

        asset.IsMissing = true;
        _projects.NotifyMediaAssetsChanged();
        await TickUntil(() => TopPicture()?.State == LayerPictureState.Offline, "offline not shown");
        Assert.Equal("Media offline", TopLabel());
        Assert.Null(ShownNumber());

        Preview.PlayPauseCommand.Execute(null);
        _projects.CreateNew("Other");
        await TickUntil(() => Preview.Layers.IsEmpty && !Preview.IsPlaying, "new project not applied");
        Assert.Equal(MediaTime.Zero, _playback.Duration);
        Assert.Equal(MediaTime.Zero, Timeline.Playhead);
    }

    [Fact]
    public async Task ChangingAnAssetTheTimelineDoesNotUse_WhilePlaying_DoesNotResync()
    {
        var used = Video("used.mp4", 250);
        var unused = Video("unused.mp4", 250);
        AddClip(used);
        Preview.PlayPauseCommand.Execute(null);
        AdvanceTo(0, 10);
        await Settled(10);
        var opens = _decoder.Requests.Count;

        // Import another file and finish "analysis" of the unused one: media events, no playback impact.
        Video("imported-later.mp4", 100);
        unused.Metadata!.AudioCodec = "aac";
        unused.IsMissing = true;
        _projects.NotifyMediaAssetsChanged();

        for (var frame = 11; frame <= 20; frame++)
        {
            AdvanceTo(frame - 1, frame);
            Preview.Tick();
            Assert.False(Preview.IsBuffering, $"resync at frame {frame}");
            await Settled(frame);
        }
        Assert.Equal(opens, _decoder.Requests.Count); // no decoder reopened
        Assert.True(Preview.IsPlaying);

        // A change to the asset in use still rebuilds.
        used.IsMissing = true;
        _projects.NotifyMediaAssetsChanged();
        await TickUntil(() => TopPicture()?.State == LayerPictureState.Offline, "used asset change not applied");
    }

    [Fact]
    public async Task Preview_ShowsGapAsBlack_TopTrackClip_AndLowerClipAgainAfterIt()
    {
        var low = Video("low.mp4", 100);
        var top = Video("top.mp4", 20);
        var lowClip = AddClip(low);
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        var v2 = _projects.Current.Timeline.VideoTracks.Single(t => t.Id != _projects.Current.Timeline.VideoTracks[0].Id);
        AddClip(top, v2.Id, 30);
        AddClip(Video("tail.mp4", 10), _projects.Current.Timeline.VideoTracks[0].Id, 150); // gap 100–150

        Timeline.SetPlayhead(F(35));
        await TickUntil(() => ShownNumber() == 5, "top clip");
        Timeline.SetPlayhead(F(50));
        await TickUntil(() => ShownNumber() == 50 && Preview.AreLayersCurrent, "lower clip after top clip");
        Timeline.SetPlayhead(F(120));
        await TickUntil(() => Preview.Layers.IsEmpty && Preview.AreLayersCurrent && !Preview.IsBuffering, "gap");
        Assert.Null(ShownNumber());
        Assert.Equal(lowClip.Id, _projects.Current.Timeline.VideoTracks[0].Clips[0].Id);
    }

    private string TimeFormat(MediaTime time) => Common.TimeFormat.ToTimecode(time, Rate);

    // --- Minimal stand-ins for services the shell needs but these tests don't use ------------------

    // ---- window title (Phase 6) ---------------------------------------------------------

    [Fact]
    public async Task Title_shows_the_project_name_and_a_star_while_there_are_unsaved_changes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"), "My Film");
        var changes = new List<string?>();
        _vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        try
        {
            Assert.Equal("Untitled Project — AI Video Editor", _vm.Title);

            _edit.AddTrack(TrackType.Video);
            Assert.Equal("Untitled Project* — AI Video Editor", _vm.Title);
            Assert.Contains(nameof(MainWindowViewModel.Title), changes);

            await _projects.SaveAsAsync(folder);
            Assert.Equal("My Film — AI Video Editor", _vm.Title);

            _edit.AddTrack(TrackType.Video);
            Assert.Equal("My Film* — AI Video Editor", _vm.Title);

            _undoRedo.Undo(); // back to the save point
            Assert.Equal("My Film — AI Video Editor", _vm.Title);

            _projects.CreateNew("Untitled Project");
            Assert.Equal("Untitled Project — AI Video Editor", _vm.Title);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
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

    private sealed class NoPicker : IFilePickerService
    {
        public Task<IReadOnlyList<string>> PickFilesAsync(FilePickerRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFileAsync(SaveFilePickerRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
