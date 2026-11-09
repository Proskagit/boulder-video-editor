using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>A route's start <c>T</c> on its video clip (D056 §4): its frame in pixels from the clip's left edge and its
/// tooltip; editor only — never in Preview or export.</summary>
public sealed record RouteStartGlyph(double Left, string Tip);

/// <summary>
/// D056 §4 (the owner's decision of 2026-11-09) on the timeline: every route area — a «Боулдер» anchored to a video clip,
/// laid out by hand or made from the FIT — shows its computed start <c>T</c> as a blue mark on its video clip; it comes with
/// the area and goes with it. The FIT marks of D051 are no longer drawn on the clips (their data, the calibration and their
/// actions in the Inspector are unchanged).
/// </summary>
public sealed partial class TimelineViewModel
{
    /// <summary>The colour of the start mark <c>T</c> (the blue the FIT's proposed marks had).</summary>
    public const string RouteStartHex = "#29B6F6";

    private void RefreshRouteStartMarks()
    {
        var clips = _projectService.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).ToList();
        foreach (var vm in _clipViewModels.Values)
        {
            if (vm.Clip is not VideoClip clip)
            {
                vm.RouteStartMarks = Array.Empty<RouteStartGlyph>();
                continue;
            }
            var glyphs = new List<RouteStartGlyph>();
            long start = clip.TimelineStart.ToNearestFrame(FrameRate), end = clip.TimelineEnd.ToNearestFrame(FrameRate);
            var seen = new HashSet<long>();
            foreach (var area in RouteOverlays.AnchoredTo(clip, clips).Where(t => t.Kind == TemplateKind.Boulder))
            {
                if (RouteOverlays.RouteStartOf(area, clip, FrameRate) is not { } t) continue;
                var frame = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, t, FrameRate);
                if (frame < start || frame >= end || !seen.Add(frame)) continue;   // a split area: one mark
                var left = TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(frame, FrameRate) - clip.TimelineStart, PixelsPerSecond);
                glyphs.Add(new RouteStartGlyph(left, string.Format(Strings.Timeline_RouteStartMark, Timecode(frame))));
            }
            vm.RouteStartMarks = glyphs;
        }
    }
}
