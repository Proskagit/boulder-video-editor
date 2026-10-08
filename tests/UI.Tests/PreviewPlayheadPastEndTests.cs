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
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// The playhead after an edit that changes the sequence's duration (the Phase 15 Step 15.8 observation): kept inside
/// [0, Duration] — at the old end included — and never moved to the new end by an extension; clamped to the new
/// Duration past it. The playhead parked past the end (playback clamps a seek to the end and does not report a frame it
/// already reported: always in an empty project, in a non-empty one from the second seek past the end): a clip added
/// there shows in the Preview at once, without another seek. Edits inside the sequence keep the D010 / D011 behaviour.
/// Real shell and real playback service (fake decoder, manual clock; ticks driven like the view's timer).
/// </summary>
public sealed class PreviewPlayheadPastEndTests : IAsyncLifetime
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

    public PreviewPlayheadPastEndTests()
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
    private InspectorViewModel Inspector => _vm.Inspector;
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private MediaAsset Video(int frames)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-ui-tests", Guid.NewGuid().ToString("N"), "v.mp4"),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = F(frames), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36 }
        };
        _decoder.Add(asset.FilePath, new FakeSource(Rate, frames));
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private async Task TickUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Preview.Tick();
            if (condition()) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"{what}: playhead {Timeline.Playhead}, playback {_playback.Position}, layers "
                    + $"[{string.Join(", ", Preview.Layers.Select(l => l.Layer.GetType().Name + " " + l.State))}], buffering {Preview.IsBuffering}");
            await Task.Delay(1);
        }
    }

    private bool Settled => !Preview.IsBuffering && Preview.AreLayersCurrent;

    private bool ShowsText => Settled && Preview.Layers.Any(l => l.Layer is TextLayer);

    private bool ShowsFrame => Settled && Preview.Layers.Any(l => l.Layer is PictureLayer && l.State == LayerPictureState.Frame);

    private long ShownFrame => FakeVideoDecoder.Number(Preview.Layers.Single(l => l.Layer is PictureLayer).Frame!);

    /// <summary>Shift+Right <paramref name="seconds"/> times, then the preview settles.</summary>
    private async Task StepSeconds(int seconds)
    {
        await TickUntil(() => Settled, "start not settled");        // the view's timer has run since the window opened
        for (var i = 0; i < seconds; i++)
        {
            Timeline.StepForwardSecondCommand.Execute(null);
            Preview.Tick();
        }
        await TickUntil(() => Settled, "seek not settled");
    }

    [Fact]
    public async Task Text_added_past_the_end_of_an_empty_project_shows_at_once()
    {
        await StepSeconds(6);
        Assert.Equal(F(150), Timeline.Playhead);                   // the playhead stays where the user put it
        Assert.Equal(MediaTime.Zero, _playback.Position);           // playback is clamped to the empty sequence

        Timeline.AddTextCommand.Execute(null);                       // a 5 s text at 6 s

        await TickUntil(() => ShowsText, "the new text is not shown");
        Assert.Equal(F(150), Timeline.Playhead);
        Assert.Equal(F(150), _playback.Position);
        Assert.False(Preview.IsPlaying);
    }

    [Fact]
    public async Task A_video_clip_added_past_the_end_of_an_empty_project_shows_at_once()
    {
        var asset = Video(100);
        await StepSeconds(6);

        Assert.True(_edit.AddClip(asset.Id, null, F(150)).Success);

        await TickUntil(() => Settled && Preview.Layers.Any(l => l.Layer is PictureLayer && l.State == LayerPictureState.Frame),
            "the new clip is not shown");
        var shown = Preview.Layers.Single(l => l.Layer is PictureLayer);
        Assert.Equal(0, FakeVideoDecoder.Number(shown.Frame!));     // the clip's first frame, under the playhead
        Assert.Equal(F(150), Timeline.Playhead);
    }

    [Fact]
    public async Task A_clip_that_ends_before_the_parked_playhead_clamps_the_playhead_to_the_new_end()
    {
        var asset = Video(75);
        await StepSeconds(6);

        Assert.True(_edit.AddClip(asset.Id, null, MediaTime.Zero).Success); // frames 0–75, the playhead at 150

        // The sequence grew but still ends before the playhead: playback at the new end, the playhead clamped there.
        await TickUntil(() => Settled && Timeline.Playhead == F(75), "the playhead was not clamped to the new end");
        Assert.Equal(F(75), _playback.Position);
        var shown = Preview.Layers.Single(l => l.Layer is PictureLayer);
        Assert.Equal(74, FakeVideoDecoder.Number(shown.Frame!));    // the last frame (D011)
    }

    [Fact]
    public async Task Text_that_extends_the_sequence_from_inside_it_keeps_the_playhead()
    {
        Assert.True(_edit.AddClip(Video(100).Id).Success);
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        await StepSeconds(2);

        Timeline.AddTextCommand.Execute(null);                       // 50–175 on V2: the sequence 100 → 175

        await TickUntil(() => ShowsText, "the new text is not shown");
        Assert.Equal(F(175), Timeline.SequenceDuration);
        Assert.Equal(F(50), Timeline.Playhead);
        Assert.Equal(F(50), _playback.Position);
    }

    [Fact]
    public async Task A_playhead_at_the_old_end_stays_there_when_the_sequence_grows()
    {
        Assert.True(_edit.AddClip(Video(75).Id).Success);
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        await TickUntil(() => Settled, "start not settled");
        Timeline.GoToEndCommand.Execute(null);
        await TickUntil(() => Settled && _playback.Position == F(75), "not at the end");

        Timeline.AddTextCommand.Execute(null);                       // 75–200 on V2: the sequence 75 → 200

        await TickUntil(() => ShowsText, "the new text is not shown");
        Assert.Equal(F(200), Timeline.SequenceDuration);
        Assert.Equal(F(75), Timeline.Playhead);                    // not moved to the new end
        Assert.Equal(F(75), _playback.Position);
    }

    [Fact]
    public async Task Text_added_at_a_playhead_parked_past_the_end_of_a_clip_shows_at_once()
    {
        Assert.True(_edit.AddClip(Video(75).Id).Success);
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        await TickUntil(() => Settled, "start not settled");
        Timeline.GoToEndCommand.Execute(null);
        await StepSeconds(2);                                        // 75 → 125; the seek clamped to 75, not reported
        Assert.Equal(F(125), Timeline.Playhead);
        Assert.Equal(F(75), _playback.Position);

        Timeline.AddTextCommand.Execute(null);                       // 125–250 on V2

        await TickUntil(() => ShowsText && _playback.Position == F(125), "the new text is not shown at the playhead");
        Assert.Equal(F(125), Timeline.Playhead);
    }

    [Fact]
    public async Task A_clip_that_ends_before_a_playhead_parked_past_a_clip_clamps_the_playhead_to_the_new_end()
    {
        Assert.True(_edit.AddClip(Video(75).Id).Success);
        var second = Video(25);
        await TickUntil(() => Settled, "start not settled");
        Timeline.GoToEndCommand.Execute(null);
        await StepSeconds(3);                                        // 75 → 150, playback at 75
        Assert.Equal(F(150), Timeline.Playhead);

        Assert.True(_edit.AddClip(second.Id, null, F(75)).Success);  // 75–100: the sequence 75 → 100 < 150

        await TickUntil(() => ShowsFrame && Timeline.Playhead == F(100), "the playhead was not clamped to the new end");
        Assert.Equal(F(100), _playback.Position);
        Assert.Equal(24, ShownFrame);                                // the new clip's last frame (D011)
    }

    [Fact]
    public async Task Shortening_the_sequence_below_the_playhead_clamps_it_to_the_new_end()
    {
        var clip = _edit.AddClip(Video(250).Id).ClipIds.Single();
        await StepSeconds(8);                                        // 200

        Assert.True(_edit.TrimClip(clip, ClipEdge.End, F(100)).Success);

        await TickUntil(() => ShowsFrame && Timeline.Playhead == F(100), "the playhead was not clamped to the new end");
        Assert.Equal(F(100), Timeline.SequenceDuration);
        Assert.Equal(F(100), _playback.Position);
        Assert.Equal(99, ShownFrame);
    }

    [Fact]
    public async Task Shortening_the_sequence_exactly_to_the_playhead_keeps_it()
    {
        var clip = _edit.AddClip(Video(250).Id).ClipIds.Single();
        await StepSeconds(4);                                        // 100

        Assert.True(_edit.TrimClip(clip, ClipEdge.End, F(100)).Success);

        await TickUntil(() => ShowsFrame && ShownFrame == 99, "the last frame is not shown");
        Assert.Equal(F(100), Timeline.SequenceDuration);
        Assert.Equal(F(100), Timeline.Playhead);
        Assert.Equal(F(100), _playback.Position);
    }

    [Fact]
    public async Task An_edit_that_keeps_the_duration_leaves_a_parked_playhead_parked()
    {
        await StepSeconds(6);

        Assert.True(_edit.AddTrack(TrackType.Video).Success);        // the empty sequence stays empty

        await TickUntil(() => Settled, "track added");
        Assert.Equal(F(150), Timeline.Playhead);
        Assert.Equal(MediaTime.Zero, _playback.Position);
    }

    [Fact]
    public async Task Edits_while_paused_inside_the_sequence_do_not_seek()
    {
        Assert.True(_edit.AddClip(Video(250).Id).Success);
        Assert.True(_edit.AddTrack(TrackType.Video).Success);
        Timeline.AddTextCommand.Execute(null);                       // a text at 0 on V2
        Preview.PlayPauseCommand.Execute(null);
        _clock.Advance(F(40) - MediaTime.Zero + new MediaTime(1234)); // not on a frame boundary
        await TickUntil(() => Settled && Timeline.Playhead == F(40), "not playing");
        Preview.PlayPauseCommand.Execute(null);
        await TickUntil(() => Settled && !Preview.IsPlaying, "not paused");
        var paused = _playback.Position;
        Assert.NotEqual(Timeline.Playhead, paused);
        var pipeline = _playback.VideoPipelineInstance;
        var generation = _playback.SeekGeneration;

        Inspector.TextContent = "Changed";                          // presentation only: no resync (D010)
        await TickUntil(() => Settled, "text edit");
        Assert.Same(pipeline, _playback.VideoPipelineInstance);
        Assert.Equal(generation, _playback.SeekGeneration);

        Assert.True(_edit.AddTrack(TrackType.Video).Success);       // a resync, where playback is
        await TickUntil(() => Settled, "track added");
        Assert.Equal(paused, _playback.Position);
        Assert.Equal(F(40), Timeline.Playhead);
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
