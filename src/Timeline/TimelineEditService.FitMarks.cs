using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Timeline.Commands;

namespace AiVideoEditor.Timeline;

// Phase 37 (D051 §5): the FIT marks — the user's actions on them, the proposals of a Z and the marks following every
// other edit. Every mark change is one Undo step; positionChangedAt is set by D051 §5.6 alone.
public sealed partial class TimelineEditService : IFitMarkEditService
{
    private DateTimeOffset Now() => _time.GetUtcNow();

    /// <summary>Executes an edit; with FIT marks in the project the marks follow it in the same step (D051 §5.4, §5.6).</summary>
    private void ExecuteTracked(IUndoableCommand command) => ExecuteTracked(command, null);

    private void ExecuteTracked(IUndoableCommand command, IReadOnlySet<ClimbIdentity>? keep)
    {
        if (Project.Training is { } training && (training.Marks.Count > 0 || training.DeclinedClimbs.Count > 0))
            command = new FitMarksFollowingCommand(command, Project, training, Now, keep);
        _undoRedo.Execute(command);
    }

    /// <summary>The marks of a <c>Z</c> being set or changed (D051 §5.5): the proposals of the climbs whose chosen video is
    /// this one, with the current estimate frozen into them; null when there are none.</summary>
    private IUndoableCommand? ProposalsForZero(MediaAsset asset, DateTimeOffset? zero)
    {
        if (zero is null || Project.Training is not { IsUsable: true } training) return null;
        var videos = FitMarkRules.Videos(Project.MediaAssets, Settings.FrameRate, (asset.Id, zero));
        var estimate = _startCorrection?.Current ?? FitCalibrationSnapshot.Initial;
        var proposals = FitMarkRules.Proposals(training, videos, estimate.EstimateTicks, estimate.EstimateVersion, Now(), asset.Id);
        if (proposals.Count == 0) return null;
        return new SetFitMarksCommand(training, training.Marks.ToList(), training.DeclinedClimbs.ToList(),
            training.Marks.Concat(proposals).ToList(), training.DeclinedClimbs.ToList(), "Propose FIT Marks");
    }

    // ---- The user's actions -------------------------------------------------------------------------------------------

    public TimelineEditResult MoveFitMark(ClimbIdentity climb, MediaTime sourceTime)
    {
        if (ActiveMark(climb, out var training, out var mark) is { } error) return TimelineEditResult.Fail(error);
        var asset = FindAsset(mark.MediaAssetId);
        if (asset?.Metadata is not { } metadata || sourceTime < MediaTime.Zero || sourceTime >= metadata.Duration)
            return TimelineEditResult.Fail(Messages.FitMark_OutsideVideo);

        var rate = FitMarkRules.RateOf(asset, Settings.FrameRate);
        var ownBefore = OwnSourceTime(mark);
        var moved = mark with { Position = FitMarkPosition.Moved(sourceTime) };
        if (ownBefore is { } b && b.ToFrameFloor(rate) == sourceTime.ToFrameFloor(rate) && mark.Position.Kind == FitMarkPositionKind.Moved)
            return TimelineEditResult.Unchanged();
        // D051 §5.6: the own position actually changed — by its video's frame.
        if (ownBefore is not { } own || own.ToFrameFloor(rate) != sourceTime.ToFrameFloor(rate))
            moved = moved with { PositionChangedAt = Now() };
        return ReplaceMarks(training, Replace(training.Marks, mark, moved), training.DeclinedClimbs.ToList(), "Move FIT Mark");
    }

