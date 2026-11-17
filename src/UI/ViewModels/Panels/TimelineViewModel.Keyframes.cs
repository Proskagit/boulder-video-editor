using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiVideoEditor.Core.Composition;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>D063 §2: one keyframe on a clip in the timeline — a diamond at <see cref="Left"/> pixels from the clip's left edge
/// (its frame's start), on the clip (<see cref="IsFollow"/> false: the clip's transform) or on the followed part's strip.</summary>
public sealed record KeyframeMarker(Guid Id, int? FollowPart, double Left, double Top, bool IsSelected, string Tip)
{
    public bool IsFollow => FollowPart is not null;
    public string Fill => IsSelected ? "#FFFFFF" : IsFollow ? TimelineViewModel.FollowHex : "#FFC94D";
}

/// <summary>
/// D063 §1–§2 on the timeline: the keyframe button (add / remove at the playhead for the owner placing the picture there —
/// the followed part or the clip), the diamonds on the clips (the clip's keyframes on the clip, a part's on its cyan strip),
/// a click on a diamond selects it and puts the playhead on its frame, a drag moves it along the frames (the release one
/// undo step; Esc, a lost pointer or the editing lock cancel), Delete removes the selected keyframe, and with Snap on the
/// playhead clicked or scrubbed near a diamond of the selected clip lands on it.
/// </summary>
public sealed partial class TimelineViewModel
{
    /// <summary>The diamond's half size in pixels (its hit reach is a little wider).</summary>
    public const double KeyframeHalfPixels = 5;

    private TransformKeyframeEditor? _keyframes;
    private TransformKeyframeEditor Keyframes => _keyframes ??= new TransformKeyframeEditor(_edit, _projectService);

    /// <summary>The selected keyframe (its owner and id), or null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private (KeyframeOwner Owner, Guid Id)? _selectedKeyframe;

