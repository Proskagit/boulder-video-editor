using AiVideoEditor.Core.Common;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 7 Step 9 (D022) through the real shell wiring: the Inspector Speed field (video and audio
/// only, multiples of 0.05× from 0.25× to 4×, live, merged undo, rejection → status + model value) and
/// the timeline following the new duration.
/// </summary>
public sealed class SpeedInspectorTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly MainWindowViewModel _vm;

    public SpeedInspectorTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });
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

    private InspectorViewModel Inspector => _vm.Inspector;
    private TimelineViewModel Timeline => _vm.Timeline;
    private Sequence Sequence => _projects.Current.Timeline;
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private MediaAsset Asset(MediaKind kind, double seconds = 6)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-speed-ui", Guid.NewGuid().ToString("N"), kind == MediaKind.Image ? "i.png" : "m.mp4"),
            Kind = kind,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(seconds), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080,
                DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080, AudioCodec = kind == MediaKind.Image ? null : "aac"
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private Clip Add(MediaAsset asset, MediaTime? at = null)
    {
        var result = _edit.AddClip(asset.Id, null, at);
        Assert.True(result.Success, result.Message);
        var clip = Sequence.VideoTracks.Concat(Sequence.AudioTracks).SelectMany(t => t.Clips).Single(c => c.Id == result.ClipIds[0]);
        Timeline.OnClipPressed(ViewOf(clip), toggle: false);
        return clip;
    }

    private TimelineClipViewModel ViewOf(Clip clip) => Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);

    [Fact]
    public void The_speed_field_is_for_video_and_audio_only()
    {
        Add(Asset(MediaKind.Video));
        Assert.True(Inspector.HasSpeed);
        Assert.Equal(1.00m, Inspector.SpeedValue);
        Assert.Equal((0.25m, 4m, 0.05m), (Inspector.MinSpeed, Inspector.MaxSpeed, Inspector.SpeedIncrement));

        Add(Asset(MediaKind.Audio));
        Assert.True(Inspector.HasSpeed);

        Add(Asset(MediaKind.Image), F(1000));
        Assert.False(Inspector.HasSpeed);

        Assert.True(_edit.AddTextClip(F(2000)).Success);
        Timeline.OnClipPressed(ViewOf(Sequence.VideoTracks[0].Clips.OfType<TextClip>().Single()), toggle: false);
        Assert.False(Inspector.HasSpeed);
    }

    [Fact]
    public void A_speed_changes_the_duration_on_the_timeline_and_in_the_inspector_but_not_the_source()
    {
        var clip = (VideoClip)Add(Asset(MediaKind.Video));        // 150 frames, source 0–6 s
        var width = ViewOf(clip).Width;

        Inspector.SpeedValue = 2m;

        Assert.Equal(ClipSpeed.FromSteps(40), clip.Speed);
        Assert.Equal(F(75), clip.TimelineEnd);
        Assert.Equal((MediaTime.Zero, MediaTime.FromSeconds(6)), (clip.SourceIn, clip.SourceOut));
        Assert.Equal(width / 2, ViewOf(clip).Width, 6);
        Assert.Equal(TimeFormat.ToTimecode(F(75), Rate), Inspector.DurationDisplay);
        Assert.Equal(2m, Inspector.SpeedValue);
    }

    [Fact]
    public void Spinning_merges_into_one_undo_step_and_undo_redo_refresh_the_field_without_edits()
    {
        var clip = (VideoClip)Add(Asset(MediaKind.Video));
        for (var v = 1.05m; v <= 1.5m; v += 0.05m)
            Inspector.SpeedValue = v;
        Assert.Equal(ClipSpeed.FromSteps(30), clip.Speed);

        _undo.Undo();
        Assert.Equal(ClipSpeed.Normal, clip.Speed);
        Assert.Equal(1m, Inspector.SpeedValue);
        Assert.Equal(F(150), clip.TimelineEnd);

        _undo.Redo();
        Assert.Equal(1.5m, Inspector.SpeedValue);
        Assert.Equal(F(100), clip.TimelineEnd);
        Assert.False(_undo.CanRedo);
    }

    [Theory]
    [InlineData(1.33)]
    [InlineData(0.2)]
    [InlineData(5)]
    public void Values_off_the_grid_or_out_of_range_are_reported_and_the_model_is_shown(double value)
    {
        var clip = (VideoClip)Add(Asset(MediaKind.Video));
        Inspector.SpeedValue = 1.5m;
        var depth = _undo.CurrentPosition;

        Inspector.SpeedValue = (decimal)value;

        Assert.Equal(ClipSpeed.FromSteps(30), clip.Speed);
        Assert.Equal(1.5m, Inspector.SpeedValue);
        Assert.Contains("multiple of 0.05", _status.Message);
        Assert.Same(depth, _undo.CurrentPosition);
    }

    [Fact]
    public void A_rejected_speed_change_reports_why_and_an_empty_field_changes_nothing()
    {
        var first = (VideoClip)Add(Asset(MediaKind.Video));
        Add(Asset(MediaKind.Video), F(150));                     // right after the first clip
        Timeline.OnClipPressed(ViewOf(first), toggle: false);

        Inspector.SpeedValue = 0.5m;                             // would need 300 frames

        Assert.Equal(ClipSpeed.Normal, first.Speed);
        Assert.Equal(1m, Inspector.SpeedValue);
        Assert.Contains("overlap", _status.Message);

        Inspector.SpeedValue = null;                             // emptied field
        Assert.Equal(ClipSpeed.Normal, first.Speed);
        Inspector.ShowModelValues();                             // blur
        Assert.Equal(1m, Inspector.SpeedValue);
    }

    [Fact]
    public void Showing_a_clip_at_another_speed_is_not_an_edit()
    {
        var clip = (VideoClip)Add(Asset(MediaKind.Video));
        Inspector.SpeedValue = 1.35m;
        var position = _undo.CurrentPosition;

        Assert.True(_edit.MoveClips(new[] { clip.Id }, 10).Success);           // refresh → SyncFromModel
        _undo.Undo();
        _undo.Redo();

        Assert.Equal(1.35m, Inspector.SpeedValue);
        Assert.Equal(ClipSpeed.FromSteps(27), clip.Speed);
        Assert.NotSame(position, _undo.CurrentPosition);                        // only the move was added
        _undo.Undo();
        Assert.Same(position, _undo.CurrentPosition);
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