    public TimelineEditResult DeclineClimb(ClimbIdentity climb)
    {
        if (Project.Training is not { } training) return TimelineEditResult.Fail(Messages.Training_None);
        if (training.DeclinedClimbs.Any(d => d.Climb == climb)) return TimelineEditResult.Unchanged();
        var mark = training.Marks.FirstOrDefault(m => m.Climb == climb);
        DeclinedClimb declined;
        if (mark is null)
        {
            if (FitMarkRules.ClimbOf(training.Workout, climb) is null) return TimelineEditResult.Fail(Messages.FitMark_None);
            declined = new DeclinedClimb(climb);
        }
        else
        {
            // D051 §5.7: the link is removed first, by §5.6 — positionChangedAt set when the effective start changes.
            var unlinked = mark with { LinkedClipId = null, LinkedRouteId = null };
            if (mark.LinkedClipId is not null &&
                !FitMarkRules.SameStart(FitMarkRules.EffectiveStart(mark, Project), FitMarkRules.EffectiveStart(unlinked, Project), Project))
                unlinked = unlinked with { PositionChangedAt = Now() };
            declined = new DeclinedClimb(climb, unlinked);
        }
        return ReplaceMarks(training, training.Marks.Where(m => m.Climb != climb).ToList(),
            training.DeclinedClimbs.Append(declined).ToList(), "Decline FIT Climb");
    }

    public TimelineEditResult RestoreClimb(ClimbIdentity climb)
    {
        if (Project.Training is not { } training) return TimelineEditResult.Fail(Messages.Training_None);
        if (training.DeclinedClimbs.FirstOrDefault(d => d.Climb == climb) is not { } declined)
            return TimelineEditResult.Fail(Messages.FitMark_NotDeclined);

        var marks = training.Marks.ToList();
        if (declined.FormerMark is { } former)
            marks.Add(former);                                        // as it was: video, kind, positionChangedAt; no link
        else if (FitMarkRules.ClimbOf(training.Workout, climb) is { } segment && training.IsUsable)
        {
            var estimate = _startCorrection?.Current ?? FitCalibrationSnapshot.Initial;
            var videos = FitMarkRules.Videos(Project.MediaAssets, Settings.FrameRate);
            var autoStart = segment.StartUtc.AddTicks(-estimate.EstimateTicks);
            if (FitMarkRules.ChooseVideo(segment, autoStart, training.ClockCorrection, videos) is { } chosen)
                marks.Add(new FitMark(climb, autoStart, estimate.EstimateTicks, estimate.EstimateVersion, chosen.AssetId, chosen.Position, Now()));
        }
        return ReplaceMarks(training, marks, training.DeclinedClimbs.Where(d => d.Climb != climb).ToList(), "Restore FIT Climb");
    }

    public TimelineEditResult LinkFitClimb(ClimbIdentity climb, Guid clipId, Guid? routeId = null)
    {
        if (LinkableClip(climb, clipId, routeId, out var training, out var mark, out var route) is { } error) return TimelineEditResult.Fail(error);
        if (mark.LinkedClipId == clipId && mark.LinkedRouteId == route) return TimelineEditResult.Unchanged();
        if (mark.LinkedClipId is not null) return TimelineEditResult.Fail(Messages.FitMark_ClimbLinked);
        // positionChangedAt for a changed effective start is set by the marks following the step (D051 §5.6).
        return ReplaceMarks(training, Replace(training.Marks, mark, mark with { LinkedClipId = clipId, LinkedRouteId = route }),
            training.DeclinedClimbs.ToList(), "Link FIT Climb");
    }

    public TimelineEditResult UnlinkFitClimb(ClimbIdentity climb)
    {
        if (ActiveMark(climb, out var training, out var mark) is { } error) return TimelineEditResult.Fail(error);
        if (mark.LinkedClipId is null) return TimelineEditResult.Fail(Messages.FitMark_NotLinked);
        return ReplaceMarks(training, Replace(training.Marks, mark, mark with { LinkedClipId = null, LinkedRouteId = null }),
            training.DeclinedClimbs.ToList(), "Unlink FIT Climb");
    }

