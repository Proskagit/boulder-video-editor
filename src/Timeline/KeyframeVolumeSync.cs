using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.People;

namespace AiVideoEditor.Timeline;

/// <summary>
/// D064 amendment 1 (+ the owner's second check: Follow): on a video clip with its own sound every keyframe of the clip — its own
/// transform keyframes (D063) and each followed part's framing keyframes (D063 §4) — and its volume points are one set: the same
/// ids at the same source times. An edit that adds, moves or removes one side does the same to the other; what it adds keeps what
/// is heard and seen (a point takes the curve's gain there; a keyframe the values that place the picture there — inside a followed
/// part the part's framing, the part's keyframe — and the easing of the segment it splits). The last keyframe of an owner removed
/// leaves its values static (D063 §2: the clip's position and scale, a part's offset); the last point removed folds its gain into the
/// clip's volume. A part removed with its keyframes keeps their points (the sound does not change). An edit that changes both sides
/// (Undo, Redo, a cancel) is taken as it is.
/// </summary>
public static class KeyframeVolumeSync
{
    /// <summary>The synced values and Follow after an edit from (<paramref name="before"/>, <paramref name="followBefore"/>) to
    /// (<paramref name="after"/>, <paramref name="followAfter"/>).</summary>
    public static (ClipPropertyValues Values, FollowSettings? Follow) Sync(ClipPropertyValues before, ClipPropertyValues after,
        FollowSettings? followBefore, FollowSettings? followAfter)
    {
        if (before.Audio is not { } soundBefore || after.Audio is not { } soundAfter || before.Keyframes is not { } keysBefore ||
            after.Keyframes is not { } keysAfter)
            return (after, followAfter);
        var allBefore = AllKeys(keysBefore.Keys, followBefore);
        var allAfter = AllKeys(keysAfter.Keys, followAfter);
        var keysChanged = !SameKeys(allBefore, allAfter);
        var pointsChanged = soundBefore.Volumes != soundAfter.Volumes;
        if (keysChanged == pointsChanged) return (after, followAfter);

        return keysChanged
            ? (PointsFollowKeys(after, soundBefore, soundAfter, allBefore, allAfter, followAfter), followAfter)
            : KeysFollowPoints(before, after, soundBefore, soundAfter, keysBefore.Keys, followBefore);
    }

    /// <summary>Every keyframe of the clip by id: the clip's own (part −1) and each part's.</summary>
    private static Dictionary<Guid, (TransformKeyframe Key, int Part)> AllKeys(TransformKeyframes? own, FollowSettings? follow)
    {
        var all = new Dictionary<Guid, (TransformKeyframe, int)>();
        if (own is not null) foreach (var key in own.Keys) all[key.Id] = (key, -1);
        if (follow is not null)
            for (var i = 0; i < follow.Parts.Length; i++)
                if (follow.Parts[i].Keyframes is { } keys)
                    foreach (var key in keys.Keys) all.TryAdd(key.Id, (key, i));
        return all;
    }

    private static bool SameKeys(Dictionary<Guid, (TransformKeyframe Key, int Part)> a, Dictionary<Guid, (TransformKeyframe Key, int Part)> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && other.Key.Time == p.Value.Key.Time);

    /// <summary>The keyframes lead: a point per keyframe (moved with it, added with the curve's gain); a point whose keyframe was
    /// removed goes — unless its part itself was removed; the last point gone folds its gain into the clip's volume.</summary>
    private static ClipPropertyValues PointsFollowKeys(ClipPropertyValues after, AudioProperties soundBefore, AudioProperties soundAfter,
        Dictionary<Guid, (TransformKeyframe Key, int Part)> allBefore, Dictionary<Guid, (TransformKeyframe Key, int Part)> allAfter,
        FollowSettings? followAfter)
    {
        var old = soundBefore.Volumes;
        var points = new List<VolumePoint>();
        foreach (var (id, (key, _)) in allAfter)
            points.Add(old?.Find(id) is { } p
                ? p with { Time = key.Time }
                : new VolumePoint(id, key.Time, Math.Clamp(old?.At(key.Time) ?? 1, VolumeEnvelope.MinGain, VolumeEnvelope.MaxGain)));
        if (old is not null)
            foreach (var point in old.Points)
            {
                if (allAfter.ContainsKey(point.Id)) continue;
                var partGone = allBefore.TryGetValue(point.Id, out var was) && was.Part >= 0 && (followAfter is null || was.Part >= followAfter.Parts.Length);
                if (!allBefore.ContainsKey(point.Id) || partGone) points.Add(point);   // a free point, or a removed part's: kept
            }
        // Two points may not share a time: a kept one at a new keyframe's time gives way.
        var distinct = points.GroupBy(p => p.Time).Select(g => g.FirstOrDefault(p => allAfter.ContainsKey(p.Id)) ?? g.First()).ToList();
        var volumes = VolumeEnvelope.Of(distinct);
        var volume = soundAfter.Volume;
        if (volumes is null && old is { Count: > 0 })
        {
            var removed = allBefore.Values.Select(v => v.Key).OrderBy(k => k.Time).LastOrDefault();
            volume = Math.Clamp(volume * old.At(removed?.Time ?? old.Points[^1].Time), ClipPropertyLimits.MinVolume, ClipPropertyLimits.MaxVolume);
        }
        return after with { Audio = soundAfter with { Volumes = volumes, Volume = volume } };
    }

