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
/// Phase 10 Step 10.5 (D025 §2, PO-8): the Inspector's Fade In / Fade Out fields through the real shell wiring — whole
/// frames, the time shown, merged undo steps, rejections showing the model again, the editing lock during an export, a
/// fade on an edge with a dissolve shown as inactive — and the ramps drawn on the timeline clip.
/// </summary>
public sealed class InspectorFadeTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly MainWindowViewModel _vm;

    public InspectorFadeTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance);
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status, editingLock: _lock),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private InspectorViewModel Inspector => _vm.Inspector;
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);
    private Track V1 => _projects.Current.Timeline.VideoTracks[0];

    private VideoClip AddVideo(long frames = 100)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-inspector-fade", Guid.NewGuid().ToString("N"), "v.mp4"),
            Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = F(frames), FrameRate = Rate, Width = 1920, Height = 1080 }
        };
        _projects.AddMediaAssets(new[] { asset });
        var result = _edit.AddClip(asset.Id);
        Assert.True(result.Success, result.Message);
        return (VideoClip)V1.Clips.Single(c => c.Id == result.ClipIds[0]);
    }

    private TimelineClipViewModel ClipVm(Clip clip) => _vm.Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);

    private void Select(Clip clip) => _vm.Timeline.OnClipPressed(ClipVm(clip), toggle: false);

    private string? Top => (_undo.CurrentPosition as IUndoableCommand)?.Description;

    [Fact]
    public void Every_clip_kind_shows_its_fades_in_frames_with_the_time()
    {
        var clip = AddVideo();
        Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange { Fade = new FadeProperties(F(25), F(12)) }).Success);
        Select(clip);

        Assert.True(Inspector.HasFades);
        Assert.Equal((25m, 12m), (Inspector.FadeInFrames, Inspector.FadeOutFrames));
        Assert.Equal((100m, 100m), (Inspector.MaxFadeInFrames, Inspector.MaxFadeOutFrames));
        Assert.Equal(TimeFormat.ToTimecode(F(25), Rate), Inspector.FadeInTimeDisplay);
        Assert.Equal(TimeFormat.ToTimecode(F(12), Rate), Inspector.FadeOutTimeDisplay);
        Assert.False(Inspector.IsFadeInInactive);
        Assert.False(Inspector.IsFadeOutInactive);

        var text = new TextClip { Text = "Title", TimelineStart = F(200), Duration = F(25) };
        V1.Clips.Add(text);
        _projects.NotifyTimelineChanged();
        Select(text);
        Assert.True(Inspector.HasFades);
        Assert.Equal((0m, 0m), (Inspector.FadeInFrames, Inspector.FadeOutFrames));
    }

    [Fact]
    public void Typing_a_fade_edits_it_and_consecutive_changes_merge_into_one_undo_step()
    {
        var clip = AddVideo();
        Select(clip);

        Inspector.FadeInFrames = 10;
        Inspector.FadeInFrames = 20;
        Assert.Equal(F(20), clip.FadeIn);
        Assert.Equal("Change Fade In", Top);
        Assert.Equal(TimeFormat.ToTimecode(F(20), Rate), Inspector.FadeInTimeDisplay);

        Inspector.FadeOutFrames = 7;
        Assert.Equal((F(20), F(7)), (clip.FadeIn, clip.FadeOut));

        _undo.Undo();
        Assert.Equal((F(20), MediaTime.Zero), (clip.FadeIn, clip.FadeOut));
        Assert.Equal(0m, Inspector.FadeOutFrames);            // the field follows undo
        _undo.Undo();
        Assert.Equal(MediaTime.Zero, clip.FadeIn);
        Assert.Equal(0m, Inspector.FadeInFrames);
    }

    [Fact]
    public void A_rejected_or_fractional_fade_reports_and_shows_the_model_again()
    {
        var clip = AddVideo(frames: 50);
        Select(clip);

        Inspector.FadeInFrames = 51;                          // longer than the clip
        Assert.Equal(MediaTime.Zero, clip.FadeIn);
        Assert.Equal(0m, Inspector.FadeInFrames);
        Assert.Contains("longer than the clip", _status.Message);

        Inspector.FadeOutFrames = 2.5m;
        Assert.Equal(MediaTime.Zero, clip.FadeOut);
        Assert.Equal(0m, Inspector.FadeOutFrames);
        Assert.Equal("A fade is a whole number of frames.", _status.Message);
    }

    [Fact]
    public void A_trim_shorter_than_the_fade_shows_the_cut_fade_and_the_arrow_lowers_it()
    {
        var clip = AddVideo();
        Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange { Fade = new FadeProperties(F(80), MediaTime.Zero) }).Success);
        Assert.True(_edit.TrimClip(clip.Id, ClipEdge.End, F(30)).Success);

        Select(clip);
        Assert.Equal((30m, 30m), (Inspector.FadeInFrames, Inspector.MaxFadeInFrames));

        Inspector.FadeInFrames = 29;                           // the down arrow
        Assert.Equal(F(29), clip.FadeIn);
        Assert.Equal(29m, Inspector.FadeInFrames);
    }

    /// <summary>A fade longer than its clip can only come from a file saved before fades were cut to the clip (D025 §2,
    /// 2026-09-30); the loader keeps every tick, and the Inspector shows it without coercing it into an edit.</summary>
    [Fact]
    public void A_stored_fade_longer_than_the_clip_from_a_file_is_shown_as_stored_and_never_coerced_into_an_edit()
    {
        var clip = AddVideo();
        Assert.True(_edit.TrimClip(clip.Id, ClipEdge.End, F(30)).Success);
        clip.FadeIn = F(80);                                   // as loaded from such a file
        var steps = _undo.CurrentPosition;

        Select(clip);

        Assert.Equal(80m, Inspector.FadeInFrames);
        Assert.Equal(80m, Inspector.MaxFadeInFrames);         // the maximum allows the stored value
        Assert.Equal(30m, Inspector.MaxFadeOutFrames);
        Assert.Same(steps, _undo.CurrentPosition);            // showing it made no edit
    }

    [Fact]
    public void While_an_export_runs_the_fade_fields_edit_nothing()
    {
        var clip = AddVideo();
        Select(clip);
        using (_lock.Acquire())
        {
            Assert.False(Inspector.IsEditingAllowed);
            Inspector.FadeInFrames = 10;
            Assert.Equal(MediaTime.Zero, clip.FadeIn);
            Assert.Equal(0m, Inspector.FadeInFrames);
        }
        Assert.True(Inspector.IsEditingAllowed);
    }

    [Fact]
    public void A_fade_on_an_edge_with_a_dissolve_is_shown_as_inactive_with_its_value()
    {
        var a = AddVideo();
        var b = AddVideo();                                   // appended: touches A
        Assert.True(_edit.SetClipProperties(a.Id, new ClipPropertyChange { Fade = new FadeProperties(F(5), F(9)) }).Success);
        Assert.True(_edit.SetClipProperties(b.Id, new ClipPropertyChange { Fade = new FadeProperties(F(6), F(4)) }).Success);
        var dissolve = new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(10), LeftClipId = a.Id, RightClipId = b.Id };
        V1.Transitions.Add(dissolve);                         // placed directly: dissolve editing is Step 10.6
        _projects.NotifyTimelineChanged();

        Select(a);
        Assert.Equal((false, true), (Inspector.IsFadeInInactive, Inspector.IsFadeOutInactive));
        Assert.Equal((5m, 9m), (Inspector.FadeInFrames, Inspector.FadeOutFrames));
        Assert.True(ClipVm(a).HasFadeIn);
        Assert.False(ClipVm(a).HasFadeOut);                   // not applied, not drawn

        Select(b);
        Assert.Equal((true, false), (Inspector.IsFadeInInactive, Inspector.IsFadeOutInactive));
        Assert.False(ClipVm(b).HasFadeIn);

        V1.Transitions.Remove(dissolve);
        _projects.NotifyTimelineChanged();
        Assert.Equal((false, false), (Inspector.IsFadeInInactive, Inspector.IsFadeOutInactive));
        Assert.True(ClipVm(b).HasFadeIn);                     // applies again
        Assert.True(ClipVm(a).HasFadeOut);
    }

    [Fact]
    public void The_timeline_draws_the_effective_ramps_and_follows_zoom_and_trim()
    {
        var clip = AddVideo();
        var vm = ClipVm(clip);
        Assert.False(vm.HasFadeIn);
        Assert.False(vm.HasFadeOut);

        Assert.True(_edit.SetClipProperties(clip.Id, new ClipPropertyChange { Fade = new FadeProperties(F(25), F(50)) }).Success);
        var pps = _vm.Timeline.PixelsPerSecond;
        Assert.Equal(TimelineCoordinateMapper.TimeToX(F(25), pps), vm.FadeInWidth, 6);
        Assert.Equal(TimelineCoordinateMapper.TimeToX(F(50), pps), vm.FadeOutWidth, 6);

        Assert.True(_edit.TrimClip(clip.Id, ClipEdge.End, F(40)).Success);   // clamped to the 40 frames left
        Assert.Equal(TimelineCoordinateMapper.TimeToX(F(40), pps), vm.FadeOutWidth, 6);
        Assert.Equal(TimelineCoordinateMapper.TimeToX(F(25), pps), vm.FadeInWidth, 6);
    }

    [Fact]
    public void A_speed_change_that_removes_a_dissolve_says_so_in_the_status_bar()
    {
        var clip = AddVideo(frames: 200);
        Assert.True(_edit.Split(F(100)).Success);
        var (a, b) = (V1.Clips[0], V1.Clips[1]);
        Assert.True(_edit.AddTransition(a.Id, b.Id, F(10)).Success);
        Select(a);

        Inspector.SpeedValue = 2m;                            // A ends at 50: its cut with B opens (D025 §5)

        Assert.Empty(V1.Transitions);
        Assert.Equal("A dissolve was removed: its clips no longer meet.", _status.Message);
        Assert.Same(clip, a);
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
