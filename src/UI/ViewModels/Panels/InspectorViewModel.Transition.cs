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
// seconds of the stored whole frames with their count; the arrows step on the 0.1 s grid — and Remove. A type or a
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

    /// <summary>The seconds the field shows (the stored frames') — an arrow step is recognised against it.</summary>
    private decimal _shownSeconds;

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
        _shownSeconds = TransitionNames.SecondsOf(frames, rate);
        TransitionSeconds = _shownSeconds;
        TransitionDurationDisplay = TransitionNames.Duration(frames, rate);
        TransitionLimitDisplay = string.Format(Strings.Transition_Longest, TransitionNames.Duration(longest, rate));
    }

    /// <summary>
    /// A duration typed or dragged (seconds) becomes the nearest whole frame; an arrow step (±0.1 s from the shown value)
    /// goes along the 0.1 s grid — the shown value rounded to 0.1 s, ± 0.1 s — so a step never sticks on a frame that
    /// rounds back (D044 §5).
    /// </summary>
    partial void OnTransitionSecondsChanged(decimal? value)
    {
        if (_syncing || value is not { } seconds || _transition is not { } s || RejectWhileLocked()) return;
        var step = seconds - _shownSeconds;
        if (step is 0.1m or -0.1m) seconds = Math.Round(_shownSeconds, 1, MidpointRounding.AwayFromZero) + step;
        var frames = (long)Math.Round(seconds * s.Rate.Numerator / s.Rate.Denominator, MidpointRounding.AwayFromZero);

        var result = _edit.SetTransitionDuration(s.Transition.Id, MediaTime.FromFrame(Math.Max(0, frames), s.Rate));
        if (!result.Success && !result.NoChange)
        {
            _status.Report(result.Message ?? Strings.Transition_NotChanged);
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
        if (typeId is null || _transition is not { } s || RejectWhileLocked()) return;
        var before = TransitionRules.Frames(s.Transition.Duration, s.Rate);
        var result = _edit.SetTransitionType(s.Transition.Id, typeId);
        if (result.NoChange) return;
        if (!result.Success)
        {
            _status.Report(result.Message ?? Strings.Transition_NotChanged);
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
        if (_transition is not { } s || RejectWhileLocked()) return;
        var result = _edit.SetTransitionDirection(s.Transition.Id, direction);
        if (!result.Success && !result.NoChange) _status.Report(result.Message ?? Strings.Transition_NotChanged);
    }
}
