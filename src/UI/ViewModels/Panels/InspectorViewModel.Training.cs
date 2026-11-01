using System.Collections.ObjectModel;
using System.Globalization;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// The COROS training of a video clip (Phase 35, D049 §5–§6, D050 amendment B2 / B4): the state of this video's recording
/// start Z (set / unknown — the automatic state comes with Step 35.3's rule), «Совместить кадр с началом подъёма N» —
/// the playhead on the frame where climb N starts sets <c>Z = W_split − ClockCorrection − s(n)</c> for this video only,
/// one Undo step — and a read-only readout of the mapping at the playhead. The training's clock correction is not here.
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

    /// <summary>The playhead moved: the readout follows it.</summary>
    public void OnPlayheadMoved() => UpdateTrainingReadout();

    private void OnTrainingChanged(object? sender, EventArgs e) => RefreshTraining();

    private void ShowTraining(MediaAsset? asset)
    {
        _trainingAsset = asset;
        RefreshTraining();
    }

    private void HideTraining()
    {
        _trainingAsset = null;
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
                TrainingClimbs.Add(string.Format(Strings.Inspector_ClimbItem, climb.ClimbNumber, WatchClock(climb.StartUtc, workout!)));
            SelectedClimbIndex = selected < climbs.Count ? selected : -1;
        }
        RecordingStartText = _trainingAsset?.RecordingZeroOverrideUtc is not null
            ? Strings.Inspector_RecordingStartSet
            : Strings.Inspector_RecordingStartUnknown;
        AlignToClimbCommand.NotifyCanExecuteChanged();
        ClearRecordingStartCommand.NotifyCanExecuteChanged();
        UpdateTrainingReadout();
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
            { Reason: TrainingReadoutReason.RecordingStartUnknown } => string.Format(Strings.Readout_NoValue, Strings.Readout_RecordingStartUnknown),
            { Reason: TrainingReadoutReason.OutsideClip } => string.Format(Strings.Readout_NoValue, Strings.Readout_OutsideClip),
            _ => string.Format(Strings.Readout_NoValue, Strings.Readout_NoTraining)
        };
    }

    /// <summary>A watch instant in the watch's local time (D049 §1: W → watch local, display only).</summary>
    private static string WatchClock(DateTimeOffset w, ClimbingWorkout workout) =>
        w.ToOffset(workout.UtcOffset ?? TimeSpan.Zero).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    [RelayCommand(CanExecute = nameof(CanAlignToClimb))]
    private void AlignToClimb()
    {
        if (!CanAlignToClimb() || RejectWhileLocked()) return;
        var clip = (VideoClip)_clip!;
        var training = _training!.Training!;
        var frame = Playhead!().ToFrameFloor(_rate);
        if (!TrainingAlignment.Covers(clip, frame, _rate))
        {
            _status.Report(Strings.Inspector_AlignPlayheadOutside);
            return;
        }
        var climb = _climbs[SelectedClimbIndex];
        var zero = TrainingAlignment.ZeroForSplit(climb.StartUtc, training.ClockCorrection, TrainingAlignment.SourceTimeAt(clip, frame, _rate));
        var result = _edit.SetRecordingZeroOverride(_trainingAsset!.Id, zero);
        _status.Report(result.Success ? string.Format(Strings.Inspector_AlignedToClimb, climb.ClimbNumber) : result.Message ?? "");
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
}
