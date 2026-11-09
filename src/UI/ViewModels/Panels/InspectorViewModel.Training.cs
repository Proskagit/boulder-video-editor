using System.Collections.ObjectModel;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// The COROS training of a video clip (Phase 35, D049 §5–§6, D050 amendment B2 / B4 and its amendment of 2026-11-02): the
/// state of this video's recording start Z (set / unknown — the automatic state comes with Step 35.3's rule), the match of
/// the playhead's frame to the end (the default) or the start of climb N — <c>Z = W_end − ClimbEndLag − ClockCorrection −
/// s(n)</c> or <c>Z = W_start − ClockCorrection − s(n)</c> for this video only, one Undo step; choosing the point changes
/// nothing — and a read-only readout of the mapping at the playhead. The training's clock correction is not here.
/// </summary>
public sealed partial class InspectorViewModel
{
    private TrainingSessionService? _training;
    private MediaAsset? _trainingAsset;
    private IReadOnlyList<WorkoutSegment> _climbs = Array.Empty<WorkoutSegment>();

    /// <summary>The project's training (set by the main window); null: no training section.</summary>
    public TrainingSessionService? Training
    {
        get => _training;
        set
        {
            if (_training is not null) _training.Changed -= OnTrainingChanged;
            _training = value;
            if (_training is not null) _training.Changed += OnTrainingChanged;
            RefreshTraining();
        }
    }

    /// <summary>A video clip is shown and a training service exists.</summary>
    [ObservableProperty] private bool _showsTraining;

    /// <summary>«Тренировка в позиции плейхеда: …» (B4).</summary>
    [ObservableProperty] private string _trainingReadout = "";

    /// <summary>This video's recording start: set / unknown.</summary>
    [ObservableProperty] private string _recordingStartText = "";

    /// <summary>The training's climbs, «Подъём N · чч:мм:сс» in the watch's local time (display only).</summary>
    public ObservableCollection<string> TrainingClimbs { get; } = new();

    [ObservableProperty] private int _selectedClimbIndex = -1;

    partial void OnSelectedClimbIndexChanged(int value) => AlignToClimbCommand.NotifyCanExecuteChanged();

    /// <summary>The points a frame can be matched to, in <see cref="AlignAnchorIndex"/> order: the end, the start.</summary>
    public IReadOnlyList<string> AlignAnchors { get; } = new[] { Strings.Inspector_AnchorEnd, Strings.Inspector_AnchorStart };

    /// <summary>0: the end of the climb (the default), 1: its start. Kept while the app runs; choosing changes nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlignAnchor), nameof(AlignToClimbText), nameof(AlignToClimbTip))]
    private int _alignAnchorIndex;

    public ClimbAnchor AlignAnchor => AlignAnchorIndex == 1 ? ClimbAnchor.Start : ClimbAnchor.End;

    public string AlignToClimbText => AlignAnchor == ClimbAnchor.End ? Strings.Inspector_AlignToClimbEnd : Strings.Inspector_AlignToClimb;

    public string AlignToClimbTip => AlignAnchor == ClimbAnchor.End
        ? string.Format(Strings.Inspector_AlignToClimbEndTip, TrainingAlignment.ClimbEndLag.Ticks / (double)TimeSpan.TicksPerSecond)
        : Strings.Inspector_AlignToClimbTip;

    /// <summary>The playhead moved: the readout and the climb chosen for it follow it.</summary>
    public void OnPlayheadMoved()
    {
        UpdateTrainingReadout();
        PreselectClimb();
    }

    /// <summary>How far from a climb's FIT interval the playhead's watch time may lie and still show it (D056 §1): the
    /// watch's late start of a climb is up to about 39 s (D050 B2), the rests between climbs are longer.</summary>
    internal static readonly MediaTime ClimbAtPlayheadTolerance = MediaTime.FromSeconds(30);

    private readonly IDialogService? _dialogs;

    /// <summary>The climb last chosen for the playhead (an index in <see cref="TrainingClimbs"/>, −1: none); the list
    /// follows the playhead only when this changes, so a climb the user chose stays while the playhead stays in its area.</summary>
    private int _preselectedClimb = -1;

