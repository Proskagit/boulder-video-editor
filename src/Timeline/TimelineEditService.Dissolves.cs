using System.Collections.Immutable;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Timeline.Commands;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline;

/// <summary>Dissolves (D025 §3–§5) (split out of TimelineEditService.cs, D075 amendment 1 — no change).</summary>
public sealed partial class TimelineEditService
{
    // --- Dissolves (D025 §3–§5) ------------------------------------------------------

    public TimelineEditResult AddTransition(Guid leftClipId, Guid rightClipId, MediaTime duration)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (ResolveCut(plan, leftClipId, rightClipId, out var track, out var left, out var right) is { } cutError)
            return TimelineEditResult.Fail(cutError);
        var noun = TransitionNoun(track);
        if (CutHas(track, left) is { } cutHas) return TimelineEditResult.Fail(cutHas);

        // The track's kind decides the type: a dissolve on a video track, a crossfade on an audio track (D032 R9).
        var typeId = TransitionRules.TypeFor(track.Type);
        var rate = plan.Rate;
        var frames = TransitionRules.Frames(duration, rate);
        if (RangeError(typeId, frames, rate, noun) is { } rangeError)
            return TimelineEditResult.Fail(rangeError);
        if (TooLong(frames, MaxFrames(track, left, right, rate, except: null, typeId), noun, typeId) is { } tooLong)
            return TimelineEditResult.Fail(tooLong);