    partial void OnSelectedKeyframeChanged((KeyframeOwner Owner, Guid Id)? value)
    {
        foreach (var vm in _clipViewModels.Values) ShowKeyframes(vm);
        KeyframeSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The selected keyframe changed (the Inspector shows it).</summary>
    public event EventHandler? KeyframeSelectionChanged;

    /// <summary>The drag of a diamond: the clip, the marker, the press's x and the marker's place then.</summary>
    private (TimelineClipViewModel Clip, KeyframeMarker Marker, double PressX, double Left)? _keyframeDrag;

    public bool IsKeyframeDragging => _keyframeDrag is not null;

    /// <summary>The primary selected video clip under the playhead, or null.</summary>
    private VideoClip? KeyframeClip()
    {
        if (_selection.Count == 0 || !_clipViewModels.TryGetValue(_selection[^1], out var vm) || vm.Clip is not VideoClip clip) return null;
        var frame = EditPlayhead.ToFrameFloor(FrameRate);
        var (first, last) = Keyframes.FramesOf(clip);
        return frame >= first && frame <= last ? clip : null;
    }

    private bool CanToggleKeyframe() => CanEdit() && (KeyframeClip() is not null || AudioKeyframeClip() is not null);

    /// <summary>The keyframe button: adds a keyframe on the playhead's frame (or removes the one there).</summary>
    [RelayCommand(CanExecute = nameof(CanToggleKeyframe))]
    private void ToggleKeyframe()
    {
        if (KeyframeClip() is null && AudioKeyframeClip() is { } audio)
        {
            ToggleAudioKeyframe(audio);   // D064 amendment 1: an audio clip's keyframes are its volume points
            return;
        }
        if (KeyframeClip() is not { } clip)
        {
            _status.Report(_selection.Count == 0 ? Core.Localization.Messages.Keyframe_NoClip : Core.Localization.Messages.Keyframe_NotUnderPlayhead);
            return;
        }
        var frame = EditPlayhead.ToFrameFloor(FrameRate);
        var owner = Keyframes.OwnerAt(clip, frame);
        var (result, added) = Keyframes.Toggle(clip, frame);
        Report(result, successMessage: added is not null ? (owner.IsFollow ? Strings.Keyframe_AddedFollow : Strings.Keyframe_Added) : Strings.Keyframe_Removed);
        SelectedKeyframe = added is { } id && result.Success ? (owner, id) : null;
    }

    private void NotifyKeyframeCommands() => ToggleKeyframeCommand.NotifyCanExecuteChanged();

    /// <summary>The diamonds of every clip at the current zoom.</summary>
    private void RefreshKeyframeMarkers()
    {
        if (SelectedKeyframe is { } selected && Keyframes.Clip(selected.Owner.ClipId) is { } clip &&
            TransformKeyframeEditor.KeysOf(clip, selected.Owner)?.Find(selected.Id) is null)
            SelectedKeyframe = null;   // removed by an undo, a delete or a Follow change
        else if (SelectedKeyframe is { } audioSelected && AudioClipById(audioSelected.Owner.ClipId) is { } audioClip &&
                 audioClip.Volumes?.Find(audioSelected.Id) is null)
            SelectedKeyframe = null;
        foreach (var vm in _clipViewModels.Values)
            if (_keyframeDrag?.Clip != vm) ShowKeyframes(vm);
        NotifyKeyframeCommands();
    }

    private void ShowKeyframes(TimelineClipViewModel vm)
    {
        if (vm.Clip is AudioClip audioClip)
        {
            var points = AudioKeyframeMarkers(audioClip);
            if (points.Count > 0 || vm.KeyframeMarkers.Count > 0) vm.KeyframeMarkers = points;
            return;
        }
        if (vm.Clip is not VideoClip clip || (clip.Keyframes is null && clip.Follow?.Parts.All(p => p.Keyframes is null) != false))
        {
            if (vm.KeyframeMarkers.Count > 0) vm.KeyframeMarkers = Array.Empty<KeyframeMarker>();
            return;
        }
        var markers = new List<KeyframeMarker>();
        var start = clip.TimelineStart.ToFrameFloor(FrameRate);
        foreach (var shown in Keyframes.AllShown(clip))
        {
            var left = TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(shown.Frame - start, FrameRate), PixelsPerSecond);
            var selected = SelectedKeyframe is { } s && s.Id == shown.Key.Id && s.Owner == shown.Owner;
            var tip = string.Format(shown.Owner.IsFollow ? Strings.Keyframe_TipFollow : Strings.Keyframe_Tip,
                TimeFormat.ToTimecode(MediaTime.FromFrame(shown.Frame, FrameRate), FrameRate), Num(shown.Key.X), Num(shown.Key.Y),
                Num(shown.Key.Scale * 100), EasingName(shown.Key.Easing));
            markers.Add(new KeyframeMarker(shown.Key.Id, shown.Owner.FollowPart, left, shown.Owner.IsFollow ? 30 : 2, selected, tip));
        }
        vm.KeyframeMarkers = markers;
    }

