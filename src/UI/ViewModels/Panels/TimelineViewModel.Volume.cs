using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// D064 §2 on the timeline: the volume line inside every clip with sound (the clip's volume × its points), on the selected clip
/// with its points and the two fade handles. A drag of the line changes the clip's volume (with points: the whole curve), a drag
/// of a point moves it in time (between its neighbours) and value, a drag of a fade handle sets the sound's fade in / out (whole
/// frames); a double click on the line adds a point (the line's value — nothing changes until it is dragged), on a point removes
/// it; Delete removes the selected point. Every drag is live (heard while dragging — the mix-only update) and one undo step; Esc,
/// a lost pointer or the editing lock put the values back.
/// </summary>
public sealed partial class TimelineViewModel
{
    /// <summary>The drag of a volume line, point or fade handle: the clip, what was pressed, the values at the press.</summary>
    private sealed record VolumeDrag(TimelineClipViewModel Clip, VolumeHit Hit, double PressX, double PressY, double PressGain,
        AudioProperties Sound, FadeProperties Fades, ClipPropertyGesture Gesture)
    {
        public bool Moved { get; set; }
    }

    private VolumeDrag? _volumeDrag;

    /// <summary>D064: the master and every track's mix (the tool bar's «Микшер», the tracks' headers).</summary>
    public MixerViewModel Mixer { get; }

    /// <summary>The selected volume point (its clip and id), or null.</summary>
    private (Guid ClipId, Guid PointId)? _selectedVolumePoint;

    public bool IsVolumeDragging => _volumeDrag is not null;

    /// <summary>The volume shape of every clip at the current zoom.</summary>
    private void RefreshVolumeShapes()
    {
        foreach (var track in Tracks)
            foreach (var vm in track.Clips)
                if (_volumeDrag?.Clip != vm) vm.VolumeShape = VolumeShapeOf(vm, track.Track);
    }

    private ClipVolumeShape? VolumeShapeOf(TimelineClipViewModel vm, Track track)
    {
        var clip = vm.Clip;
        if (AudioProperties.Of(clip) is not { } sound || clip is VideoClip { IsAudioDetached: true } || clip is not MediaBackedClip media)
            return null;
        if (clip is VideoClip && FindAsset(media.MediaAssetId)?.Metadata?.AudioCodec is null) return null;
        var (fadeIn, fadeOut) = SoundFadeWidths(clip, sound);
        var selected = _selectedVolumePoint is { } s && s.ClipId == clip.Id ? s.PointId : (Guid?)null;
        return new ClipVolumeShape(sound.Volume, sound.Volumes, media.SourceIn, (double)media.Speed.ToDecimal(), PixelsPerSecond,
            vm.Width, clip is VideoClip, fadeIn, fadeOut, sound.IsMuted || track.IsMuted, vm.IsSelected, selected);
    }

