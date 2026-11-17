using System.Collections.Immutable;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Timeline;

/// <summary>
/// D064 amendment 1: on a video clip with its own sound, the clip's transform keyframes (D063, not a followed part's) and its
/// volume points are one set of keyframes — the same ids at the same source times. An edit that adds, moves or removes one side
/// does the same to the other; what it adds keeps what is heard and seen (a point takes the curve's gain there, a keyframe the
/// picture's position and scale there and the easing of the segment it splits). The last one removed leaves its values static
/// (D063 §2): the picture its position and scale, the sound its gain folded into the clip's volume. An edit that changes both sides
/// (Undo, Redo, a cancel) is taken as it is.
/// </summary>
public static class KeyframeVolumeSync
{
    /// <summary><paramref name="after"/> with the other side following the side the edit changed.</summary>
    public static ClipPropertyValues Sync(VideoClip clip, ClipPropertyValues before, ClipPropertyValues after)
    {
        if (before.Audio is not { } soundBefore || after.Audio is not { } soundAfter || before.Keyframes is not { } keysBefore ||
            after.Keyframes is not { } keysAfter)
            return after;
        var keysChanged = !Equals(keysBefore.Keys, keysAfter.Keys);
        var pointsChanged = soundBefore.Volumes != soundAfter.Volumes;
        if (keysChanged == pointsChanged) return after;

        if (keysChanged)
        {
            // The keyframes lead: the points follow (same ids and times).
            var keys = keysAfter.Keys;
            if (keys is null)
            {
                var volume = soundAfter.Volume;
                if (soundBefore.Volumes is { } old && keysBefore.Keys is { Count: > 0 } removed)
                    volume = Math.Clamp(volume * old.At(removed.Keys[^1].Time), ClipPropertyLimits.MinVolume, ClipPropertyLimits.MaxVolume);
                return after with { Audio = soundAfter with { Volumes = null, Volume = volume } };
            }
            var points = keys.Keys.Select(k => soundBefore.Volumes?.Find(k.Id) is { } p
                ? p with { Time = k.Time }
                : new VolumePoint(k.Id, k.Time, Math.Clamp(soundBefore.Volumes?.At(k.Time) ?? 1, VolumeEnvelope.MinGain, VolumeEnvelope.MaxGain)));
            return after with { Audio = soundAfter with { Volumes = VolumeEnvelope.Of(points) } };
        }

        // The points lead: the keyframes follow.
        var volumes = soundAfter.Volumes;
        if (volumes is null)
        {
            if (keysBefore.Keys is not { Count: > 0 } removedKeys) return after;
            var last = removedKeys.Keys[^1];
            var visual = (after.Visual ?? before.Visual)!.Value;
            return after with
            {
                Keyframes = new KeyframeValues(null),
                Visual = visual with { PositionX = last.X, PositionY = last.Y, Scale = last.Scale }
            };
        }
        var staticVisual = before.Visual ?? VisualProperties.Default;
        var next = volumes.Points.Select(p =>
        {
            if (keysBefore.Keys?.Find(p.Id) is { } key) return key with { Time = p.Time };
            var value = TransformKeyframes.Apply(staticVisual, keysBefore.Keys, p.Time);
            var easing = keysBefore.Keys?.Keys.LastOrDefault(k => k.Time <= p.Time)?.Easing ?? KeyframeEasing.Linear;
            return new TransformKeyframe(p.Id, p.Time, value.PositionX, value.PositionY, value.Scale, easing);
        });
        return after with { Keyframes = new KeyframeValues(TransformKeyframes.Of(next)) };
    }
}
