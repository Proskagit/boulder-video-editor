using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiVideoEditor.Core.Composition;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// D063 §2 in the Inspector: the keyframes of a video clip at the playhead — whose they are (the clip's transform, or the
/// followed part's framing there), how many, the button that adds or removes the one on the playhead's frame, the previous /
/// next keyframe, and the easing of the keyframe on the frame (its segment to the next). The Position X / Y and Scale fields
/// (and a part's climber offset) show the values on the playhead's frame and, with keyframes, edit the keyframe on that frame
/// (or add one there).
/// </summary>
public sealed partial class InspectorViewModel
{
    private TransformKeyframeEditor? _keyframeEditor;
    private TransformKeyframeEditor KeyframeEditor => _keyframeEditor ??= new TransformKeyframeEditor(_edit, () => _rate);

    /// <summary>Moves the playhead to the previous (-1) / next (+1) keyframe of the clip; set by the main window.</summary>
    public Func<int, bool>? GoToKeyframe { get; set; }

    [ObservableProperty] private bool _hasKeyframeSection;
    [ObservableProperty] private string _keyframeOwnerText = "";
    [ObservableProperty] private string _keyframeButtonText = Strings.Keyframe_Add;

    /// <summary>A keyframe of the owner lies on the playhead's frame (its easing can be chosen).</summary>
    [ObservableProperty] private bool _isOnKeyframe;

    [ObservableProperty] private int _keyframeEasingIndex;

    public IReadOnlyList<string> KeyframeEasings { get; } = new[]
    {
        TimelineViewModel.EasingName(KeyframeEasing.Linear), TimelineViewModel.EasingName(KeyframeEasing.EaseIn),
        TimelineViewModel.EasingName(KeyframeEasing.EaseOut), TimelineViewModel.EasingName(KeyframeEasing.EaseInOut)
    };

    /// <summary>The playhead's frame clamped to the clip (the values a field shows when the playhead is beyond it).</summary>
    private long KeyframeFrame(VideoClip clip)
    {
        var (first, last) = KeyframeEditor.FramesOf(clip);
        var frame = Playhead is { } playhead ? playhead().ToFrameFloor(_rate) : first;
        return Math.Clamp(frame, first, last);
    }

    private bool PlayheadOnClip(VideoClip clip)
    {
        if (Playhead is null) return false;
        var frame = Playhead().ToFrameFloor(_rate);
        var (first, last) = KeyframeEditor.FramesOf(clip);
        return frame >= first && frame <= last;
    }

    /// <summary>The keyframe section from the model (the clip, the timeline, the playhead changed).</summary>
    private void RefreshKeyframes()
    {
        ShowVolumeAtPlayhead();   // D064 amendment 1: with points, the volume field shows the playhead's value
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            if (_clip is not VideoClip clip)
            {
                HasKeyframeSection = false;
                IsOnKeyframe = false;
                return;
            }
            HasKeyframeSection = true;
            var frame = KeyframeFrame(clip);
            var owner = KeyframeEditor.OwnerAt(clip, frame);
            var count = TransformKeyframeEditor.KeysOf(clip, owner)?.Count ?? 0;
            KeyframeOwnerText = owner.FollowPart is { } part
                ? string.Format(Strings.Keyframe_OwnerFollow, part + 1, count)
                : string.Format(Strings.Keyframe_OwnerClip, count);
            var key = PlayheadOnClip(clip) ? KeyframeEditor.KeyOnFrame(clip, owner, frame) : null;
            IsOnKeyframe = key is not null;
            KeyframeButtonText = key is null ? Strings.Keyframe_Add : Strings.Keyframe_Remove;
            KeyframeEasingIndex = (int)(key?.Easing ?? KeyframeEasing.Linear);
            ToggleKeyframeHereCommand.NotifyCanExecuteChanged();

            // The values on the playhead's frame (the keyframes' interpolation, as the Preview draws them).
            if (clip.Keyframes is not null)
            {
                var value = KeyframeEditor.ValueAt(clip, new KeyframeOwner(clip.Id, null), frame);
                PositionX = (decimal)value.X;
                PositionY = (decimal)value.Y;
                ScalePercent = (decimal)value.Scale * 100m;
            }
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    private bool CanToggleKeyframeHere() => _clip is VideoClip clip && PlayheadOnClip(clip);

    [RelayCommand(CanExecute = nameof(CanToggleKeyframeHere))]
    private void ToggleKeyframeHere()
    {
        if (_clip is not VideoClip clip || RejectWhileLocked() || !PlayheadOnClip(clip)) return;
        var (result, added) = KeyframeEditor.Toggle(clip, KeyframeFrame(clip));
        if (!result.Success) _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
        else _status.Report(added is not null ? Strings.Keyframe_Added : Strings.Keyframe_Removed);
    }

    [RelayCommand] private void PreviousKeyframe() => GoToKeyframe?.Invoke(-1);
    [RelayCommand] private void NextKeyframe() => GoToKeyframe?.Invoke(1);

    partial void OnKeyframeEasingIndexChanged(int value)
    {
        if (_syncing || _clip is not VideoClip clip || value < 0 || value > 3 || RejectWhileLocked()) return;
        var frame = KeyframeFrame(clip);
        var owner = KeyframeEditor.OwnerAt(clip, frame);
        if (KeyframeEditor.KeyOnFrame(clip, owner, frame) is not { } key) return;
        var result = KeyframeEditor.SetEasing(clip, owner, key.Id, (KeyframeEasing)value);
        if (!result.Success)
        {
            _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
            ShowModelAfterRejection();
        }
    }

    /// <summary>D063 §2: a Position X / Y or Scale edit of a clip with keyframes — the keyframe on the playhead's frame (or a
    /// new one there); false when the clip has none (the static values, as before).</summary>
    private bool EditKeyframedVisual(VisualProperties current, VisualProperties changed)
    {
        if (_clip is not VideoClip { Keyframes: not null } clip) return false;
        if (changed.RotationDegrees != current.RotationDegrees || changed.Opacity != current.Opacity || changed.Crop != current.Crop) return false;
        var frame = KeyframeFrame(clip);
        var owner = new KeyframeOwner(clip.Id, null);
        var shown = KeyframeEditor.ValueAt(clip, owner, frame);
        var value = new TransformValue(
            changed.PositionX != current.PositionX ? changed.PositionX : shown.X,
            changed.PositionY != current.PositionY ? changed.PositionY : shown.Y,
            changed.Scale != current.Scale ? changed.Scale : shown.Scale);
        var result = KeyframeEditor.SetValue(clip, owner, frame, value);
        if (!result.Success)
        {
            _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
            ShowModelAfterRejection();
        }
        return true;
    }

    /// <summary>The visual properties as the fields show them (the values on the playhead's frame for a keyframed clip).</summary>
    private VisualProperties? ShownVisual()
    {
        if (_clip is null || VisualProperties.Of(_clip) is not { } visual) return null;
        if (_clip is not VideoClip { Keyframes: not null } clip) return visual;
        var value = KeyframeEditor.ValueAt(clip, new KeyframeOwner(clip.Id, null), KeyframeFrame(clip));
        return visual with { PositionX = value.X, PositionY = value.Y, Scale = value.Scale };
    }

    /// <summary>The playhead's source time in the clip for Fill / Fit (its keyframe there), or null.</summary>
    private Core.Common.MediaTime? KeyframeTimeForFraming(Clip clip) =>
        Playhead is { } playhead ? KeyframeEditor.FramingTimeAt(clip, playhead().ToFrameFloor(_rate)) : null;
}