    /// <summary>The points lead: a point added makes a keyframe of the owner at its time (the followed part whose range holds it,
    /// else the clip); a point moved moves its keyframe; a point removed removes its keyframe (the owner's last leaves its values
    /// static).</summary>
    private static (ClipPropertyValues, FollowSettings?) KeysFollowPoints(ClipPropertyValues before, ClipPropertyValues after,
        AudioProperties soundBefore, AudioProperties soundAfter, TransformKeyframes? ownBefore, FollowSettings? follow)
    {
        var visual = before.Visual ?? VisualProperties.Default;
        var own = ownBefore?.Keys.ToList() ?? new List<TransformKeyframe>();
        var parts = follow?.Parts.Select(p => p.Keyframes?.Keys.ToList() ?? new List<TransformKeyframe>()).ToList() ?? new List<List<TransformKeyframe>>();
        var newPoints = soundAfter.Volumes?.Points.ToDictionary(p => p.Id) ?? new Dictionary<Guid, VolumePoint>();
        var oldPoints = soundBefore.Volumes?.Points.ToDictionary(p => p.Id) ?? new Dictionary<Guid, VolumePoint>();

        List<TransformKeyframe>? OwnerOf(Guid id) =>
            own.Any(k => k.Id == id) ? own : parts.FirstOrDefault(list => list.Any(k => k.Id == id));

        // Removed and moved.
        foreach (var (id, _) in oldPoints)
        {
            if (OwnerOf(id) is not { } list) continue;
            var index = list.FindIndex(k => k.Id == id);
            if (!newPoints.TryGetValue(id, out var point)) list.RemoveAt(index);
            else if (point.Time != list[index].Time) list[index] = list[index] with { Time = point.Time };
        }
        // Added: the owner at its time.
        foreach (var (id, point) in newPoints)
        {
            if (oldPoints.ContainsKey(id) || OwnerOf(id) is not null) continue;
            var part = follow?.IndexAt(point.Time) ?? -1;
            var clipValue = TransformKeyframes.Apply(visual, ownBefore, point.Time);
            if (part >= 0)
            {
                var value = follow!.Parts[part].FramingAt(point.Time, clipValue.Scale);
                var easing = parts[part].LastOrDefault(k => k.Time <= point.Time)?.Easing ?? KeyframeEasing.Linear;
                parts[part].Add(new TransformKeyframe(id, point.Time, value.X, value.Y, value.Scale, easing));
            }
            else
            {
                var easing = own.LastOrDefault(k => k.Time <= point.Time)?.Easing ?? KeyframeEasing.Linear;
                own.Add(new TransformKeyframe(id, point.Time, clipValue.PositionX, clipValue.PositionY, clipValue.Scale, easing));
            }
        }

        var result = after;
        var ownKeys = TransformKeyframes.Of(own);
        if (ownKeys is null && ownBefore is { Count: > 0 } hadOwn)
        {
            var last = hadOwn.Keys[^1];
            result = result with { Visual = (result.Visual ?? visual) with { PositionX = last.X, PositionY = last.Y, Scale = last.Scale } };
        }
        result = result with { Keyframes = new KeyframeValues(ownKeys) };

        var nextFollow = follow;
        if (follow is not null)
            for (var i = 0; i < follow.Parts.Length; i++)
            {
                var keys = TransformKeyframes.Of(parts[i]);
                var part = follow.Parts[i];
                if (Equals(keys, part.Keyframes)) continue;
                var updated = keys is null && part.Keyframes is { Count: > 0 } had
                    ? part with { Keyframes = null, OffsetX = had.Keys[^1].X, OffsetY = had.Keys[^1].Y }
                    : part with { Keyframes = keys };
                nextFollow = nextFollow!.WithPart(i, updated);
            }
        return (result, nextFollow);
    }
}
