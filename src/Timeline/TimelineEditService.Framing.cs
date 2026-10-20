using System.Globalization;
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
    public TimelineEditResult FrameClip(Guid clipId, ClipFraming framing)
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

        return SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Visual = visual with { Scale = scale },
            Gesture = new ClipPropertyGesture(framing == ClipFraming.Fill ? "Fill Frame" : "Fit in Frame")
        });
    }

    private static string Percent(double fraction) =>
        double.IsFinite(fraction) ? (fraction * 100).ToString("0.##", CultureInfo.InvariantCulture) : "∞";
}
