using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Motion;

/// <summary>
/// The source range of a video clip whose camera motion the hold tracking needs: where the clip's contours are shown —
/// with the outline animation (D036) from its start to the last contour gone (or the clip's end with "until clip end"),
/// otherwise the clip's whole source range — widened to every hold's marking time, so each contour can be carried from
/// the frame it was marked on. The reference frame is the first hold's marking frame. Null without holds or when the
/// range is empty (an animation entirely outside the clip).
/// </summary>
public readonly record struct CameraMotionRange(MediaTime ReferenceTime, MediaTime Start, MediaTime End)
{
    public static CameraMotionRange? For(VideoClip clip, FrameRate rate)
    {
        if (clip.HoldMarkup is not { Holds.Length: > 0 } markup) return null;
        MediaTime start = clip.SourceIn, end = clip.SourceOut;
        if (markup.Animation is { } animation)
        {
            var startFrame = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, animation.StartSourceTime, rate);
            var schedule = HoldAnimationRule.Schedule(animation, markup.Holds, startFrame, rate);
            var shownFrom = HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, schedule.StartFrame, rate);
            start = Max(start, shownFrom);
            if (schedule.EndFrame is { } endFrame)
                end = Min(end, HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, endFrame, rate));
            if (end <= start) return null;
        }
        foreach (var hold in markup.Holds)
        {
            start = Min(start, hold.SourceTime);
            end = Max(end, hold.SourceTime);
        }
        return new CameraMotionRange(markup.Holds[0].SourceTime, start, end);
    }

    private static MediaTime Min(MediaTime a, MediaTime b) => a < b ? a : b;
    private static MediaTime Max(MediaTime a, MediaTime b) => a > b ? a : b;
}
