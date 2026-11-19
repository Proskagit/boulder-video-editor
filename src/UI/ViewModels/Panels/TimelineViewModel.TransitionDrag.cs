using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.ViewModels.Panels;

// --- D069 amendment 2: a transition's length dragged on the timeline ----------------------------------------------------------
// A press on an edge of a transition's zone (it gets selected), then a drag: the zone grows or shrinks around its cut (both edges, the
// zone stays centred — D025 §3), whole frames, from the type's minimum to its maximum and what the clips hold; the release is
// one Undo step (SetTransitionDuration), Esc / a refusal puts the zone back. D071 amendment 2: each edge stays on its side of the
// cut (dragged past it, the zone stops at its minimum — never turned inside out), and the dragged edge snaps (the playhead, the
// markers, the clips' edges — the timeline's magnet).
public sealed partial class TimelineViewModel
{
    /// <summary>How near a zone's edge a press grabs it, in pixels.</summary>
    public const double TransitionEdgePixels = 6;

    private (TimelineTransitionViewModel Zone, long Cut, long Frames, long Min, long Max, bool LeftEdge)? _zoneDrag;

    /// <summary>True while a zone's edge is dragged.</summary>
    public bool IsTransitionResizing => _zoneDrag is not null;

    /// <summary>A press at <paramref name="contentX"/> on <paramref name="zone"/>: when it is on (or by) one of the zone's edges,
    /// the drag of its length starts and the transition is selected; false otherwise (the press selects it as before).</summary>
    /// <summary>Whether <paramref name="contentX"/> is on (or by) one of <paramref name="zone"/>'s edges and its length can be
    /// dragged now — the hover's resize cursor and the press ask the same.</summary>
    public bool IsTransitionEdge(TimelineTransitionViewModel zone, double contentX) =>
        CanEdit() && (Math.Abs(contentX - zone.Left) <= TransitionEdgePixels || Math.Abs(contentX - (zone.Left + zone.Width)) <= TransitionEdgePixels);

    public bool BeginTransitionResize(TimelineTransitionViewModel zone, double contentX)
    {
        if (!IsTransitionEdge(zone, contentX)) return false;
        var right = zone.Track.Clips.FirstOrDefault(c => c.Id == zone.Transition.RightClipId);
        if (right is null) return false;
        SelectTransition(zone.Id);
        var type = zone.Transition.TransitionTypeId;
        var max = _edit.MaxTransitionFrames(zone.Transition.LeftClipId, zone.Transition.RightClipId, type) ?? TransitionRules.Frames(zone.Transition.Duration, FrameRate);
        max = Math.Min(max, TransitionRules.MaxFramesOf(type, FrameRate));
        var leftEdge = Math.Abs(contentX - zone.Left) <= Math.Abs(contentX - (zone.Left + zone.Width));
        _zoneDrag = (zone, right.TimelineStart.ToFrameFloor(FrameRate), TransitionRules.Frames(zone.Transition.Duration, FrameRate),
            TransitionRules.MinFramesOf(type, FrameRate), Math.Max(TransitionRules.MinFramesOf(type, FrameRate), max), leftEdge);
        return true;
    }

    /// <summary>The pointer at <paramref name="contentX"/>: the zone's length is twice its distance from the cut (whole frames,
    /// within the limits); the zone is drawn at that length.</summary>
    public void UpdateTransitionResize(double contentX)
    {
        if (_zoneDrag is not { } drag) return;
        var raw = TimelineCoordinateMapper.XToTime(Math.Max(0, contentX), PixelsPerSecond);
        // The dragged edge snaps; the transition's own clips are left out (their cut is the zone's centre).
        var own = Tracks.SelectMany(t => t.Clips).Where(c => c.Id == drag.Zone.Transition.LeftClipId || c.Id == drag.Zone.Transition.RightClipId);
        var snapped = raw + ApplySnap(new[] { raw }, own, MediaTime.Zero);
        var at = snapped.ToNearestFrame(FrameRate);
        // Each edge on its own side of the cut: past it the distance is 0 and the zone stops at its minimum.
        var distance = Math.Max(0, drag.LeftEdge ? drag.Cut - at : at - drag.Cut);
        var frames = Math.Clamp(2 * distance, drag.Min, drag.Max);
        _zoneDrag = drag with { Frames = frames };
        drag.Zone.LayoutWithFrames(PixelsPerSecond, FrameRate, frames);
        _status.Report(string.Format(Strings.Transition_Dragging, TransitionNames.Duration(frames, FrameRate)));
    }

    /// <summary>The release: the length set, one Undo step; a refusal (or no change) shows the zone as it is.</summary>
    public void EndTransitionResize()
    {
        if (_zoneDrag is not { } drag) return;
        _zoneDrag = null;
        IsSnapIndicatorVisible = false;
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
        IsSnapIndicatorVisible = false;
        drag.Zone.Layout(PixelsPerSecond, FrameRate);
    }
}