    /// <summary>The climb the playhead's frame shows (D056 §1): of the green areas made from the FIT on this clip under the
    /// playhead, the one whose climb the video's time shows there, else the latest begun; without one, the climb whose FIT
    /// interval is nearest to the frame's watch time by the video's binding time (<see cref="FitRouteBinding.TimeOf"/>), within
    /// <see cref="ClimbAtPlayheadTolerance"/>. <c>ByArea</c>: it came from an area. Null: none.</summary>
    private (int Index, bool ByArea)? ClimbAtPlayhead()
    {
        if (!ShowsTraining || _clip is not VideoClip clip || _trainingAsset is null || Playhead is null ||
            _training?.Training is not { IsUsable: true, Workout: { } workout } training) return null;
        var playhead = Playhead();
        var frame = playhead.ToFrameFloor(_rate);
        int? byTime = null;
        if (TrainingAlignment.Covers(clip, frame, _rate) && FitRouteBinding.TimeOf(_trainingAsset, workout) is { } time)
        {
            var w = TrainingAlignment.WatchTime(time.Zero, TrainingAlignment.SourceTimeAt(clip, frame, _rate), training.ClockCorrection);
            long best = ClimbAtPlayheadTolerance.Ticks + 1;
            for (var i = 0; i < _climbs.Count; i++)
            {
                var c = _climbs[i];
                var distance = w < c.StartUtc ? (c.StartUtc - w).Ticks : w > c.End ? (w - c.End).Ticks : 0;
                if (distance < best) (best, byTime) = (distance, i);
            }
        }

        var areas = _training.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>()
            .Where(t => t.AnchorClipId == clip.Id && t.FitClimb is not null && t.TimelineStart <= playhead && playhead < t.TimelineEnd)
            .Select(t => (Area: t, Index: IndexOfClimb(t.FitClimb!.Value)))
            .Where(a => a.Index >= 0)
            .OrderByDescending(a => a.Area.TimelineStart)
            .ToList();
        if (areas.Count > 0)
            return (areas.Any(a => a.Index == byTime) ? byTime!.Value : areas[0].Index, true);
        return byTime is { } index ? (index, false) : null;
    }

    private int IndexOfClimb(ClimbIdentity climb)
    {
        for (var i = 0; i < _climbs.Count; i++)
            if (FitMarkRules.IdentityOf(_climbs[i]) == climb) return i;
        return -1;
    }

    /// <summary>D056 §1: the list «Подъём» shows the climb at the playhead — whenever that climb changes (another clip,
    /// another area, the playhead moved to another climb); a climb chosen by hand stays until then.</summary>
    private void PreselectClimb()
    {
        var at = ClimbAtPlayhead()?.Index ?? -1;
        if (at == _preselectedClimb) return;
        _preselectedClimb = at;
        if (at >= 0) SelectedClimbIndex = at;
    }

    private void OnTrainingChanged(object? sender, EventArgs e)
    {
        RefreshTraining();
        if (ShowsTemplateHeartRate || ShowsTemplateBoulder) RefreshHeartRateHint();
    }

    private void ShowTraining(MediaAsset? asset)
    {
        _trainingAsset = asset;
        _preselectedClimb = -1;
        RefreshTraining();
    }

    private void HideTraining()
    {
        _trainingAsset = null;
        _preselectedClimb = -1;
        RefreshTraining();
    }

    private void RefreshTraining()
    {
        ShowsTraining = _training is not null && _clip is VideoClip && _trainingAsset is not null;
        var workout = _training?.Training is { IsUsable: true } t ? t.Workout : null;
        var climbs = workout?.Climbs.ToList() ?? new List<WorkoutSegment>();
        if (!climbs.SequenceEqual(_climbs))
        {
            var selected = SelectedClimbIndex;
            _climbs = climbs;
            TrainingClimbs.Clear();
            foreach (var climb in climbs)
                TrainingClimbs.Add(string.Format(Strings.Inspector_ClimbItem, climb.ClimbNumber,
                    WatchClock(climb.StartUtc, workout!), WatchClock(climb.End, workout!)));
            SelectedClimbIndex = selected < climbs.Count ? selected : -1;
            _preselectedClimb = -1;
        }
        RecordingStartText = _trainingAsset is not null && RecordingZeroEstimator.Resolve(_trainingAsset).State == RecordingZeroState.Override
            ? Strings.Inspector_RecordingStartSet
            : Strings.Inspector_RecordingStartUnknown;
        AlignToClimbCommand.NotifyCanExecuteChanged();
        ClearRecordingStartCommand.NotifyCanExecuteChanged();
        RecognizeFitClimbsCommand.NotifyCanExecuteChanged();
        UpdateTrainingReadout();
        PreselectClimb();
        RefreshFitMarks();
    }

    private void UpdateTrainingReadout()
    {
        if (!ShowsTraining || _clip is not VideoClip clip || _trainingAsset is null || Playhead is null)
        {
            TrainingReadout = "";
            return;
        }
        var frame = Playhead().ToFrameFloor(_rate);
        var readout = TrainingAlignment.ReadoutAt(_training!.Training, clip, _trainingAsset, frame, _rate);
        var workout = _training.Training?.Workout;
        TrainingReadout = readout switch
        {
            { Bpm: { } bpm, WatchTime: { } w } => string.Format(Strings.Readout_Value, WatchClock(w, workout!), bpm),
            { Reason: TrainingReadoutReason.OutsideRecording, WatchTime: { } w } =>
                string.Format(Strings.Readout_NoValue, string.Format(Strings.Readout_OutsideRecording, WatchClock(w, workout!))),
            { Reason: TrainingReadoutReason.NoSample, WatchTime: { } w } =>
                string.Format(Strings.Readout_NoValue, string.Format(Strings.Readout_NoSample, WatchClock(w, workout!))),
            { Reason: TrainingReadoutReason.TrainingNotAvailable } => string.Format(Strings.Readout_NoValue, Strings.Readout_TrainingNotAvailable),
            { Reason: TrainingReadoutReason.NoFileTime } => string.Format(Strings.Readout_NoValue, Strings.Readout_NoFileTime),
            { Reason: TrainingReadoutReason.FileTimesDisagree } => string.Format(Strings.Readout_NoValue, Strings.Readout_FileTimesDisagree),
            { Reason: TrainingReadoutReason.OutsideClip } => string.Format(Strings.Readout_NoValue, Strings.Readout_OutsideClip),
            _ => string.Format(Strings.Readout_NoValue, Strings.Readout_NoTraining)
        };
    }

