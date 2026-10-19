using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;

namespace AiVideoEditor.Timeline;

// --- Linked clips in the edits (D032 R5, R6, L5, AQ1–AQ7, AQ13) --------------------------------------------------------
// No rule of its own: the partner of a clip an edit changes is planned into the same EditPlan by the very rule the edit
// applies — the move rule for a shift (AQ4 / AQ7, here for every edit, in Validate), the trim rule (AQ3), the split rule
// (AQ5), the speed rule (AQ6), the slip rule (AQ7), the insert / overwrite rule on the partner alone (AQ13) — and the
// result is validated as a whole. A partner on a locked track refuses the whole edit (L5).
public sealed partial class TimelineEditService
{
    private static string PartnerCantFollow => Messages.Link_CantFollow;

    /// <summary>The other clip of <paramref name="clip"/>'s link, or null.</summary>
    private static MediaBackedClip? PartnerOf(EditPlan plan, Clip clip) =>
        LinkRules.PartnerOf(clip, plan.AllTracks.SelectMany(t => t.Clips));

    /// <summary>The partner an edit of <paramref name="clip"/> has to take along: null when there is none, when the edit
    /// already plans it, or when <paramref name="alone"/> (Alt + edge, a J / L cut, AQ3); an error when it is locked (L5).</summary>
    private static (MediaBackedClip? Partner, string? Error) FollowingPartner(EditPlan plan, Clip clip, bool alone = false)
    {
        if (alone || PartnerOf(plan, clip) is not { } partner || plan.Changes(partner)) return (null, null);
        var track = plan.TrackOf(partner);
        return track.IsLocked ? (null, string.Format(Messages.Edit_TrackLocked, track.Name)) : (partner, null);
    }

    /// <summary>
    /// AQ4 / AQ7: a clip the plan moves (by the move rule — its frame count unchanged, its start changed) takes its partner
    /// along by the same frames, on the partner's own track, by the same rule; the partner's neighbours stay (no ripple
    /// across tracks). A frame-rate re-grid moves every clip by its own rule (D007): nothing to follow.
    /// </summary>
    private string? PlanPartnerShifts(EditPlan plan)
    {
        if (plan.NewFrameRate is not null) return null;
        var rate = plan.Rate;
        foreach (var (clip, _, after) in plan.Updates.ToList())
        {
            var before = ClipState.Capture(clip);
            var delta = after.Start.ToNearestFrame(rate) - before.Start.ToNearestFrame(rate);
            if (delta == 0 || Frames(after) != Frames(before)) continue;
            var (partner, error) = FollowingPartner(plan, clip);
            if (error is not null) return error;
            if (partner is not null && PlanShift(plan, partner, plan.TrackOf(partner), delta) is { } shiftError)
                return shiftError;
        }
        return null;

        long Frames(ClipState s) => s.End.ToNearestFrame(rate) - s.Start.ToNearestFrame(rate);
    }

    /// <summary>AQ5: where both clips of a link are split, their right parts form a new link (the left parts keep theirs).</summary>
    private static void LinkSplitParts(IEnumerable<(Clip Left, Clip Right)> splits)
    {
        foreach (var pair in splits.Where(s => s.Left is MediaBackedClip { LinkId: not null })
                     .GroupBy(s => ((MediaBackedClip)s.Left).LinkId))
        {
            if (pair.Count() != 2) continue;
            var link = Guid.NewGuid();
            foreach (var (_, right) in pair) ((MediaBackedClip)right).LinkId = link;
        }
    }

    /// <summary><paramref name="clips"/> with the partners covering frame <paramref name="atFrame"/> strictly inside (AQ3 for
    /// Q / W, AQ5 for a split), each once.</summary>
    private static List<Clip> WithPartnersCovering(EditPlan plan, IEnumerable<Clip> clips, long atFrame)
    {
        var rate = plan.Rate;
        var result = clips.ToList();
        foreach (var clip in result.ToList())
        {
            if (PartnerOf(plan, clip) is { } partner && !result.Contains(partner)
                && partner.TimelineStart.ToNearestFrame(rate) < atFrame && atFrame < partner.TimelineEnd.ToNearestFrame(rate))
                result.Add(partner);
        }
        return result;
    }

    /// <summary>The frames from <paramref name="clip"/>'s end to the next clip of <paramref name="track"/> (unbounded at
    /// the end): how far a partner that follows a ripple trim can grow — its neighbours don't move (AQ4).</summary>
    private static long RoomAfter(Clip clip, Track track, FrameRate rate)
    {
        var end = clip.TimelineEnd.ToNearestFrame(rate);
        return track.Clips.Where(c => c != clip && c.TimelineStart.ToNearestFrame(rate) >= end)
            .Select(c => c.TimelineStart.ToNearestFrame(rate) - end)
            .DefaultIfEmpty(long.MaxValue).Min();
    }

    private static long EdgeFrame(ClipState state, ClipEdge edge, FrameRate rate) =>
        (edge == ClipEdge.Start ? state.Start : state.End).ToNearestFrame(rate);
}
