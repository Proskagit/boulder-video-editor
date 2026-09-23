using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// The instant in source time (ticks relative to the file's start time) at which a
/// timeline frame samples its clip's source, as the exact rational
/// <c>TicksNumerator / TicksDenominator</c>. Produced by
/// <see cref="SourceFrameSelector.SamplePoint(MediaTime, MediaTime, long, FrameRate, FrameRate?)"/>.
/// </summary>
public readonly record struct SourceSamplePoint(Int128 TicksNumerator, long TicksDenominator)
{
    /// <summary>For logs and diagnostics only.</summary>
    public override string ToString() => $"{(double)TicksNumerator / TicksDenominator / TimeSpan.TicksPerSecond:0.######}s";
}

/// <summary>
/// Decides which decoded source frame a timeline frame shows (see DECISIONS D009).
/// <para>
/// For timeline frame <c>n</c> of a clip starting at <c>S</c>:
/// <c>t(n) = SourceIn + (FromFrame(n) − S)</c> and the sample point is
/// <c>t(n) + δ</c> with <c>δ = ½ · min(P, Ssrc)</c>, where <c>P</c> is the length of
/// timeline frame <c>n</c> and <c>Ssrc</c> the nominal source frame length. The frame
/// shown is the last one whose source time <c>Pts · TimeBase − StartTime</c> is
/// <c>≤</c> the sample point; before the first frame the first one is held, after the
/// last frame the last one is held.
/// </para>
/// <para>
/// Pure integer (<see cref="Int128"/>) arithmetic, independent of any decoder backend.
/// </para>
/// </summary>
public static class SourceFrameSelector
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>
    /// Nominal source frame rate used for <c>Ssrc</c>: <c>avg_frame_rate</c>, falling back to
    /// <c>r_frame_rate</c>. Null when neither is known (δ then uses the timeline frame only).
    /// Real frame timing always comes from the decoded PTS, never from this rate.
    /// </summary>
    public static FrameRate? NominalRate(MediaMetadata? metadata) =>
        metadata?.AvgFrameRate ?? metadata?.FrameRate;

    /// <summary>Sample point for timeline frame <paramref name="timelineFrame"/> of a clip at
    /// speed 1.0 starting at <paramref name="clipStart"/> with <paramref name="sourceIn"/>.</summary>
    public static SourceSamplePoint SamplePoint(
        MediaTime clipStart, MediaTime sourceIn, long timelineFrame, FrameRate projectRate, FrameRate? nominalSourceRate)
    {
        var frameStart = MediaTime.FromFrame(timelineFrame, projectRate).Ticks;
        var frameLength = MediaTime.FromFrame(timelineFrame + 1, projectRate).Ticks - frameStart;
        var t = (Int128)sourceIn.Ticks + frameStart - clipStart.Ticks;

        checked
        {
            if (nominalSourceRate is not { IsValid: true } rate)
                return new SourceSamplePoint(2 * t + frameLength, 2);

            // δ = ½·min(P, 10⁷·den/num); scale everything by 2·num to stay integral.
            var projectFrame = (Int128)frameLength * rate.Numerator;
            var sourceFrame = (Int128)TicksPerSecond * rate.Denominator;
            var denominator = 2L * rate.Numerator;
            return new SourceSamplePoint(t * denominator + Int128.Min(projectFrame, sourceFrame), denominator);
        }
    }

    /// <summary>Sample point for a timeline frame of <paramref name="clip"/>, which must lie
    /// inside the clip. Only speed 1.0 is supported (D008).</summary>
    public static SourceSamplePoint SamplePoint(MediaBackedClip clip, long timelineFrame, FrameRate projectRate, MediaMetadata? metadata)
    {
        if (clip.Speed != 1.0)
            throw new NotSupportedException("Only clips at speed 1.0 can be played.");

        var frameStart = MediaTime.FromFrame(timelineFrame, projectRate);
        if (frameStart < clip.TimelineStart || frameStart >= clip.TimelineEnd)
            throw new ArgumentOutOfRangeException(nameof(timelineFrame), "Timeline frame is outside the clip.");

        return SamplePoint(clip.TimelineStart, clip.SourceIn, timelineFrame, projectRate, NominalRate(metadata));
    }

    /// <summary>True when the frame at <paramref name="timestamp"/> starts at or before
    /// <paramref name="point"/>, with source time measured from <paramref name="startTime"/>.</summary>
    public static bool IsAtOrBefore(SourceTimestamp timestamp, MediaTime startTime, SourceSamplePoint point)
    {
        var tb = timestamp.TimeBase;
        if (!tb.IsValid) throw new ArgumentException("Timestamp has no valid time base.", nameof(timestamp));

        checked
        {
            // Pts·tbN/tbD·10⁷ − start ≤ num/den  ⇔  Pts·tbN·10⁷·den ≤ (num + start·den)·tbD
            var left = (Int128)timestamp.Pts * tb.Numerator * TicksPerSecond * point.TicksDenominator;
            var right = (point.TicksNumerator + (Int128)startTime.Ticks * point.TicksDenominator) * tb.Denominator;
            return left <= right;
        }
    }

    /// <summary>
    /// Index of the frame to show from <paramref name="frames"/>, which must be in
    /// ascending presentation order: the last frame at or before the sample point,
    /// the first frame if all start later (hold-first), or −1 if the list is empty.
    /// Holding the last frame after the end follows from the rule itself.
    /// </summary>
    public static int Select(IReadOnlyList<SourceTimestamp> frames, MediaTime startTime, SourceSamplePoint point)
    {
        if (frames.Count == 0) return -1;

        // Binary search for the first frame after the point.
        int lo = 0, hi = frames.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (IsAtOrBefore(frames[mid], startTime, point)) lo = mid + 1;
            else hi = mid;
        }
        return Math.Max(lo - 1, 0);
    }
}
