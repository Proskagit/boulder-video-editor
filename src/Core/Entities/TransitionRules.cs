using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Localization;

namespace AiVideoEditor.Core.Entities;

/// <summary>
/// The structural rules of transitions (D025 §1, §3; D044). A transition of <c>F</c> frames sits on the cut <c>c</c>
/// between A and B (A's end is exactly B's start); its zone is <c>[c − ⌊F/2⌋, c + ⌈F/2⌉)</c>: <see cref="Zone"/>'s
/// <c>BeforeCut</c> frames lie in A, <c>AfterCut</c> frames in B. Source handles are not checked here — they depend
/// on the media (D025 §4) — and only the types of <see cref="RequiresHandles"/> need them (D044 §3).
/// </summary>
public static class TransitionRules
{
    /// <summary>Наплыв: B over A at the D025 ramp (D025 §3) — the default video transition.</summary>
    public const string CrossDissolve = "crossDissolve";

    /// <summary>Затемнение: through black, each clip on its own frames (D044 §2).</summary>
    public const string DipToBlack = "dipToBlack";

    /// <summary>Вспышка: one white frame at the cut, each clip on its own frames (D044 §2, §4).</summary>
    public const string Flash = "flash";

    /// <summary>Наезд: a scale about the canvas centre peaking at the cut, each clip on its own frames (D044 §2).</summary>
    public const string Zoom = "zoom";

    /// <summary>Сдвиг: B pushes A out from <see cref="Transition.Direction"/>, both from their handles (D044 §2).</summary>
    public const string Push = "push";

    /// <summary>The transition of an audio track (D032 R9): a linear crossfade of the two clips' sound.</summary>
    public const string Crossfade = "crossfade";

    /// <summary>The video transitions of the library, in the order the interface lists them (D044 §1).</summary>
    public static readonly ImmutableArray<string> VideoTypes = ImmutableArray.Create(CrossDissolve, DipToBlack, Flash, Zoom, Push);

    /// <summary>The default transition type of a track of <paramref name="trackType"/>: a dissolve on a video track, a
    /// crossfade on an audio track.</summary>
    public static string TypeFor(TrackType trackType) => trackType == TrackType.Audio ? Crossfade : CrossDissolve;

    /// <summary>True when a track of <paramref name="trackType"/> takes the type: the five video types on a video track,
    /// the crossfade on an audio track.</summary>
    public static bool Allows(TrackType trackType, string? typeId) =>
        trackType == TrackType.Audio ? typeId == Crossfade : typeId is not null && VideoTypes.Contains(typeId);

    /// <summary>True for the types that show both clips at once and so need source handles (D044 §3; D032 R9 for the
    /// crossfade).</summary>
    public static bool RequiresHandles(string typeId) => typeId is CrossDissolve or Push or Crossfade;

    /// <summary>True for the types drawn with a fill colour whose peak — the cut frame — is fully covered (D044 §2, §4).</summary>
    public static bool IsFill(string typeId) => typeId is DipToBlack or Flash;

    /// <summary>True for the type that has a <see cref="Transition.Direction"/>.</summary>
    public static bool HasDirection(string typeId) => typeId == Push;

    /// <summary>The direction a new transition of the type gets: from the right for a push, none otherwise (D044 §11).</summary>
    public static TransitionDirection? DefaultDirection(string typeId) => HasDirection(typeId) ? TransitionDirection.FromRight : null;

    /// <summary>The shortest distance between the peaks (cuts) of two fill transitions on the video tracks (D044 §4).</summary>
    public static readonly MediaTime FillPeakSpacing = MediaTime.FromSeconds(1);

    /// <summary>The default duration and the input range of a type (D044 §1, §5); the crossfade keeps D032's rule (a
    /// one-second default, at least <see cref="MinFrames"/> frames, no maximum).</summary>
    public static TransitionLimits LimitsOf(string typeId) => typeId switch
    {
        DipToBlack => new(Seconds(0.6), Seconds(0.1), null),
        Flash => new(Seconds(0.2), Seconds(0.1), Seconds(0.5)),
        Zoom => new(Seconds(0.3), Seconds(0.2), Seconds(1.0)),
        Push => new(Seconds(0.3), Seconds(0.2), null),
        Crossfade => new(Seconds(1.0), null, null),
        _ => new(Seconds(1.0), Seconds(0.1), null)          // the dissolve
    };

    private static MediaTime Seconds(double seconds) => MediaTime.FromSeconds(seconds);

    /// <summary>The shortest duration of the type a user may set, in frames: its minimum rounded up to whole frames, so
    /// the interface never shows less than the minimum, and never below <see cref="MinFrames"/> (D044 §5).</summary>
    public static long MinFramesOf(string typeId, FrameRate rate) =>
        Math.Max(MinFrames, LimitsOf(typeId).Min is { } min ? min.ToFrameCeiling(rate) : 0);