        return CommitAdd(plan, track, left, right, typeId, frames, noun);
    }

    public TimelineEditResult AddTransition(Guid leftClipId, Guid rightClipId, string typeId)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (ResolveCut(plan, leftClipId, rightClipId, out var track, out var left, out var right, typeId: typeId) is { } cutError)
            return TimelineEditResult.Fail(cutError);
        if (!TransitionRules.Allows(track.Type, typeId)) return TimelineEditResult.Fail(Messages.Transition_WrongType);
        var noun = TransitionNoun(track);
        if (CutHas(track, left) is { } cutHas) return TimelineEditResult.Fail(cutHas);

        // D044 §6: the type's default, or the longest that fits when that is shorter (never below the type's minimum);
        // never another type.
        var rate = plan.Rate;
        var fits = MaxFrames(track, left, right, rate, except: null, typeId);
        if (TransitionRules.ClampFrames(typeId, TransitionRules.DefaultFramesOf(typeId, rate), fits, rate) is not { } frames)
            return TimelineEditResult.Fail(NothingFits(typeId, noun));

        return CommitAdd(plan, track, left, right, typeId, frames, noun);
    }

    private TimelineEditResult CommitAdd(EditPlan plan, Track track, Clip left, Clip right, string typeId, long frames, string noun)
    {
        var transition = new Transition
        {
            TransitionTypeId = typeId, Duration = MediaTime.FromFrame(frames, plan.Rate),
            LeftClipId = left.Id, RightClipId = right.Id, Direction = TransitionRules.DefaultDirection(typeId)
        };
        plan.AddTransition(track, transition);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(IsNamed(track, typeId)
                ? string.Format(Messages.Transition_CantAddNamed, TypeName(typeId), error)
                : string.Format(Tr(noun, Messages.Transition_CantAddDissolve, Messages.Transition_CantAddCrossfade), error));

        var description = typeId is TransitionRules.CrossDissolve or TransitionRules.Crossfade ? $"Add {Title(noun)}" : "Add Transition";
        ExecuteTracked(new NotifyingCommand(new AddTransitionCommand(track, transition, description), _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { left.Id, right.Id }, TransitionId = transition.Id };
    }

    public TimelineEditResult SetTransitionType(Guid transitionId, string typeId)
    {
        if (FindTransition(transitionId) is not { } found) return TimelineEditResult.Fail(Messages.Edit_DissolveGone);
        var (track, transition) = found;
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));
        if (!TransitionRules.Allows(track.Type, typeId)) return TimelineEditResult.Fail(Messages.Transition_WrongType);
        if (typeId == transition.TransitionTypeId) return TimelineEditResult.Unchanged();

        // D044 §6: the duration is kept when it lies in the new type's range, else clamped to the nearest allowed value
        // (the longest that fits for a type needing handles); one step with the type, never merged.
        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var noun = TransitionNoun(track);
        var left = track.Clips.First(c => c.Id == transition.LeftClipId);
        var right = track.Clips.First(c => c.Id == transition.RightClipId);
        var fits = MaxFrames(track, left, right, rate, except: transition, typeId);
        if (TransitionRules.ClampFrames(typeId, TransitionRules.Frames(transition.Duration, rate), fits, rate) is not { } frames)
            return TimelineEditResult.Fail(NothingFits(typeId, noun));

        var before = TransitionState.Capture(track, transition);
        var stored = frames == TransitionRules.Frames(transition.Duration, rate) ? transition.Duration : MediaTime.FromFrame(frames, rate);
        var after = before with { TypeId = typeId, Direction = TransitionRules.DefaultDirection(typeId), Duration = stored };
        plan.UpdateTransition(transition, after);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(CantChange(track, transition, error));

        ExecuteTracked(new NotifyingCommand(new UpdateTransitionCommand(transition, before, after, "Change Transition Type"),
            _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { left.Id, right.Id }, TransitionId = transition.Id };
    }

    public TimelineEditResult SetTransitionDirection(Guid transitionId, TransitionDirection direction)
    {
        if (FindTransition(transitionId) is not { } found) return TimelineEditResult.Fail(Messages.Edit_DissolveGone);
        var (track, transition) = found;
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));
        if (!TransitionRules.HasDirection(transition.TransitionTypeId) || !Enum.IsDefined(direction))
            return TimelineEditResult.Fail(Messages.Transition_NoDirection);
        if (transition.Direction == direction) return TimelineEditResult.Unchanged();

        var before = TransitionState.Capture(track, transition);
        ExecuteTracked(new NotifyingCommand(
            new UpdateTransitionCommand(transition, before, before with { Direction = direction }, "Change Push Direction"),
            _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { transition.LeftClipId, transition.RightClipId }, TransitionId = transition.Id };
    }

    public TimelineEditResult RemoveTransition(Guid transitionId)
    {
        if (FindTransition(transitionId) is not { } found) return TimelineEditResult.Fail(Messages.Edit_DissolveGone);
        var (track, transition) = found;
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        ExecuteTracked(new NotifyingCommand(new RemoveTransitionCommand(track, transition, $"Remove {Title(TransitionNoun(track))}"),
            _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { transition.LeftClipId, transition.RightClipId } };
    }

    public TimelineEditResult SetTransitionDuration(Guid transitionId, MediaTime duration)
    {
        if (FindTransition(transitionId) is not { } found) return TimelineEditResult.Fail(Messages.Edit_DissolveGone);
        var (track, transition) = found;
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var noun = TransitionNoun(track);
        var typeId = transition.TransitionTypeId;
        var frames = TransitionRules.Frames(duration, rate);
        var stored = MediaTime.FromFrame(frames, rate);
        if (stored == transition.Duration) return TimelineEditResult.Unchanged();
        if (RangeError(typeId, frames, rate, noun) is { } rangeError)
            return TimelineEditResult.Fail(rangeError);

        var left = track.Clips.First(c => c.Id == transition.LeftClipId);
        var right = track.Clips.First(c => c.Id == transition.RightClipId);
        if (TooLong(frames, MaxFrames(track, left, right, rate, except: transition, typeId), noun, typeId) is { } tooLong)
            return TimelineEditResult.Fail(tooLong);

        var before = TransitionState.Capture(track, transition);
        var after = before with { Duration = stored };
        plan.UpdateTransition(transition, after);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(CantChange(track, transition, error));

        ExecuteTracked(new NotifyingCommand(new UpdateTransitionCommand(transition, before, after, $"Change {Title(noun)} Duration"),
            _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { left.Id, right.Id }, TransitionId = transition.Id };
    }

    public long? MaxTransitionFrames(Guid leftClipId, Guid rightClipId)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (ResolveCut(plan, leftClipId, rightClipId, out var track, out var left, out var right, allowLocked: true) is not null)
            return null;
        var own = track.Transitions.FirstOrDefault(t => t.LeftClipId == left.Id);
        var type = own?.TransitionTypeId ?? TransitionRules.TypeFor(track.Type);
        return Math.Min(MaxFrames(track, left, right, plan.Rate, except: own, type), TransitionRules.MaxFramesOf(type, plan.Rate));   // D069 am. 2: ≤ 5 s
    }

    public long? MaxTransitionFrames(Guid leftClipId, Guid rightClipId, string typeId)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (ResolveCut(plan, leftClipId, rightClipId, out var track, out var left, out var right, allowLocked: true) is not null)
            return null;
        if (!TransitionRules.Allows(track.Type, typeId)) return null;
        var own = track.Transitions.FirstOrDefault(t => t.LeftClipId == left.Id);
        return Math.Min(MaxFrames(track, left, right, plan.Rate, except: own, typeId), TransitionRules.MaxFramesOf(typeId, plan.Rate));
    }

    /// <summary>The reason a duration of <paramref name="frames"/> is outside what the type allows (D044 §5), or null; the
    /// dissolve and the crossfade keep their minimum messages.</summary>
    private static string? RangeError(string typeId, long frames, FrameRate rate, string noun)
    {
        var min = TransitionRules.MinFramesOf(typeId, rate);
        var max = TransitionRules.MaxFramesOf(typeId, rate);
        if (frames >= min && frames <= max) return null;
        if (frames < min && typeId is TransitionRules.CrossDissolve or TransitionRules.Crossfade)
            return string.Format(Tr(noun, Messages.Transition_MinDissolve, Messages.Transition_MinCrossfade), min);
        return max == long.MaxValue
            ? string.Format(Messages.Transition_DurationMin, min)
            : string.Format(Messages.Transition_DurationRange, min, max);
    }

    /// <summary>Why not even the type's minimum fits the cut: the source handles for the crossfade, else the clips' length
    /// (D069 amendment 2: no video type needs handles).</summary>
    private static string NothingFits(string typeId, string noun) =>
        TransitionRules.NeedsSourceHandles(typeId) ? NotEnoughMediaFor(noun) : Messages.TransitionRule_ClipTooShort;

    /// <summary>What a transition of <paramref name="track"/> is called: a dissolve on a video track, a crossfade on an audio
    /// track (D032 R9).</summary>
    private static string TransitionNoun(Track track) => track.Type == TrackType.Audio ? "crossfade" : "dissolve";

    private static string Title(string noun) => char.ToUpperInvariant(noun[0]) + noun[1..];

    /// <summary>Two touching clips of one (unlocked) track, A left of B: a dissolve on a video track, a crossfade on an audio
    /// track (D032 R9).</summary>
    private static string? ResolveCut(EditPlan plan, Guid leftClipId, Guid rightClipId, out Track track, out Clip left, out Clip right,
        bool allowLocked = false, string? typeId = null)
    {
        track = null!;
        left = right = null!;
        if (Resolve(plan, new[] { leftClipId, rightClipId }, out var clips) is { } resolveError) return resolveError;
        if (clips.Count != 2) return IsNamed(typeId) ? Messages.TransitionRule_TwoClips : Messages.Edit_DissolveTwoClips;
        (left, right) = (clips[0], clips[1]);
        track = plan.TrackOf(left);
        if (plan.TrackOf(right) != track) return IsNamed(typeId) ? Messages.Edit_TransitionSameTrack : Messages.Edit_DissolveSameTrack;
        if (!allowLocked && track.IsLocked) return string.Format(Messages.Edit_TrackLocked, track.Name);
        if (left.TimelineEnd != right.TimelineStart)
            return Tr(track, typeId, Messages.Transition_NeedsMeetDissolve, Messages.Transition_NeedsMeetCrossfade, Messages.Transition_NeedsMeetNamed);
        // D045: a template starts and ends with its own animation; it takes no transition.
        if (left is TemplateClip || right is TemplateClip) return Messages.Transition_NotOnTemplate;
        return null;
    }

    /// <summary>The longest transition of <paramref name="typeId"/> the cut A | B can take (D025 §3–§4, D044 §3): the frames
    /// of each clip not used by its transition on the other edge, and the source handles for a type that needs them.
    /// <paramref name="except"/> is the cut's own transition.</summary>
    private long MaxFrames(Track track, Clip left, Clip right, FrameRate rate, Transition? except, string typeId)
    {
        var handles = TransitionRules.NeedsSourceHandles(typeId);
        var others = track.Transitions.Where(t => t != except).ToList();
        long PartAtStart(Clip clip) => others.Where(t => t.RightClipId == clip.Id)
            .Select(t => TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate)).AfterCut).DefaultIfEmpty(0).Max();
        long PartAtEnd(Clip clip) => others.Where(t => t.LeftClipId == clip.Id)
            .Select(t => TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate)).BeforeCut).DefaultIfEmpty(0).Max();

        var leftState = ClipState.Capture(left);
        var rightState = ClipState.Capture(right);
        return TransitionRules.MaxFrames(
            roomInLeft: DissolveHandles.Frames(leftState, rate) - PartAtStart(left),
            roomInRight: DissolveHandles.Frames(rightState, rate) - PartAtEnd(right),
            handleAfterLeft: handles ? DissolveHandles.After(left, leftState, AssetOf(left), rate) : DissolveHandles.Unlimited,
            handleBeforeRight: handles ? DissolveHandles.Before(right, rightState, AssetOf(right), rate) : DissolveHandles.Unlimited);
    }

    private static string? TooLong(long frames, long max, string noun, string typeId) =>
        frames <= max ? null
        : typeId is not (TransitionRules.CrossDissolve or TransitionRules.Crossfade)
            ? max < TransitionRules.MinFrames ? NothingFits(typeId, noun) : string.Format(Messages.Transition_TooLongType, frames, max)
        : max < TransitionRules.MinFrames ? (typeId == TransitionRules.Crossfade ? NotEnoughMediaFor(noun) : Messages.TransitionRule_ClipTooShort)
        : string.Format(Tr(noun, Messages.Transition_TooLongDissolve, Messages.Transition_TooLongCrossfade), frames, max);

    /// <summary><see cref="NotEnoughMedia"/> for a dissolve, the same words for a crossfade.</summary>
    private static string NotEnoughMediaFor(string noun) => Tr(noun, Messages.Transition_NotEnoughMediaDissolve, Messages.Transition_NotEnoughMediaCrossfade);

    /// <summary>The message of a dissolve or of a crossfade (D039: no noun is glued into a sentence).</summary>
    private static string Tr(string noun, string dissolve, string crossfade) => noun == "crossfade" ? crossfade : dissolve;

    private static string Tr(Track track, string dissolve, string crossfade) => Tr(TransitionNoun(track), dissolve, crossfade);

    /// <summary>True when a message about a transition of <paramref name="typeId"/> on <paramref name="track"/> names its
    /// type (D044): a library type other than the dissolve; a dissolve and a crossfade keep their own words.</summary>
    private static bool IsNamed(Track track, string? typeId) => track.Type == TrackType.Video && IsNamed(typeId);

    private static bool IsNamed(string? typeId) => typeId is not (null or TransitionRules.CrossDissolve or TransitionRules.Crossfade);

    /// <summary>The message of a dissolve, of a crossfade, or <paramref name="named"/> with the type's name for the other
    /// types of the library (D044).</summary>
    private static string Tr(Track track, string? typeId, string dissolve, string crossfade, string named) =>
        IsNamed(track, typeId) ? string.Format(named, TypeName(typeId!)) : Tr(track, dissolve, crossfade);

    /// <summary>The name of a library type in a message (the Inspector's and the menu's name).</summary>
    private static string TypeName(string typeId) => TransitionTypeNames.Of(typeId);

    /// <summary>The type a message about <paramref name="clip"/>'s transitions names: of the transitions at its
    /// <paramref name="edge"/> (both edges when null) — with <paramref name="handlesOnly"/> only those that need source
    /// handles — a library type before a dissolve; null when it has none.</summary>
    private static string? EdgeTypeOf(Track track, Clip clip, ClipEdge? edge = null, bool handlesOnly = false) =>
        track.Transitions
            .Where(t => (edge != ClipEdge.Start && t.LeftClipId == clip.Id) || (edge != ClipEdge.End && t.RightClipId == clip.Id))
            .Where(t => !handlesOnly || TransitionRules.NeedsSourceHandles(t.TransitionTypeId))
            .Select(t => t.TransitionTypeId)
            .OrderBy(id => !IsNamed(id))
            .FirstOrDefault();

    /// <summary>The refusal when the cut after <paramref name="left"/> already has a transition, naming that one; else null.</summary>
    private static string? CutHas(Track track, Clip left) =>
        track.Transitions.FirstOrDefault(t => t.LeftClipId == left.Id) is { } existing
            ? Tr(track, existing.TransitionTypeId, Messages.Transition_CutHasDissolve, Messages.Transition_CutHasCrossfade, Messages.Transition_CutHasNamed)
            : null;

    private static string CantChange(Track track, Transition transition, string error) =>
        IsNamed(track, transition.TransitionTypeId)
            ? string.Format(Messages.Transition_CantChangeNamed, TypeName(transition.TransitionTypeId), error)
            : string.Format(Tr(track, Messages.Transition_CantChangeDissolve, Messages.Transition_CantChangeCrossfade), error);

    private (Track Track, Transition Transition)? FindTransition(Guid id)
    {
        foreach (var track in Sequence.VideoTracks.Concat(Sequence.AudioTracks))
            if (track.Transitions.FirstOrDefault(t => t.Id == id) is { } transition)
                return (track, transition);
        return null;
    }
}
