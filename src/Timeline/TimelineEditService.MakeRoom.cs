using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;

namespace AiVideoEditor.Timeline;

// --- Making room on the right (Phase 22, D037 §3 — variant B) -----------------------------------------------------------
// A placement edit (a move, a nudge, a drop, a paste, a duplicate, a new text) fixes its placed clips; on each of their
// tracks a clip that a placed clip overlaps from its start on moves right by exactly the overlap, and the later clips of that
// track move only as far as they are then overlapped (a gap absorbs the push). A placed clip that starts inside another clip
// — a collision on the left — refuses the edit (Q11). A pushed clip's link partner moves by the same frames on its own track
// by the move rule and never pushes there: when it can't follow, the edit is refused. A push that would open a dissolve
// between a pushed clip and a clip that is not moved is refused (Q9). Everything is planned into the edit's one EditPlan.
public sealed partial class TimelineEditService
{
    /// <summary>
    /// Plans the room <paramref name="placed"/> needs on their tracks into <paramref name="plan"/>: null when planned (or
    /// nothing to do), otherwise the refusal ("Can't <paramref name="verb"/>: …"). The placed clips must already be planned
    /// at their new places (updated or inserted), their partners too.
    /// </summary>
    private string? MakeRoom(EditPlan plan, IReadOnlyCollection<(Clip Clip, Track Track)> placedIn, string verb)
    {
        var rate = plan.Rate;
        var placed = RelocateAnchoredTemplates(plan, placedIn);   // D069 amendment 2: plates never push other clips
        var placedSet = placed.Select(p => p.Clip).ToHashSet();
        var pushed = new Dictionary<Clip, (Track Track, long Delta)>();

        foreach (var track in placed.Select(p => p.Track).Distinct().ToList())
        {
            var clips = plan.EffectiveClips(track)
                .Select(c => (c.Clip, c.State, Start: c.State.Start.ToNearestFrame(rate), End: c.State.End.ToNearestFrame(rate)))
                .ToList();
            var fixedSpans = clips.Where(c => placedSet.Contains(c.Clip)).Select(c => (c.Start, c.End)).ToList();
            var others = clips.Where(c => !placedSet.Contains(c.Clip)).OrderBy(c => c.Start).ToList();
            if (fixedSpans.Count == 0 || others.Count == 0) continue;

            // Q11: a placed clip may not start inside a clip that stays.
            if (others.Any(o => fixedSpans.Any(f => o.Start < f.Start && f.Start < o.End)))
                return string.Format(Messages.MakeRoom_InsideClip, verb);

            var previousEnd = long.MinValue;
            foreach (var other in others)
            {
                var length = other.End - other.Start;
                var start = Math.Max(other.Start, previousEnd);
                for (var moved = true; moved;)
                {
                    moved = false;
                    foreach (var (fixedStart, fixedEnd) in fixedSpans)
                    {
                        if (fixedEnd <= start || fixedStart >= start + length) continue;
                        // A placed clip starting inside a clip that is being pushed: the push would run into it.
                        if (fixedStart > start) return string.Format(Messages.MakeRoom_NoRoom, verb, track.Name);
                        start = fixedEnd;
                        moved = true;
                    }
                }
                previousEnd = start + length;
                if (start == other.Start) continue;
                // D071 amendment 2 (the owner): another clip's plate is never pushed by an edit of something else — its place is
                // taken (the ghost red), it stays where it is.
                if (other.Clip is TemplateClip { AnchorClipId: { } anchor } && !placed.Any(p => p.Clip.Id == anchor))
                    return string.Format(Messages.MakeRoom_OtherClipsPlate, verb);

                var (after, error) = ShiftedState(other.Clip, other.State, start - other.Start, rate);
                if (error is not null) return $"Can't {verb}: {error}";
                plan.Update(other.Clip, track, after);
                pushed[other.Clip] = (track, start - other.Start);
            }
        }

        if (pushed.Count == 0) return null;
        if (plan.NewFrameRate is not null && pushed.Keys.Any(c => PartnerOf(plan, c) is not null))
            return string.Format(Messages.MakeRoom_RateSet, verb);

        // Variant B: a pushed clip's partner follows by the same frames (the move rule, AQ4 / AQ7) and pushes nothing.
        var moving = new Dictionary<Clip, (Track Track, long Delta)>(pushed);
        foreach (var (clip, (_, delta)) in pushed)
        {
            if (PartnerOf(plan, clip) is not { } partner) continue;
            if (placedSet.Contains(partner) || (pushed.TryGetValue(partner, out var partnerPush) && partnerPush.Delta != delta))
                return string.Format(Messages.MakeRoom_PartnerDifferent, verb);
            moving.TryAdd(partner, (plan.TrackOf(partner), delta));
        }
        if (PlanPartnerShifts(plan) is { } partnerError) return $"Can't {verb}: {partnerError}";
        foreach (var track in moving.Where(m => !pushed.ContainsKey(m.Key)).Select(m => m.Value.Track).Distinct())
        {
            if (Overlaps(plan.EffectiveClips(track), rate))
                return string.Format(Messages.MakeRoom_PartnerNoRoom, verb, track.Name);
        }

        // Q9: a clip that makes room (or its partner) keeps every dissolve it has with a clip moving by the same frames; one
        // with a clip that stays would open — refused, never removed.
        foreach (var (clip, (track, delta)) in moving)
        {
            foreach (var (_, state) in plan.EffectiveTransitions(track))
            {
                var other = state.LeftClipId == clip.Id ? state.RightClipId : state.RightClipId == clip.Id ? state.LeftClipId : (Guid?)null;
                if (other is not { } otherId) continue;
                var otherClip = plan.EffectiveClips(track).FirstOrDefault(c => c.Clip.Id == otherId).Clip;
                if (otherClip is null || placedSet.Contains(otherClip)) continue;   // a moved clip's own dissolves: D025 §5
                if (!moving.TryGetValue(otherClip, out var otherMove) || otherMove.Delta != delta)
                    return IsNamed(track, state.TypeId)
                        ? string.Format(Messages.MakeRoom_OpensNamed, verb, TypeName(state.TypeId))
                        : string.Format(Tr(track, Messages.MakeRoom_OpensDissolve, Messages.MakeRoom_OpensCrossfade), verb);
            }
        }
        return null;
    }

