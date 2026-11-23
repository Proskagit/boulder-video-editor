using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Timeline.Commands;

namespace AiVideoEditor.Timeline;

/// <summary>D078: the FIT's route areas refined by the attempt's events found in the video.</summary>
public sealed partial class TimelineEditService
{
    /// <summary>D078 §3: a lift-off snaps <c>T</c> only this near (the owner's Q3).</summary>
    public static readonly MediaTime LiftOffSnapRadius = MediaTime.FromSeconds(3);

    /// <summary>D078 §3: a fall is looked for this near to the climb's watch end − <see cref="FallBeforeWatchEnd"/>.</summary>
    public static readonly MediaTime FallSnapRadius = MediaTime.FromSeconds(8);

    /// <summary>D078 §1: on the watch's automatic detection a fall lies ~3 s before the climb's end (research stage 0).</summary>
    public static readonly MediaTime FallBeforeWatchEnd = MediaTime.FromSeconds(3);

    /// <summary>
    /// D078 §3: refines the route areas made from the FIT whose bounds are still the laid-out ones (<see cref="TemplateClip.FitLayout"/>)
    /// by <paramref name="events"/> (per area's template id, its window's events). <paramref name="fall"/>: on «Попытка» the lilac's
    /// start moves to the detected fall nearest to the climb's watch end − 3 s within 8 s (when the status moment is the laid-out
    /// one), the green's end growing to the fall + 5 s when shorter. <paramref name="liftOff"/>: <c>T</c> — on a route without an
    /// orange — moves to the lift-off nearest to it within 3 s, the green's start following as at the layout. The layout takes the
    /// new bounds. Areas changed by hand, on a locked track or whose new bounds would cover another clip stay. One Undo step;
    /// nothing moved → no step.
    /// </summary>
    public TimelineEditResult RefineFitAreasFromVideo(IReadOnlyDictionary<Guid, AttemptEventSet> events, bool fall, bool liftOff)
    {
        if ((!fall && !liftOff) || events.Count == 0) return new TimelineEditResult { Success = true, NoChange = true };
        var rate = Settings.FrameRate;
        var allClips = Sequence.VideoTracks.SelectMany(t => t.Clips).ToList();
        var workout = Project.Training is { Workout: { } w } training && training.IsUsable ? w : null;
        var clockCorrection = Project.Training?.ClockCorrection ?? MediaTime.Zero;

        var plan = new EditPlan(Sequence, Settings);
        var layouts = new List<(TemplateClip Template, FitAreaLayout? Before, FitAreaLayout After)>();
        var starts = new List<(TemplateClip Template, MediaTime? Before, MediaTime? After)>();
        var refined = new List<Guid>();
        foreach (var (templateId, found) in events)
        {
            if (allClips.FirstOrDefault(c => c.Id == templateId) is not TemplateClip { Kind: TemplateKind.Boulder, FitClimb: { } climb, FitLayout: { } laid } template ||
                template.AnchorClipId is not { } clipId || allClips.FirstOrDefault(c => c.Id == clipId) is not VideoClip clip)
                continue;
            var current = CurrentFitLayout(template, clip, rate);
            if (laid.SourceStart != current.SourceStart || laid.SourceEnd != current.SourceEnd) continue;   // changed by hand
            var track = plan.TrackOf(template);
            if (track.IsLocked) continue;

            long vs = clip.TimelineStart.ToNearestFrame(rate), ve = clip.TimelineEnd.ToNearestFrame(rate);
            long ps = template.TimelineStart.ToNearestFrame(rate), pe = template.TimelineEnd.ToNearestFrame(rate);
            var s = ps + template.StatusAt.ToNearestFrame(rate);
            var (ps2, pe2, s2) = (ps, pe, s);
            MediaTime? newStart = null;
            long? tFrame = template.RouteStart is { } rs0 ? HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, rs0, rate) : null;

            // The lift-off: T without an orange (the user's animation is the user's T).
            var routeId = RouteOverlays.RouteIdOf(template, clip);
            var hasOrange = routeId is { } rid && RouteOverlays.OrangeEndOf(clip, rid, rate) is not null;
            if (liftOff && !hasOrange && template.RouteStart is { } t &&
                AttemptEventSet.Nearest(found.LiftOffs, t, LiftOffSnapRadius) is { } lift && lift != t)
            {
                var tf = Math.Clamp(HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, lift, rate), vs, ve - 1);
                var newPs = tf - Math.Min(LayoutIntro.ToNearestFrame(rate), tf - vs);
                if (newPs < s2)
                {
                    ps2 = newPs;
                    newStart = TrainingAlignment.SourceTimeAt(clip, tf, rate);
                    tFrame = tf;
                }
            }

            // The fall: the lilac of «Попытка» from the detected fall.
            if (fall && template.Status == BoulderStatus.Attempt && template.StatusAt == laid.StatusAt && workout is not null &&
                workout.Climbs.FirstOrDefault(c => FitMarkRules.IdentityOf(c) == climb) is { } segment &&
                FindAsset(clip.MediaAssetId) is { } asset && FitRouteBinding.TimeOf(asset, workout) is { } time)
            {
                var watchEnd = TrainingAlignment.SourceTimeOf(time.Zero, segment.End, clockCorrection);
                if (AttemptEventSet.Nearest(found.Falls, watchEnd - FallBeforeWatchEnd, FallSnapRadius) is { } fallAt)
                {
                    var f = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, fallAt, rate);
                    // After the route's start (never a lilac before T), room for the status before the clip's end.
                    if (f > Math.Max(ps2, tFrame ?? ps2) && f < ve - MinStatusFrames)
                    {
                        s2 = f;
                        pe2 = Math.Min(ve, Math.Max(pe2, f + LayoutFinal.ToNearestFrame(rate)));
                    }
                }
            }

            if (ps2 == ps && pe2 == pe && s2 == s && newStart is null) continue;
            if (s2 <= ps2 || pe2 - s2 < MinStatusFrames) continue;
            var statusAt = MediaTime.FromFrame(s2 - ps2, rate);
            var timing = ClipState.FromFrames(ps2, pe2, MediaTime.Zero, rate);
            var fields = plan.TemplateFieldsOf(template) with { StatusAt = statusAt };

            // Alone first: an area whose new bounds would cover another clip of its track stays where it is.
            var trial = new EditPlan(Sequence, Settings);
            trial.Update(template, track, timing);
            trial.Carried.Add(template);
            trial.SetTemplateFields(template, fields);
            if (Validate(trial) is not null) continue;
            plan.Update(template, track, timing);
            plan.Carried.Add(template);
            plan.SetTemplateFields(template, fields);
            layouts.Add((template, template.FitLayout, FitLayoutOf(clip, ps2, pe2, statusAt, rate)));
            if (newStart is { } start) starts.Add((template, template.RouteStart, start));
            refined.Add(template.Id);
        }

        if (refined.Count == 0 || Validate(plan) is not null) return new TimelineEditResult { Success = true, NoChange = true };
        var commands = new List<IUndoableCommand> { plan.BuildCommand("Refine Areas from Video"), new SetFitLayoutsCommand(layouts) };
        if (starts.Count > 0) commands.Add(new SetRouteStartsCommand(starts));
        ExecuteTracked(new NotifyingCommand(new CompositeCommand("Refine Areas from Video", commands), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(refined, string.Format(Messages.FitRoutes_RefinedByVideo, refined.Count));
    }
}
