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
            var unlinked = mark with { LinkedClipId = null };
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

    public TimelineEditResult LinkFitClimb(ClimbIdentity climb, Guid clipId)
    {
        if (LinkableClip(climb, clipId, out var training, out var mark) is { } error) return TimelineEditResult.Fail(error);
        if (mark.LinkedClipId == clipId) return TimelineEditResult.Unchanged();
        if (mark.LinkedClipId is not null) return TimelineEditResult.Fail(Messages.FitMark_ClimbLinked);
        // positionChangedAt for a changed effective start is set by the marks following the step (D051 §5.6).
        return ReplaceMarks(training, Replace(training.Marks, mark, mark with { LinkedClipId = clipId }),
            training.DeclinedClimbs.ToList(), "Link FIT Climb");
    }

    public TimelineEditResult UnlinkFitClimb(ClimbIdentity climb)
    {
        if (ActiveMark(climb, out var training, out var mark) is { } error) return TimelineEditResult.Fail(error);
        if (mark.LinkedClipId is null) return TimelineEditResult.Fail(Messages.FitMark_NotLinked);
        return ReplaceMarks(training, Replace(training.Marks, mark, mark with { LinkedClipId = null }),
            training.DeclinedClimbs.ToList(), "Unlink FIT Climb");
    }

    public TimelineEditResult LayOutRouteFromMark(ClimbIdentity climb, Guid clipId)
    {
        if (ActiveMark(climb, out var training, out var mark) is { } error) return TimelineEditResult.Fail(error);
        if (mark.LinkedClipId is { } linked && linked != clipId) return TimelineEditResult.Fail(Messages.FitMark_ClimbLinked);
        if (FitMarkRules.FindVideoClip(Sequence, clipId) is not { } clip) return TimelineEditResult.Fail(Messages.Holds_OnlyVideo);
        if (training.Marks.Any(m => m.LinkedClipId == clipId && m.Climb != climb)) return TimelineEditResult.Fail(Messages.FitMark_AnimationLinked);
        if (clip.MediaAssetId != mark.MediaAssetId) return TimelineEditResult.Fail(Messages.FitMark_ClipNotOnMarkVideo);
        if (OwnSourceTime(mark) is not { } source) return TimelineEditResult.Fail(Messages.FitMark_NoRecordingStart);

        var rate = Settings.FrameRate;
        var frame = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, source, rate);
        if (frame < clip.TimelineStart.ToNearestFrame(rate) || frame > clip.TimelineEnd.ToNearestFrame(rate))
            return TimelineEditResult.Fail(Messages.FitMark_MarkNotInClip);

        // D051 §5.6: linking a mark by laying the route out from it keeps its positionChangedAt (T on its frame).
        var keep = mark.LinkedClipId is null ? new HashSet<ClimbIdentity> { climb } : null;
        return LayOutIntroAndFinal(clipId, MediaTime.FromFrame(frame, rate), keep, hasAnimation => hasAnimation
            ? new SetFitMarksCommand(training, training.Marks.ToList(), training.DeclinedClimbs.ToList(),
                Replace(training.Marks, mark, mark with { LinkedClipId = clipId }), training.DeclinedClimbs.ToList(), "Link FIT Climb")
            : null);
    }

    // ---- The paste of a duplicate FIT markup (D051 §5.4, B1) -------------------------------------------------------------

    /// <summary>Whether a pasted clip's markup shares a hold with the markup of a clip linked to a FIT climb now.</summary>
    private bool DuplicatesFitMarkup(TimelineClipboard clipboard)
    {
        if (Project.Training is not { Marks.Count: > 0 } training) return false;
        var linkedHolds = training.Marks.Where(m => m.LinkedClipId is not null)
            .Select(m => FitMarkRules.FindVideoClip(Sequence, m.LinkedClipId!.Value)?.HoldMarkup)
            .OfType<HoldMarkup>()
            .SelectMany(markup => markup.Holds.Select(h => h.Id))
            .ToHashSet();
        if (linkedHolds.Count == 0) return false;
        return clipboard.Entries.Select(e => e.Clip).OfType<VideoClip>()
            .Any(c => c.HoldMarkup is { } markup && markup.Holds.Any(h => linkedHolds.Contains(h.Id)));
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

    private string? LinkableClip(ClimbIdentity climb, Guid clipId, out TrainingReference training, out FitMark mark)
    {
        if (ActiveMark(climb, out training, out mark) is { } error) return error;
        if (FitMarkRules.FindVideoClip(Sequence, clipId) is not { } clip) return Messages.Holds_OnlyVideo;
        if (clip.HoldMarkup?.Animation is not { Mode: HoldAnimationMode.Duration }) return Messages.FitMark_NotDurationAnimation;
        var other = training.Marks.FirstOrDefault(m => m.LinkedClipId == clipId);
        if (other is not null && other.Climb != climb) return Messages.FitMark_AnimationLinked;
        return null;
    }

    /// <summary>The source time of a mark's own position on its video; for <c>proposed</c> by the video's <c>Z</c> (null
    /// without one).</summary>
    private MediaTime? OwnSourceTime(FitMark mark) => mark.Position.SourceTime ??
        FitMarkRules.EffectiveStart(mark with { LinkedClipId = null }, Project)?.SourceTime;

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
