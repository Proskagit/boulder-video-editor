using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Timeline;

/// <summary>
/// Absolute snapshot of a clip's timing. Commands store before/after snapshots
/// rather than deltas, so Undo restores every tick exactly and Redo can re-apply
/// from the "before" state. Source fields are zero for non-media-backed clips.
/// </summary>
public readonly record struct ClipState(MediaTime Start, MediaTime Duration, MediaTime SourceIn, MediaTime SourceOut)
{
    public MediaTime End => Start + Duration;

    public static ClipState Capture(Clip clip) => clip is MediaBackedClip m
        ? new ClipState(clip.TimelineStart, clip.Duration, m.SourceIn, m.SourceOut)
        : new ClipState(clip.TimelineStart, clip.Duration, MediaTime.Zero, MediaTime.Zero);

    public void ApplyTo(Clip clip)
    {
        clip.TimelineStart = Start;
        clip.Duration = Duration;
        if (clip is MediaBackedClip m)
        {
            m.SourceIn = SourceIn;
            m.SourceOut = SourceOut;
        }
    }

    /// <summary>State spanning frames [<paramref name="startFrame"/>, <paramref name="endFrame"/>)
    /// on the grid, with SourceOut = SourceIn + Duration (speed 1.0).</summary>
    public static ClipState FromFrames(long startFrame, long endFrame, MediaTime sourceIn, FrameRate rate)
    {
        var start = MediaTime.FromFrame(startFrame, rate);
        var duration = MediaTime.FromFrame(endFrame, rate) - start;
        return new ClipState(start, duration, sourceIn, sourceIn + duration);
    }
}
