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

/// <summary>Moving clips; copy, paste and duplicate (D027 §5) (split out of TimelineEditService.cs, D075 amendment 1 — no change).</summary>
public sealed partial class TimelineEditService
{
    // --- Move ------------------------------------------------------------------

    public TimelineEditResult MoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null, bool insertOnDrop = false)
    {
        var (plan, clips, error) = PlanMove(clipIds, frameDelta, targetTrackId, insertOnDrop);
        if (error is not null) return TimelineEditResult.Fail(error);
        if (plan is null) return TimelineEditResult.Unchanged();

        if (!Commit(plan, clips.Count == 1 ? "Move Clip" : "Move Clips")) return TimelineEditResult.Unchanged();
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList(), TransitionNote(plan));
    }

    public string? CanMoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null, bool insertOnDrop = false) =>
        PlanMove(clipIds, frameDelta, targetTrackId, insertOnDrop).Error;

    public MovePreview? PreviewMove(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null, bool insertOnDrop = false)
    {
        var (plan, _, error) = PlanMove(clipIds, frameDelta, targetTrackId, insertOnDrop);
        if (error is not null) return null;
        var clips = plan?.Updates.ToDictionary(u => u.Clip.Id, u => (u.After.Start, u.After.End))
            ?? new Dictionary<Guid, (MediaTime Start, MediaTime End)>();
        return new MovePreview(clips);
    }

    /// <summary>Plan + validation for a move. Plan is null (and Error null) when nothing would change.</summary>
    private (EditPlan? Plan, List<Clip> Clips, string? Error) PlanMove(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId,
        bool insertOnDrop = false)
    {
        if (clipIds.Count == 0) return (null, new List<Clip>(), null);

        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var clips) is { } resolveError) return (null, clips, resolveError);
        if (CheckEditable(plan, clips) is { } editError) return (null, clips, editError);

        Track? target = null;
        if (targetTrackId == MoveTargets.NewVideoTrack)
        {
            // D071 amendment 3: dragged above the top track — a new video track on top, in the same step. As onto an existing track
            // (AQ7): a linked partner of the other kind selected with them moves in time on its own track.
            var others = clips.Where(c => !TimelineValidator.IsCompatible(c, TrackType.Video)).ToList();
            if (others.Count > 0 && others.Count < clips.Count && others.All(o => clips.Contains(PartnerOf(plan, o)!)))
                clips = clips.Except(others).ToList();
            if (clips.Any(c => !TimelineValidator.IsCompatible(c, TrackType.Video)) || clips.Select(plan.TrackOf).Distinct().Count() != 1)
                return (null, clips, Messages.Edit_SeveralTracksInTime);
            target = TopNewVideoTrack(plan);
        }
        else if (targetTrackId is { } tid)
        {
            target = FindTrack(tid);
            if (target is null) return (null, clips, Messages.Edit_TrackGone);
            // AQ7: only the clips of the target's kind change track; a linked clip's partner of the other kind (selected
            // with it, AQ1) moves in time on its own track — taken along by the move rule (PlanPartnerShifts).
            var others = clips.Where(c => !TimelineValidator.IsCompatible(c, target.Type)).ToList();
            if (others.Count > 0 && others.Count < clips.Count && others.All(o => clips.Contains(PartnerOf(plan, o)!)))
                clips = clips.Except(others).ToList();
            if (clips.Select(plan.TrackOf).Distinct().Count() != 1)
                return (null, clips, Messages.Edit_SeveralTracksInTime);
            if (target.IsLocked) return (null, clips, string.Format(Messages.Edit_TrackLocked, target.Name));
            if (target == plan.TrackOf(clips[0])) target = null;
        }

        // D057 §3.12: a drag whose leading clip would start inside a clip that stays puts it at that clip's start; the rest
        // is D037 §3's push (that clip and the later ones move right only as far as needed).
        if (insertOnDrop) frameDelta = DropDelta(plan, clips, target, frameDelta);

        if (frameDelta == 0 && target is null) return (null, clips, null);

        foreach (var clip in clips)
        {
            if (PlanShift(plan, clip, target ?? plan.TrackOf(clip), frameDelta) is { } shiftError)
                return (null, clips, shiftError);
        }

        // D037 §3: the moved clips and the partners that follow them are placed; the clips on their right make room.
        if (PlanPartnerShifts(plan) is { } partnerError)
            return (null, clips, string.Format(Messages.Edit_CantMove, partnerError));
        // D048 §3.5: the anchored templates move with their clip and are placed with it (they make room as moved clips).
        if (ReconcileOverlays(plan) is { } overlayError)
            return (null, clips, string.Format(Messages.Edit_CantMove, overlayError));
        // D071 amendment 2: a clip moved to another track takes its plates to the lowest free track above it.
        if (target is not null) CompactAnchoredPlates(plan, clips);
        if (MakeRoom(plan, PlacedByUpdates(plan), Messages.Verb_Move) is { } roomError)
            return (null, clips, roomError);
        if (Validate(plan) is { } error)
            return (null, clips, string.Format(Messages.Edit_CantMove, error));
        if (target is not null) RemoveEmptiedVideoTracks(plan);

        return (plan, clips, null);
    }

    /// <summary>
    /// D057 §3.12: the delta that puts the leading clip of a dragged group at the start of the clip <c>X</c> of its
    /// destination track it would otherwise start inside (<c>X.start &lt; start &lt; X.end</c>, <c>X</c> not moved with the
    /// group nor a partner of a moved clip); <paramref name="frameDelta"/> unchanged when it starts on no clip.
    /// </summary>
    private long DropDelta(EditPlan plan, List<Clip> clips, Track? target, long frameDelta)
    {
        var rate = plan.Rate;
        var leader = clips.OrderBy(c => c.TimelineStart.ToNearestFrame(rate)).First();
        var track = target ?? plan.TrackOf(leader);
        var moving = clips.ToHashSet();
        foreach (var clip in clips)
            if (PartnerOf(plan, clip) is { } partner) moving.Add(partner);
        var start = leader.TimelineStart.ToNearestFrame(rate) + frameDelta;
        foreach (var other in plan.EffectiveClips(track))
        {
            if (moving.Contains(other.Clip)) continue;
            long otherStart = other.State.Start.ToNearestFrame(rate), otherEnd = other.State.End.ToNearestFrame(rate);
            if (otherStart < start && start < otherEnd)
                return frameDelta + ((IsMagneticMain(track) ? NearestCut(otherStart, otherEnd, start) : otherStart) - start);   // D069 §6
        }
        return frameDelta;
    }

    /// <summary>Plans one clip moved by <paramref name="frameDelta"/> whole frames onto <paramref name="toTrack"/> — the
    /// move's timing rule, shared by Move and the ripple edits (D027 §2): the frame count, speed and source range go
    /// along. Null when planned, otherwise why not.</summary>
    private string? PlanShift(EditPlan plan, Clip clip, Track toTrack, long frameDelta)
    {
        if (!TimelineValidator.IsCompatible(clip, toTrack.Type))
            return clip is AudioClip ? Messages.Edit_AudioOnAudioTrack : Messages.Edit_VideoOnVideoTrack;
        var (after, error) = ShiftedState(clip, frameDelta, plan.Rate);
        if (error is not null) return error;
        plan.Update(clip, toTrack, after);
        return null;
    }

    /// <summary>The timing <paramref name="clip"/> has when moved by <paramref name="frameDelta"/> whole frames (the move's
    /// rule, also used for pasted copies), or why it can't move.</summary>
    private (ClipState State, string? Error) ShiftedState(Clip clip, long frameDelta, FrameRate rate) =>
        ShiftedState(clip, ClipState.Capture(clip), frameDelta, rate);

    /// <summary>The move's rule applied to <paramref name="state"/> (the timing <paramref name="clip"/> has, or is planned
    /// to have): also used by the ripple trim (D030 §5), which moves a clip it has just trimmed.</summary>
    private (ClipState State, string? Error) ShiftedState(Clip clip, ClipState state, long frameDelta, FrameRate rate)
    {
        var startFrame = state.Start.ToNearestFrame(rate) + frameDelta;
        var endFrame = state.End.ToNearestFrame(rate) + frameDelta;
        if (startFrame < 0)
            return (default, Messages.Edit_BeforeTimelineStart);

        if (clip is MediaBackedClip && !state.Speed.IsNormal)
        {
            // D022: the source range and speed move along unchanged; so does the frame count.
            return (ClipState.FromFrames(startFrame, endFrame, state.SourceIn, state.SourceOut, state.Speed, rate), null);
        }

        var sourceIn = clip is MediaBackedClip ? state.SourceIn : MediaTime.Zero;
        var after = ClipState.FromFrames(startFrame, endFrame, sourceIn, rate);

        // The tick length of the same frame count can differ by one tick between
        // positions. If that would push SourceOut one tick past the source end
        // (possible only for the right half of a split), slip SourceIn back by
        // the excess — a sub-frame, invisible adjustment — instead of failing.
        if (SourceDuration(clip) is { } sourceDuration && after.SourceOut > sourceDuration)
        {
            var excess = after.SourceOut - sourceDuration;
            if (after.SourceIn < excess)
                return (default, Messages.Edit_ClipPastSourceEnd);
            after = after with { SourceIn = after.SourceIn - excess, SourceOut = sourceDuration };
        }

        return (after, null);
    }

    // --- Copy / paste / duplicate (D027 §5) --------------------------------------

    public TimelineClipboard? CopyClips(IReadOnlyCollection<Guid> clipIds)
    {
        if (clipIds.Count == 0) return null;
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var clips) is not null) return null;
        // D048 §3.10: a video clip's anchored templates are copied with it (independent copies anchored to the copy).
        foreach (var video in clips.OfType<VideoClip>().ToList())
            clips.AddRange(AnchoredTemplates(plan, video).Where(p => !clips.Contains(p)));
        var copies = clips.ToDictionary(c => c, CopyWithLink);
        foreach (var copy in copies.Values.OfType<TemplateClip>())
        {
            copy.AnchorClipId = copy.AnchorClipId is { } anchor && copies.FirstOrDefault(p => p.Key.Id == anchor).Value is { } copiedVideo
                ? copiedVideo.Id : null;
            // D053: the copy's route is the copied route (its id kept on the copied clip); a copy is never the FIT's route.
            if (copy.AnchorClipId is null) copy.AnchorRouteId = null;
            copy.FitClimb = null;
            copy.FitLayout = null;
        }
        var entries = clips.OrderBy(c => c.TimelineStart)
            .Select(c => new TimelineClipboardEntry(copies[c], plan.TrackOf(c).Id))
            .ToList();
        return new TimelineClipboard(plan.Rate, CurrentCanvas, entries);
    }

    public TimelineEditResult PasteClips(TimelineClipboard clipboard, MediaTime at)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (clipboard.Count == 0) return TimelineEditResult.Unchanged();
        var plan = new EditPlan(Sequence, Settings);
        return PasteInto(plan, clipboard, Math.Max(0, at.ToNearestFrame(plan.Rate)), clipboard.Count == 1 ? "Paste Clip" : "Paste Clips", Messages.Verb_Paste);
    }

    public TimelineEditResult DuplicateClips(IReadOnlyCollection<Guid> clipIds)
    {
        if (clipIds.Count == 0) return TimelineEditResult.Unchanged();
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        if (CheckEditable(plan, clips) is { } editError) return TimelineEditResult.Fail(editError);

        var clipboard = CopyClips(clipIds)!;
        var end = clips.Max(c => c.TimelineEnd.ToNearestFrame(plan.Rate));
        return PasteInto(plan, clipboard, end, clips.Count == 1 ? "Duplicate Clip" : "Duplicate Clips", Messages.Verb_Duplicate);
    }

    /// <summary>New copies of the clipboard's clips, the earliest at <paramref name="atFrame"/>, each on its own track,
    /// planned, validated and committed as one step — or the reason nothing was added.</summary>
    private TimelineEditResult PasteInto(EditPlan plan, TimelineClipboard clipboard, long atFrame, string description, string verb)
    {
        var rate = plan.Rate;
        if (clipboard.FrameRate != rate)
            return TimelineEditResult.Fail(Messages.Edit_RateChangedSinceCopy);
        if (clipboard.Canvas != CurrentCanvas)
            return TimelineEditResult.Fail(Messages.Edit_SizeChangedSinceCopy);

        var earliest = clipboard.Entries.Min(e => e.Clip.TimelineStart.ToNearestFrame(rate));
        // D032 R5: both clips of a link copied → the copies form a new link; one alone is pasted unlinked.
        var newLinks = clipboard.Entries.Select(e => (e.Clip as MediaBackedClip)?.LinkId).OfType<Guid>()
            .GroupBy(l => l).Where(g => g.Count() == 2).ToDictionary(g => g.Key, _ => Guid.NewGuid());
        var ids = new List<Guid>();
        var placed = new List<(Clip Clip, Track Track)>();
        foreach (var entry in clipboard.Entries)
        {
            if (FindTrack(entry.TrackId) is not { } track)
                return TimelineEditResult.Fail(string.Format(Messages.Edit_CopiedTrackGone, verb));
            if (track.IsLocked)
                return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));
            if (entry.Clip is MediaBackedClip media && FindAsset(media.MediaAssetId) is null)
                return TimelineEditResult.Fail(string.Format(Messages.Edit_CopiedMediaGone, verb));

            var copy = CloneClip(entry.Clip);                      // a new id; the same asset, never the file
            if (copy is MediaBackedClip linked && entry.Clip is MediaBackedClip { LinkId: { } link } && newLinks.TryGetValue(link, out var newLink))
                linked.LinkId = newLink;
            var (state, error) = ShiftedState(copy, atFrame - earliest, rate);
            if (error is not null) return TimelineEditResult.Fail($"Can't {verb}: {error}");
            state.ApplyTo(copy);
            plan.Insert(track, copy);
            ids.Add(copy.Id);
            placed.Add((copy, track));
        }

        // D048 §3.10: a pasted template is anchored to the pasted copy of its clip; one pasted alone is free.
        var pastedOf = clipboard.Entries.Zip(placed).ToDictionary(p => p.First.Clip.Id, p => p.Second.Clip.Id);
        foreach (var (pasted, _) in placed)
            if (pasted is TemplateClip template)
            {
                template.AnchorClipId = template.AnchorClipId is { } anchor && pastedOf.TryGetValue(anchor, out var video) ? video : null;
                if (template.AnchorClipId is null) template.AnchorRouteId = null;
                template.FitClimb = null;
                template.FitLayout = null;
            }

        // D051 §5.4 (B1): never a second copy of a markup linked to a FIT climb.
        if (DuplicatesFitMarkup(clipboard)) return TimelineEditResult.Fail(string.Format(Messages.Edit_PasteDuplicateFitMarkup, verb));

        // D069 §6: on the magnetic main track the copies go to the cut nearest the paste point.
        if (PasteToNearestCut(plan, placed) is { } cutError) return TimelineEditResult.Fail($"Can't {verb}: {cutError}");

        // D037 §3 (Q10): the clips on the right make room; a copy starting inside a clip is refused.
        if (MakeRoom(plan, placed, verb) is { } roomError) return TimelineEditResult.Fail(roomError);

        // Any other broken rule rejects the whole paste (D027 §5); no dissolve is created.
        if (Validate(plan) is { } invalid) return TimelineEditResult.Fail($"Can't {verb}: {invalid}");

        Commit(plan, description);
        return TimelineEditResult.Ok(ids);
    }
}
