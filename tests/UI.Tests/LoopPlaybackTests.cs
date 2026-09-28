using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D024 Step 9.6c (PO-H3): loop on the real shell and the real playback service (fake decoder, manual clock; ticks driven
/// like the view's timer) — while on, reaching the end continues from the start and keeps playing (again and again);
/// off, the D011 end rule is unchanged; a pause before the end is not undone; Ctrl+L and the command toggle it; it is
/// session state only (no edit, not dirty, not undoable) and survives another project.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class LoopPlaybackTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undoRedo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _playback;
    private readonly MainWindowViewModel _vm;

    public LoopPlaybackTests()
    {
        _projects = new ProjectService(_undoRedo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undoRedo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });
        var status = new StatusService();
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var picker = new ScriptedPicker();
        var workflow = new MediaImportWorkflow(picker, new NoImport(), _projects, analysis, status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(1 /* Don't Save */), picker,
            status, NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undoRedo, files, workflow, status),
            new MediaBrowserViewModel(_projects, workflow, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, status),
            new TimelineViewModel(_projects, _edit, status, NullLogger<TimelineViewModel>.Instance),
            status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private PreviewViewModel Preview => _vm.Preview;
    private TimelineViewModel Timeline => _vm.Timeline;
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private void Clip(int frames)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-ui-tests", Guid.NewGuid().ToString("N"), "a.mp4"),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = F(frames), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36 }
        };
        _decoder.Add(asset.FilePath, new FakeSource(Rate, frames));
        _projects.AddMediaAssets(new[] { asset });
        Assert.True(_edit.AddClip(asset.Id, null, null).Success);
    }

    private async Task TickUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Preview.Tick();
            if (condition()) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"{what}; playhead {Timeline.Playhead}, playing {Preview.IsPlaying}");
            await Task.Delay(1);
        }
    }

    private int? Shown() => Preview.Layers.LastOrDefault(l => l.Layer is PictureLayer) is { State: LayerPictureState.Frame, Frame: { } f }
        ? FakeVideoDecoder.Number(f) : null;

    private Task Settled(long frame) =>
        TickUntil(() => Timeline.Playhead == F(frame) && Preview.AreLayersCurrent && !Preview.IsBuffering && Shown() == frame,
            $"frame {frame} not shown");

    /// <summary>Plays from the start and lets the clock run past the end of a <paramref name="frames"/>-frame sequence.</summary>
    private void PlayPastTheEnd(int frames)
    {
        Preview.PlayCommand.Execute(null);
        _clock.Advance(F(frames + 3) - MediaTime.Zero);
    }

    [Fact]
    public async Task With_loop_off_the_end_pauses_there_as_before()
    {
        Clip(50);
        Assert.False(Preview.IsLooping);                                     // off by default

        PlayPastTheEnd(50);

        await TickUntil(() => !Preview.IsPlaying && Timeline.Playhead == F(50), "end not reached");
        for (var i = 0; i < 10; i++) Preview.Tick();
        Assert.False(Preview.IsPlaying);
        Assert.Equal(F(50), Timeline.Playhead);
    }

    [Fact]
    public async Task With_loop_on_the_end_continues_from_the_start_and_keeps_playing_every_time()
    {
        Clip(50);
        Preview.ToggleLoopCommand.Execute(null);

        PlayPastTheEnd(50);
        await TickUntil(() => Preview.IsPlaying && Timeline.Playhead < F(10), "did not continue from the start");
        Assert.True(Preview.IsPlaying);

        _clock.Advance(F(20) - MediaTime.Zero);                             // plays on from 0
        await TickUntil(() => Timeline.Playhead >= F(20) && Timeline.Playhead < F(40), "did not play on after the loop");

        _clock.Advance(F(40) - MediaTime.Zero);                             // and loops again
        await TickUntil(() => Preview.IsPlaying && Timeline.Playhead < F(30), "did not loop a second time");
    }

    [Fact]
    public async Task A_pause_before_the_end_is_not_undone_by_the_loop()
    {
        Clip(50);
        Preview.ToggleLoopCommand.Execute(null);
        Preview.PlayCommand.Execute(null);
        _clock.Advance(F(45) - MediaTime.Zero);
        await Settled(45);

        Preview.PauseCommand.Execute(null);
        _clock.Advance(2.0);
        for (var i = 0; i < 20; i++) Preview.Tick();

        Assert.False(Preview.IsPlaying);
        Assert.Equal(F(45), Timeline.Playhead);
    }

    [Fact]
    public async Task Turning_the_loop_on_while_paused_at_the_end_starts_nothing()
    {
        Clip(50);
        PlayPastTheEnd(50);
        await TickUntil(() => !Preview.IsPlaying && Timeline.Playhead == F(50), "end not reached");

        Preview.ToggleLoopCommand.Execute(null);
        _clock.Advance(1.0);
        for (var i = 0; i < 20; i++) Preview.Tick();

        Assert.False(Preview.IsPlaying);
        Assert.Equal(F(50), Timeline.Playhead);
    }

    [Fact]
    public void Ctrl_L_toggles_the_loop()
    {
        Assert.True(ShortcutRouter.Handle(_vm, Key.L, KeyModifiers.Control, new Button(), null));
        Assert.True(Preview.IsLooping);
        Assert.True(ShortcutRouter.Handle(_vm, Key.L, KeyModifiers.Control, new Button(), null));
        Assert.False(Preview.IsLooping);
    }

    [Fact]
    public async Task The_loop_is_session_state_no_edit_and_it_survives_another_project()
    {
        Clip(50);
        var folder = Path.Combine(Path.GetTempPath(), "aive-ui-tests", Guid.NewGuid().ToString("N"));
        await _projects.SaveAsAsync(folder);
        var undoDepth = _undoRedo.CurrentPosition;

        Preview.ToggleLoopCommand.Execute(null);

        Assert.False(_projects.Current.IsDirty);                             // not an edit
        Assert.Equal(undoDepth, _undoRedo.CurrentPosition);                  // nothing added to undo
        await _projects.SaveAsAsync(folder);
        Assert.DoesNotContain("loop", File.ReadAllText(Path.Combine(folder, "project.json")), StringComparison.OrdinalIgnoreCase);

        _projects.CreateNew("Other");
        Assert.True(Preview.IsLooping);                                      // the session's, not the project's
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
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
