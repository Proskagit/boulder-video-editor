using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.ViewModels.Panels;

// --- D069 amendment 2: a transition's length dragged on the timeline ----------------------------------------------------------
// A press on an edge of a transition's zone (it gets selected), then a drag: the zone grows or shrinks around its cut (both edges, the
// zone stays centred — D025 §3), whole frames, from the type's minimum to its maximum and what the clips hold; the release is
// one Undo step (SetTransitionDuration), Esc / a refusal puts the zone back.
public sealed partial class TimelineViewModel
{
    /// <summary>How near a zone's edge a press grabs it, in pixels.</summary>
    public const double TransitionEdgePixels = 6;

    private (TimelineTransitionViewModel Zone, long Cut, long Frames, long Min, long Max)? _zoneDrag;

    /// <summary>True while a zone's edge is dragged.</summary>
    public bool IsTransitionResizing => _zoneDrag is not null;

    /// <summary>A press at <paramref name="contentX"/> on <paramref name="zone"/>: when it is on (or by) one of the zone's edges,
    /// the drag of its length starts and the transition is selected; false otherwise (the press selects it as before).</summary>
    public bool BeginTransitionResize(TimelineTransitionViewModel zone, double contentX)
    {
        if (!CanEdit()) return false;
        var nearLeft = Math.Abs(contentX - zone.Left) <= TransitionEdgePixels;
        var nearRight = Math.Abs(contentX - (zone.Left + zone.Width)) <= TransitionEdgePixels;
        if (!nearLeft && !nearRight) return false;
        var right = zone.Track.Clips.FirstOrDefault(c => c.Id == zone.Transition.RightClipId);
        if (right is null) return false;
        SelectTransition(zone.Id);
        var type = zone.Transition.TransitionTypeId;
        var max = _edit.MaxTransitionFrames(zone.Transition.LeftClipId, zone.Transition.RightClipId, type) ?? TransitionRules.Frames(zone.Transition.Duration, FrameRate);
        max = Math.Min(max, TransitionRules.MaxFramesOf(type, FrameRate));
        _zoneDrag = (zone, right.TimelineStart.ToFrameFloor(FrameRate), TransitionRules.Frames(zone.Transition.Duration, FrameRate),
            TransitionRules.MinFramesOf(type, FrameRate), Math.Max(TransitionRules.MinFramesOf(type, FrameRate), max));
        return true;
    }

    /// <summary>The pointer at <paramref name="contentX"/>: the zone's length is twice its distance from the cut (whole frames,
    /// within the limits); the zone is drawn at that length.</summary>
    public void UpdateTransitionResize(double contentX)
    {
        if (_zoneDrag is not { } drag) return;
        var at = TimelineCoordinateMapper.XToTime(Math.Max(0, contentX), PixelsPerSecond).ToNearestFrame(FrameRate);
        var frames = Math.Clamp(2 * Math.Abs(at - drag.Cut), drag.Min, drag.Max);
        _zoneDrag = drag with { Frames = frames };
        drag.Zone.LayoutWithFrames(PixelsPerSecond, FrameRate, frames);
        _status.Report(string.Format(Strings.Transition_Dragging, TransitionNames.Duration(frames, FrameRate)));
    }

    /// <summary>The release: the length set, one Undo step; a refusal (or no change) shows the zone as it is.</summary>
    public void EndTransitionResize()
    {
        if (_zoneDrag is not { } drag) return;
        _zoneDrag = null;
        if (!CanEdit())
        {
            drag.Zone.Layout(PixelsPerSecond, FrameRate);   // an export started meanwhile: nothing changes
            return;
        }
        if (drag.Frames != TransitionRules.Frames(drag.Zone.Transition.Duration, FrameRate))
            Report(_edit.SetTransitionDuration(drag.Zone.Id, MediaTime.FromFrame(drag.Frames, FrameRate)));
        drag.Zone.Layout(PixelsPerSecond, FrameRate);
        SelectTransition(drag.Zone.Id);   // the Inspector shows the new length
    }

    public void CancelTransitionResize()
    {
        if (_zoneDrag is not { } drag) return;
        _zoneDrag = null;
        drag.Zone.Layout(PixelsPerSecond, FrameRate);
    }
}