    private static string Num(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public static string EasingName(KeyframeEasing easing) => easing switch
    {
        KeyframeEasing.EaseIn => Strings.Easing_In,
        KeyframeEasing.EaseOut => Strings.Easing_Out,
        KeyframeEasing.EaseInOut => Strings.Easing_InOut,
        _ => Strings.Easing_Linear
    };

    /// <summary>A press on a diamond: the clip becomes the selection, the keyframe is selected and the playhead goes to its
    /// frame; the drag may start.</summary>
    public bool BeginKeyframeDrag(TimelineClipViewModel vm, KeyframeMarker marker, double contentX)
    {
        if (vm.Clip is AudioClip audio)
        {
            if (_selection.Count == 0 || _selection[^1] != vm.Id) SetSelection(new[] { vm });
            SelectedKeyframe = (new KeyframeOwner(audio.Id, null), marker.Id);
            if (audio.Volumes?.Find(marker.Id) is { } point) SetPlayhead(MediaTime.FromFrame(FrameOfSource(audio, point.Time), FrameRate));
            if (!CanEdit() || TrackOf(vm) is { IsLocked: true }) return true;
            _keyframeDrag = (vm, marker with { IsSelected = true }, contentX, marker.Left);
            return true;
        }
        if (vm.Clip is not VideoClip clip) return false;
        if (_selection.Count == 0 || _selection[^1] != vm.Id) SetSelection(new[] { vm });
        var owner = new KeyframeOwner(clip.Id, marker.FollowPart);
        SelectedKeyframe = (owner, marker.Id);
        if (TransformKeyframeEditor.KeysOf(clip, owner)?.Find(marker.Id) is { } key)
            SetPlayhead(MediaTime.FromFrame(Keyframes.FrameOf(clip, key.Time), FrameRate));
        if (!CanEdit() || TrackOf(vm) is { IsLocked: true }) return true;
        _keyframeDrag = (vm, marker with { IsSelected = true }, contentX, marker.Left);
        return true;
    }

    public void UpdateKeyframeDrag(double contentX)
    {
        if (_keyframeDrag is not { } drag) return;
        var left = Math.Clamp(drag.Left + contentX - drag.PressX, 0, Math.Max(0, drag.Clip.Width - 1));
        var frame = FrameAt(drag.Clip, left);
        var x = TimelineCoordinateMapper.TimeToX(frame - drag.Clip.Clip.TimelineStart, PixelsPerSecond);
        drag.Clip.KeyframeMarkers = drag.Clip.KeyframeMarkers.Select(m => m.Id == drag.Marker.Id && m.FollowPart == drag.Marker.FollowPart
            ? m with { Left = x } : m).ToList();
        drag.Clip.StripText = string.Format(Strings.Keyframe_Moving, TimeFormat.ToTimecode(frame, FrameRate));
        drag.Clip.IsStripInvalid = false;
    }

    /// <summary>The release: the keyframe moves to the frame under its diamond (one undo step); a click without a move edits
    /// nothing.</summary>
    public void EndKeyframeDrag()
    {
        if (_keyframeDrag is not { } drag) return;
        _keyframeDrag = null;
        drag.Clip.StripText = null;
        var shown = drag.Clip.KeyframeMarkers.FirstOrDefault(m => m.Id == drag.Marker.Id && m.FollowPart == drag.Marker.FollowPart);
        if (drag.Clip.Clip is VideoClip clip && shown is not null && Math.Abs(shown.Left - drag.Left) >= 0.5 && CanEdit())
        {
            var frame = FrameAt(drag.Clip, shown.Left).ToFrameFloor(FrameRate);
            var result = Keyframes.Move(clip, new KeyframeOwner(clip.Id, drag.Marker.FollowPart), drag.Marker.Id, frame);
            Report(result);
            if (result.Success && !result.NoChange) SetPlayhead(MediaTime.FromFrame(frame, FrameRate));
        }
        else if (drag.Clip.Clip is AudioClip audio && shown is not null && Math.Abs(shown.Left - drag.Left) >= 0.5 && CanEdit())
            MoveAudioKeyframe(audio, drag.Marker.Id, FrameAt(drag.Clip, shown.Left).ToFrameFloor(FrameRate));
        ShowKeyframes(drag.Clip);
    }

    public void CancelKeyframeDrag()
    {
        if (_keyframeDrag is not { } drag) return;
        _keyframeDrag = null;
        drag.Clip.StripText = null;
        ShowKeyframes(drag.Clip);
    }

    /// <summary>Delete with a keyframe selected: removes it (the clip stays).</summary>
    private bool DeleteSelectedKeyframe()
    {
        if (SelectedKeyframe is { } audioSelected && AudioClipById(audioSelected.Owner.ClipId) is { } audioClip)
        {
            if (DeleteAudioKeyframe(audioClip, audioSelected.Id)) return true;
            SelectedKeyframe = null;
            return false;
        }
        if (SelectedKeyframe is not { } selected || Keyframes.Clip(selected.Owner.ClipId) is not { } clip) return false;
        var result = Keyframes.Delete(clip, selected.Owner, selected.Id);
        Report(result, successMessage: Strings.Keyframe_Removed);
        if (result.Success) SelectedKeyframe = null;
        return true;
    }

    /// <summary>D063 §2: with Snap on, a playhead put within <see cref="SnapTolerancePixels"/> of a diamond of the selected
    /// clip lands on that keyframe's frame.</summary>
    private MediaTime SnapToKeyframe(MediaTime time)
    {
        if (!SnappingEnabled || _selection.Count == 0 || !_clipViewModels.TryGetValue(_selection[^1], out var vm) || vm.KeyframeMarkers.Count == 0)
            return time;
        var x = TimelineCoordinateMapper.TimeToX(time - vm.Clip.TimelineStart, PixelsPerSecond);
        var nearest = vm.KeyframeMarkers.OrderBy(m => Math.Abs(m.Left - x)).First();
        if (Math.Abs(nearest.Left - x) > SnapTolerancePixels) return time;
        return FrameAt(vm, nearest.Left);
    }

    /// <summary>The playhead moved off the selected keyframe's frame: the keyframe is no longer selected.</summary>
    private void DropKeyframeSelectionOffFrame()
    {
        if (SelectedKeyframe is not { } selected || _keyframeDrag is not null) return;
        if (AudioClipById(selected.Owner.ClipId) is { } audio)
        {
            if (audio.Volumes?.Find(selected.Id) is not { } point || FrameOfSource(audio, point.Time) != Playhead.ToFrameFloor(FrameRate))
                SelectedKeyframe = null;
            return;
        }
        if (Keyframes.Clip(selected.Owner.ClipId) is not { } clip || TransformKeyframeEditor.KeysOf(clip, selected.Owner)?.Find(selected.Id) is not { } key ||
            Keyframes.FrameOf(clip, key.Time) != Playhead.ToFrameFloor(FrameRate))
            SelectedKeyframe = null;
    }

    /// <summary>The playhead to the previous / next keyframe of the selected clip (any owner); false when there is none.</summary>
    public bool GoToKeyframe(int direction)
    {
        if (_selection.Count > 0 && _clipViewModels.TryGetValue(_selection[^1], out var audioVm) && audioVm.Clip is AudioClip audio)
        {
            var at = Playhead.ToFrameFloor(FrameRate);
            var frames = AudioKeyframeFrames(audio).ToList();
            var target = direction > 0 ? frames.Where(f => f.Frame > at).OrderBy(f => f.Frame).FirstOrDefault()
                : frames.Where(f => f.Frame < at).OrderByDescending(f => f.Frame).FirstOrDefault();
            if (target.Id == Guid.Empty) return false;
            SetPlayhead(MediaTime.FromFrame(target.Frame, FrameRate));
            SelectedKeyframe = (new KeyframeOwner(audio.Id, null), target.Id);
            return true;
        }
        if (_selection.Count == 0 || !_clipViewModels.TryGetValue(_selection[^1], out var vm) || vm.Clip is not VideoClip clip) return false;
        var now = Playhead.ToFrameFloor(FrameRate);
        var all = Keyframes.AllShown(clip);
        var next = direction > 0
            ? all.Where(k => k.Frame > now).OrderBy(k => k.Frame).FirstOrDefault()
            : all.Where(k => k.Frame < now).OrderByDescending(k => k.Frame).FirstOrDefault();
        if (next is null) return false;
        SetPlayhead(MediaTime.FromFrame(next.Frame, FrameRate));
        SelectedKeyframe = (next.Owner, next.Key.Id);
        return true;
    }
}
