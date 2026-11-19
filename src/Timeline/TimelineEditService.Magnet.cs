using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Timeline.Commands;

namespace AiVideoEditor.Timeline;

// --- The magnetic main track (D069 §6, CapCut's main track magnet) ------------------------------------------------------
// While Sequence.MagneticMainTrack is on, the main track (V1, the lowest video track) has no gaps: every edit that touches it
// is followed — in Validate, inside the same plan and Undo step — by closing its gaps from 0 on. A drop or a move onto it
// goes to the cut nearest the drop point (the clips after it make room, D037 §3). What is bound to a moved clip goes with it:
// its link partner (by the move rule), its anchored templates (ReconcileOverlays), everything in source time (holds, Follow,
// keyframes, FIT climbs); and (the owner's answer) the clips of the other video tracks that start inside it. Audio tracks'
// own clips stay. Titles and templates never land on the main track: with only V1 a track is added for them.
public sealed partial class TimelineEditService
{
    /// <summary>The main track: the lowest video track (V1), or null without video tracks.</summary>
    private Track? MainTrack => Sequence.VideoTracks.OrderBy(t => t.Order).FirstOrDefault();

    /// <summary>True when <paramref name="track"/> is the main track and the magnet is on.</summary>
    private bool IsMagneticMain(Track? track) => Sequence.MagneticMainTrack && track is not null && track == MainTrack;

