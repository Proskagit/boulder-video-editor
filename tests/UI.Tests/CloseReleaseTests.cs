using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
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
/// Closing the main window (D024 Step 9.3): once closing is agreed, playback — decoders, their processes, the
/// audio device — is released by the shell on the UI thread, before the window closes; a cancelled close keeps
/// it. Real shell, project, timeline and playback services with fake decoders.
/// </summary>
public sealed class CloseReleaseTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undoRedo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly FakeVideoDecoder _decoder = new();
    private readonly PlaybackService _playback;
    private readonly ScriptedDialogs _dialogs = new(2 /* Cancel */, 1 /* Don't Save */);
    private readonly MainWindowViewModel _vm;

    public CloseReleaseTests()
    {
        _projects = new ProjectService(_undoRedo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undoRedo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(_decoder, new FakeReferenceClock(), NullLogger<PlaybackService>.Instance);
        var status = new StatusService();
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var picker = new ScriptedPicker();
        var workflow = new MediaImportWorkflow(picker, new NoImport(), _projects, analysis, status, NullLogger<MediaImportWorkflow>.Instance);
        var projectFiles = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), _dialogs, picker, status, NullLogger<ProjectFileWorkflow>.Instance);

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
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private MediaAsset AddVideoClip()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-ui-tests", Guid.NewGuid().ToString("N"), "a.mp4"),
            Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromFrame(250, Rate), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36 }
        };
        _decoder.Add(asset.FilePath, new FakeSource(Rate, 250));
        _projects.AddMediaAssets(new[] { asset });
        Assert.True(_edit.AddClip(asset.Id).Success);
        return asset;
    }

    private async Task TickUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            _vm.Preview.Tick();
            if (condition()) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task A_cancelled_close_keeps_playback_an_agreed_one_releases_it_before_the_window_closes()
    {
        var asset = AddVideoClip(); // the project is dirty: closing asks Save / Don't Save / Cancel
        await TickUntil(() => _vm.Preview.Layers.Length == 1 && _vm.Preview.AreLayersCurrent, "clip not shown");
        Assert.True(_decoder.LiveStreams >= 1);

        Assert.False(await _vm.PrepareToCloseAsync());                 // Cancel
        Assert.True(_decoder.LiveStreams >= 1);
        _vm.Timeline.SetPlayhead(MediaTime.FromFrame(40, Rate));        // playback still works
        await TickUntil(() => _vm.Preview.AreLayersCurrent && !_vm.Preview.IsBuffering &&
                              _vm.Preview.Layers[0].Frame is { } f && FakeVideoDecoder.Number(f) == 40, "seek after a cancelled close");

        Assert.True(await _vm.PrepareToCloseAsync());                  // Don't Save
        Assert.Equal(0, _decoder.LiveStreams);                         // released before the window closes
        var layers = _vm.Preview.Layers;
        var opens = _decoder.OpenCount(asset.FilePath);

        // Late events while the window closes: a timeline change, a playhead move, a tick of the view's timer.
        _edit.MoveClips(new[] { _projects.Current.Timeline.VideoTracks[0].Clips[0].Id }, 5);
        _vm.Timeline.SetPlayhead(MediaTime.FromFrame(80, Rate));
        _vm.Preview.Tick();
        await Task.Delay(50);

        Assert.Equal(opens, _decoder.OpenCount(asset.FilePath));
        Assert.Equal(0, _decoder.LiveStreams);
        Assert.Equal(layers, _vm.Preview.Layers);                      // the last picture stays until the window is gone
        Assert.Equal(2, _dialogs.Asked.Count);
    }

    [Fact]
    public async Task Closing_a_clean_project_releases_playback_without_asking()
    {
        AddVideoClip();
        var folder = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"), "Saved");
        try
        {
            await _projects.SaveAsAsync(folder); // nothing to ask
            await TickUntil(() => _vm.Preview.Layers.Length == 1 && _vm.Preview.AreLayersCurrent, "clip not shown");

            Assert.True(await _vm.PrepareToCloseAsync());
            Assert.Empty(_dialogs.Asked);
            Assert.Equal(0, _decoder.LiveStreams);
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
}
