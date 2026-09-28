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
/// D024 Step 9.6b: J / K / L through the main window's routing on the real shell and the real playback service (fake
/// decoder, manual clock; ticks driven like the view's timer) — L plays and never pauses, K pauses and never plays,
/// J steps back one second keeping the playback state (PO-H1 / PO-H2); L at the end starts from 0 (D011); \ fits the
/// sequence (PO-H4).
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class PlaybackShortcutTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undoRedo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _playback;
    private readonly MainWindowViewModel _vm;

    public PlaybackShortcutTests()
    {
        _projects = new ProjectService(_undoRedo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undoRedo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });
        var status = new StatusService();
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var picker = new ScriptedPicker();
        var workflow = new MediaImportWorkflow(picker, new NoImport(), _projects, analysis, status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), picker, status,
            NullLogger<ProjectFileWorkflow>.Instance);
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

    private void Press(Key key) => Assert.True(ShortcutRouter.Handle(_vm, key, KeyModifiers.None, new Button(), null));

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

    private void Advance(long frames) => _clock.Advance(F(frames) - MediaTime.Zero);

    [Fact]
    public async Task L_plays_and_pressed_again_keeps_playing()
    {
        Clip(250);
        await Settled(0);

        Press(Key.L);
        Assert.True(Preview.IsPlaying);
        Press(Key.L);                                                    // not a toggle
        Assert.True(Preview.IsPlaying);

        Advance(10);
        await Settled(10);
    }

    [Fact]
    public async Task K_pauses_and_pressed_again_stays_paused()
    {
        Clip(250);
        Press(Key.L);
        Advance(10);
        await Settled(10);

        Press(Key.K);
        Assert.False(Preview.IsPlaying);
        Press(Key.K);                                                    // not a toggle
        Assert.False(Preview.IsPlaying);

        _clock.Advance(2.0);
        for (var i = 0; i < 20; i++) Preview.Tick();
        Assert.Equal(F(10), Timeline.Playhead);
    }

    [Fact]
    public async Task J_while_playing_steps_back_one_second_and_keeps_playing()
    {
        Clip(250);
        Press(Key.L);
        Advance(60);
        await Settled(60);

        Press(Key.J);

        await Settled(35);                                               // 60 − 25 frames (1 s at 25 fps)
        Assert.True(Preview.IsPlaying);
        Advance(5);
        await Settled(40);                                               // and plays on from there
    }

    [Fact]
    public async Task J_while_paused_steps_back_one_second_and_stays_paused_and_stops_at_zero()
    {
        Clip(250);
        Timeline.SetPlayhead(F(40));
        await Settled(40);

        Press(Key.J);
        await Settled(15);
        Assert.False(Preview.IsPlaying);

        Press(Key.J);                                                    // less than a second left
        await Settled(0);
        Assert.False(Preview.IsPlaying);
    }

    [Fact]
    public async Task L_at_the_end_starts_again_from_zero()
    {
        Clip(50);
        Press(Key.L);
        _clock.Advance(3.0);
        await TickUntil(() => !Preview.IsPlaying && Timeline.Playhead == F(50), "end not reached");

        Press(Key.L);

        Assert.True(Preview.IsPlaying);
        await Settled(0);
    }

    [Fact]
    public void Backslash_fits_the_sequence_into_the_view()
    {
        Clip(250);
        Timeline.SetViewport(0, 500);
        Timeline.ZoomToFitCommand.Execute(null);
        var fit = Timeline.PixelsPerSecond;
        Timeline.ZoomInCommand.Execute(null);
        Timeline.ZoomInCommand.Execute(null);
        Assert.NotEqual(fit, Timeline.PixelsPerSecond);

        Press(Key.OemPipe);
        Assert.Equal(fit, Timeline.PixelsPerSecond);

        Timeline.ZoomInCommand.Execute(null);
        Press(Key.OemBackslash);
        Assert.Equal(fit, Timeline.PixelsPerSecond);
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