    public TimelineEditResult LayOutRouteFromMark(ClimbIdentity climb, Guid clipId, Guid? routeId = null)
    {
        if (ActiveMark(climb, out var training, out var mark) is { } error) return TimelineEditResult.Fail(error);
        if (mark.LinkedClipId is { } linked && linked != clipId) return TimelineEditResult.Fail(Messages.FitMark_ClimbLinked);
        if (FitMarkRules.FindVideoClip(Sequence, clipId) is not { } clip) return TimelineEditResult.Fail(Messages.Holds_OnlyVideo);
        if (clip.MediaAssetId != mark.MediaAssetId) return TimelineEditResult.Fail(Messages.FitMark_ClipNotOnMarkVideo);
        if (OwnSourceTime(mark) is not { } source) return TimelineEditResult.Fail(Messages.FitMark_NoRecordingStart);

        var rate = Settings.FrameRate;
        var frame = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, source, rate);
        if (frame < clip.TimelineStart.ToNearestFrame(rate) || frame > clip.TimelineEnd.ToNearestFrame(rate))
            return TimelineEditResult.Fail(Messages.FitMark_MarkNotInClip);
        // D053: the route laid out — named, the one linked already, the one whose green holds the mark's frame, else the
        // clip's only route.
        routeId ??= mark.LinkedClipId == clipId ? mark.LinkedRouteId : null;
        routeId ??= RouteOverlays.RouteAt(clip, RouteOverlays.AnchoredTo(clip, Sequence.VideoTracks.SelectMany(t => t.Clips)), frame, rate);
        if (training.Marks.Any(m => m.Climb != climb && m.LinkedClipId == clipId &&
                (routeId is null || FitMarkRules.LinkedRoute(clip, m.LinkedRouteId)?.Id == routeId)))
            return TimelineEditResult.Fail(Messages.FitMark_AnimationLinked);

