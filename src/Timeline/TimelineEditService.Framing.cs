using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Timeline;

// --- Fill frame / Fit in frame (Phase 26, D041 §2) ----------------------------------------------------------------------
// A one-off computation of the clip's Scale from the canvas, the asset's display size, the crop and the rotation
// (CompositionMath.FillScale / FitScale); nothing else changes and no mode is stored. The edit is a gesture of its own, so
// it is one undo step that never merges with a typed Scale before or after it.
public sealed partial class TimelineEditService
{
    public TimelineEditResult SetClipBackground(Guid clipId, ClipBackground background)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        if (clips[0] is not VideoClip clip) return TimelineEditResult.Fail(Messages.Frame_NoPicture);
        var track = plan.TrackOf(clip);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));
        if (clip.Background == background) return TimelineEditResult.Unchanged();
        ExecuteTracked(new Commands.NotifyingCommand(new Commands.SetClipBackgroundCommand(clip, clip.Background, background), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { clip.Id });
    }

    /// <summary>D061: switches a video clip's Follow on (with its settings: target, smoothness) or off (null) — one undo step;
    /// with <paramref name="hideOutlinesBehindPeople"/> the outlines' Occlusion is set in the same step (D061 amendment 1:
    /// switching Follow on switches Occlusion on).</summary>
    public TimelineEditResult SetClipFollow(Guid clipId, FollowSettings? follow, bool? hideOutlinesBehindPeople = null,
        ClipPropertyGesture? gesture = null)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        if (clips[0] is not VideoClip clip) return TimelineEditResult.Fail(Messages.Frame_NoPicture);
        var track = plan.TrackOf(clip);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));
        // D061 amendment 1: parts sorted and never overlapping — what the project file accepts (never a file that no
        // longer opens).
        if (follow is not null && (follow.Parts.IsEmpty || !FollowSettings.AreValid(follow.Parts)))
            return TimelineEditResult.Fail(Messages.Follow_InvalidParts);
        // D063 §4: a part's framing keyframes are valid as the clip's (the offset as a position, the scale within its limits).
        if (follow is not null && follow.Parts.Any(p => p.Keyframes is { } keys && !TransformKeyframes.AreValid(keys.Keys)))
            return TimelineEditResult.Fail(Messages.Prop_KeyframesInvalid);
        var occlusion = hideOutlinesBehindPeople is { } hides && hides != clip.HidesOutlinesBehindPeople;
        if (Equals(clip.Follow, follow) && !occlusion) return TimelineEditResult.Unchanged();
        var commands = new List<IUndoableCommand>();
        if (!Equals(clip.Follow, follow))
        {
            var followCommand = new Commands.SetClipFollowCommand(clip, clip.Follow, follow, gesture);
            // D064 amendment 1: a part's keyframe added, moved or removed brings its volume point along — one step (a drag merges).
            var values = ClipPropertyValues.Capture(clip);
            var synced = HasOwnSound(clip) ? KeyframeVolumeSync.Sync(values, values, clip.Follow, follow).Values : values;
            var fields = ClipPropertyValues.Diff(values, synced);
            commands.Add(fields == ClipPropertyFields.None
                ? followCommand
                : new Commands.SetSoundAndFollowCommand(new Commands.SetClipPropertiesCommand(clip, values, synced, fields, gesture), followCommand));
        }
        if (occlusion) commands.Add(new Commands.SetClipOcclusionCommand(clip, hideOutlinesBehindPeople!.Value));
        IUndoableCommand command = commands.Count == 1 ? commands[0] : new CompositeCommand(commands[0].Description, commands);
        ExecuteTracked(new Commands.NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { clip.Id });
    }

    /// <summary>D061: hides (or shows again) a video clip's hold outlines behind people — one undo step.</summary>
    public TimelineEditResult SetClipHidesOutlinesBehindPeople(Guid clipId, bool hides)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        if (clips[0] is not VideoClip clip) return TimelineEditResult.Fail(Messages.Frame_NoPicture);
        var track = plan.TrackOf(clip);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));
        if (clip.HidesOutlinesBehindPeople == hides) return TimelineEditResult.Unchanged();
        ExecuteTracked(new Commands.NotifyingCommand(new Commands.SetClipOcclusionCommand(clip, hides), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { clip.Id });
    }

    public TimelineEditResult FrameClip(Guid clipId, ClipFraming framing, MediaTime? at = null)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);

        var clip = clips[0];
        if (clip is not (VideoClip or ImageClip) || VisualProperties.Of(clip) is not { } visual)
            return TimelineEditResult.Fail(Messages.Frame_NoPicture);
        var track = plan.TrackOf(clip);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        var asset = FindAsset(((MediaBackedClip)clip).MediaAssetId);
        if (PlaybackSnapshotBuilder.DisplaySize(asset?.Metadata) is not { } source)
            return TimelineEditResult.Fail(string.Format(Messages.Frame_SizeUnknown, asset?.FileName ?? string.Empty));

        // D063: with keyframes the scale is the keyframe's at the given time (its position and the clip's crop and rotation
        // are what the fit depends on — the position never does).
        var keys = (clip as VideoClip)?.Keyframes;
        var when = at ?? keys?.Keys[0].Time;
        if (keys is not null && when is { } t) visual = TransformKeyframes.Apply(visual, keys, t);
        var canvas = new FrameSize(Settings.FrameWidth, Settings.FrameHeight);
        var scale = framing == ClipFraming.Fill
            ? CompositionMath.FillScale(canvas, source, visual)
            : CompositionMath.FitScale(canvas, source, visual);
        if (!double.IsFinite(scale) || scale < ClipPropertyLimits.MinScale || scale > ClipPropertyLimits.MaxScale)
            return TimelineEditResult.Fail(string.Format(Messages.Frame_ScaleOutOfRange, Percent(scale),
                Percent(ClipPropertyLimits.MinScale), Percent(ClipPropertyLimits.MaxScale)));

        if (scale == visual.Scale)
            return new TimelineEditResult
            {
                Success = true, NoChange = true,
                Message = framing == ClipFraming.Fill ? Messages.Frame_AlreadyFills : Messages.Frame_AlreadyFits
            };

        var gesture = new ClipPropertyGesture(framing == ClipFraming.Fill ? "Fill Frame" : "Fit in Frame");
        if (keys is not null && when is { } time)
        {
            var value = new TransformValue(visual.PositionX, visual.PositionY, scale);
            return SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(keys.WithValueAt(time, value).Keys), Gesture = gesture });
        }
        return SetClipProperties(clip.Id, new ClipPropertyChange { Visual = visual with { Scale = scale }, Gesture = gesture });
    }

    private static string Percent(double fraction) =>
        double.IsFinite(fraction) ? (fraction * 100).ToString("0.##", CultureInfo.InvariantCulture) : "∞";
}
