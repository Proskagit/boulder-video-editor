using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
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

/// <summary>FU-21.1 (D040 §2): Previous / Next marker with the real shell and the real playback service (fake decoder,
/// manual clock; ticks driven like the view's timer).</summary>
public sealed class MarkerNavigationTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly MainWindowViewModel _vm;

    public MarkerNavigationTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private TimelineViewModel Timeline => _vm.Timeline;
    private PreviewViewModel Preview => _vm.Preview;
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private void Video(int frames)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-ui-tests", Guid.NewGuid().ToString("N"), "v.mp4"),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = F(frames), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36 }
        };
        _decoder.Add(asset.FilePath, new FakeSource(Rate, frames));
        _projects.AddMediaAssets(new[] { asset });
        Assert.True(_edit.AddClip(asset.Id).Success);
    }

    private bool Settled => !Preview.IsBuffering && Preview.AreLayersCurrent;

    /// <summary>Runs the view's timer until the Preview has settled (a few ticks after the last seek).</summary>
    private async Task Settle()
    {
        var watch = Stopwatch.StartNew();
        var settledTicks = 0;
        while (settledTicks < 5)
        {
            Preview.Tick();
            settledTicks = Settled ? settledTicks + 1 : 0;
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"not settled: playhead {Timeline.Playhead}, playback {_playback.Position}");
            await Task.Delay(1);
        }
    }

    private async Task Next() { Timeline.NextMarkerCommand.Execute(null); await Settle(); }
    private async Task Previous() { Timeline.PreviousMarkerCommand.Execute(null); await Settle(); }

    private async Task PutPlayhead(long frame) { Timeline.SetPlayhead(F(frame)); await Settle(); }

    [Fact]
    public async Task Next_reaches_a_marker_past_the_sequence_end_at_the_first_press()
    {
        Video(100);                                                  // the sequence 0–100
        Assert.True(_edit.AddMarker(F(40)).Success);
        Assert.True(_edit.AddMarker(F(150)).Success);               // past the end
        await Settle();

        await Next();
        Assert.Equal(F(40), Timeline.Playhead);
        await Next();
        Assert.Equal(F(150), Timeline.Playhead);                    // not pulled back to the end by playback (FU-21.1)
        Assert.Equal(F(100), _playback.Position);                   // playback clamped to the end
        Assert.Equal("At the marker 00:00:06:00", _status.Message);
        await Previous();
        Assert.Equal(F(40), Timeline.Playhead);
        Assert.Equal("At the marker 00:00:01:15", _status.Message);
    }

    [Fact]
    public async Task Previous_from_a_playhead_parked_past_the_end_reaches_each_marker_in_turn()
    {
        Video(100);
        Assert.True(_edit.AddMarker(F(40)).Success);
        Assert.True(_edit.AddMarker(F(150)).Success);
        Assert.True(_edit.AddMarker(F(175)).Success);
        await PutPlayhead(250);                                      // a click on the ruler past the end, the first one
        Assert.Equal(F(250), Timeline.Playhead);

        await Previous();
        Assert.Equal(F(175), Timeline.Playhead);
        await Previous();
        Assert.Equal(F(150), Timeline.Playhead);                    // never a false "no marker before the playhead"
        await Previous();
        Assert.Equal(F(40), Timeline.Playhead);
        await Previous();
        Assert.Equal(F(40), Timeline.Playhead);
        Assert.Equal("There is no marker before the playhead.", _status.Message);
    }

    [Theory]
    [InlineData(49, 50, 50)]     // one frame before the marker: Next reaches it, Previous finds none
    [InlineData(51, 50, 50)]     // one frame after it: Previous reaches it, Next finds none
    public async Task Markers_are_found_on_the_playhead_frame(long playhead, long expectedNext, long expectedPrevious)
    {
        Video(100);
        Assert.True(_edit.AddMarker(F(50)).Success);
        await PutPlayhead(playhead);

        await Next();
        Assert.Equal(F(playhead < 50 ? expectedNext : playhead), Timeline.Playhead);
        await PutPlayhead(playhead);
        await Previous();
        Assert.Equal(F(playhead > 50 ? expectedPrevious : playhead), Timeline.Playhead);
    }

    [Fact]
    public async Task On_the_marker_itself_neither_direction_finds_it_and_the_refusal_is_reported()
    {
        Video(100);
        Assert.True(_edit.AddMarker(F(50)).Success);
        await PutPlayhead(50);

        await Next();
        Assert.Equal(F(50), Timeline.Playhead);
        Assert.Equal("There is no marker after the playhead.", _status.Message);
        await Previous();
        Assert.Equal(F(50), Timeline.Playhead);
        Assert.Equal("There is no marker before the playhead.", _status.Message);
    }

    [Fact]
    public async Task A_jump_replaces_an_earlier_refusal_in_the_status_bar()
    {
        Video(100);
        Assert.True(_edit.AddMarker(F(50)).Success);
        await Settle();

        await Previous();
        Assert.Equal("There is no marker before the playhead.", _status.Message);
        await Next();
        Assert.Equal(F(50), Timeline.Playhead);
        Assert.Equal("At the marker 00:00:02:00", _status.Message);
    }

    [Fact]
    public async Task The_jump_is_reported_in_Russian()
    {
        Video(100);
        Assert.True(_edit.AddMarker(F(50)).Success);
        await Settle();

        using (UiLanguage.Use("ru"))
        {
            await Next();
            Assert.Equal("Переход к маркеру 00:00:02:00", _status.Message);
            await Next();
            Assert.Equal("После указателя нет маркеров.", _status.Message);
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
