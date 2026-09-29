using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Entities;

/// <summary>
/// The structural rules of transitions (D025 §1, §3). A cross dissolve of <c>F</c> frames sits on the cut <c>c</c>
/// between A and B (A's end is exactly B's start); its zone is <c>[c − ⌊F/2⌋, c + ⌈F/2⌉)</c>: <see cref="Zone"/>'s
/// <c>BeforeCut</c> frames lie in A, <c>AfterCut</c> frames in B. Source handles are not checked here — they depend
/// on the media (D025 §4).
/// </summary>
public static class TransitionRules
{
    /// <summary>The only transition type (D025).</summary>
    public const string CrossDissolve = "crossDissolve";

    /// <summary>The shortest transition, in frames: one frame on each side of the cut.</summary>
    public const long MinFrames = 2;

    /// <summary>Frames covered by a transition (or fade) of <paramref name="duration"/> at <paramref name="rate"/>
    /// (D025: durations are times; frames are derived, ties up).</summary>
    public static long Frames(MediaTime duration, FrameRate rate) => duration.ToNearestFrame(rate);

    /// <summary>How the <paramref name="frames"/> of a zone split around the cut: <c>⌊F/2⌋</c> before it (in A),
    /// <c>⌈F/2⌉</c> after it (in B).</summary>
    public static (long BeforeCut, long AfterCut) Zone(long frames) => (frames / 2, frames - frames / 2);

    /// <summary>
    /// Checks the transitions of <paramref name="track"/> against its clips: a known type, at least
    /// <see cref="MinFrames"/> frames, two different clips of this track that touch exactly (A's end is B's start),
    /// at most one transition per cut, and every zone part fits its clip — together with the part of a
    /// transition on the clip's other edge. Returns a short user-facing reason for the first violation, or null.
    /// Audio tracks have no transitions.
    /// </summary>
    public static string? ValidateTrack(Track track, FrameRate rate)
    {
        if (track.Transitions.Count == 0) return null;
        if (track.Type != TrackType.Video) return "Only video tracks have transitions.";

        var clips = track.Clips.ToDictionary(c => c.Id);
        var usedParts = new Dictionary<Guid, long>();   // frames of zones inside each clip
        var lefts = new HashSet<Guid>();

        foreach (var transition in track.Transitions)
        {
            if (transition.TransitionTypeId != CrossDissolve) return "A transition has an unknown type.";
            if (transition.Duration < MediaTime.Zero) return "A transition has a negative duration.";

            var frames = Frames(transition.Duration, rate);
            if (frames < MinFrames) return $"A transition must be at least {MinFrames} frames long.";

            if (transition.LeftClipId == transition.RightClipId) return "A transition needs two different clips.";
            if (!clips.TryGetValue(transition.LeftClipId, out var left) || !clips.TryGetValue(transition.RightClipId, out var right))
                return $"A transition refers to a clip that is not on track {track.Name}.";
            if (left.TimelineEnd != right.TimelineStart) return "A transition's clips don't touch.";
            if (!lefts.Add(left.Id)) return "A cut has more than one transition.";

            var (beforeCut, afterCut) = Zone(frames);
            if (Add(usedParts, left, beforeCut, rate) || Add(usedParts, right, afterCut, rate))
                return "A clip is too short for its transitions.";
        }

        return null;

        // Adds a zone part to the clip's total; true when the clip can't hold it.
        static bool Add(Dictionary<Guid, long> used, Clip clip, long part, FrameRate rate)
        {
            var total = used.GetValueOrDefault(clip.Id) + part;
            used[clip.Id] = total;
            return total > ClipFrames(clip, rate);
        }
    }

    /// <summary>The clip's length in frames (its edges lie on the grid).</summary>
    public static long ClipFrames(Clip clip, FrameRate rate) =>
        clip.TimelineEnd.ToFrameFloor(rate) - clip.TimelineStart.ToFrameFloor(rate);
}
