using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// D064 amendment 1: the keyframes of an audio clip — its volume points, edited like a video clip's keyframes (D063 §2): the tool
/// bar's «Ключ. кадр» adds one on the playhead's frame with the line's value there (nothing changes until it is moved up or down)
/// or removes the one there; an amber diamond along the clip's top for each, a press selects it (the playhead goes to it), a drag
/// moves it along the frames, Delete removes it. (A video clip's volume points are its keyframes — the same diamonds.)
/// </summary>
public sealed partial class TimelineViewModel
{
    /// <summary>The source time on timeline <paramref name="frame"/> of <paramref name="clip"/> (D009 / D022).</summary>
    private MediaTime SourceAtFrame(MediaBackedClip clip, long frame) =>
        HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, frame, FrameRate);

    /// <summary>The timeline frame showing source time <paramref name="source"/> of <paramref name="clip"/> (nearest).</summary>
    private long FrameOfSource(MediaBackedClip clip, MediaTime source)
    {
        long a = clip.Speed.Numerator, b = clip.Speed.Denominator;
        var offset = new MediaTime((long)((Int128)(source - clip.SourceIn).Ticks * b / a));
        return (clip.TimelineStart + offset).ToNearestFrame(FrameRate);
    }

    private (long First, long Last) FramesOfClip(Clip clip) =>
        (clip.TimelineStart.ToFrameFloor(FrameRate), Math.Max(clip.TimelineStart.ToFrameFloor(FrameRate), clip.TimelineEnd.ToFrameFloor(FrameRate) - 1));

    /// <summary>The primary selected audio clip under the playhead, or null.</summary>
    private AudioClip? AudioKeyframeClip()
    {
        if (_selection.Count == 0 || !_clipViewModels.TryGetValue(_selection[^1], out var vm) || vm.Clip is not AudioClip clip) return null;
        var frame = EditPlayhead.ToFrameFloor(FrameRate);
        var (first, last) = FramesOfClip(clip);
        return frame >= first && frame <= last ? clip : null;
    }

    private AudioClip? AudioClipById(Guid id) => _clipViewModels.TryGetValue(id, out var vm) ? vm.Clip as AudioClip : null;

    /// <summary>The audio clip's point on <paramref name="frame"/>, or null.</summary>
    private VolumePoint? PointOnFrame(AudioClip clip, long frame) =>
        clip.Volumes?.Points.FirstOrDefault(p => FrameOfSource(clip, p.Time) == frame);

    /// <summary>The keyframe button on an audio clip: a point on the playhead's frame added (the line's value) or removed.</summary>
    private void ToggleAudioKeyframe(AudioClip clip)
    {
        var frame = EditPlayhead.ToFrameFloor(FrameRate);
        var sound = AudioProperties.Of(clip)!.Value;
        if (PointOnFrame(clip, frame) is { } existing)
        {
            Report(_edit.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = sound with { Volumes = sound.Volumes?.Without(existing.Id) } }),
                successMessage: Strings.Keyframe_Removed);
            SelectedKeyframe = null;
            return;
        }
        var time = SourceAtFrame(clip, frame);
        var point = new VolumePoint(Guid.NewGuid(), time, Math.Clamp(sound.Volumes?.At(time) ?? 1, VolumeEnvelope.MinGain, VolumeEnvelope.MaxGain));
        var points = sound.Volumes is { } current ? current.With(point) : VolumeEnvelope.Of(new[] { point });
        if (points is null) return;
        var result = _edit.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = sound with { Volumes = points } });
        Report(result, successMessage: Strings.Keyframe_AddedVolume);
        if (result.Success) SelectedKeyframe = (new KeyframeOwner(clip.Id, null), point.Id);
    }

    /// <summary>The diamonds of an audio clip's points.</summary>
    private IReadOnlyList<KeyframeMarker> AudioKeyframeMarkers(AudioClip clip)
    {
        if (clip.Volumes is not { } points) return Array.Empty<KeyframeMarker>();
        var (first, last) = FramesOfClip(clip);
        var start = clip.TimelineStart.ToFrameFloor(FrameRate);
        var markers = new List<KeyframeMarker>();
        foreach (var point in points.Points)
        {
            var frame = FrameOfSource(clip, point.Time);
            if (frame < first || frame > last) continue;
            var left = TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(frame - start, FrameRate), PixelsPerSecond);
            var selected = SelectedKeyframe is { } s && s.Id == point.Id && s.Owner.ClipId == clip.Id;
            var tip = string.Format(Strings.Keyframe_TipVolume, TimeFormat.ToTimecode(MediaTime.FromFrame(frame, FrameRate), FrameRate),
                ClipVolumeShape.Describe(clip.Volume * point.Gain));
            markers.Add(new KeyframeMarker(point.Id, null, left, 2, selected, tip));
        }
        return markers;
    }

    /// <summary>The release of an audio clip's diamond: its point moves to the frame under it (never onto another point's).</summary>
    private void MoveAudioKeyframe(AudioClip clip, Guid id, long frame)
    {
        if (clip.Volumes is not { } points || points.Find(id) is not { } point) return;
        var (first, last) = FramesOfClip(clip);
        frame = Math.Clamp(frame, first, last);
        if (points.Points.Any(p => p.Id != id && FrameOfSource(clip, p.Time) == frame))
        {
            _status.Report(Core.Localization.Messages.Keyframe_Exists);
            return;
        }
        var time = SourceAtFrame(clip, frame);
        if (time == point.Time || points.With(point with { Time = time }) is not { } moved) return;
        var result = _edit.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = AudioProperties.Of(clip)!.Value with { Volumes = moved } });
        Report(result);
        if (result.Success) SetPlayhead(MediaTime.FromFrame(frame, FrameRate));
    }

    private bool DeleteAudioKeyframe(AudioClip clip, Guid id)
    {
        if (clip.Volumes?.Find(id) is null) return false;
        var sound = AudioProperties.Of(clip)!.Value;
        Report(_edit.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = sound with { Volumes = sound.Volumes!.Without(id) } }),
            successMessage: Strings.Keyframe_Removed);
        SelectedKeyframe = null;
        return true;
    }

    /// <summary>The frames of an audio clip's points (for ◀ / ▶ and the playhead's snap).</summary>
    private IEnumerable<(Guid Id, long Frame)> AudioKeyframeFrames(AudioClip clip)
    {
        var (first, last) = FramesOfClip(clip);
        return (clip.Volumes?.Points ?? default).IsDefaultOrEmpty
            ? Enumerable.Empty<(Guid, long)>()
            : clip.Volumes!.Points.Select(p => (p.Id, FrameOfSource(clip, p.Time))).Where(p => p.Item2 >= first && p.Item2 <= last);
    }
}
