using System.Collections.ObjectModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>One of the library's types in the Inspector's TRANSITION section (D044 §6): disabled with the reason when
/// it cannot fit the cut.</summary>
public sealed partial class TransitionTypeOption(string typeId) : ViewModelBase
{
    public string TypeId { get; } = typeId;
    public string Name => TransitionNames.Name(TypeId);
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string? _tip;
}

/// <summary>A push's direction in the TRANSITION section — the side B comes from.</summary>
public sealed partial class TransitionDirectionOption(TransitionDirection direction) : ViewModelBase
{
    public TransitionDirection Direction { get; } = direction;
    public string Name => TransitionNames.Name(Direction);
    [ObservableProperty] private bool _isSelected;
}

// The TRANSITION section of a video transition (D044 §5–§6): the type (five buttons; one that cannot fit is disabled
// with the reason), a push's direction (four buttons), the duration in seconds — a field and a slider, shown as the
// seconds of the stored whole frames with their count; the arrows step on the 0.1 s grid (StepTransitionSeconds), a typed
// value goes to the nearest frame — and Remove. A type or a
// direction is one undo step, durations merge; every rule is the edit service's, its message shown when it refuses.
// The audio crossfade keeps the frames field of the section above (D032).
public sealed partial class InspectorViewModel
{
    public ObservableCollection<TransitionTypeOption> TransitionTypes { get; } =
        new(TransitionRules.VideoTypes.Select(t => new TransitionTypeOption(t)));

    public ObservableCollection<TransitionDirectionOption> TransitionDirections { get; } =
        new(new[] { TransitionDirection.FromRight, TransitionDirection.FromLeft, TransitionDirection.FromTop, TransitionDirection.FromBottom }
            .Select(d => new TransitionDirectionOption(d)));

    [ObservableProperty] private decimal? _transitionSeconds;
    [ObservableProperty] private decimal _minTransitionSeconds = 0.1m;
    [ObservableProperty] private decimal _maxTransitionSeconds = 1m;
    [ObservableProperty] private string _transitionDurationDisplay = "";
    [ObservableProperty] private string _transitionLimitDisplay = "";
    [ObservableProperty] private bool _isPushSelected;

    public bool IsVideoTransitionSelected => IsTransitionSelected && _transition?.Track.Type == TrackType.Video;
    public bool IsAudioTransitionSelected => IsTransitionSelected && _transition?.Track.Type == TrackType.Audio;

    private void SyncLibrary(TimelineTransitionSelection s)
    {
        var rate = s.Rate;
        var t = s.Transition;
        var typeId = t.TransitionTypeId;
        var frames = TransitionRules.Frames(t.Duration, rate);

        foreach (var option in TransitionTypes)
        {
            option.IsSelected = option.TypeId == typeId;
            var fits = _edit.MaxTransitionFrames(t.LeftClipId, t.RightClipId, option.TypeId) ?? 0;
            option.IsAvailable = option.IsSelected || fits >= TransitionRules.MinFramesOf(option.TypeId, rate);
            option.Tip = option.IsAvailable ? null
                : TransitionRules.RequiresHandles(option.TypeId) ? Messages.Transition_NotEnoughMediaType : Messages.TransitionRule_ClipTooShort;
        }
        IsPushSelected = TransitionRules.HasDirection(typeId);
        foreach (var option in TransitionDirections) option.IsSelected = IsPushSelected && option.Direction == t.Direction;

        var longest = _edit.MaxTransitionFrames(t.LeftClipId, t.RightClipId, typeId) ?? frames;
        // A stored value outside the range is shown as it is and kept (D044 §5): the field's range includes it.
        MinTransitionSeconds = TransitionNames.SecondsOf(Math.Min(frames, TransitionRules.MinFramesOf(typeId, rate)), rate);
        MaxTransitionSeconds = TransitionNames.SecondsOf(Math.Max(frames, longest), rate);
        TransitionSeconds = TransitionNames.SecondsOf(frames, rate);
        TransitionDurationDisplay = TransitionNames.Duration(frames, rate);
        // A longest shorter than the stored duration (the source unknown or shorter since) is not shown as a limit.
        TransitionLimitDisplay = longest < frames ? Strings.Transition_NoLonger
            : string.Format(Strings.Transition_Longest, TransitionNames.Duration(longest, rate));
    }