    private static string WatchClock(DateTimeOffset w, ClimbingWorkout workout) => TrainingText.WatchClock(w, workout.UtcOffset);

    /// <summary>Matches the playhead's frame to the chosen climb. D056 §1: when the playhead is in the area of another climb
    /// or the video's time shows another climb there, it asks first — a <c>Z</c> by the wrong climb binds that climb to the
    /// video too (D054 §6) and adds an area for it.</summary>
    [RelayCommand(CanExecute = nameof(CanAlignToClimb))]
    private async Task AlignToClimb()
    {
        if (!CanAlignToClimb() || RejectWhileLocked()) return;
        var clip = (VideoClip)_clip!;
        var frame = Playhead!().ToFrameFloor(_rate);
        if (!TrainingAlignment.Covers(clip, frame, _rate))
        {
            _status.Report(Strings.Inspector_AlignPlayheadOutside);
            return;
        }
        var chosen = SelectedClimbIndex;
        var climb = _climbs[chosen];
        if (ClimbAtPlayhead() is { } at && at.Index != chosen && _dialogs is not null)
        {
            var shown = _climbs[at.Index].ClimbNumber;
            var message = string.Format(at.ByArea ? Strings.Inspector_AlignMismatchArea : Strings.Inspector_AlignMismatchTime, shown, climb.ClimbNumber);
            var answer = await _dialogs.AskAsync(new DialogRequest
            {
                Title = Strings.Inspector_AlignMismatchTitle, Message = message,
                Buttons = new[] { string.Format(Strings.Inspector_AlignMismatchConfirm, climb.ClimbNumber), Strings.Common_Cancel }
            });
            if (answer != 0) return;
            // The clip, the playhead or the training may have changed while asking: match only what was asked about.
            if (!ReferenceEquals(_clip, clip) || !CanAlignToClimb() || Playhead!().ToFrameFloor(_rate) != frame ||
                chosen >= _climbs.Count || _climbs[chosen] != climb) return;
        }
        var training = _training!.Training!;
        var anchor = AlignAnchor;
        var zero = TrainingAlignment.ZeroForClimb(climb, anchor, training.ClockCorrection, TrainingAlignment.SourceTimeAt(clip, frame, _rate));
        var result = _edit.SetRecordingZeroOverride(_trainingAsset!.Id, zero);
        var done = anchor == ClimbAnchor.End ? Strings.Inspector_AlignedToClimbEnd : Strings.Inspector_AlignedToClimb;
        _status.Report(result.Success ? string.Format(done, climb.ClimbNumber) : result.Message ?? "");
    }

    private bool CanAlignToClimb() =>
        IsEditingAllowed && ShowsTraining && Playhead is not null && _training?.Training is { IsUsable: true }
        && SelectedClimbIndex >= 0 && SelectedClimbIndex < _climbs.Count;

    [RelayCommand(CanExecute = nameof(CanClearRecordingStart))]
    private void ClearRecordingStart()
    {
        if (!CanClearRecordingStart() || RejectWhileLocked()) return;
        var result = _edit.SetRecordingZeroOverride(_trainingAsset!.Id, null);
        if (!result.Success && result.Message is { } message) _status.Report(message);
    }

    private bool CanClearRecordingStart() => IsEditingAllowed && ShowsTraining && _trainingAsset?.RecordingZeroOverrideUtc is not null;

    /// <summary>«Распознать прохождения из FIT» (D054 §1): the climbs this clip shows get their green route areas — by its
    /// video's time, no choice of a climb or a route; one Undo step, the status bar says what was found.</summary>
    [RelayCommand(CanExecute = nameof(CanRecognizeFitClimbs))]
    private void RecognizeFitClimbs()
    {
        if (!CanRecognizeFitClimbs() || RejectWhileLocked()) return;
        var result = FitEdit!.CreateRoutesFromFit(_clip!.Id);
        if (result.Message is { } message) _status.Report(message);
    }

    private bool CanRecognizeFitClimbs() =>
        IsEditingAllowed && ShowsTraining && FitEdit is not null && _training?.Training is { IsUsable: true };
}
