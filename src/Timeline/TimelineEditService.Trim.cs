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

/// <summary>Trim, speed (D022), split, trim to the playhead (D030 §5) and slip (D030 §7) (split out, D075 amendment 1 — no change).</summary>
public sealed partial class TimelineEditService
{
    // --- Trim ------------------------------------------------------------------

    public TimelineEditResult TrimClip(Guid clipId, ClipEdge edge, MediaTime edgeTime, bool alone = false)
    {
        var (plan, _, error) = PlanTrim(clipId, edge, edgeTime, alone);
        if (error is not null) return TimelineEditResult.Fail(error);
        if (plan is null) return TimelineEditResult.Unchanged();

        if (!Commit(plan, "Trim Clip")) return TimelineEditResult.Unchanged();
        return TimelineEditResult.Ok(new[] { clipId }, TransitionNote(plan));
    }

    public (MediaTime Start, MediaTime End)? PreviewTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime, bool alone = false)
    {
        var (_, after, error) = PlanTrim(clipId, edge, edgeTime, alone);
        return error is null && after is { } state ? (state.Start, state.End) : null;
    }

    /// <summary>Clamped trim result. Plan is null when nothing would change; After is
    /// the resulting timing either way (null on error).</summary>
    private (EditPlan? Plan, ClipState? After, string? Error) PlanTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime, bool alone)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return (null, null, resolveError);
        if (CheckEditable(plan, clips) is { } editError) return (null, null, editError);

        var clip = clips[0];
        var track = plan.TrackOf(clip);
        var rate = plan.Rate;

        // D025 §5: a trim of the far edge keeps the part of the dissolve on the other edge inside the clip (the trimmed
        // edge's own dissolve, if any, goes when its cut opens).
        var (partAtStart, partAtEnd) = DissolveParts(track, clip, rate);
        var minFrames = Math.Max(1, edge == ClipEdge.End ? partAtStart : partAtEnd);

        var before = ClipState.Capture(clip);
        var after = TrimmedState(clip, track, edge, edgeTime.ToNearestFrame(rate), minFrames, rate);

        // AQ3: the partner's same edge moves by the same frames, by the same rule; where one of them is limited, both take
        // the smaller change. Alt + edge (alone) trims the clip by itself: a J / L cut (R6).
        var (partner, partnerError) = FollowingPartner(plan, clip, alone);
        if (partnerError is not null) return (null, null, partnerError);
        ClipState? partnerAfter = null;
        if (partner is not null)
        {
            var partnerTrack = plan.TrackOf(partner);
            var partnerBefore = ClipState.Capture(partner);
            var (partnerAtStart, partnerAtEnd) = DissolveParts(partnerTrack, partner, rate);
            var partnerMin = Math.Max(1, edge == ClipEdge.End ? partnerAtStart : partnerAtEnd);
            for (var round = 0; round < 3; round++)
            {
                var delta = EdgeFrame(after, edge, rate) - EdgeFrame(before, edge, rate);
                partnerAfter = TrimmedState(partner, partnerTrack, edge, EdgeFrame(partnerBefore, edge, rate) + delta, partnerMin, rate);
                var partnerDelta = EdgeFrame(partnerAfter.Value, edge, rate) - EdgeFrame(partnerBefore, edge, rate);
                if (partnerDelta == delta) break;
                after = TrimmedState(clip, track, edge, EdgeFrame(before, edge, rate) + partnerDelta, minFrames, rate);
            }
            if (EdgeFrame(partnerAfter!.Value, edge, rate) - EdgeFrame(partnerBefore, edge, rate) != EdgeFrame(after, edge, rate) - EdgeFrame(before, edge, rate))
                return (null, null, string.Format(Messages.Edit_CantTrim, PartnerCantFollow));
            if (partnerAfter != partnerBefore) plan.Update(partner, partnerTrack, partnerAfter.Value);
        }

        if (after == before) return (null, after, null);

        plan.Update(clip, track, after);
        if (Validate(plan) is { } error)
            return (null, null, string.Format(Messages.Edit_CantTrim, error));

        return (plan, after, null);
    }

    /// <summary>The timing of <paramref name="clip"/> with <paramref name="edge"/> trimmed to
    /// <paramref name="targetFrame"/>, clamped to the neighbours on <paramref name="track"/>, the source (start ≥ 0, its
    /// end) and <paramref name="minFrames"/> — the trim's one rule (D008 / D022 / D025), shared by the edge drag and the
    /// trim to the playhead (D030 §5).</summary>
    private ClipState TrimmedState(Clip clip, Track track, ClipEdge edge, long targetFrame, long minFrames, FrameRate rate) =>
        TrimmedState(clip, ClipState.Capture(clip), track.Clips.Where(c => c != clip).ToList(), edge, targetFrame, minFrames, rate);

    /// <summary>The same rule for <paramref name="clip"/> at the timing <paramref name="state"/> (planned, or its own) and
    /// the neighbours <paramref name="others"/> — none for a ripple, whose neighbours move instead of limiting it.</summary>
    private ClipState TrimmedState(Clip clip, ClipState state, IReadOnlyList<Clip> others, ClipEdge edge, long targetFrame,
        long minFrames, FrameRate rate)
    {
        var startFrame = state.Start.ToNearestFrame(rate);
        var endFrame = state.End.ToNearestFrame(rate);
        var sourceIn = clip is MediaBackedClip ? state.SourceIn : MediaTime.Zero;
        var sourceDuration = SourceDuration(clip);

        ClipState after;
        if (clip is MediaBackedClip && !state.Speed.IsNormal)
        {
            after = PlanTrimAtSpeed(state, edge, startFrame, endFrame, targetFrame, others, sourceDuration, rate, minFrames);
        }
        else if (edge == ClipEdge.End)
        {
            var hi = others.Where(c => c.TimelineStart >= state.End)
                .Select(c => c.TimelineStart.ToNearestFrame(rate))
                .DefaultIfEmpty(long.MaxValue).Min();
            if (sourceDuration is { } duration)
                hi = Math.Min(hi, startFrame + FrameMath.MaxWholeFrames(duration - sourceIn, rate));

            var newEnd = Math.Clamp(targetFrame, startFrame + minFrames, Math.Max(startFrame + minFrames, hi));
            after = ClipState.FromFrames(startFrame, newEnd, sourceIn, rate);
        }
        else
        {
            var lo = others.Where(c => c.TimelineEnd <= state.Start)
                .Select(c => c.TimelineEnd.ToNearestFrame(rate))
                .DefaultIfEmpty(0).Max();
            if (sourceDuration is not null)
                lo = Math.Max(lo, FrameMath.CeilingFrame(state.Start - sourceIn, rate)); // keeps SourceIn ≥ 0

            var newStart = Math.Clamp(targetFrame, Math.Min(lo, endFrame - minFrames), endFrame - minFrames);
            var newStartTime = MediaTime.FromFrame(newStart, rate);

            // Video/audio: the in-point follows the edge so the content under the
            // playhead doesn't shift. Images have no source timing: SourceIn stays 0.
            var newSourceIn = sourceDuration is not null ? sourceIn + (newStartTime - state.Start) : sourceIn;
            after = ClipState.FromFrames(newStart, endFrame, newSourceIn, rate);
        }
        return after;
    }

    /// <summary>
    /// Trim of a clip at a speed other than 1× (D022), in the same one rounding rule
    /// (<see cref="SpeedTiming.SourceLength"/>): the end edge sets SourceOut = SourceIn +
    /// SourceLength(N); the start edge moves SourceIn by SourceLength(Δ) frames' worth (so the content
    /// under the playhead stays) and keeps SourceOut, normalized only if the invariant is missed by
    /// the rounding. Limits: neighbours, <paramref name="minFrames"/> (one frame, or a dissolve's part), SourceIn ≥ 0
    /// and the end of the source.
    /// </summary>
    private static ClipState PlanTrimAtSpeed(ClipState clip, ClipEdge edge, long startFrame, long endFrame, long targetFrame,
        IReadOnlyList<Clip> others, MediaTime? sourceDuration, FrameRate rate, long minFrames)
    {
        var speed = clip.Speed;
        if (edge == ClipEdge.End)
        {
            var hi = others.Where(c => c.TimelineStart >= clip.End)
                .Select(c => c.TimelineStart.ToNearestFrame(rate))
                .DefaultIfEmpty(long.MaxValue).Min();
            if (sourceDuration is { } duration)
                hi = Math.Min(hi, startFrame + SpeedTiming.FramesFor(duration - clip.SourceIn, speed, rate));

            var newEnd = Math.Clamp(targetFrame, startFrame + minFrames, Math.Max(startFrame + minFrames, hi));
            var sourceOut = clip.SourceIn + SpeedTiming.SourceLength(newEnd - startFrame, speed, rate);
            return ClipState.FromFrames(startFrame, newEnd, clip.SourceIn, sourceOut, speed, rate);
        }

        var lo = others.Where(c => c.TimelineEnd <= clip.Start)
            .Select(c => c.TimelineEnd.ToNearestFrame(rate))
            .DefaultIfEmpty(0).Max();
        lo = Math.Max(lo, startFrame - SpeedTiming.FramesFor(clip.SourceIn, speed, rate)); // keeps SourceIn ≥ 0

        var newStart = Math.Clamp(targetFrame, Math.Min(lo, endFrame - minFrames), endFrame - minFrames);
        var delta = newStart - startFrame;
        var sourceIn = delta >= 0
            ? clip.SourceIn + SpeedTiming.SourceLength(delta, speed, rate)
            : clip.SourceIn - SpeedTiming.SourceLength(-delta, speed, rate);
        var (normalizedIn, normalizedOut) = ClipState.Normalize(endFrame - newStart, sourceIn, clip.SourceOut, speed, rate);
        return ClipState.FromFrames(newStart, endFrame, normalizedIn, normalizedOut, speed, rate);
    }

    // --- Speed (D022) ---------------------------------------------------------------

    public TimelineEditResult SetClipSpeed(Guid clipId, ClipSpeed speed)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        if (clips[0] is not (VideoClip or AudioClip)) return TimelineEditResult.Fail(Messages.Edit_SpeedOnlyMedia);
        if (CheckEditable(plan, clips) is { } editError) return TimelineEditResult.Fail(editError);

        var clip = (MediaBackedClip)clips[0];
        if (clip.Speed == speed) return TimelineEditResult.Unchanged();

        // Back to the speed the clip's current speed step started from, when that step removed dissolves (D025 §5): this
        // is undoing the step — the clip gets its timing back and the dissolves return. (A value typed back, the arrows
        // back, or Ctrl+Z inside the Speed field, which is the field's own text undo, all end here.)
        if (_undoRedo.NextUndo is NotifyingCommand { Inner: SetClipSpeedCommand step } && step.Clip == clip &&
            step.Changes.Count > 0 && step.Before.Speed == speed && step.After == ClipState.Capture(clip))
        {
            _undoRedo.Undo();
            return TimelineEditResult.Ok(new[] { clip.Id },
                step.Changes.OfType<RemoveTransitionCommand>().FirstOrDefault() is { } back
                    ? Tr(back.Track, back.Transition.TransitionTypeId, Messages.Edit_DissolveBack, Messages.Edit_DissolveBack, Messages.Edit_TransitionBackNamed)
                    : null);
        }

        // D048 §3.8: speed changes merged into one Undo step scale the linked overlays from the step's first state, not from
        // the rounded state in between — the open step is taken back and the change planned again from its start; the new
        // step then holds the whole change. Only for a clip with overlays (D022's own merge is kept for the others).
        if (_undoRedo.NextUndo is NotifyingCommand { Inner: SetClipSpeedCommand open } && open.Clip == clip &&
            open.After == ClipState.Capture(clip) && HasLinkedOverlays(plan, clip))
        {
            _undoRedo.Undo();
            var replanned = ApplySpeed(clipId, speed);
            if (!replanned.Success) _undoRedo.Redo();
            return replanned;
        }
        return ApplySpeed(clipId, speed);
    }

    /// <summary>Whether <paramref name="clip"/> or its linked partner carries D048 overlays: an anchored template or a
    /// Duration-mode hold animation.</summary>
    private static bool HasLinkedOverlays(EditPlan plan, Clip clip)
    {
        foreach (var c in new[] { clip, PartnerOf(plan, clip) })
            if (c is VideoClip video && (video.Routes.Any(r => r.Animation is { Mode: HoldAnimationMode.Duration }) || AnchoredTemplates(plan, video).Count > 0))
                return true;
        return false;
    }

    /// <summary>The speed change itself, planned from the model's current state.</summary>
    private TimelineEditResult ApplySpeed(Guid clipId, ClipSpeed speed)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        var clip = (MediaBackedClip)clips[0];
        if (clip.Speed == speed) return TimelineEditResult.Unchanged();

        // The speed never changes the source range: the frame count follows from it.
        var rate = plan.Rate;
        var frames = SpeedTiming.FramesFor(clip.SourceOut - clip.SourceIn, speed, rate);
        if (frames < 1) return TimelineEditResult.Fail(string.Format(Messages.Edit_SpeedTooShort, speed));

        var startFrame = clip.TimelineStart.ToNearestFrame(rate);
        var before = ClipState.Capture(clip);
        var after = ClipState.FromFrames(startFrame, startFrame + frames, clip.SourceIn, clip.SourceOut, speed, rate);
        plan.Update(clip, plan.TrackOf(clip), after);

        // AQ6: the partner takes the same speed by the same rule (its own source range gives its frame count).
        var (partner, partnerError) = FollowingPartner(plan, clip);
        if (partnerError is not null) return TimelineEditResult.Fail(partnerError);
        ClipChange? partnerChange = null;
        if (partner is not null && partner.Speed != speed)
        {
            var partnerFrames = SpeedTiming.FramesFor(partner.SourceOut - partner.SourceIn, speed, rate);
            if (partnerFrames < 1) return TimelineEditResult.Fail(string.Format(Messages.Edit_SpeedTooShortLinked, speed));
            var partnerStart = partner.TimelineStart.ToNearestFrame(rate);
            var partnerAfter = ClipState.FromFrames(partnerStart, partnerStart + partnerFrames, partner.SourceIn, partner.SourceOut, speed, rate);
            var partnerTrack = plan.TrackOf(partner);
            plan.Update(partner, partnerTrack, partnerAfter);
            partnerChange = new ClipChange(partner, partnerTrack, ClipState.Capture(partner), partnerTrack, partnerAfter);
        }
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_CantChangeSpeed, error));

        // A speed change of A moves its end: its dissolve goes (D025 §5), and fades longer than the shorter clip are cut
        // (§2), in the same step — which still merges with the next speed changes of the clip, so one Undo restores the
        // speed, the fades and the dissolve together.
        var changes = plan.BuildPropertyCommands();
        changes.AddRange(plan.BuildTransitionCommands());
        if (partnerChange is not null) changes.Insert(0, new UpdateClipsCommand("Change Speed", new[] { partnerChange }));
        // D048 §3.8: the anchored templates and the Duration animation scale in the same step.
        var carried = plan.Updates.Where(u => plan.Carried.Contains(u.Clip))
            .Select(u => new ClipChange(u.Clip, plan.TrackOf(u.Clip), ClipState.Capture(u.Clip), u.To, u.After)).ToList();
        if (carried.Count > 0) changes.Add(new UpdateClipsCommand("Change Speed", carried));
        changes.AddRange(plan.BuildOverlayCommands());
        var command = new SetClipSpeedCommand(clip, before, after, changes.Count > 0 ? changes : null);
        ExecuteTracked(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { clip.Id }, TransitionNote(plan));
    }

    // --- Split -----------------------------------------------------------------

    public TimelineEditResult Split(MediaTime at, IReadOnlyCollection<Guid>? clipIds = null)
    {
        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var atFrame = at.ToNearestFrame(rate);

        List<Clip> candidates;
        var explicitSelection = clipIds is { Count: > 0 };
        if (explicitSelection)
        {
            if (Resolve(plan, clipIds!, out var selected) is { } resolveError) return TimelineEditResult.Fail(resolveError);
            candidates = selected;
        }
        else
        {
            candidates = plan.AllTracks.Where(t => !t.IsLocked).SelectMany(t => t.Clips).ToList();
        }

        // AQ5: a linked clip's partner covering the point is split too — refused with the whole split when it is locked.
        var toSplit = WithPartnersCovering(plan, candidates
            .Where(c => c.TimelineStart.ToNearestFrame(rate) < atFrame && atFrame < c.TimelineEnd.ToNearestFrame(rate)), atFrame);

        if (toSplit.Count == 0)
            return TimelineEditResult.Fail(explicitSelection
                ? Messages.Edit_PlayheadNotInside
                : Messages.Edit_NoClipToSplit);
        toSplit.AddRange(AnchoredSpanning(plan, toSplit, atFrame).ToList());   // D048 §3.6: their templates are split too

        if (CheckEditable(plan, toSplit) is { } editError) return TimelineEditResult.Fail(editError);

        var ids = new List<Guid>();
        var splits = new List<(Clip Left, Clip Right)>();
        foreach (var clip in toSplit)
        {
            if (!PlanSplitClip(plan, plan.TrackOf(clip), clip, ClipState.Capture(clip), atFrame, out var rightClip))
                return TimelineEditResult.Fail(Tr(plan.TrackOf(clip),
                    ZoneAt(plan.TrackOf(clip), clip, ClipState.Capture(clip), atFrame, plan.Rate)?.TransitionTypeId,
                    Messages.Split_InsideDissolve, Messages.Split_InsideCrossfade, Messages.Split_InsideNamed));
            ids.Add(clip.Id);
            ids.Add(rightClip.Id);
            splits.Add((clip, rightClip));
        }
        LinkSplitParts(splits);
        AnchorSplitParts(plan, splits, atFrame);

        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_CantSplit, error));

        Commit(plan, toSplit.Count == 1 ? "Split Clip" : "Split Clips");
        return TimelineEditResult.Ok(ids, TransitionNote(plan));
    }

    /// <summary>
    /// The split rule for one clip of <paramref name="track"/> at the timing <paramref name="state"/> (its own, or the one
    /// a plan gives it), cut at <paramref name="atFrame"/> strictly inside it: the left part keeps the clip (its id and
    /// its dissolve at the start), <paramref name="rightClip"/> is a new clip planned as an insert; the source cut follows
    /// D022, the fades stay on the outer edges (D025 §2), a dissolve at the clip's end moves to the right part. Shared by
    /// Split and Insert / Overwrite (D031 SQ6 / SQ9). False — nothing planned — when the cut is inside a dissolve's zone
    /// (D025 §5).
    /// </summary>
    /// <summary>The transition of <paramref name="clip"/> whose zone a split at <paramref name="atFrame"/> would fall
    /// strictly inside (D025 §5), or null.</summary>
    private static Transition? ZoneAt(Track track, Clip clip, ClipState state, long atFrame, FrameRate rate)
    {
        var startFrame = state.Start.ToNearestFrame(rate);
        var endFrame = state.End.ToNearestFrame(rate);
        foreach (var transition in track.Transitions)
        {
            var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(transition.Duration, rate));
            if ((transition.LeftClipId == clip.Id && atFrame > endFrame - beforeCut) ||
                (transition.RightClipId == clip.Id && atFrame < startFrame + afterCut))
                return transition;
        }
        return null;
    }

    private bool PlanSplitClip(EditPlan plan, Track track, Clip clip, ClipState state, long atFrame, out Clip rightClip)
    {
        var rate = plan.Rate;
        var startFrame = state.Start.ToNearestFrame(rate);
        var endFrame = state.End.ToNearestFrame(rate);
        var sourceIn = clip is MediaBackedClip ? state.SourceIn : MediaTime.Zero;
        var hasSource = SourceDuration(clip) is not null;
        rightClip = null!;

        // D025 §5: never inside a dissolve's zone.
        if (ZoneAt(track, clip, state, atFrame, rate) is not null) return false;

        ClipState left, right;
        if (clip is MediaBackedClip && !state.Speed.IsNormal)
        {
            // D022: the cut in the source is SourceIn + SourceLength(frames left of the split);
            // the right half keeps the original SourceOut (normalized only if rounding misses).
            var cut = state.SourceIn + SpeedTiming.SourceLength(atFrame - startFrame, state.Speed, rate);
            left = ClipState.FromFrames(startFrame, atFrame, state.SourceIn, cut, state.Speed, rate);
            var (rightIn, rightOut) = ClipState.Normalize(endFrame - atFrame, cut, state.SourceOut, state.Speed, rate);
            right = ClipState.FromFrames(atFrame, endFrame, rightIn, rightOut, state.Speed, rate);
        }
        else
        {
            left = ClipState.FromFrames(startFrame, atFrame, sourceIn, rate);
            right = ClipState.FromFrames(atFrame, endFrame, hasSource ? left.SourceOut : sourceIn, rate);
        }

        rightClip = CloneClip(clip);
        right.ApplyTo(rightClip);
        PlanSplitOverlays(plan, clip, state, rightClip, atFrame);   // D048 §3.6

        // D045 §3: a template's counter goes on from the cut in the right part.
        if (rightClip is TemplateClip template)
            template.CounterStart += MediaTime.FromFrame(atFrame - startFrame, rate);

        // D025 §2: the fades stay with the outer edges — the right part keeps the fade out, the left part the
        // fade in; the new inner edges have none.
        rightClip.FadeIn = MediaTime.Zero;
        // D064 §1: so do a video clip's sound-only fades (the volume points, in source time, go with both parts unchanged).
        if (rightClip is VideoClip rightVideo) rightVideo.SoundFadeIn = MediaTime.Zero;
        var leftSound = clip is VideoClip { SoundFadeOut.Ticks: not 0 } && AudioProperties.Of(clip) is { } sound
            ? sound with { SoundFadeOut = MediaTime.Zero }
            : (AudioProperties?)null;
        if (clip.FadeOut != MediaTime.Zero || leftSound is not null)
            plan.SetProperties(clip, new ClipPropertyValues(null, leftSound, null,
                clip.FadeOut != MediaTime.Zero ? new FadeProperties(clip.FadeIn, MediaTime.Zero) : null));

        // A dissolve at the clip's end moves to the right part (the left part keeps the clip's id and its dissolve at
        // the start).
        foreach (var transition in track.Transitions.Where(t => t.LeftClipId == clip.Id))
            plan.UpdateTransition(transition, plan.StateOf(transition) with { LeftClipId = rightClip.Id });

        plan.Update(clip, track, left);
        plan.Insert(track, rightClip);
        return true;
    }

    // --- Trim to the playhead (D030 §5) -----------------------------------------------

    public TimelineEditResult TrimToPlayhead(IReadOnlyCollection<Guid> clipIds, ClipEdge edge, MediaTime playhead, bool ripple)
    {
        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var at = playhead.ToNearestFrame(rate);
        List<Clip> selected;
        if (clipIds.Count == 0)
        {
            // D074 §1: nothing selected — the video clips the playhead is inside on the unlocked tracks (their linked sound
            // joins below, as for a selection; their anchored plates follow them as with any trim).
            selected = plan.AllTracks.Where(t => !t.IsLocked).SelectMany(t => t.Clips).OfType<VideoClip>()
                .Where(c => c.TimelineStart.ToNearestFrame(rate) < at && at < c.TimelineEnd.ToNearestFrame(rate)).Cast<Clip>().ToList();
            if (selected.Count == 0) return TimelineEditResult.Fail(Messages.Edit_NoVideoToTrim);
        }
        else if (Resolve(plan, clipIds, out selected) is { } resolveError) return TimelineEditResult.Fail(resolveError);

        // Q1: only selected clips with the playhead's frame strictly inside; the others are left as they are (as Split).
        var eligible = WithPartnersCovering(plan, selected
            .Where(c => c.TimelineStart.ToNearestFrame(rate) < at && at < c.TimelineEnd.ToNearestFrame(rate)), at)
            .OrderBy(c => c is VideoClip ? 0 : 1).ToList();   // AQ3: a linked pair's video clip leads a ripple
        if (eligible.Count == 0) return TimelineEditResult.Fail(Messages.Edit_PlayheadNotInside);
        if (CheckEditable(plan, eligible) is { } editError) return TimelineEditResult.Fail(editError);

        var trimmed = new List<Clip>();
        var keptAtCut = 0;      // plain trims not made: the trimmed edge is a dissolve's cut (Q4)
        var stopped = 0;        // trims stopped where a dissolve needs the clip's frames (Q4 / Q6)
        string? keptType = null, stoppedType = null;   // the types the notes name (D044)
        long? playheadAfter = null;
        foreach (var clip in eligible)
        {
            var track = plan.TrackOf(clip);
            var start = clip.TimelineStart.ToNearestFrame(rate);
            var end = clip.TimelineEnd.ToNearestFrame(rate);
            var wanted = edge == ClipEdge.Start ? at - start : end - at;

            // The frames each dissolve keeps inside the clip (D025 §5 zone fit). A plain trim opens the trimmed edge, so a
            // dissolve there could not stay: that edge is not trimmed (Q4). A ripple keeps the trimmed edge's cut — its
            // dissolve stays and keeps its part; the far edge's dissolve always does (Q6).
            if (ripple)
            {
                if (plan.Changes(clip)) continue;    // the partner a linked clip's ripple trim already took along (AQ3)

                // The ripple trim's one planner, shared with the Shift + edge drag (D030 §5 / §6).
                var (changed, wasStopped, rippleError) = PlanRippleTrim(plan, clip, track, edge, at);
                if (rippleError is not null) return TimelineEditResult.Fail(string.Format(Messages.Edit_CantTrim, rippleError));
                if (wasStopped && stopped++ == 0) stoppedType = EdgeTypeOf(track, clip);
                if (!changed) continue;
                if (edge == ClipEdge.Start) playheadAfter = Math.Min(playheadAfter ?? long.MaxValue, start);   // Q5
                trimmed.Add(clip);
                continue;
            }

            var (partAtStart, partAtEnd) = DissolveParts(track, clip, rate);
            long allowed;
            if ((edge == ClipEdge.Start ? partAtStart : partAtEnd) > 0)
            {
                allowed = 0;
                if (keptAtCut++ == 0) keptType = EdgeTypeOf(track, clip, edge);
            }
            else
            {
                var minFrames = Math.Max(1, edge == ClipEdge.Start ? partAtEnd : partAtStart);
                allowed = Math.Min(wanted, Math.Max(0, end - start - minFrames));
                if (allowed < wanted && stopped++ == 0) stoppedType = EdgeTypeOf(track, clip);
            }
            if (allowed == 0) continue;

            // The edge trim's own rule (source mapping, speed — D022) to the allowed frame; inward, so no neighbour limits it.
            plan.Update(clip, track, TrimmedState(clip, track, edge, edge == ClipEdge.Start ? start + allowed : end - allowed, 1, rate));
            trimmed.Add(clip);
        }

        var notes = Join(
            keptAtCut == 0 ? null : IsNamed(keptType)
                ? string.Format(edge == ClipEdge.Start ? Messages.Edit_NotTrimmedStartNamed : Messages.Edit_NotTrimmedEndNamed, TypeName(keptType!))
                : edge == ClipEdge.Start
                ? Messages.Edit_NotTrimmedStart
                : Messages.Edit_NotTrimmedEnd,
            stopped == 0 ? null : StopNote(stoppedType));
        if (trimmed.Count == 0) return TimelineEditResult.Fail(notes!);

        if (Validate(plan) is { } error) return TimelineEditResult.Fail(string.Format(Messages.Edit_CantTrim, error));

        if (!Commit(plan, (ripple, edge) switch
        {
            (false, ClipEdge.Start) => "Trim Start to Playhead",
            (false, _) => "Trim End to Playhead",
            (true, ClipEdge.Start) => "Ripple Trim Start to Playhead",
            _ => "Ripple Trim End to Playhead"
        })) return TimelineEditResult.Unchanged();
        return new TimelineEditResult
        {
            Success = true,
            ClipIds = trimmed.Select(c => c.Id).ToList(),
            Message = Join(notes, TransitionNote(plan)),
            Playhead = playheadAfter is { } frame ? MediaTime.FromFrame(frame, rate) : null
        };
    }

    /// <summary>Ripple trim of <paramref name="clip"/>'s <paramref name="edge"/> to <paramref name="targetFrame"/> (D030 §5 /
    /// §6), planned into <paramref name="plan"/> — the one rule of Shift+Q / Shift+W and the Shift + edge drag. The clip
    /// keeps its start; its length changes by whole frames; every clip of its track that starts at or after its old end
    /// moves by the same frames (other tracks, markers, the playhead stay). Inward the clip keeps the frames its dissolves
    /// need (Q4 / Q6: <c>Stopped</c>); outward the source limits it (the edge trim's clamp) and a dissolve on the dragged
    /// edge keeps its source handle (D025 §4, <c>Stopped</c>). <c>Changed</c> false: nothing to do (the same frame, or no
    /// room). The caller checks the lock and validates the plan.</summary>
    private (bool Changed, bool Stopped, string? Error) PlanRippleTrim(EditPlan plan, Clip clip, Track track, ClipEdge edge, long targetFrame)
    {
        var rate = plan.Rate;
        var start = clip.TimelineStart.ToNearestFrame(rate);
        var end = clip.TimelineEnd.ToNearestFrame(rate);
        var change = edge == ClipEdge.End ? targetFrame - end : start - targetFrame;    // frames added (> 0) or removed (< 0)
        if (change == 0) return (false, false, null);

        // AQ3 / AQ4: the partner's same edge changes by the same frames (its start stays, as the clip's does) — limited by
        // the room after it, since its neighbours don't move; both take the smaller change.
        var (partner, partnerError) = FollowingPartner(plan, clip);
        if (partnerError is not null) return (false, false, partnerError);
        if (partner is null) return PlanRippleTrimAlone(plan, clip, track, change, edge);

        var partnerTrack = plan.TrackOf(partner);
        var (_, partnerFrames, partnerStopped, partnerError2) =
            RippleTrimmedState(plan, partner, partnerTrack, edge, change > 0 ? Math.Min(change, RoomAfter(partner, partnerTrack, plan.Rate)) : change);
        if (partnerError2 is not null) return (false, partnerStopped, partnerError2);
        if (partnerFrames == 0) return (false, true, null);
        var result = PlanRippleTrimAlone(plan, clip, track, Math.Abs(partnerFrames) < Math.Abs(change) ? partnerFrames : change, edge);
        if (result.Error is not null || !result.Changed) return result;

        var frames = plan.Updates.Single(u => u.Clip == clip).After.End.ToNearestFrame(plan.Rate) - clip.TimelineEnd.ToNearestFrame(plan.Rate);
        var (partnerAfter, followed, _, followError) = RippleTrimmedState(plan, partner, partnerTrack, edge, frames);
        if (followError is not null) return (false, result.Stopped, followError);
        if (followed != frames) return (false, result.Stopped, PartnerCantFollow);
        plan.Update(partner, partnerTrack, partnerAfter!.Value);
        return (true, result.Stopped || partnerStopped, null);
    }

    /// <summary><see cref="PlanRippleTrim"/> for one clip: <paramref name="change"/> frames added (&gt; 0) or removed at
    /// <paramref name="edge"/>, the later clips of its track moved by the frames the clip's end moves.</summary>
    private (bool Changed, bool Stopped, string? Error) PlanRippleTrimAlone(EditPlan plan, Clip clip, Track track, long change, ClipEdge edge)
    {
        var rate = plan.Rate;
        var end = clip.TimelineEnd.ToNearestFrame(rate);
        var (after, frames, stopped, error) = RippleTrimmedState(plan, clip, track, edge, change);
        if (error is not null) return (false, stopped, error);
        if (after is null) return (false, stopped, null);

        plan.Update(clip, track, after.Value);
        foreach (var later in track.Clips.Where(c => c != clip && c.TimelineStart.ToNearestFrame(rate) >= end))
        {
            if (PlanShift(plan, later, track, frames) is { } shiftError)
                return (false, stopped, shiftError);
        }
        return (true, stopped, null);
    }

    /// <summary>The ripple trim's timing of <paramref name="clip"/> with <paramref name="change"/> frames added (&gt; 0) or
    /// removed at <paramref name="edge"/>: its start stays, its end moves by <c>Frames</c> (after the dissolve and source
    /// limits; <c>After</c> null when that is 0).</summary>
    private (ClipState? After, long Frames, bool Stopped, string? Error) RippleTrimmedState(EditPlan plan, Clip clip, Track track,
        ClipEdge edge, long change)
    {
        var rate = plan.Rate;
        var start = clip.TimelineStart.ToNearestFrame(rate);
        var end = clip.TimelineEnd.ToNearestFrame(rate);
        if (change == 0) return (null, 0, false, null);

        long frames;
        bool stopped;
        if (change < 0)
        {
            var (partAtStart, partAtEnd) = DissolveParts(track, clip, rate);
            var minFrames = Math.Max(1, partAtStart + partAtEnd);
            frames = -Math.Min(-change, Math.Max(0, end - start - minFrames));
            stopped = frames != change && minFrames > 1;    // the dissolve stopped it, not the one-frame minimum
        }
        else
        {
            // A dissolve on the dragged edge needs the source beyond it: A after its end, B before its start (D025 §4) — only
            // the types that need handles (D044 §3: Dissolve, Push; D032: the crossfade).
            var needed = 0L;
            foreach (var t in track.Transitions.Where(t => TransitionRules.NeedsSourceHandles(t.TransitionTypeId)))
            {
                var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate));
                if (edge == ClipEdge.End && t.LeftClipId == clip.Id) needed = afterCut;
                if (edge == ClipEdge.Start && t.RightClipId == clip.Id) needed = beforeCut;
            }
            var room = long.MaxValue;
            if (needed > 0)
            {
                var state = ClipState.Capture(clip);
                var handle = edge == ClipEdge.End
                    ? DissolveHandles.After(clip, state, AssetOf(clip), rate)
                    : DissolveHandles.Before(clip, state, AssetOf(clip), rate);
                room = Math.Max(0, handle - needed);
            }
            frames = Math.Min(change, room);
            stopped = frames != change;
        }
        if (frames == 0) return (null, 0, stopped, null);

        // The edge trim's own rule (source mapping, speed — D022) with no neighbour: they move. For the start, in the
        // clip's own frame of reference — moved right by the frames to add, trimmed back to its start, then moved so that
        // it starts where it did (the move's rule) — so the trimmed timeline never runs before 0.
        ClipState after;
        if (edge == ClipEdge.End)
        {
            after = TrimmedState(clip, ClipState.Capture(clip), Array.Empty<Clip>(), edge, end + frames, 1, rate);
        }
        else
        {
            var added = Math.Max(0, frames);
            var moved = ClipState.Capture(clip);
            if (added > 0)
            {
                var (shifted, moveError) = ShiftedState(clip, moved, added, rate);
                if (moveError is not null) return (null, 0, stopped, moveError);
                moved = shifted;
            }
            after = TrimmedState(clip, moved, Array.Empty<Clip>(), edge, start + Math.Max(0, -frames), 1, rate);
            var back = start - after.Start.ToNearestFrame(rate);
            if (back != 0)
            {
                var (shifted, backError) = ShiftedState(clip, after, back, rate);
                if (backError is not null) return (null, 0, stopped, backError);
                after = shifted;
            }
        }
        frames = after.End.ToNearestFrame(rate) - end;                                   // after the source clamp
        return frames == 0 ? (null, 0, stopped, null) : (after, frames, stopped, null);
    }

    public TimelineEditResult RippleTrimClip(Guid clipId, ClipEdge edge, MediaTime edgeTime)
    {
        var note = StopNoteOf(clipId, edge, edgeTime);       // before the trim: which way it goes
        var (plan, stopped, error) = PlanRippleTrimOf(clipId, edge, edgeTime);
        if (error is not null) return TimelineEditResult.Fail(error);
        if (plan is null) return stopped ? TimelineEditResult.Fail(note) : TimelineEditResult.Unchanged();

        if (!Commit(plan, edge == ClipEdge.Start ? "Ripple Trim Start" : "Ripple Trim End")) return TimelineEditResult.Unchanged();
        return TimelineEditResult.Ok(new[] { clipId }, Join(stopped ? note : null, TransitionNote(plan)));
    }

    /// <summary><see cref="StopNote"/> for a ripple trim of <paramref name="edge"/> of the clip <paramref name="clipId"/> to
    /// <paramref name="edgeTime"/>: outward it is stopped by the handle of the transition at that edge (a type that needs
    /// one), inward by the frames the transitions at either edge keep in the clip.</summary>
    private string StopNoteOf(Guid clipId, ClipEdge edge, MediaTime edgeTime)
    {
        foreach (var track in Sequence.VideoTracks.Concat(Sequence.AudioTracks))
        {
            if (track.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip) continue;
            var outward = edge == ClipEdge.End ? edgeTime > clip.TimelineEnd : edgeTime < clip.TimelineStart;
            return StopNote(outward ? EdgeTypeOf(track, clip, edge, handlesOnly: true) : EdgeTypeOf(track, clip));
        }
        return StopNote(null);
    }

    public RippleTrimPreview? PreviewRippleTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime)
    {
        var (plan, stopped, error) = PlanRippleTrimOf(clipId, edge, edgeTime);
        if (error is not null) return null;
        if (plan is null) return new RippleTrimPreview(new Dictionary<Guid, (MediaTime, MediaTime)>(), stopped);
        var clips = plan.AffectedTracks.SelectMany(plan.EffectiveClips)
            .Where(c => c.State != ClipState.Capture(c.Clip))
            .ToDictionary(c => c.Clip.Id, c => (c.State.Start, c.State.End));
        return new RippleTrimPreview(clips, stopped);
    }

    /// <summary>The ripple drag's plan (validated), or null when nothing would change; refused on a locked track.</summary>
    private (EditPlan? Plan, bool Stopped, string? Error) PlanRippleTrimOf(Guid clipId, ClipEdge edge, MediaTime edgeTime)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return (null, false, resolveError);
        if (CheckEditable(plan, clips) is { } editError) return (null, false, editError);

        var clip = clips[0];
        var (changed, stopped, error) = PlanRippleTrim(plan, clip, plan.TrackOf(clip), edge, edgeTime.ToNearestFrame(plan.Rate));
        if (error is not null) return (null, stopped, string.Format(Messages.Edit_CantTrim, error));
        if (!changed) return (null, stopped, null);
        if (Validate(plan) is { } invalid) return (null, stopped, string.Format(Messages.Edit_CantTrim, invalid));
        return (plan, stopped, null);
    }

    /// <summary>The note of a trim stopped where a transition of <paramref name="typeId"/> needs the clip's frames.</summary>
    private static string StopNote(string? typeId) =>
        IsNamed(typeId) ? string.Format(Messages.Edit_TrimStoppedNamed, TypeName(typeId!)) : Messages.Edit_TrimStoppedDissolve;

    // --- Slip (D030 §7) ------------------------------------------------------------------

    public TimelineEditResult SlipClip(Guid clipId, long frames)
    {
        var (plan, applied, note, error) = PlanSlipOf(clipId, frames);
        if (error is not null) return TimelineEditResult.Fail(error);
        if (plan is null) return note is null ? TimelineEditResult.Unchanged() : TimelineEditResult.Fail(note);

        Commit(plan, "Slip Clip");
        return TimelineEditResult.Ok(new[] { clipId }, note);
    }

    public SlipPreview? PreviewSlip(Guid clipId, long frames)
    {
        var (plan, applied, note, error) = PlanSlipOf(clipId, frames);
        if (error is not null) return null;
        var clip = (MediaBackedClip)Sequence.VideoTracks.Concat(Sequence.AudioTracks).SelectMany(t => t.Clips).First(c => c.Id == clipId);
        var state = SlippedState(clip, applied, Settings.FrameRate);
        return new SlipPreview(state.SourceIn, state.SourceOut, applied, note);
    }

    /// <summary>The slip's plan (validated), the frames it applies and the note of a limit; Plan null when nothing
    /// changes.</summary>
    private (EditPlan? Plan, long Applied, string? Note, string? Error) PlanSlipOf(Guid clipId, long frames, bool alone = false)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return (null, 0, null, resolveError);
        var clip = clips[0];
        if (clip is not (VideoClip or AudioClip)) return (null, 0, null, Messages.Edit_SlipOnlyMedia);
        if (CheckEditable(plan, clips) is { } editError) return (null, 0, null, editError);
        if (SourceDuration(clip) is not { } duration) return (null, 0, null, Messages.Edit_SlipNoLength);

        var media = (MediaBackedClip)clip;
        var rate = plan.Rate;
        var track = plan.TrackOf(clip);

        // A dissolve keeps the source it needs beyond the clip (D025 §4): before its start (B), after its end (A) — only the
        // types that need handles (D044 §3: Dissolve, Push; D032: the crossfade).
        long needBefore = 0, needAfter = 0;
        foreach (var t in track.Transitions.Where(t => TransitionRules.NeedsSourceHandles(t.TransitionTypeId)))
        {
            var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate));
            if (t.RightClipId == clip.Id) needBefore = beforeCut;
            if (t.LeftClipId == clip.Id) needAfter = afterCut;
        }
        var asset = AssetOf(clip);
        bool SourceOk(long k)
        {
            var st = SlippedState(media, k, rate);
            return st.SourceIn >= MediaTime.Zero && st.SourceOut <= duration;
        }
        bool DissolveOk(long k)
        {
            var st = SlippedState(media, k, rate);
            return (needBefore == 0 || DissolveHandles.Before(clip, st, asset, rate) >= needBefore) &&
                   (needAfter == 0 || DissolveHandles.After(clip, st, asset, rate) >= needAfter);
        }

        // Δ(k) grows with k, so each limit is monotone and the allowed slips are an interval around 0: its ends by binary
        // search, no further than the whole source.
        var bound = SpeedTiming.FramesFor(duration, media.Speed, rate) + 2;
        long Reach(int direction, Func<long, bool> ok)
        {
            if (!ok(0)) return 0;
            long lo = 0, hi = bound;
            while (lo < hi)
            {
                var mid = lo + (hi - lo + 1) / 2;
                if (ok(direction * mid)) lo = mid;
                else hi = mid - 1;
            }
            return direction * lo;
        }
        bool Allowed(long k) => SourceOk(k) && DissolveOk(k);
        var applied = Math.Clamp(frames, Reach(-1, Allowed), Reach(+1, Allowed));

        string? note = null;
        if (applied != frames)
        {
            var sourceEnd = Reach(frames > 0 ? 1 : -1, SourceOk);
            note = applied != sourceEnd
                ? IsNamed(track, EdgeTypeOf(track, clip, handlesOnly: true))
                    ? string.Format(Messages.Edit_SlipStoppedNamed, TypeName(EdgeTypeOf(track, clip, handlesOnly: true)!))
                : Messages.Edit_SlipStoppedDissolve
                : frames > 0 ? Messages.Edit_SlipStoppedEnd
                : Messages.Edit_SlipStoppedStart;
        }

        // AQ7: the partner slips by the same timeline frames, by the same rule and limits; both take the smaller slip.
        if (!alone && applied != 0)
        {
            var (partner, partnerError) = FollowingPartner(plan, clip);
            if (partnerError is not null) return (null, 0, null, partnerError);
            if (partner is not null)
            {
                var (_, partnerApplied, partnerNote, slipError) = PlanSlipOf(partner.Id, applied, alone: true);
                if (slipError is not null) return (null, 0, null, slipError);
                if (partnerApplied != applied)
                {
                    applied = partnerApplied;
                    note = partnerNote;
                }
                if (applied != 0) plan.Update(partner, plan.TrackOf(partner), SlippedState(partner, applied, rate));
            }
        }
        if (applied == 0) return (null, 0, note, null);

        // Only the source mapping changes: the clip's timing, speed, fades and dissolves stay (D030 §7).
        plan.Update(clip, track, SlippedState(media, applied, rate));
        if (Validate(plan) is { } invalid) return (null, 0, null, string.Format(Messages.Edit_CantSlip, invalid));
        return (plan, applied, note, null);
    }

    /// <summary><paramref name="clip"/>'s timing slipped by <paramref name="frames"/> timeline frames (D030 §7, D022):
    /// <c>SourceIn</c> moves by the start trim's own amount — 1× <c>FromFrame(S + k) − FromFrame(S)</c>, another speed
    /// <c>±SourceLength(|k|)</c> — and <c>SourceOut</c> by the same, so the source width, the frame count and D022's
    /// invariant are unchanged; start, duration and speed stay. Positive: later source content.</summary>
    private static ClipState SlippedState(MediaBackedClip clip, long frames, FrameRate rate)
    {
        var state = ClipState.Capture(clip);
        if (frames == 0) return state;
        MediaTime delta;
        if (clip.Speed.IsNormal)
        {
            var start = clip.TimelineStart.ToNearestFrame(rate);
            delta = MediaTime.FromFrame(start + frames, rate) - MediaTime.FromFrame(start, rate);
        }
        else
        {
            var length = SpeedTiming.SourceLength(Math.Abs(frames), clip.Speed, rate);
            delta = frames > 0 ? length : MediaTime.Zero - length;
        }
        return state with { SourceIn = state.SourceIn + delta, SourceOut = state.SourceOut + delta };
    }
}
