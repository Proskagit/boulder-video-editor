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
        var clips = track.Clips.ToDictionary(c => c.Id, c => (c.TimelineStart, c.TimelineEnd));
        return Validate(track.Type, track.Name, track.Transitions.Select(TransitionSpec.Of),
            id => clips.TryGetValue(id, out var edges) ? edges : null, rate);
    }

    /// <summary>
    /// <see cref="ValidateTrack"/> for a track as an edit would leave it: <paramref name="transitions"/> and the clip
    /// edges <paramref name="clipEdges"/> returns for a clip of the track (null for a clip that isn't on it).
    /// </summary>
    public static string? Validate(TrackType trackType, string trackName, IEnumerable<TransitionSpec> transitions,
        Func<Guid, (MediaTime Start, MediaTime End)?> clipEdges, FrameRate rate)
    {
        var usedParts = new Dictionary<Guid, long>();   // frames of zones inside each clip
        var lefts = new HashSet<Guid>();

        foreach (var transition in transitions)
        {
            if (trackType != TrackType.Video) return "Only video tracks have transitions.";
            if (transition.TypeId != CrossDissolve) return "A transition has an unknown type.";
            if (transition.Duration < MediaTime.Zero) return "A transition has a negative duration.";

            var frames = Frames(transition.Duration, rate);
            if (frames < MinFrames) return $"A transition must be at least {MinFrames} frames long.";

            if (transition.LeftClipId == transition.RightClipId) return "A transition needs two different clips.";
            if (clipEdges(transition.LeftClipId) is not { } left || clipEdges(transition.RightClipId) is not { } right)
                return $"A transition refers to a clip that is not on track {trackName}.";
            if (left.End != right.Start) return "A transition's clips don't touch.";
            if (!lefts.Add(transition.LeftClipId)) return "A cut has more than one transition.";

            var (beforeCut, afterCut) = Zone(frames);
            if (Add(transition.LeftClipId, left, beforeCut) || Add(transition.RightClipId, right, afterCut))
                return "A clip is too short for its transitions.";
        }

        return null;

        // Adds a zone part to the clip's total; true when the clip can't hold it.
        bool Add(Guid clipId, (MediaTime Start, MediaTime End) edges, long part)
        {
            var total = usedParts.GetValueOrDefault(clipId) + part;
            usedParts[clipId] = total;
            return total > edges.End.ToFrameFloor(rate) - edges.Start.ToFrameFloor(rate);
        }
    }

    /// <summary>The clip's length in frames (its edges lie on the grid).</summary>
    public static long ClipFrames(Clip clip, FrameRate rate) =>
        clip.TimelineEnd.ToFrameFloor(rate) - clip.TimelineStart.ToFrameFloor(rate);

    /// <summary>
    /// The longest dissolve (in frames) a cut can take (D025 §3–§4) when <paramref name="roomInLeft"/> frames of A and
    /// <paramref name="roomInRight"/> frames of B are free of other zones, and A's source reaches
    /// <paramref name="handleAfterLeft"/> frames past its end and B's <paramref name="handleBeforeRight"/> frames before
    /// its start: the largest <c>F</c> with <c>⌊F/2⌋ ≤ min(roomInLeft, handleBeforeRight)</c> and
    /// <c>⌈F/2⌉ ≤ min(roomInRight, handleAfterLeft)</c>. Below <see cref="MinFrames"/> no dissolve fits.
    /// </summary>
    public static long MaxFrames(long roomInLeft, long roomInRight, long handleAfterLeft, long handleBeforeRight)
    {
        var before = Math.Max(0, Math.Min(roomInLeft, handleBeforeRight));
        var after = Math.Max(0, Math.Min(roomInRight, handleAfterLeft));
        return before >= after ? 2 * after : 2 * before + 1;
    }
}

/// <summary>What <see cref="TransitionRules.Validate"/> needs of a transition (as it is, or as an edit would leave it).</summary>
public readonly record struct TransitionSpec(Guid Id, string TypeId, MediaTime Duration, Guid LeftClipId, Guid RightClipId)
{
    public static TransitionSpec Of(Transition t) => new(t.Id, t.TransitionTypeId, t.Duration, t.LeftClipId, t.RightClipId);
}