    public TimelineEditResult SetMagneticMainTrack(bool on)
    {
        if (Sequence.MagneticMainTrack == on) return TimelineEditResult.Unchanged();
        var flag = new SetMagneticMainTrackCommand(Sequence, !on, on);
        if (!on || MainTrack is not { } main || main.IsLocked)
        {
            ExecuteTracked(new NotifyingCommand(flag, _projectService.NotifyTimelineChanged));
            return TimelineEditResult.Ok();
        }

        // Turning it on closes the main track's gaps at once — one Undo step with the switch.
        var plan = new EditPlan(Sequence, Settings);
        Sequence.MagneticMainTrack = true;
        string? error;
        try
        {
            error = CompactMainTrack(plan, main) ?? Validate(plan);
        }
        finally
        {
            Sequence.MagneticMainTrack = false;
        }
        if (error is not null || plan.IsEmpty)
        {
            // The gaps cannot be closed (a partner with no room, …): the magnet is still switched on; the next edit of
            // the main track closes what it can.
            ExecuteTracked(new NotifyingCommand(flag, _projectService.NotifyTimelineChanged));
            return TimelineEditResult.Ok(Array.Empty<Guid>(), error is null ? null : string.Format(Messages.Magnet_GapsKept, error));
        }
        var command = new CompositeCommand("Magnetic Main Track", new IUndoableCommand[] { flag, plan.BuildCommand("Close Gaps") });
        ExecuteTracked(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    /// <summary>Validate's first step: when the plan touches the main track and the magnet is on, its gaps are closed and
    /// what goes with the moved clips is planned. Null when planned (or nothing to do), otherwise the refusal.</summary>
    private string? ApplyMagnet(EditPlan plan)
    {
        // A plan that fixes the frame rate (the first video, D007) is closed up too, on the new grid.
        if (!Sequence.MagneticMainTrack || MainTrack is not { IsLocked: false } main) return null;
        if (!plan.AffectedTracks.Contains(main) && !main.Clips.Any(plan.Removes)) return null;
        return CompactMainTrack(plan, main);
    }

    /// <summary>A removal (Delete, Remove Media) that leaves a gap on the magnetic main track: closed with everything that
    /// follows the moved clips, validated as any edit. Null when nothing to do or planned.</summary>
    private string? CloseMainTrackGaps(EditPlan plan)
    {
        if (!Sequence.MagneticMainTrack || MainTrack is not { IsLocked: false } main || !main.Clips.Any(plan.Removes)) return null;
        return Validate(plan);
    }

    /// <summary>Closes every gap of <paramref name="main"/> as the plan leaves it — the first clip at 0, each next one at the
    /// previous one's end — then carries the partners already planned and the other video tracks' clips (see the top).</summary>
    private string? CompactMainTrack(EditPlan plan, Track main)
    {
        var rate = plan.Rate;
        var clips = plan.EffectiveClips(main)
            .Select(c => (c.Clip, c.State, Start: c.State.Start.ToNearestFrame(rate), End: c.State.End.ToNearestFrame(rate)))
            .OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        long cursor = 0;
        foreach (var c in clips)
        {
            if (c.Start <= cursor)
            {
                cursor = Math.Max(cursor, c.End);   // adjacent (or an overlap the validator refuses)
                continue;
            }
            var delta = cursor - c.Start;
            var (after, error) = ShiftedState(c.Clip, c.State, delta, rate);
            if (error is not null) return error;
            PlanState(plan, c.Clip, main, after);
            cursor = c.End + delta;

            // A partner the plan already moves or trims follows the closing too; one it doesn't touch follows by the
            // move rule in PlanPartnerShifts (from the model, so the whole shift).
            if (PartnerOf(plan, c.Clip) is { } partner && !plan.Removes(partner) && PlannedPlace(plan, partner) is { } place)
            {
                var (partnerAfter, partnerError) = ShiftedState(partner, place.State, delta, rate);
                if (partnerError is not null) return partnerError;
                PlanState(plan, partner, place.Track, partnerAfter);
            }
        }
        // A re-grid moves every clip by its own rule: the other tracks keep their re-gridded places.
        if (plan.NewFrameRate is null) CarryOtherVideoTracks(plan, main);
        return null;
    }

    /// <summary>The owner's answer (D069 §0): a clip of another video track that starts inside a main track clip the plan
    /// moves (same length, new start) moves by the same frames. Not a template anchored to a clip (its own rule), not a clip
    /// the plan changes itself, not on a locked track. Where the carried clips would overlap on their track, that track's
    /// clips stay (never a refusal of the edit). Computed from the model, so it may run again on the same plan.</summary>
    private void CarryOtherVideoTracks(EditPlan plan, Track main)
    {
        var rate = plan.Rate;
        var moves = new List<(long Start, long End, long Delta)>();
        foreach (var clip in main.Clips)
        {
            if (plan.Removes(clip) || plan.PlannedState(clip) is not { } after) continue;
            long start = clip.TimelineStart.ToNearestFrame(rate), end = clip.TimelineEnd.ToNearestFrame(rate);
            long afterStart = after.Start.ToNearestFrame(rate), afterEnd = after.End.ToNearestFrame(rate);
            if (afterStart != start && afterEnd - afterStart == end - start) moves.Add((start, end, afterStart - start));
        }
        if (moves.Count == 0) return;

        foreach (var track in Sequence.VideoTracks.Where(t => t != main && !t.IsLocked))
        {
            var carried = new List<Clip>();
            foreach (var clip in track.Clips)
            {
                if (clip is TemplateClip { AnchorClipId: not null }) continue;
                if (plan.Changes(clip) && !plan.MagnetCarried.Contains(clip)) continue;
                var start = clip.TimelineStart.ToNearestFrame(rate);
                if (moves.FirstOrDefault(m => m.Start <= start && start < m.End) is not { Delta: not 0 } move) continue;
                var (after, error) = ShiftedState(clip, move.Delta, rate);
                if (error is not null) continue;
                plan.Update(clip, track, after);
                plan.MagnetCarried.Add(clip);
                carried.Add(clip);
            }
            if (carried.Count > 0 && Overlaps(plan.EffectiveClips(track), rate))
            {
                foreach (var clip in carried)
                {
                    plan.CancelUpdate(clip);
                    plan.MagnetCarried.Remove(clip);
                }
            }
        }
    }

    /// <summary>Where the plan leaves <paramref name="clip"/> (its track and timing) when it moves, trims or inserts it; null
    /// when the plan does not touch it.</summary>
    private static (Track Track, ClipState State)? PlannedPlace(EditPlan plan, Clip clip)
    {
        foreach (var (updated, to, after) in plan.Updates)
            if (updated == clip) return (to, after);
        return plan.InsertTrackOf(clip) is { } track ? (track, ClipState.Capture(clip)) : null;
    }

    /// <summary>Plans <paramref name="state"/> for <paramref name="clip"/>: an update of a clip of the model, the new timing of
    /// a clip the plan inserts (not in the model yet — its own fields are its planned state).</summary>
    private static void PlanState(EditPlan plan, Clip clip, Track track, ClipState state)
    {
        if (plan.InsertTrackOf(clip) is not null) state.ApplyTo(clip);
        else plan.Update(clip, track, state);
    }

    /// <summary>D069 §6: copies pasted onto the magnetic main track whose earliest one starts inside a clip there move — all of
    /// them by the same frames — to that clip's nearest cut (MakeRoom then moves the clips after it).</summary>
    private string? PasteToNearestCut(EditPlan plan, List<(Clip Clip, Track Track)> placed)
    {
        if (MainTrack is not { } main || !IsMagneticMain(main)) return null;
        var rate = plan.Rate;
        var onMain = placed.Where(p => p.Track == main).Select(p => p.Clip).ToHashSet();
        if (onMain.Count == 0) return null;
        var start = onMain.Min(c => c.TimelineStart.ToNearestFrame(rate));
        long? delta = null;
        foreach (var (clip, state) in plan.EffectiveClips(main))
        {
            if (onMain.Contains(clip)) continue;
            long s = state.Start.ToNearestFrame(rate), e = state.End.ToNearestFrame(rate);
            if (s < start && start < e) delta = NearestCut(s, e, start) - start;
        }
        if (delta is not { } d || d == 0) return null;
        foreach (var (clip, _) in placed)
        {
            var (state, error) = ShiftedState(clip, d, rate);
            if (error is not null) return error;
            state.ApplyTo(clip);
        }
        return null;
    }

    /// <summary>D069 §6: the cut nearest <paramref name="frame"/> when it lies strictly inside [<paramref name="start"/>,
    /// <paramref name="end"/>) — the clip's start in its first half, its end in the second.</summary>
    private static long NearestCut(long start, long end, long frame) => (frame - start) * 2 < end - start ? start : end;

    /// <summary>D069 §6: titles and templates never go onto the magnetic main track — with only the main track a video track
    /// is added for them in the same step.</summary>
    private TimelineEditResult OverMainTrack(string description, Func<TimelineEditResult> add)
    {
        var top = Sequence.VideoTracks.OrderByDescending(t => t.Order).FirstOrDefault();
        if (!IsMagneticMain(top)) return add();
        return EditAsOneStep(description, null, () =>
        {
            var track = AddTrack(TrackType.Video);
            return track.Success ? new[] { track, add() } : new[] { track };
        });
    }
}
