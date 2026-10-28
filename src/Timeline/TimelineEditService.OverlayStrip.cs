using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Commands;

namespace AiVideoEditor.Timeline;

// --- The overlay strip (D048 stage 3, §4) -----------------------------------------------------------------------------
// The strip's segments read from the model, and its handles as edits of the owning entities — the anchored «Боулдер»'s
// bounds and status moment, the Duration animation's start and length — each one step. The limits of §4.2 stop a value;
// the no-overlap limits (the lilac over the green or the orange, the orange over the lilac) refuse the edit with a message.
// The checks are here, in the edit service, not only in the strip's UI.
public sealed partial class TimelineEditService
{
    public OverlayStripState? OverlayStripOf(Guid videoClipId)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (FindVideo(plan, videoClipId) is not { } video) return null;
        var parts = StripParts(plan, video);
        if (parts.Template is null && parts.Orange is null) return null;
        FrameSpan? green = null, lilac = null;
        if (parts.Template is not null)
        {
            green = new FrameSpan(parts.Ps, parts.S ?? parts.Pe);
            if (parts.S is { } s) lilac = new FrameSpan(s, parts.Pe);
        }
        return new OverlayStripState(new FrameSpan(parts.Vs, parts.Ve), parts.Template?.Id, green,
            parts.Orange is { } o ? new FrameSpan(o.Start, o.End) : null, lilac);
    }

    public TimelineEditResult EditOverlayStrip(Guid videoClipId, OverlayStripHandle handle, long frameDelta)
    {
        var planned = PlanOverlayStrip(videoClipId, handle, frameDelta);
        if (planned.Error is { } error) return TimelineEditResult.Fail(error);
        if (planned.Plan is not { } plan) return TimelineEditResult.Unchanged();
        var orangeHandle = IsOrangeHandle(handle);
        var description = orangeHandle ? "Edit Hold Animation" : "Edit Route Overlay";
        // Its own step: a lone property command would merge with an earlier Inspector edit of the template (D017).
        var command = new CompositeCommand(description, new[] { plan.BuildCommand(description) });
        _undoRedo.Execute(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(planned.TemplateId is { } t && !orangeHandle ? new[] { videoClipId, t } : new[] { videoClipId });
    }

    public OverlayStripPreview? PreviewOverlayStrip(Guid videoClipId, OverlayStripHandle handle, long frameDelta)
    {
        var planned = PlanOverlayStrip(videoClipId, handle, frameDelta);
        return planned.Strip is { } strip ? new OverlayStripPreview(strip, planned.Error) : null;
    }

    public TimelineEditResult DeleteHoldMarkup(Guid videoClipId)
    {
        if (MarkupClip(videoClipId, out var video) is { } clipError) return TimelineEditResult.Fail(clipError);
        if (video.HoldMarkup is null) return TimelineEditResult.Unchanged();
        var plan = new EditPlan(Sequence, Settings);
        plan.SetMarkup(video, null);
        if (Validate(plan) is { } error) return TimelineEditResult.Fail(error);
        var command = new CompositeCommand("Delete Hold Markup", new[] { plan.BuildCommand("Delete Hold Markup") });
        _undoRedo.Execute(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { videoClipId });
    }

    private static bool IsOrangeHandle(OverlayStripHandle handle) =>
        handle is OverlayStripHandle.OrangeStart or OverlayStripHandle.OrangeEnd or OverlayStripHandle.OrangeBody;

    /// <summary>
    /// A strip gesture planned, nothing applied: <c>Strip</c> — the segments it leads to (also for a refusal, so a preview
    /// can show where the pointer has them); <c>Plan</c> — null for no change or a refusal; <c>Error</c> — the refusal.
    /// The limits of §4.2 stop the value; the no-overlap ones and the plan's validation refuse.
    /// </summary>
    private (OverlayStripState? Strip, EditPlan? Plan, string? Error, Guid? TemplateId) PlanOverlayStrip(Guid videoClipId,
        OverlayStripHandle handle, long frameDelta)
    {
        if (MarkupClip(videoClipId, out var video) is { } clipError) return (null, null, clipError, null);
        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var p = StripParts(plan, video);
        long ps = p.Ps, pe = p.Pe, vs = p.Vs, ve = p.Ve;
        var s = p.S;
        var orange = p.Orange;

        var lilacHandle = handle is OverlayStripHandle.Status or OverlayStripHandle.LilacEnd or OverlayStripHandle.LilacBody;
        if ((handle == OverlayStripHandle.GreenStart && p.Template is null) || (lilacHandle && s is null) ||
            (IsOrangeHandle(handle) && orange is null))
            return (null, null, Messages.Strip_NoSegment, null);

        // The limits stop the value (no push); the no-overlap ones refuse below.
        switch (handle)
        {
            case OverlayStripHandle.GreenStart:
                ps = Math.Clamp(ps + frameDelta, vs, (s ?? pe) - 1);
                break;
            case OverlayStripHandle.Status:
                s = Math.Clamp(s!.Value + frameDelta, ps + 1, pe - MinStatusFrames);
                break;
            case OverlayStripHandle.LilacEnd:
                pe = Math.Clamp(pe + frameDelta, s!.Value + MinStatusFrames, ve);
                break;
            case OverlayStripHandle.LilacBody:
                var lilacDelta = Math.Clamp(frameDelta, ps + 1 - s!.Value, ve - pe);
                (s, pe) = (s + lilacDelta, pe + lilacDelta);
                break;
            case OverlayStripHandle.OrangeStart:
                orange = (Math.Clamp(orange!.Value.Start + frameDelta, vs, orange.Value.End - 1), orange.Value.End);
                break;
            case OverlayStripHandle.OrangeEnd:
                orange = (orange!.Value.Start, Math.Clamp(orange.Value.End + frameDelta, orange.Value.Start + 1, ve));
                break;
            case OverlayStripHandle.OrangeBody:
                var orangeDelta = Math.Clamp(frameDelta, vs - orange!.Value.Start, ve - orange.Value.End);
                orange = (orange.Value.Start + orangeDelta, orange.Value.End + orangeDelta);
                break;
        }

        var strip = new OverlayStripState(new FrameSpan(vs, ve), p.Template?.Id,
            p.Template is null ? null : new FrameSpan(ps, s ?? pe),
            orange is { } span ? new FrameSpan(span.Start, span.End) : null,
            s is { } lilacStart ? new FrameSpan(lilacStart, pe) : null);
        if (ps == p.Ps && pe == p.Pe && s == p.S && orange == p.Orange) return (strip, null, null, p.Template?.Id);
        // No overlap of the lilac (§1.24, §4.2): refused, never resolved by deleting a segment.
        if (s is { } status && orange is { } o && status < o.End && o.Start < pe) return (strip, null, Messages.Overlay_LilacOverlap, p.Template?.Id);

        if (p.Template is { } template && (ps != p.Ps || pe != p.Pe || s != p.S))
        {
            var track = plan.TrackOf(template);
            if (track.IsLocked) return (strip, null, string.Format(Messages.Edit_TrackLocked, track.Name), template.Id);
            var state = ClipState.Capture(template);
            if (ps != p.Ps || pe != p.Pe) plan.Update(template, track, ClipState.FromFrames(ps, pe, state.SourceIn, rate));
            var fields = plan.TemplateFieldsOf(template);
            var offset = (s ?? p.Ps + StatusFrames(template, fields, rate)) - ps;   // without a status its moment keeps its frame
            var statusAt = StatusAtFor(template, offset, rate);
            if (statusAt != fields.StatusAt) plan.SetTemplateFields(template, fields with { StatusAt = statusAt });
        }
        if (orange != p.Orange && video.HoldMarkup is { Animation: { } animation } markup)
        {
            var state = ClipState.Capture(video);
            plan.SetMarkup(video, markup with
            {
                Animation = animation with
                {
                    StartSourceTime = HoldAnimationRule.SourceTimeAt(state.Start, state.SourceIn, state.Speed, orange!.Value.Start, rate),
                    Duration = MediaTime.FromFrame(orange.Value.End - orange.Value.Start, rate)
                }
            });
        }

        return Validate(plan) is { } error ? (strip, null, error, p.Template?.Id) : (strip, plan, null, p.Template?.Id);
    }
    /// <summary>The strip's frames now: the clip, its anchored «Боулдер» (the first one) with its status moment when it
    /// has a lilac on the timeline, and the Duration animation's orange.</summary>
    private static (long Vs, long Ve, TemplateClip? Template, long Ps, long Pe, long? S, (long Start, long End)? Orange) StripParts(
        EditPlan plan, VideoClip video)
    {
        var rate = plan.Rate;
        var state = ClipState.Capture(video);
        long vs = state.Start.ToNearestFrame(rate), ve = state.End.ToNearestFrame(rate);
        var orange = OrangeOf(video.HoldMarkup, state, rate);
        var template = AnchoredTemplates(plan, video).Where(t => t.Kind == TemplateKind.Boulder).OrderBy(t => t.TimelineStart).FirstOrDefault();
        if (template is null) return (vs, ve, null, 0, 0, null, orange);
        long ps = template.TimelineStart.ToNearestFrame(rate), pe = template.TimelineEnd.ToNearestFrame(rate);
        var fields = plan.TemplateFieldsOf(template);
        long? s = fields.Status != BoulderStatus.None && ps + StatusFrames(template, fields, rate) is var at && at < pe ? at : null;
        return (vs, ve, template, ps, pe, s, orange);
    }
}