    /// <summary>
    /// D069 amendment 2 (the owner's check): a template anchored to a clip (a «Боулдер» with its heart rate, holds, route) that
    /// the edit places where it would overlap a clip it does not place goes to the lowest unlocked video track above its clip
    /// free over its span — or to a new track at the top in the same step — instead of pushing that clip (two clips on one
    /// canvas each keep their own plate).
    /// </summary>
    private List<(Clip Clip, Track Track)> RelocateAnchoredTemplates(EditPlan plan, IReadOnlyCollection<(Clip Clip, Track Track)> placed)
    {
        var rate = plan.Rate;
        var result = placed.ToList();
        var placedSet = placed.Select(p => p.Clip).ToHashSet();
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i] is not (TemplateClip { AnchorClipId: { } anchorId } template, var track)) continue;
            var state = plan.PlannedState(template) ?? ClipState.Capture(template);
            long start = state.Start.ToNearestFrame(rate), end = state.End.ToNearestFrame(rate);
            bool Collides(Track t) => plan.EffectiveClips(t).Any(c => c.Clip != template && !placedSet.Contains(c.Clip) &&
                c.State.Start.ToNearestFrame(rate) < end && start < c.State.End.ToNearestFrame(rate));
            if (!Collides(track)) continue;

            var anchorOrder = plan.AllTracks.Concat(plan.NewTracks).FirstOrDefault(t => plan.EffectiveClips(t).Any(c => c.Clip.Id == anchorId))?.Order ?? -1;
            var target = Sequence.VideoTracks.Concat(plan.NewTracks).Where(t => t != track && !t.IsLocked && t.Order > anchorOrder)
                .OrderBy(t => t.Order).FirstOrDefault(t => !Collides(t));
            if (target is null)
            {
                target = NewTrack(TrackType.Video);
                if (plan.NewTracks.Count > 0)
                {
                    var top = plan.NewTracks.Max(t => t.Order);
                    target = new Track { Type = TrackType.Video, Name = $"V{Sequence.VideoTracks.Count + plan.NewTracks.Count + 1}", Order = top + 1 };
                }
                plan.NewTracks.Add(target);
            }
            if (plan.InsertTrackOf(template) is not null) plan.MoveInsert(template, target);
            else plan.Update(template, target, state);
            plan.Relocated[template] = target;
            result[i] = (template, target);
        }
        return result;
    }

    /// <summary>The placed clips of <paramref name="plan"/>: every clip it moves to a new place (with the partners that
    /// follow, once <see cref="PlanPartnerShifts"/> ran) — what a move fixes before it makes room.</summary>
    private static List<(Clip Clip, Track Track)> PlacedByUpdates(EditPlan plan) =>
        plan.Updates.Select(u => (u.Clip, u.To)).ToList();

    private static bool Overlaps(IEnumerable<(Clip Clip, ClipState State)> clips, Core.Common.FrameRate rate)
    {
        var spans = clips.Select(c => (Start: c.State.Start.ToNearestFrame(rate), End: c.State.End.ToNearestFrame(rate)))
            .OrderBy(s => s.Start).ToList();
        for (var i = 1; i < spans.Count; i++)
            if (spans[i].Start < spans[i - 1].End) return true;
        return false;
    }

    /// <summary>
    /// D071 amendment 2 (the owner): the plates anchored to a video the step moves to another track go to the lowest unlocked
    /// video track above it that is free over their span — the timeline stays compact (a plate left high above its clip moves
    /// down to where the other plates are). A plate the user moves too, or with no free track, keeps its track.
    /// </summary>
    private void CompactAnchoredPlates(EditPlan plan, IReadOnlyCollection<Clip> moved)
    {
        var rate = plan.Rate;
        var movedSet = moved.ToHashSet();
        foreach (var (video, to, _) in plan.Updates.Where(u => u.Clip is VideoClip && u.To != plan.TrackOf(u.Clip)).ToList())
        {
            foreach (var template in AnchoredTemplates(plan, (VideoClip)video).Where(t => !movedSet.Contains(t) && !plan.Removes(t)))
            {
                var state = plan.PlannedState(template) ?? ClipState.Capture(template);
                long start = state.Start.ToNearestFrame(rate), end = state.End.ToNearestFrame(rate);
                bool Free(Track t) => !plan.EffectiveClips(t).Any(c => c.Clip != template &&
                    c.State.Start.ToNearestFrame(rate) < end && start < c.State.End.ToNearestFrame(rate));
                var current = plan.TrackFor(template);
                if (current.IsLocked) continue;   // a locked track is never changed (D008): its plate stays
                var target = Sequence.VideoTracks.Where(t => !t.IsLocked && t.Order > to.Order).OrderBy(t => t.Order).FirstOrDefault(Free);
                if (target is null || target == current) continue;
                plan.Update(template, target, state);
                plan.Relocated[template] = target;
            }
        }
    }

    /// <summary>
    /// D071 amendment 2 (the owner): the video tracks a move leaves without a clip — they had clips, the step takes them all
    /// away — are removed in the same step (Undo puts them back). Never the main track (the lowest), a locked track, an
    /// empty track the user made, one the step adds, or one another track's ducking listens to (D064 §4: the ducking stays as the
    /// user set it — never a reference to a removed track).
    /// </summary>
    private void RemoveEmptiedVideoTracks(EditPlan plan)
    {
        var main = Sequence.VideoTracks.OrderBy(t => t.Order).FirstOrDefault();
        foreach (var track in Sequence.VideoTracks)
        {
            if (track == main || track.IsLocked || track.Clips.Count == 0 || plan.NewTracks.Contains(track)) continue;
            if (Sequence.VideoTracks.Concat(Sequence.AudioTracks).Any(t => t.Ducking?.SpeechTrackId == track.Id)) continue;
            if (!plan.EffectiveClips(track).Any()) plan.RemovedTracks.Add(track);   // its transitions went with its clips (D025 §5)
        }
    }
}
