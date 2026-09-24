using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Timeline;

/// <summary>
/// Absolute snapshot of a clip's timing, speed included. Commands store before/after snapshots
/// rather than deltas, so Undo restores every tick exactly and Redo can re-apply
/// from the "before" state. Source fields are zero (and the speed 1×) for non-media-backed clips.
/// </summary>
public readonly record struct ClipState(MediaTime Start, MediaTime Duration, MediaTime SourceIn, MediaTime SourceOut, ClipSpeed Speed = default)
{
    public MediaTime End => Start + Duration;

    public static ClipState Capture(Clip clip) => clip is MediaBackedClip m
        ? new ClipState(clip.TimelineStart, clip.Duration, m.SourceIn, m.SourceOut, m.Speed)
        : new ClipState(clip.TimelineStart, clip.Duration, MediaTime.Zero, MediaTime.Zero);

    public void ApplyTo(Clip clip)
    {
        clip.TimelineStart = Start;
        clip.Duration = Duration;
        if (clip is MediaBackedClip m)
        {
            m.SourceIn = SourceIn;
            m.SourceOut = SourceOut;
            m.Speed = Speed;
        }
    }

    /// <summary>State spanning frames [<paramref name="startFrame"/>, <paramref name="endFrame"/>)
    /// on the grid, with SourceOut = SourceIn + Duration (speed 1×).</summary>
    public static ClipState FromFrames(long startFrame, long endFrame, MediaTime sourceIn, FrameRate rate)
    {
        var start = MediaTime.FromFrame(startFrame, rate);
        var duration = MediaTime.FromFrame(endFrame, rate) - start;
        return new ClipState(start, duration, sourceIn, sourceIn + duration);
    }

    /// <summary>State spanning frames [<paramref name="startFrame"/>, <paramref name="endFrame"/>) with
    /// an explicit source range and speed (clips at other speeds, D022).</summary>
    public static ClipState FromFrames(long startFrame, long endFrame, MediaTime sourceIn, MediaTime sourceOut, ClipSpeed speed, FrameRate rate)
    {
        var start = MediaTime.FromFrame(startFrame, rate);
        return new ClipState(start, MediaTime.FromFrame(endFrame, rate) - start, sourceIn, sourceOut, speed);
    }

    /// <summary>
    /// The smallest change that brings a source range back into the timing invariant
    /// (<see cref="SpeedTiming"/>): <c>SourceLength(N) ≤ SourceOut − SourceIn &lt; SourceLength(N + 1)</c>.
    /// Needed only where two floored lengths are added or subtracted (trim start, split), which can
    /// miss the range by at most one tick: a range that is too short moves SourceIn back (or, at the
    /// start of the source, SourceOut on), one that is too long gives up the excess at SourceOut. A
    /// range that already fits is returned unchanged.
    /// </summary>
    public static (MediaTime SourceIn, MediaTime SourceOut) Normalize(long frames, MediaTime sourceIn, MediaTime sourceOut, ClipSpeed speed, FrameRate rate)
    {
        var min = SpeedTiming.SourceLength(frames, speed, rate);
        if (sourceOut - sourceIn < min)
            return sourceOut - min >= MediaTime.Zero ? (sourceOut - min, sourceOut) : (sourceIn, sourceIn + min);
        var limit = SpeedTiming.SourceLength(frames + 1, speed, rate);
        if (sourceOut - sourceIn >= limit) return (sourceIn, sourceIn + limit - new MediaTime(1));
        return (sourceIn, sourceOut);
    }
}
