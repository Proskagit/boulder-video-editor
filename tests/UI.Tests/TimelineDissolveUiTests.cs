using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
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
/// Phase 10 Step 10.8 (D025): the dissolve UI through the real shell wiring — the Dissolve command on two selected
/// clips (1 s, or the longest the edit service says fits), the service's messages when it refuses, the zone on the
/// timeline (zoom, length), selecting a dissolve, Delete, the Inspector's DISSOLVE section, the editing lock, and
/// Undo / Redo leaving no stale selection.
/// </summary>
public sealed class TimelineDissolveUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly MainWindowViewModel _vm;

    public TimelineDissolveUiTests()
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
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private TimelineViewModel Timeline => _vm.Timeline;
    private InspectorViewModel Inspector => _vm.Inspector;
    private Track V1 => _projects.Current.Timeline.VideoTracks[0];
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private MediaAsset Asset(double seconds)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-dissolve-ui", Guid.NewGuid().ToString("N"), "v.mp4"),
            Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(seconds), FrameRate = Rate, Width = 1920, Height = 1080 }
        };
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    /// <summary>A <paramref name="seconds"/> s video split at <paramref name="at"/>: two clips meeting with handles.</summary>
    private (Clip A, Clip B) SplitVideo(double seconds = 20, long at = 100)
    {
        Assert.True(_edit.AddClip(Asset(seconds).Id).Success);
        Assert.True(_edit.Split(F(at)).Success);
        return (V1.Clips[0], V1.Clips[1]);
    }

    private TimelineClipViewModel ClipVm(Clip clip) => Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);
    private TimelineTransitionViewModel ZoneVm() => Timeline.Tracks.SelectMany(t => t.Transitions).Single();

    private void SelectBoth(Clip a, Clip b)
    {
        Timeline.ClearSelection();
        Timeline.OnClipPressed(ClipVm(a), toggle: false);
        Timeline.OnClipPressed(ClipVm(b), toggle: true);
    }

    // --- the Dissolve command -------------------------------------------------------------------------------------------

    [Fact]
    public void The_command_needs_exactly_two_selected_clips_and_no_export()
    {
        var (a, b) = SplitVideo();
        Assert.True(_edit.Split(F(200)).Success);
        var c = V1.Clips[2];

        Assert.False(Timeline.AddDissolveCommand.CanExecute(null));
        Timeline.OnClipPressed(ClipVm(a), toggle: false);
        Assert.False(Timeline.AddDissolveCommand.CanExecute(null));
        Timeline.OnClipPressed(ClipVm(b), toggle: true);
        Assert.True(Timeline.AddDissolveCommand.CanExecute(null));
        Timeline.OnClipPressed(ClipVm(c), toggle: true);
        Assert.False(Timeline.AddDissolveCommand.CanExecute(null));                   // three clips

        Timeline.OnClipPressed(ClipVm(c), toggle: true);
        using (_lock.Acquire())
            Assert.False(Timeline.AddDissolveCommand.CanExecute(null));
        Assert.True(Timeline.AddDissolveCommand.CanExecute(null));
    }

    /// <summary>A button follows <c>CanExecuteChanged</c>, not <c>CanExecute</c> itself: the command must announce every
    /// change of its availability — also from one selected clip to two (the bug found in the 10.8 manual run: the
    /// button stayed disabled because "something is selected" didn't change).</summary>
    [Fact]
    public void The_button_is_told_whenever_the_command_becomes_available_or_not()
    {
        var (a, b) = SplitVideo();
        var state = Timeline.AddDissolveCommand.CanExecute(null);
        Timeline.AddDissolveCommand.CanExecuteChanged += (_, _) => state = Timeline.AddDissolveCommand.CanExecute(null);

        Timeline.OnClipPressed(ClipVm(a), toggle: false);
        Assert.False(state);
        Timeline.OnClipPressed(ClipVm(b), toggle: true);                          // 1 → 2 clips
        Assert.True(state);
        Timeline.OnClipPressed(ClipVm(b), toggle: true);                          // 2 → 1
        Assert.False(state);
        Timeline.OnClipPressed(ClipVm(b), toggle: true);
        using (_lock.Acquire())
            Assert.False(state);
        Assert.True(state);
        Timeline.ClearSelection();
        Assert.False(state);
    }

    [Fact]
    public void Adding_selects_the_new_dissolve_and_shows_it_in_the_inspector_and_undo_leaves_nothing_stale()
    {
        var (a, b) = SplitVideo();
        SelectBoth(a, b);

        Timeline.AddDissolveCommand.Execute(null);

        var dissolve = Assert.Single(V1.Transitions);
        Assert.Equal((a.Id, b.Id, F(25)), (dissolve.LeftClipId, dissolve.RightClipId, dissolve.Duration));   // 1 s
        Assert.Equal("Dissolve added: 25 frames.", _status.Message);
        Assert.True(Timeline.HasTransitionSelection);
        Assert.False(Timeline.HasSelection);
        Assert.True(ZoneVm().IsSelected);
        Assert.True(Inspector.IsTransitionSelected);
        Assert.Equal(25m, Inspector.DissolveFrames);
        Assert.Equal(TimeFormat.ToTimecode(F(25), Rate), Inspector.DissolveTimeDisplay);
        Assert.Equal("Longest that fits here: 201 frames", Inspector.DissolveLimitDisplay);   // ⌊F/2⌋ ≤ 100 in A

        _undo.Undo();
        Assert.Empty(V1.Transitions);
        Assert.Empty(Timeline.Tracks.SelectMany(t => t.Transitions));
        Assert.False(Timeline.HasTransitionSelection);
        Assert.False(Inspector.IsTransitionSelected);

        _undo.Redo();
        Assert.Same(dissolve, Assert.Single(V1.Transitions));
        Assert.Single(Timeline.Tracks.SelectMany(t => t.Transitions));
    }

    [Fact]
    public void Where_less_than_a_second_fits_the_longest_is_added_and_said()
    {
        var (a, b) = SplitVideo(seconds: 4, at: 90);                                 // 100 frames: B has 10
        SelectBoth(a, b);

        Timeline.AddDissolveCommand.Execute(null);

        Assert.Equal(F(20), Assert.Single(V1.Transitions).Duration);
        Assert.Equal("Dissolve added: 20 frames, the longest that fits here.", _status.Message);
    }

    [Fact]
    public void The_edit_services_refusals_are_shown_and_nothing_changes()
    {
        var whole = Asset(4);
        Assert.True(_edit.AddClip(whole.Id).Success);
        Assert.True(_edit.AddClip(whole.Id).Success);                                // two untrimmed clips: no handles
        var (a, b) = (V1.Clips[0], V1.Clips[1]);
        var before = _undo.CurrentPosition;

        SelectBoth(a, b);
        Timeline.AddDissolveCommand.Execute(null);
        Assert.Equal(TimelineEditService.NotEnoughMedia, _status.Message);

        Assert.True(_edit.MoveClips(new[] { b.Id }, 10).Success);                    // now they don't meet
        SelectBoth(a, b);
        Timeline.AddDissolveCommand.Execute(null);
        Assert.StartsWith("Select two clips that meet on a video track", _status.Message);

        Assert.True(_edit.MoveClips(new[] { b.Id }, -10).Success);
        V1.IsLocked = true;
        SelectBoth(a, b);
        Timeline.AddDissolveCommand.Execute(null);
        Assert.Contains("locked", _status.Message);

        Assert.Empty(V1.Transitions);
        Assert.False(Timeline.HasTransitionSelection);
    }

    // --- the zone on the timeline ---------------------------------------------------------------------------------------

    [Fact]
    public void The_zone_follows_the_cut_the_zoom_and_the_length()
    {
        var (a, b) = SplitVideo();
        var dissolve = _edit.AddTransition(a.Id, b.Id, F(20));
        var zone = ZoneVm();
        double X(long frame) => TimelineCoordinateMapper.TimeToX(F(frame), Timeline.PixelsPerSecond);

        Assert.Equal(X(90), zone.Left, 6);                                            // [90, 110)
        Assert.Equal(X(110) - X(90), zone.Width, 6);

        Timeline.ZoomInCommand.Execute(null);
        Assert.Equal(X(90), zone.Left, 6);
        Assert.Equal(X(110) - X(90), zone.Width, 6);

        Assert.True(_edit.SetTransitionDuration(dissolve.TransitionId!.Value, F(7)).Success);   // [97, 104)
        Assert.Equal(X(97), zone.Left, 6);
        Assert.Equal(X(104) - X(97), zone.Width, 6);
    }

    [Fact]
    public void Selecting_a_zone_clears_the_clips_and_a_clip_clears_the_zone()
    {
        var (a, b) = SplitVideo();
        _edit.AddTransition(a.Id, b.Id, F(20));
        Timeline.OnClipPressed(ClipVm(a), toggle: false);
        Assert.True(Inspector.IsTimelineClipSelected);

        Timeline.OnTransitionPressed(ZoneVm());
        Assert.False(Timeline.HasSelection);
        Assert.True(Inspector.IsTransitionSelected);
        Assert.Equal($"{ClipVm(a).Name} → {ClipVm(b).Name} (V1)", Inspector.DissolveClipsDisplay);

        Timeline.OnClipPressed(ClipVm(b), toggle: false);
        Assert.False(Timeline.HasTransitionSelection);
        Assert.False(ZoneVm().IsSelected);
        Assert.True(Inspector.IsTimelineClipSelected);
    }

    [Fact]
    public void Delete_removes_the_selected_dissolve_in_one_undo_step()
    {
        var (a, b) = SplitVideo();
        _edit.AddTransition(a.Id, b.Id, F(20));
        Timeline.OnTransitionPressed(ZoneVm());

        Assert.True(Timeline.DeleteSelectedCommand.CanExecute(null));
        Timeline.DeleteSelectedCommand.Execute(null);

        Assert.Empty(V1.Transitions);
        Assert.Equal(2, V1.Clips.Count);                                              // the clips stay
        Assert.Equal("Dissolve removed", _status.Message);
        Assert.False(Inspector.IsTransitionSelected);
        Assert.Equal("Remove Dissolve", (_undo.CurrentPosition as IUndoableCommand)?.Description);

        _undo.Undo();
        Assert.Single(V1.Transitions);
    }

    // --- the Inspector ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_inspector_changes_the_length_merges_the_steps_and_removes()
    {
        var (a, b) = SplitVideo();
        _edit.AddTransition(a.Id, b.Id, F(20));
        var added = _undo.CurrentPosition;
        Timeline.OnTransitionPressed(ZoneVm());

        Inspector.DissolveFrames = 24;
        Inspector.DissolveFrames = 30;
        var dissolve = Assert.Single(V1.Transitions);
        Assert.Equal(F(30), dissolve.Duration);
        Assert.Equal(TimeFormat.ToTimecode(F(30), Rate), Inspector.DissolveTimeDisplay);
        Assert.Equal("Change Dissolve Duration", (_undo.CurrentPosition as IUndoableCommand)?.Description);

        Inspector.DissolveFrames = 2.5m;
        Assert.Equal("A dissolve is a whole number of frames.", _status.Message);
        Assert.Equal(30m, Inspector.DissolveFrames);

        _undo.Undo();                                                                 // both changes at once
        Assert.Same(added, _undo.CurrentPosition);
        Assert.Equal(20m, Inspector.DissolveFrames);                                  // the field follows undo

        Inspector.RemoveDissolveCommand.Execute(null);
        Assert.Empty(V1.Transitions);
        Assert.False(Inspector.IsTransitionSelected);
    }

    [Fact]
    public void The_inspectors_range_is_2_to_the_longest_that_fits_and_a_refusal_is_shown()
    {
        var (a, b) = SplitVideo(seconds: 4, at: 90);                                 // the longest: 20
        _edit.AddTransition(a.Id, b.Id, F(10));
        Timeline.OnTransitionPressed(ZoneVm());

        Assert.Equal((2m, 20m), (Inspector.MinDissolveFrames, Inspector.MaxDissolveFrames));
        Assert.Equal("Longest that fits here: 20 frames", Inspector.DissolveLimitDisplay);

        V1.IsLocked = true;
        Inspector.DissolveFrames = 12;
        Assert.Contains("locked", _status.Message);
        Assert.Equal(10m, Inspector.DissolveFrames);
    }

    [Fact]
    public void While_an_export_runs_the_inspector_and_delete_change_nothing()
    {
        var (a, b) = SplitVideo();
        _edit.AddTransition(a.Id, b.Id, F(20));
        Timeline.OnTransitionPressed(ZoneVm());

        using (_lock.Acquire())
        {
            Assert.False(Inspector.IsEditingAllowed);
            Inspector.DissolveFrames = 30;
            Inspector.RemoveDissolveCommand.Execute(null);
            Assert.False(Timeline.DeleteSelectedCommand.CanExecute(null));
        }

        Assert.Equal(F(20), Assert.Single(V1.Transitions).Duration);
        Assert.Equal(20m, Inspector.DissolveFrames);
    }

    [Fact]
    public void An_edit_that_removes_the_selected_dissolve_clears_its_selection()
    {
        var (a, b) = SplitVideo();
        _edit.AddTransition(a.Id, b.Id, F(20));
        Timeline.OnTransitionPressed(ZoneVm());

        Assert.True(_edit.MoveClips(new[] { b.Id }, 10).Success);                    // the cut opens

        Assert.Empty(V1.Transitions);
        Assert.False(Timeline.HasTransitionSelection);
        Assert.False(Inspector.IsTransitionSelected);
    }

    // --- fixes from the 10.8 manual run -------------------------------------------------------------------------------

    /// <summary>16.2: dragging both clips carries the zone along during the drag, not only on release; dragging one
    /// hides it (the release removes it); cancelling restores it.</summary>
    [Fact]
    public void The_zone_follows_a_drag_of_both_clips_and_hides_when_one_is_dragged()
    {
        var (a, b) = SplitVideo();
        _edit.AddTransition(a.Id, b.Id, F(20));
        var zone = ZoneVm();
        var (left, width) = (zone.Left, zone.Width);
        double X(long frame) => TimelineCoordinateMapper.TimeToX(F(frame), Timeline.PixelsPerSecond);
        Timeline.SnappingEnabled = false;

        SelectBoth(a, b);
        Timeline.BeginMove(X(50));
        Timeline.UpdateGesture(X(50) + (X(40) - X(0)), ClipTrackVm(a));           // 40 frames to the right
        Assert.True(zone.IsVisible);
        Assert.Equal(X(130), zone.Left, 6);                                          // [90, 110) + 40
        Assert.Equal(width, zone.Width, 6);
        Timeline.CancelGesture();
        Assert.Equal(left, zone.Left, 6);

        Timeline.ClearSelection();
        Timeline.OnClipPressed(ClipVm(b), toggle: false);
        Timeline.BeginMove(X(150));
        Timeline.UpdateGesture(X(150) + (X(20) - X(0)), ClipTrackVm(b));
        Assert.False(zone.IsVisible);
        Timeline.CancelGesture();
        Assert.True(zone.IsVisible);
    }

    /// <summary>A trim preview of the cut edge hides the zone (it opens the cut); a far-edge trim keeps it.</summary>
    [Fact]
    public void A_trim_preview_hides_the_zone_only_when_it_opens_the_cut()
    {
        var (a, b) = SplitVideo();
        _edit.AddTransition(a.Id, b.Id, F(20));
        var zone = ZoneVm();
        double X(long frame) => TimelineCoordinateMapper.TimeToX(F(frame), Timeline.PixelsPerSecond);
        Timeline.SnappingEnabled = false;

        Timeline.BeginTrim(ClipVm(a), ClipEdge.End, X(100));
        Timeline.UpdateGesture(X(80), ClipTrackVm(a));
        Assert.False(zone.IsVisible);
        Timeline.CancelGesture();
        Assert.True(zone.IsVisible);

        Timeline.BeginTrim(ClipVm(a), ClipEdge.Start, X(0));
        Timeline.UpdateGesture(X(30), ClipTrackVm(a));
        Assert.True(zone.IsVisible);
        Timeline.EndGesture();
        Assert.Equal(F(30), a.TimelineStart);
        Assert.Single(V1.Transitions);
    }

    /// <summary>16.3 / 16.4: the zone is found by its position (the view asks only when no trim handle was hit), and a
    /// hidden zone is never found.</summary>
    [Fact]
    public void A_zone_is_found_by_its_position()
    {
        var (a, b) = SplitVideo();
        _edit.AddTransition(a.Id, b.Id, F(20));
        var zone = ZoneVm();
        double X(long frame) => TimelineCoordinateMapper.TimeToX(F(frame), Timeline.PixelsPerSecond);
        var track = ClipTrackVm(a);

        Assert.Same(zone, Timeline.TransitionAt(track, X(95)));
        Assert.Null(Timeline.TransitionAt(track, X(80)));
        Assert.Null(Timeline.TransitionAt(track, X(110)));
        zone.IsVisible = false;
        Assert.Null(Timeline.TransitionAt(track, X(95)));
    }

    /// <summary>17 (4x on "bars"): the edit service refuses; the Inspector reports it, the clip is unchanged and the field
    /// holds the clip speed again.</summary>
    [Fact]
    public void A_refused_speed_is_reported_and_the_field_shows_the_clip_speed()
    {
        var (a, b) = SplitVideo(at: 25);                                             // B has 25 frames before it
        _edit.AddTransition(a.Id, b.Id, F(24));                                      // needs 12 before the cut
        Timeline.OnClipPressed(ClipVm(b), toggle: false);

        Inspector.SpeedValue = 4m;                                                   // 4x: 6 frames, too few

        Assert.Contains(TimelineEditService.NotEnoughMedia, _status.Message);
        Assert.Equal(ClipSpeed.Normal, ((MediaBackedClip)b).Speed);
        Assert.Equal(1m, Inspector.SpeedValue);
        Assert.Single(V1.Transitions);
    }

    /// <summary>17 (2x on "pattern", then Undo): the Inspector arrows make several speed changes; the first removes the
    /// dissolve; one Undo restores the speed and the dissolve.</summary>
    [Fact]
    public void One_undo_after_speed_changes_in_the_inspector_brings_the_dissolve_back()
    {
        var (a, b) = SplitVideo();
        _edit.AddTransition(a.Id, b.Id, F(20));
        Timeline.OnClipPressed(ClipVm(a), toggle: false);

        Inspector.SpeedValue = 1.05m;
        Inspector.SpeedValue = 1.5m;
        Inspector.SpeedValue = 2m;
        Assert.Empty(V1.Transitions);

        _undo.Undo();

        Assert.Single(V1.Transitions);
        Assert.Equal(ClipSpeed.Normal, ((MediaBackedClip)a).Speed);
        Assert.Equal(F(100), a.TimelineEnd);
        Assert.Single(Timeline.Tracks.SelectMany(t => t.Transitions));
    }

    private TimelineTrackViewModel ClipTrackVm(Clip clip) => Timeline.Tracks.Single(t => t.Clips.Any(c => c.Id == clip.Id));

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