    /// <summary>A duration typed or dragged (seconds) becomes the nearest whole frame (D044 §5) — whatever its distance from
    /// the shown value: only the field's arrows step (<see cref="StepTransitionSecondsCommand"/>).</summary>
    partial void OnTransitionSecondsChanged(decimal? value)
    {
        if (_syncing || value is not { } seconds || _transition is not { } s || RejectWhileLocked()) return;
        ApplyTransitionFrames(s, TransitionNames.FramesOf(seconds, s.Rate));
    }

    /// <summary>
    /// An arrow of the seconds field (+1 up, −1 down) on the 0.1 s grid (D044 §5): the next mark of 0.1 s above (below) the
    /// stored duration whose nearest whole frame is longer (shorter) than the stored frames — so a step never sticks on a
    /// mark that rounds back to the same frame (0.3 s at 24 fps is 7 frames, as 0.29 s) — kept within the type's range and
    /// the longest that fits, as the field's limits; nothing beyond them.
    /// </summary>
    [RelayCommand]
    private void StepTransitionSeconds(int direction)
    {
        if (_transition is not { } s || RejectWhileLocked() || direction == 0) return;
        var rate = s.Rate;
        var typeId = s.Transition.TransitionTypeId;
        var frames = TransitionRules.Frames(s.Transition.Duration, rate);
        var lowest = Math.Min(frames, TransitionRules.MinFramesOf(typeId, rate));
        var highest = Math.Max(frames, Math.Min(TransitionRules.MaxFramesOf(typeId, rate),
            _edit.MaxTransitionFrames(s.Transition.LeftClipId, s.Transition.RightClipId, typeId) ?? frames));
        if (direction > 0 ? frames >= highest : frames <= lowest) return;
        ApplyTransitionFrames(s, Math.Clamp(TransitionNames.GridStep(frames, direction, rate), lowest, highest));
    }

    private void ApplyTransitionFrames(TimelineTransitionSelection s, long frames)
    {
        var result = _edit.SetTransitionDuration(s.Transition.Id, MediaTime.FromFrame(Math.Max(0, frames), s.Rate));
        if (!result.Success && !result.NoChange)
        {
            _status.Report(result.Message ?? Strings.Transition_NotChangedAny);
            ShowModelAfterRejection();
        }
        else if (result.NoChange)
        {
            ShowModelAfterRejection();      // the field shows the stored seconds again
        }
    }

    [RelayCommand]
    private void SetTransitionType(string? typeId)
    {
        if (typeId is null || _transition is not { } s) return;
        if (RejectWhileLocked())
        {
            SyncTransition();
            return;
        }
        var before = TransitionRules.Frames(s.Transition.Duration, s.Rate);
        var result = _edit.SetTransitionType(s.Transition.Id, typeId);
        if (result.NoChange || !result.Success)
        {
            // The button pressed has checked itself (D8 of the Phase 28 run): the buttons show the model's type again.
            if (!result.NoChange) _status.Report(result.Message ?? Strings.Transition_NotChangedAny);
            SyncTransition();
            return;
        }
        var after = TransitionRules.Frames(s.Transition.Duration, s.Rate);
        _status.Report(after == before
            ? string.Format(Strings.Transition_TypeChanged, TransitionNames.Name(typeId))
            : string.Format(Strings.Transition_TypeChangedClamped, TransitionNames.Name(typeId), TransitionNames.Duration(after, s.Rate)));
    }

    [RelayCommand]
    private void SetTransitionDirection(TransitionDirection direction)
    {
        if (_transition is not { } s) return;
        if (RejectWhileLocked())
        {
            SyncTransition();
            return;
        }
        var result = _edit.SetTransitionDirection(s.Transition.Id, direction);
        if (!result.Success && !result.NoChange) _status.Report(result.Message ?? Strings.Transition_NotChangedAny);
        if (!result.Success || result.NoChange) SyncTransition();   // the buttons show the model's direction again
    }
}
