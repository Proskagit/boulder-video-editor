using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// The timeline's In / Out range (Phase 15 Step 15.7, D030 §8): transient session state, like the playhead — never in
/// <c>project.json</c>, the recovery file or undo / redo. The range is <c>[In, Out)</c> in timeline time, both on the
/// frame grid:
/// <list type="bullet">
/// <item><c>I</c> (<see cref="WithIn"/>): In = the start of the frame containing the playhead.</item>
/// <item><c>O</c> (<see cref="WithOut"/>): Out = the end of the frame containing the playhead — that frame is inside the
/// range (Q7), so I and O on one frame give a one-frame range.</item>
/// <item>A point that would leave <c>In ≥ Out</c> keeps the new point and clears the other one (Q8) — no refusal.</item>
/// <item>Only In: the range runs to the end of the sequence; only Out: from 0 (<see cref="Frames"/>).</item>
/// </list>
/// </summary>
public readonly record struct InOutRange(MediaTime? In, MediaTime? Out)
{
    public static readonly InOutRange None = default;

    public bool IsSet => In is not null || Out is not null;

    /// <summary>In at the frame containing <paramref name="playhead"/>; an Out at or before it is cleared (Q8).</summary>
    public InOutRange WithIn(MediaTime playhead, FrameRate rate)
    {
        var frame = Math.Max(0, playhead.ToFrameFloor(rate));
        var @in = MediaTime.FromFrame(frame, rate);
        return new InOutRange(@in, Out is { } o && o <= @in ? null : Out);
    }

    /// <summary>Out after the frame containing <paramref name="playhead"/> (Q7); an In at or after it is cleared (Q8).</summary>
    public InOutRange WithOut(MediaTime playhead, FrameRate rate)
    {
        var frame = Math.Max(0, playhead.ToFrameFloor(rate));
        var @out = MediaTime.FromFrame(frame + 1, rate);
        return new InOutRange(In is { } i && i >= @out ? null : In, @out);
    }

    /// <summary>The points on the grid of <paramref name="rate"/> after a frame-rate change: each keeps its time, at the
    /// nearest frame (as markers, D027 Step 12.7); an Out that no longer lies after In is cleared.</summary>
    public InOutRange Regrid(FrameRate rate)
    {
        MediaTime? Snap(MediaTime? t) => t is { } v ? MediaTime.FromFrame(v.ToNearestFrame(rate), rate) : null;
        var (i, o) = (Snap(In), Snap(Out));
        return new InOutRange(i, i is { } a && o is { } b && b <= a ? null : o);
    }

    /// <summary>The range in whole timeline frames <c>[First, End)</c> for a sequence of <paramref name="sequenceDuration"/>,
    /// clamped to it; null when no point is set or nothing of the sequence is left inside.</summary>
    public (long First, long End)? Frames(FrameRate rate, MediaTime sequenceDuration)
    {
        if (!IsSet) return null;
        var total = Math.Max(0, sequenceDuration.ToFrameCeiling(rate));
        var first = In?.ToNearestFrame(rate) ?? 0;
        var end = Math.Min(Out?.ToNearestFrame(rate) ?? total, total);
        return first < end ? (first, end) : null;
    }
}

/// <summary>The part of the sequence playback is confined to while Loop is on with an In / Out range (D030 §8, Q9):
/// <c>[Start, End)</c>.</summary>
public readonly record struct PlaybackRange(MediaTime Start, MediaTime End);
