using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Timeline;

/// <summary>
/// Source handles of a dissolve (D025 §4): how many frames of the project rate a clip's source reaches past its end
/// (<see cref="After"/>, needed by A) and before its start (<see cref="Before"/>, needed by B), at the clip's speed —
/// exactly how far a trim of that edge outward could go. Images and text have <see cref="Unlimited"/> handles; a video
/// clip whose source duration is unknown has none.
/// </summary>
public static class DissolveHandles
{
    /// <summary>Large enough for any timeline, small enough that doubling it can't overflow.</summary>
    public const long Unlimited = long.MaxValue / 4;

    /// <summary>Frames of source after the clip's end.</summary>
    public static long After(Clip clip, ClipState state, MediaAsset? asset, FrameRate rate)
    {
        if (clip is not VideoClip) return Unlimited;
        if (asset?.Metadata is not { } metadata || metadata.Duration <= MediaTime.Zero) return 0;

        var frames = Frames(state, rate);
        var available = metadata.Duration - state.SourceIn;
        var total = state.Speed.IsNormal
            ? FrameMath.MaxWholeFrames(available, rate)
            : SpeedTiming.FramesFor(available, state.Speed, rate);
        return Math.Max(0, total - frames);
    }

    /// <summary>Frames of source before the clip's start (the source time stays ≥ 0).</summary>
    public static long Before(Clip clip, ClipState state, MediaAsset? asset, FrameRate rate)
    {
        if (clip is not VideoClip) return Unlimited;
        if (asset?.Metadata is not { } metadata || metadata.Duration <= MediaTime.Zero) return 0;

        var handle = state.Speed.IsNormal
            ? state.Start.ToFrameFloor(rate) - FrameMath.CeilingFrame(state.Start - state.SourceIn, rate)
            : SpeedTiming.FramesFor(state.SourceIn, state.Speed, rate);
        return Math.Max(0, handle);
    }

    public static long Frames(ClipState state, FrameRate rate) => state.End.ToFrameFloor(rate) - state.Start.ToFrameFloor(rate);
}
