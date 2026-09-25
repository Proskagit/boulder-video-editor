using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Timeline;

/// <summary>Frame-count helpers on top of the exact <see cref="MediaTime"/> frame grid.
/// Integer arithmetic only.</summary>
public static class FrameMath
{
    /// <summary>
    /// Largest frame count <c>k</c> whose exact rational duration fits in
    /// <paramref name="available"/>: <c>k · 10⁷ · den / num ≤ available</c>.
    /// </summary>
    /// <remarks>
    /// Guarantees that <c>k</c> frames placed at <b>any</b> grid position span at most
    /// <paramref name="available"/> ticks: the rounded span <c>G(s+k) − G(s)</c> is
    /// strictly less than <c>k·P + 1</c> (each boundary is within ½ tick of exact),
    /// and both sides are integers, so span ≤ available. This is what lets clips
    /// move freely without their SourceOut ever passing the end of the source.
    /// </remarks>
    public static long MaxWholeFrames(MediaTime available, FrameRate rate)
    {
        if (available.Ticks <= 0) return 0;
        return (long)((Int128)available.Ticks * rate.Numerator / ((Int128)TimeSpan.TicksPerSecond * rate.Denominator));
    }

    /// <summary>Smallest frame index whose start is ≥ <paramref name="time"/> (<see cref="MediaTime.ToFrameCeiling"/>).</summary>
    public static long CeilingFrame(MediaTime time, FrameRate rate) => time.ToFrameCeiling(rate);
}
