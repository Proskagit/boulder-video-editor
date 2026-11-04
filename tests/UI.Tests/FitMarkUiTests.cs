using System.Collections.Immutable;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Steps 37.4b and 37.6 (D051 §8): the FIT marks in the interface — proposals on the import of a FIT, the marks drawn on
/// the clips of their video (a click selects one, Delete declines it), the Inspector's actions (lay out from a mark, move
/// it to the playhead, decline it; link / unlink an animation), «Отклонённые подъёмы» with «Вернуть» and «Настройки FIT…»
/// in the COROS menu (also without a training), the FIT window (Beta on by default, reset asks first).
/// </summary>
public sealed class FitMarkUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly MediaAsset _video;
    private readonly VideoClip _clip;
    private readonly TrainingSessionService _session;
    private MediaTime _playhead = MediaTime.FromSeconds(6);

    public FitMarkUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _video = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-fit", Guid.NewGuid().ToString("N"), "climb.mp4"),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed, RecordingZeroOverrideUtc = W0,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(20), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080 }
        };
        _projects.Current.MediaAssets.Add(_video);
        Assert.True(_edit.AddClip(_video.Id).Success);
        _clip = (VideoClip)_projects.Current.Timeline.VideoTracks[0].Clips[0];
        foreach (var y in new[] { 0.3, 0.6 })
            Assert.True(_edit.AddHold(_clip.Id, new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0.48, y - 0.02),
                new NormalizedPoint(0.52, y - 0.02), new NormalizedPoint(0.52, y + 0.02)), RouteColor.HexToOkLab("#E53935"),
                RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero)).Success);
        _session = new TrainingSessionService(_projects);
    }

    /// <summary>Climb 1 at W0 + 15 s (proposed at 3 s of the video), climb 2 at W0 + 40 s (not filmed).</summary>
    private static ClimbingWorkout Workout(DateTimeOffset? start = null, double firstClimb = 15) => new()
    {
        DeviceName = "COROS APEX 4 42mm", Manufacturer = 294, Product = 814, StartUtc = start ?? W0.AddMinutes(-5), EndUtc = W0.AddHours(1),
        UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(3900), TimerTime = MediaTime.FromSeconds(3900),
        Sport = 31, SubSport = 69, AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Warnings = [],
        Segments = new[]
        {
            new WorkoutSegment(0, WorkoutSegmentKind.Climb, 1, W0.AddSeconds(firstClimb), W0.AddSeconds(35), MediaTime.FromSeconds(20), null, null),
            new WorkoutSegment(2, WorkoutSegmentKind.Climb, 2, W0.AddSeconds(40), W0.AddSeconds(60), MediaTime.FromSeconds(20), null, null),
        }
    };

    private void Import() => _session.Attach(new TrainingFile(Workout(), 10, new string('b', 64)), @"C:\fit\b.fit");

    private TrainingReference Training => _projects.Current.Training!;

    private FitMark Mark => Training.Marks.Single();

    private InspectorViewModel Inspector()
    {
        var inspector = new InspectorViewModel(_edit, _status) { Training = _session, Playhead = () => _playhead };
        inspector.ShowClip(new TimelineClipSelection(_clip, "climb.mp4", _video, Rate));
        return inspector;
    }

    private void Reshow(InspectorViewModel inspector) => inspector.ShowClip(new TimelineClipSelection(_clip, "climb.mp4", _video, Rate));

    [Fact]
    public void Importing_a_FIT_proposes_a_mark_for_every_filmed_climb_without_an_undo_step()
    {
        Import();
        Assert.Equal((FitMarkPosition.Proposed, W0.AddSeconds(3), 120_000_000L), (Mark.Position, Mark.AutoStartUtc, Mark.StartCorrectionTicks));
        Assert.False(_undo.CanUndo && _undo.NextUndo?.Description == "Propose FIT Marks");
    }

    [Fact]
    public void A_mark_clamped_to_the_edge_of_the_recording_is_shown_as_such_in_the_Inspector_and_on_the_timeline()
    {
        // D051 A1 «on the edge of the recording, shown as such»: climb 1 at W0 + 5 s → W_auto = W0 − 7 s, before the video.
        _session.Attach(new TrainingFile(Workout(firstClimb: 5), 10, new string('b', 64)), @"C:\fit\b.fit");
        Assert.Equal(FitMarkPositionKind.Clamped, Mark.Position.Kind);
        Assert.Equal(MediaTime.Zero, Mark.Position.SourceTime);
        Assert.Equal(string.Format(Strings.Inspector_FitMarkItem, 1, "13:00:05", Strings.FitMark_Clamped), Assert.Single(Inspector().FitMarkItems));
        Assert.NotEqual(Strings.FitMark_Proposed, Strings.FitMark_Clamped);
    }

    [Fact]
    public void A_new_import_of_the_same_activity_keeps_the_marks_and_never_recreates_a_declined_climb()
    {
        Import();
        Assert.True(_edit.MoveFitMark(Mark.Climb, MediaTime.FromSeconds(5)).Success);
        var moved = Mark;
        Import();                                                         // the same activity again (D051 §5.2, §5.3; T7, A9)
        Assert.Equal(moved, Mark);
        Assert.True(_edit.DeclineClimb(Mark.Climb).Success);
        Import();
        Assert.Empty(Training.Marks);                                     // A3: not recreated
        Assert.Single(Training.DeclinedClimbs);

        var other = Workout(W0.AddMinutes(-6));                          // another activity replaces the training (§5.11)
        _session.Attach(new TrainingFile(other, 10, new string('c', 64)), @"C:\fit\c.fit");
        Assert.Empty(Training.DeclinedClimbs);
        Assert.Equal(FitMarkPosition.Proposed, Mark.Position);
    }

    [Fact]
    public void The_Inspector_lays_out_from_the_mark_moves_it_and_declines_it()
    {
        Import();
        var inspector = Inspector();
        Assert.True(inspector.ShowsFitMarks);
        Assert.Equal(string.Format(Strings.Inspector_FitMarkItem, 1, "13:00:15", Strings.FitMark_Proposed), Assert.Single(inspector.FitMarkItems));
        Assert.False(inspector.LayOutFromFitMarkCommand.CanExecute(null));
        inspector.SelectedFitMarkIndex = 0;

        inspector.MoveFitMarkToPlayheadCommand.Execute(null);                   // 6 s
        Assert.Equal(FitMarkPosition.Moved(MediaTime.FromSeconds(6)), Mark.Position);
        Reshow(inspector);
        inspector.SelectedFitMarkIndex = 0;
        inspector.LayOutFromFitMarkCommand.Execute(null);
        Assert.Equal(_clip.Id, Mark.LinkedClipId);
        Assert.Equal(Strings.Inspector_FitLaidOut, _status.Message);
        Reshow(inspector);
        Assert.True(inspector.ShowsFitLink);
        Assert.Equal(string.Format(Strings.Inspector_FitLinked, 1), inspector.FitLinkText);

        inspector.SelectedFitMarkIndex = 0;
        inspector.DeclineFitMarkCommand.Execute(null);
        Assert.Empty(Training.Marks);
        Assert.Single(Training.DeclinedClimbs);
    }

    [Fact]
    public void The_Inspector_links_and_unlinks_the_clips_animation()
    {
        Import();
        Assert.True(_edit.LayOutIntroAndFinal(_clip.Id, MediaTime.FromFrame(200, Rate)).Success);
        var inspector = Inspector();
        Assert.True(inspector.ShowsFitLink);
        Assert.Equal(Strings.Inspector_FitNotLinked, inspector.FitLinkText);
        Assert.Single(inspector.LinkableClimbs);
        Assert.False(inspector.UnlinkFitClimbCommand.CanExecute(null));
        inspector.SelectedLinkableIndex = 0;
        inspector.LinkFitClimbCommand.Execute(null);
        Assert.Equal(_clip.Id, Mark.LinkedClipId);
        Reshow(inspector);
        Assert.Empty(inspector.LinkableClimbs);
        Assert.True(inspector.UnlinkFitClimbCommand.CanExecute(null));
        inspector.UnlinkFitClimbCommand.Execute(null);
        Assert.Null(Mark.LinkedClipId);
    }

    [Fact]
    public void The_marks_are_drawn_on_the_clip_a_click_selects_one_and_Delete_declines_it()
    {
        Import();
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance);
        _projects.NotifyTimelineChanged();
        var clipVm = timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == _clip.Id);
        var glyph = Assert.Single(clipVm.FitMarks);
        Assert.Equal(TimelineViewModel.FitProposedHex, glyph.ColorHex);
        Assert.Equal(string.Format(Strings.FitMark_Tip, 1, Strings.FitMark_Proposed), glyph.Tip);
        var frame = HoldAnimationRule.StartFrame(_clip.TimelineStart, _clip.SourceIn, _clip.Speed, MediaTime.FromSeconds(3), Rate);
        Assert.Equal(TimelineCoordinateMapperX(timeline, MediaTime.FromFrame(frame, Rate)), glyph.Left, 6);

        timeline.OnFitMarkClicked(glyph.Climb);
        Assert.True(Assert.Single(clipVm.FitMarks).IsSelected);
        Assert.True(timeline.DeleteSelectedCommand.CanExecute(null));
        timeline.DeleteSelectedCommand.Execute(null);
        Assert.Empty(Training.Marks);
        Assert.Empty(clipVm.FitMarks);
        Assert.Equal(_clip, _projects.Current.Timeline.VideoTracks[0].Clips.Single());   // the clip stays
        _undo.Undo();
        Assert.Single(clipVm.FitMarks);
    }

    private static double TimelineCoordinateMapperX(TimelineViewModel timeline, MediaTime t) =>
        AiVideoEditor.UI.Common.TimelineCoordinateMapper.TimeToX(t, timeline.PixelsPerSecond);

    [Fact]
    public void The_COROS_menu_lists_the_declined_climbs_with_Вернуть_and_offers_the_FIT_settings_without_a_training()
    {
        var store = new MemoryStore();
        var toolbar = new ToolbarViewModel(_undo, null!, null!, _status, trainingSession: _session, edit: _edit, fitMarks: _edit,
            fitSettings: new FitSettingsWorkflow(store, new NoDialog(), _status));
        Assert.True(toolbar.HasFitSettings);
        Assert.True(toolbar.FitSettingsCommand.CanExecute(null));            // no training yet (О4)
        Assert.False(toolbar.HasDeclinedClimbs);

        Import();
        Assert.True(_edit.DeclineClimb(Mark.Climb).Success);
        var item = Assert.Single(toolbar.DeclinedClimbs);
        Assert.Equal(string.Format(Strings.Toolbar_DeclinedClimb, 1, "13:00:15"), item.Label);
        item.Restore.Execute(null);
        Assert.Single(Training.Marks);
        Assert.Empty(toolbar.DeclinedClimbs);
        Assert.Equal(Strings.Toolbar_Restored, _status.Message);
    }

    // ---- «Настройки FIT…» (37.6) ------------------------------------------------------------------------------------------

    private sealed class NoDialog : IFitSettingsDialog
    {
        public FitSettingsViewModel? Shown { get; private set; }
        public Task ShowAsync(FitSettingsViewModel settings) { Shown = settings; return Task.CompletedTask; }
    }

    private sealed class MemoryStore : IFitCalibrationStore
    {
        public FitCalibrationSnapshot Current { get; set; } = FitCalibrationSnapshot.Initial;
        public int Resets { get; private set; }
        public Task<FitCalibrationSnapshot> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task<bool> SynchronizeAsync(IReadOnlySet<TrainingActivity> trainings, IReadOnlyCollection<Core.Telemetry.Calibration.CalibrationObservation> observations,
            CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetBetaAsync(bool enabled, CancellationToken ct = default)
        {
            if (Current.FileState != FitCalibrationFileState.Usable) return Task.FromResult(false);
            Current = Current with { BetaEnabled = enabled };
            return Task.FromResult(true);
        }
        public Task<bool> ResetAsync(CancellationToken ct = default)
        {
            Resets++;
            Current = Current with { UsedCount = 0, EstimateTicks = 120_000_000 };
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task The_FIT_window_shows_Beta_on_the_estimate_and_the_count_and_writes_a_change_at_once()
    {
        var store = new MemoryStore { Current = FitCalibrationSnapshot.Initial with { EstimateTicks = 135_000_000, UsedCount = 42 } };
        var dialog = new NoDialog();
        await new FitSettingsWorkflow(store, dialog, _status).RunAsync();
        var vm = dialog.Shown!;
        Assert.True(vm.BetaEnabled);
        Assert.Equal(string.Format(Strings.FitSettings_Estimate, 13.5), vm.EstimateText);
        Assert.Equal(string.Format(Strings.FitSettings_UsedCount, 42), vm.UsedCountText);
        Assert.Equal("", vm.FileNote);

        vm.BetaEnabled = false;
        Assert.False(store.Current.BetaEnabled);
        Assert.Equal(42, store.Current.UsedCount);                             // nothing removed
    }

    [Fact]
    public async Task Reset_asks_first_and_does_nothing_on_Cancel()
    {
        var store = new MemoryStore { Current = FitCalibrationSnapshot.Initial with { UsedCount = 5 } };
        var dialogs = new ScriptedDialogs(1, 0);                               // Cancel, then Reset
        var vm = new FitSettingsViewModel(store, _status, dialogs);
        await vm.ResetCommand.ExecuteAsync(null);
        Assert.Equal(0, store.Resets);
        await vm.ResetCommand.ExecuteAsync(null);
        Assert.Equal(1, store.Resets);
        Assert.Equal(2, dialogs.Asked.Count);
        Assert.Equal(Strings.FitSettings_ResetDone, _status.Message);
    }

    [Fact]
    public void A_newer_calibration_file_is_shown_and_nothing_can_be_changed()
    {
        var store = new MemoryStore { Current = FitCalibrationSnapshot.Initial with { FileState = FitCalibrationFileState.NewerFormat } };
        var vm = new FitSettingsViewModel(store, _status);
        Assert.Equal(Strings.FitSettings_NewerFile, vm.FileNote);
        Assert.False(vm.CanChange);
        Assert.False(vm.ResetCommand.CanExecute(null));
    }
}