        // D051 §5.6: linking a mark by laying the route out from it keeps its positionChangedAt (T on its frame).
        var keep = mark.LinkedClipId is null ? new HashSet<ClimbIdentity> { climb } : null;
        return LayOutIntroAndFinal(clipId, MediaTime.FromFrame(frame, rate), routeId, keep, (hasAnimation, laidOut) => hasAnimation
            ? new SetFitMarksCommand(training, training.Marks.ToList(), training.DeclinedClimbs.ToList(),
                Replace(training.Marks, mark, mark with { LinkedClipId = clipId, LinkedRouteId = laidOut }), training.DeclinedClimbs.ToList(),
                "Link FIT Climb")
            : null);
    }

    // ---- Route areas from the FIT (D053 §7, mode B) ---------------------------------------------------------------------

    /// <summary>The extra time a FIT-made green keeps after the climb's end (the product owner, 2026-11-09): room for the
    /// Top / Attempt card added by hand.</summary>
    public static readonly MediaTime FitGreenTail = MediaTime.FromSeconds(5);

    /// <summary>
    /// D053 §7 (mode B): one route — its green only, a «Боулдер» with no status, no holds, no animation — for every active
    /// FIT climb that has none yet, on the clip of the timeline that shows the climb's start: from <c>T − I</c>
    /// (<c>I = min(7 s, A)</c>, the layout's rule, D048 §3.4) to the climb's actual end (<c>W_end − ClimbEndLag</c>, D050)
    /// plus <see cref="FitGreenTail"/>, cut to the clip; <c>T</c> is the climb's effective start (D051 §4 — the mark's
    /// position converted by the video's recording zero and the training's clock correction, never a second
    /// synchronisation). A climb whose start no clip of its video shows gets no route. One Undo step; nothing new → no step.
    /// </summary>
    public TimelineEditResult CreateRoutesFromFit()
    {
        if (Project.Training is not { Workout: { } workout } training || !training.IsUsable) return TimelineEditResult.Fail(Messages.Training_None);
        var rate = Settings.FrameRate;
        var allClips = Sequence.VideoTracks.SelectMany(t => t.Clips).ToList();
        var made = allClips.OfType<TemplateClip>().Where(t => t.FitClimb is not null).Select(t => t.FitClimb!.Value).ToHashSet();
        var plan = new EditPlan(Sequence, Settings);
        var newTracks = new List<Track>();
        var created = new List<Guid>();

        foreach (var mark in training.Marks.OrderBy(m => m.Climb.ClimbStartUtc))
        {
            if (made.Contains(mark.Climb)) continue;
            if (FitMarkRules.EffectiveStart(mark, Project) is not { SourceTime: { } start } effective) continue;
            if (FitMarkRules.ClimbOf(workout, mark.Climb) is not { } segment) continue;
            if (Project.MediaAssets.FirstOrDefault(a => a.Id == effective.AssetId) is not { } asset ||
                RecordingZeroEstimator.Resolve(asset).Utc is not { } zero) continue;
            var end = TrainingAlignment.SourceTimeOf(zero, TrainingAlignment.AnchorTime(segment, ClimbAnchor.End), training.ClockCorrection) + FitGreenTail;

            // The clip of the timeline that shows the climb's start (the earliest such clip).
            VideoClip? clip = null;
            long tf = 0;
            foreach (var candidate in allClips.OfType<VideoClip>().Where(c => c.MediaAssetId == asset.Id).OrderBy(c => c.TimelineStart))
            {
                var frame = HoldAnimationRule.StartFrame(candidate.TimelineStart, candidate.SourceIn, candidate.Speed, start, rate);
                if (frame >= candidate.TimelineStart.ToNearestFrame(rate) && frame < candidate.TimelineEnd.ToNearestFrame(rate))
                {
                    (clip, tf) = (candidate, frame);
                    break;
                }
            }
            if (clip is null || plan.TrackOf(clip).IsLocked) continue;

            long vs = clip.TimelineStart.ToNearestFrame(rate), ve = clip.TimelineEnd.ToNearestFrame(rate);
            var ps = tf - Math.Min(LayoutIntro.ToNearestFrame(rate), tf - vs);
            var endFrame = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, end, rate);
            var pe = Math.Min(ve, Math.Max(endFrame, tf + 1));
            if (pe <= ps) continue;
            var s = Math.Max(Math.Max(pe - LayoutFinal.ToNearestFrame(rate), tf), ps + 1);
            // The route: the one the climb is linked to on this clip, else a new one (its green only).
            var routeId = mark.LinkedClipId == clip.Id && FitMarkRules.LinkedRoute(clip, mark.LinkedRouteId) is { } linked ? linked.Id : Guid.NewGuid();
            var template = NewRouteTemplate(clip.Id, routeId, ps, pe, s, rate);
            template.Status = BoulderStatus.None;   // the lilac is added by hand
            template.FitClimb = mark.Climb;

            // A track above the clip free on [Ps, Pe) — also of the templates placed before — or a new one.
            var videoTrack = plan.TrackOf(clip);
            bool Free(Track track) => plan.EffectiveClips(track).All(c => c.State.End.ToNearestFrame(rate) <= ps || c.State.Start.ToNearestFrame(rate) >= pe);
            var track = Sequence.VideoTracks.Where(tr => tr.Order > videoTrack.Order && !tr.IsLocked).OrderBy(tr => tr.Order).FirstOrDefault(Free)
                        ?? newTracks.FirstOrDefault(tr => tr.Clips.All(c => c.TimelineEnd.ToNearestFrame(rate) <= ps || c.TimelineStart.ToNearestFrame(rate) >= pe));
            if (track is null)
            {
                track = NewTrack(TrackType.Video);
                track.Order += newTracks.Count;
                newTracks.Add(track);
            }
            if (newTracks.Contains(track)) track.Clips.Add(template);
            else plan.Insert(track, template);
            made.Add(mark.Climb);
            created.Add(template.Id);
        }

        if (created.Count == 0) return new TimelineEditResult { Success = true, NoChange = true, Message = Messages.FitRoutes_None };
        // The new tracks are added with their templates first; the rest is one plan.
        foreach (var track in newTracks)
        {
            var clips = track.Clips.ToList();
            track.Clips.Clear();
            foreach (var clip in clips) plan.Insert(track, clip);
        }
        if (Validate(plan) is { } error) return TimelineEditResult.Fail(error);
        var commands = newTracks.Select(t => (IUndoableCommand)new AddTrackCommand(Sequence, t)).ToList();
        commands.Add(plan.BuildCommand("Create Route Areas from FIT"));
        ExecuteTracked(new NotifyingCommand(new CompositeCommand("Create Route Areas from FIT", commands), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(created, string.Format(Messages.FitRoutes_Created, created.Count));
    }

    // ---- The paste of a duplicate FIT markup (D051 §5.4, B1) -------------------------------------------------------------

    /// <summary>Whether a pasted clip's markup shares a hold with the markup of a clip linked to a FIT climb now.</summary>
    private bool DuplicatesFitMarkup(TimelineClipboard clipboard)
    {
        if (Project.Training is not { Marks.Count: > 0 } training) return false;
        var linkedHolds = training.Marks.Where(m => m.LinkedClipId is not null)
            .Select(m => FitMarkRules.FindVideoClip(Sequence, m.LinkedClipId!.Value) is { } clip ? FitMarkRules.LinkedRoute(clip, m.LinkedRouteId) : null)
            .OfType<HoldMarkup>()
            .SelectMany(markup => markup.Holds.Select(h => h.Id))
            .ToHashSet();
        if (linkedHolds.Count == 0) return false;
        return clipboard.Entries.Select(e => e.Clip).OfType<VideoClip>()
            .Any(c => c.Routes.Any(markup => markup.Holds.Any(h => linkedHolds.Contains(h.Id))));
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------------

    private string? ActiveMark(ClimbIdentity climb, out TrainingReference training, out FitMark mark)
    {
        mark = null!;
        training = Project.Training!;
        if (training is null) return Messages.Training_None;
        if (training.DeclinedClimbs.Any(d => d.Climb == climb)) return Messages.FitMark_Declined;
        if (training.Marks.FirstOrDefault(m => m.Climb == climb) is not { } found) return Messages.FitMark_None;
        mark = found;
        return null;
    }

    /// <summary>D053: the route linked — <paramref name="routeId"/>, else the clip's only route; it needs a Duration animation
    /// and no other climb linked to it.</summary>
    private string? LinkableClip(ClimbIdentity climb, Guid clipId, Guid? routeId, out TrainingReference training, out FitMark mark, out Guid route)
    {
        route = Guid.Empty;
        if (ActiveMark(climb, out training, out mark) is { } error) return error;
        if (FitMarkRules.FindVideoClip(Sequence, clipId) is not { } clip) return Messages.Holds_OnlyVideo;
        var known = routeId is { } named ? new List<Guid> { named }
            : RouteOverlays.RouteIds(clip, RouteOverlays.AnchoredTo(clip, Sequence.VideoTracks.SelectMany(t => t.Clips))).ToList();
        if (known.Count > 1) return Messages.Route_Ambiguous;
        if (known.Count == 0 || RouteOverlays.Find(clip, known[0])?.Animation is not { Mode: HoldAnimationMode.Duration })
            return Messages.FitMark_NotDurationAnimation;
        route = known[0];
        var linkedRoute = route;
        var other = training.Marks.FirstOrDefault(m => m.LinkedClipId == clipId && FitMarkRules.LinkedRoute(clip, m.LinkedRouteId)?.Id == linkedRoute);
        if (other is not null && other.Climb != climb) return Messages.FitMark_AnimationLinked;
        return null;
    }

    /// <summary>The source time of a mark's own position on its video; for <c>proposed</c> by the video's <c>Z</c> (null
    /// without one).</summary>
    private MediaTime? OwnSourceTime(FitMark mark) => mark.Position.SourceTime ??
        FitMarkRules.EffectiveStart(mark with { LinkedClipId = null, LinkedRouteId = null }, Project)?.SourceTime;

    private static List<FitMark> Replace(IEnumerable<FitMark> marks, FitMark old, FitMark @new) =>
        marks.Select(m => ReferenceEquals(m, old) ? @new : m).ToList();

    private TimelineEditResult ReplaceMarks(TrainingReference training, IReadOnlyList<FitMark> marks, IReadOnlyList<DeclinedClimb> declined,
        string description)
    {
        ExecuteTracked(new NotifyingCommand(new SetFitMarksCommand(training, training.Marks.ToList(), training.DeclinedClimbs.ToList(),
            marks, declined, description), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }
}