    /// <summary>The sound fades' widths in pixels: an audio clip's D025 fades, a video clip's sound-only fades, each clamped to the
    /// clip's frames.</summary>
    private (double In, double Out) SoundFadeWidths(Clip clip, AudioProperties sound)
    {
        var (inTime, outTime) = clip is VideoClip ? (sound.SoundFadeIn, sound.SoundFadeOut) : (clip.FadeIn, clip.FadeOut);
        var frames = clip.TimelineEnd.ToFrameFloor(FrameRate) - clip.TimelineStart.ToFrameFloor(FrameRate);
        var fadeIn = Math.Clamp(inTime.ToNearestFrame(FrameRate), 0, frames);
        var fadeOut = Math.Clamp(outTime.ToNearestFrame(FrameRate), 0, frames);
        return (TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(fadeIn, FrameRate), PixelsPerSecond),
            TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(fadeOut, FrameRate), PixelsPerSecond));
    }

    /// <summary>What a press or hover at clip-local (<paramref name="x"/>, <paramref name="y"/>) of a clip
    /// <paramref name="height"/> high is on.</summary>
    public VolumeHit VolumeHitAt(TimelineClipViewModel vm, double x, double y, double height) =>
        vm.VolumeShape is { } shape && CanEdit() ? shape.HitTest(x, y, height) : default;

    /// <summary>A press on the line, a point or a fade handle of the selected clip: the drag starts (a point gets selected).</summary>
    public bool BeginVolumeDrag(TimelineClipViewModel vm, double x, double y, double height)
    {
        var hit = VolumeHitAt(vm, x, y, height);
        if (hit.Kind == VolumeHitKind.None || AudioProperties.Of(vm.Clip) is not { } sound) return false;
        if (TrackOf(vm) is { IsLocked: true }) return false;
        SelectVolumePoint(hit.Kind == VolumeHitKind.Point ? (vm.Clip.Id, hit.PointId!.Value) : null);
        var shape = vm.VolumeShape!;
        _volumeDrag = new VolumeDrag(vm, hit, x, y, shape.GainAt(x), sound, FadeProperties.Of(vm.Clip),
            new ClipPropertyGesture(hit.Kind is VolumeHitKind.FadeIn or VolumeHitKind.FadeOut ? "Change Sound Fade" : "Change Volume"));
        return true;
    }

    public void UpdateVolumeDrag(double x, double y, double height)
    {
        if (_volumeDrag is not { } drag || vmShape(drag) is not { } shape) return;
        if (!drag.Moved && Math.Abs(x - drag.PressX) < 2 && Math.Abs(y - drag.PressY) < 2) return;
        drag.Moved = true;
        var clip = drag.Clip.Clip;
        var change = drag.Hit.Kind switch
        {
            VolumeHitKind.Line => LineChange(drag, shape, y, height),
            VolumeHitKind.Point => PointChange(drag, shape, x, y, height),
            _ => FadeChange(drag, x)
        };
        if (change is null) return;
        var result = _edit.SetClipProperties(clip.Id, change with { Gesture = drag.Gesture });
        if (!result.Success) { Report(result); return; }
        var label = drag.Hit.Kind switch
        {
            VolumeHitKind.FadeIn or VolumeHitKind.FadeOut => string.Format(Strings.Volume_FadeLabel,
                TimeFormat.ToTimecode(drag.Hit.Kind == VolumeHitKind.FadeIn ? FadeOf(clip).In : FadeOf(clip).Out, FrameRate)),
            VolumeHitKind.Point when AudioProperties.Of(clip)?.Volumes?.Find(drag.Hit.PointId!.Value) is { } p =>
                ClipVolumeShape.Describe(AudioProperties.Of(clip)!.Value.Volume * p.Gain),
            _ => ClipVolumeShape.Describe(AudioProperties.Of(clip)!.Value.Volume)
        };
        drag.Clip.StripText = label;
        drag.Clip.IsStripInvalid = false;
        drag.Clip.VolumeShape = TrackOf(drag.Clip) is { } track ? VolumeShapeOf(drag.Clip, track.Track) : null;

        static ClipVolumeShape? vmShape(VolumeDrag d) => d.Clip.VolumeShape;
    }

    private (MediaTime In, MediaTime Out) FadeOf(Clip clip) =>
        clip is VideoClip v ? (v.SoundFadeIn, v.SoundFadeOut) : (clip.FadeIn, clip.FadeOut);

    /// <summary>The line: the clip's volume follows the pointer (with points: scaled, so the curve passes through it).</summary>
    private ClipPropertyChange? LineChange(VolumeDrag drag, ClipVolumeShape shape, double y, double height)
    {
        var target = shape.GainOfY(y, height);
        double volume;
        if (drag.Sound.Volumes is null || drag.PressGain <= 0) volume = target;
        else volume = drag.Sound.Volume * target / drag.PressGain;
        volume = Math.Round(Math.Clamp(volume, ClipPropertyLimits.MinVolume, ClipPropertyLimits.MaxVolume), 3);
        return new ClipPropertyChange { Audio = drag.Sound with { Volume = volume } };
    }

    /// <summary>A point: its time (between its neighbours, inside the clip) and its value relative to the clip's volume.</summary>
    private ClipPropertyChange? PointChange(VolumeDrag drag, ClipVolumeShape shape, double x, double y, double height)
    {
        if (drag.Sound.Volumes is not { } points || points.Find(drag.Hit.PointId!.Value) is not { } point) return null;
        var index = points.Points.IndexOf(point);
        var lo = index > 0 ? points.Points[index - 1].Time + new MediaTime(1) : shape.SourceAt(0);
        var hi = index < points.Count - 1 ? points.Points[index + 1].Time - new MediaTime(1) : shape.SourceAt(shape.Width);
        var time = shape.SourceAt(Math.Clamp(x, 0, shape.Width));
        if (hi >= lo) time = time < lo ? lo : time > hi ? hi : time;
        var gain = Math.Clamp(shape.GainOfY(y, height) / Math.Max(drag.Sound.Volume, 0.01), VolumeEnvelope.MinGain, VolumeEnvelope.MaxGain);
        var moved = points.With(point with { Time = time, Gain = Math.Round(gain, 4) });
        return moved is null ? null : new ClipPropertyChange { Audio = drag.Sound with { Volumes = moved } };
    }

    /// <summary>A fade handle: the fade's length in whole frames from the clip's edge to the pointer.</summary>
    private ClipPropertyChange FadeChange(VolumeDrag drag, double x)
    {
        var clip = drag.Clip.Clip;
        var frames = clip.TimelineEnd.ToFrameFloor(FrameRate) - clip.TimelineStart.ToFrameFloor(FrameRate);
        var distance = drag.Hit.Kind == VolumeHitKind.FadeIn ? x : drag.Clip.Width - x;
        var length = MediaTime.FromFrame(Math.Clamp(TimelineCoordinateMapper.XToTime(Math.Max(0, distance), PixelsPerSecond).ToNearestFrame(FrameRate), 0, frames), FrameRate);
        var fadeIn = drag.Hit.Kind == VolumeHitKind.FadeIn;
        if (clip is VideoClip)
            return new ClipPropertyChange
            {
                Audio = fadeIn ? drag.Sound with { SoundFadeIn = length } : drag.Sound with { SoundFadeOut = length }
            };
        return new ClipPropertyChange { Fade = fadeIn ? drag.Fades with { FadeIn = length } : drag.Fades with { FadeOut = length } };
    }

    /// <summary>The release: the drag's changes stay as one undo step.</summary>
    public void EndVolumeDrag()
    {
        if (_volumeDrag is not { } drag) return;
        _volumeDrag = null;
        drag.Clip.StripText = null;
        RefreshVolumeShapes();
    }

    /// <summary>Esc, a lost pointer, the editing lock: the values at the press come back (the gesture's step cancels out).</summary>
    public void CancelVolumeDrag()
    {
        if (_volumeDrag is not { } drag) return;
        _volumeDrag = null;
        drag.Clip.StripText = null;
        if (drag.Moved)
            _edit.SetClipProperties(drag.Clip.Clip.Id, new ClipPropertyChange
            {
                Audio = drag.Sound, Fade = drag.Clip.Clip is AudioClip ? drag.Fades : null, Gesture = drag.Gesture
            });
        RefreshVolumeShapes();
    }

    /// <summary>A double click: on a point removes it, on the line adds one there with the line's value.</summary>
    public bool VolumeDoubleClick(TimelineClipViewModel vm, double x, double y, double height)
    {
        var hit = VolumeHitAt(vm, x, y, height);
        if (hit.Kind is not (VolumeHitKind.Line or VolumeHitKind.Point) || AudioProperties.Of(vm.Clip) is not { } sound || vm.VolumeShape is not { } shape)
            return false;
        if (TrackOf(vm) is { IsLocked: true })
        {
            _status.Report(Strings.Volume_TrackLocked);
            return true;
        }
        if (hit.Kind == VolumeHitKind.Point)
        {
            Report(_edit.SetClipProperties(vm.Clip.Id, new ClipPropertyChange { Audio = sound with { Volumes = sound.Volumes?.Without(hit.PointId!.Value) } }),
                Strings.Volume_PointRemoved);
            SelectVolumePoint(null);
            return true;
        }
        var time = shape.SourceAt(Math.Clamp(x, 0, shape.Width));
        var point = new VolumePoint(Guid.NewGuid(), time, Math.Clamp(shape.CurveAt(x), VolumeEnvelope.MinGain, VolumeEnvelope.MaxGain));
        var points = sound.Volumes is { } existing ? existing.With(point) : VolumeEnvelope.Of(new[] { point });
        if (points is null) return true;   // a point already there
        var result = _edit.SetClipProperties(vm.Clip.Id, new ClipPropertyChange { Audio = sound with { Volumes = points } });
        Report(result, Strings.Volume_PointAdded);
        if (result.Success) SelectVolumePoint((vm.Clip.Id, point.Id));
        return true;
    }

    private void SelectVolumePoint((Guid ClipId, Guid PointId)? point)
    {
        if (_selectedVolumePoint == point) return;
        _selectedVolumePoint = point;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RefreshVolumeShapes();
    }

    /// <summary>Delete with a volume point selected: removes it (the clip stays).</summary>
    private bool DeleteSelectedVolumePoint()
    {
        if (_selectedVolumePoint is not { } selected || !_clipViewModels.TryGetValue(selected.ClipId, out var vm) ||
            AudioProperties.Of(vm.Clip) is not { } sound || sound.Volumes?.Find(selected.PointId) is null)
        {
            _selectedVolumePoint = null;
            return false;
        }
        Report(_edit.SetClipProperties(vm.Clip.Id, new ClipPropertyChange { Audio = sound with { Volumes = sound.Volumes.Without(selected.PointId) } }),
            Strings.Volume_PointRemoved);
        SelectVolumePoint(null);
        return true;
    }

    /// <summary>The cursor's hint over a clip: what the pointer would drag.</summary>
    public string? VolumeTip(VolumeHit hit) => hit.Kind switch
    {
        VolumeHitKind.Line => Strings.Volume_LineTip,
        VolumeHitKind.Point => Strings.Volume_PointTip,
        VolumeHitKind.FadeIn => Strings.Volume_FadeInTip,
        VolumeHitKind.FadeOut => Strings.Volume_FadeOutTip,
        _ => null
    };
}