    /// <summary>The longest duration of the type a user may set, in frames: its maximum rounded down to whole frames, or
    /// no limit (D044 §5).</summary>
    public static long MaxFramesOf(string typeId, FrameRate rate) =>
        LimitsOf(typeId).Max is { } max ? max.ToFrameFloor(rate) : long.MaxValue;

    /// <summary>The default duration of the type in frames: the nearest frame, ties up (D025 §1).</summary>
    public static long DefaultFramesOf(string typeId, FrameRate rate) => Frames(LimitsOf(typeId).Default, rate);

    /// <summary>
    /// <paramref name="frames"/> clamped to what the type allows at <paramref name="rate"/> and to <paramref name="fits"/>
    /// (the longest the cut takes): the nearest allowed value (D044 §6), or null when nothing of the type fits — its
    /// minimum is longer than what fits.
    /// </summary>
    public static long? ClampFrames(string typeId, long frames, long fits, FrameRate rate)
    {
        var low = MinFramesOf(typeId, rate);
        var high = Math.Min(MaxFramesOf(typeId, rate), fits);
        return high < low ? null : Math.Clamp(frames, low, high);
    }

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
    /// A video track takes dissolves, an audio track crossfades (D032 R1, R9); both follow the same zone rules.
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
            if (!Allows(trackType, transition.TypeId))
                return transition.TypeId switch
                {
                    Crossfade => Messages.TransitionRule_AudioOnly,
                    _ when VideoTypes.Contains(transition.TypeId) => Messages.TransitionRule_VideoOnly,
                    _ => Messages.TransitionRule_UnknownType
                };
            if (HasDirection(transition.TypeId)
                    ? transition.Direction is not { } direction || !Enum.IsDefined(direction)
                    : transition.Direction is not null)
                return Messages.TransitionRule_Direction;
            if (transition.Duration < MediaTime.Zero) return Messages.TransitionRule_Negative;

            var frames = Frames(transition.Duration, rate);
            if (frames < MinFrames) return string.Format(Messages.TransitionRule_Min, MinFrames);

            if (transition.LeftClipId == transition.RightClipId) return Messages.TransitionRule_TwoClips;
            if (clipEdges(transition.LeftClipId) is not { } left || clipEdges(transition.RightClipId) is not { } right)
                return string.Format(Messages.TransitionRule_NotOnTrack, trackName);
            if (left.End != right.Start) return Messages.TransitionRule_DontTouch;
            if (!lefts.Add(transition.LeftClipId)) return Messages.TransitionRule_TwoOnCut;

            var (beforeCut, afterCut) = Zone(frames);
            if (Add(transition.LeftClipId, left, beforeCut) || Add(transition.RightClipId, right, afterCut))
                return Messages.TransitionRule_ClipTooShort;
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

    /// <summary>
    /// Checks D044 §4 on <paramref name="peaks"/> — the transitions of every video track with the time of their cut (the
    /// peak): any two fill peaks at least <see cref="FillPeakSpacing"/> apart. Returns a user-facing reason, or null.
    /// </summary>
    public static string? ValidateFillPeaks(IEnumerable<(string TypeId, MediaTime Cut)> peaks)
    {
        var cuts = peaks.Where(p => IsFill(p.TypeId)).Select(p => p.Cut).OrderBy(c => c).ToList();
        for (var i = 1; i < cuts.Count; i++)
            if (cuts[i] - cuts[i - 1] < FillPeakSpacing)
                return Messages.TransitionRule_FillPeaksTooClose;
        return null;
    }

    /// <summary>The D044 §4 check on video tracks as they are: a transition's peak is its left clip's end.</summary>
    public static string? ValidateFillPeaks(IEnumerable<Track> videoTracks) =>
        ValidateFillPeaks(videoTracks.SelectMany(track =>
        {
            var ends = track.Clips.ToDictionary(c => c.Id, c => c.TimelineEnd);
            return track.Transitions.Where(t => ends.ContainsKey(t.LeftClipId)).Select(t => (t.TransitionTypeId, ends[t.LeftClipId]));
        }));

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
public readonly record struct TransitionSpec(Guid Id, string TypeId, MediaTime Duration, Guid LeftClipId, Guid RightClipId,
    TransitionDirection? Direction = null)
{
    public static TransitionSpec Of(Transition t) =>
        new(t.Id, t.TransitionTypeId, t.Duration, t.LeftClipId, t.RightClipId, t.Direction);
}

/// <summary>A type's default duration and its input range (D044 §1, §5): <see cref="Min"/> null — only
/// <see cref="TransitionRules.MinFrames"/>; <see cref="Max"/> null — no limit but what fits.</summary>
public readonly record struct TransitionLimits(MediaTime Default, MediaTime? Min, MediaTime? Max);
