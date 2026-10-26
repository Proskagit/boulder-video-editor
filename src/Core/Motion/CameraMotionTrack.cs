using System.Collections.Immutable;
using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Motion;

/// <summary>Which model of the camera's motion a track uses: the cheaper similarity when it is accurate enough on the
/// clip, otherwise the homography of the wall plane.</summary>
public enum CameraMotionModel
{
    /// <summary>Shift, uniform scale and rotation.</summary>
    Similarity,

    /// <summary>A perspective transform of the wall plane.</summary>
    Homography
}

/// <summary>One analysed frame: where the reference frame's wall is on the frame at <see cref="SourceTime"/>, and
/// whether that estimate is reliable. An unreliable frame never shows a contour (the outline is hidden there rather
/// than drawn off the holds).</summary>
public readonly record struct CameraMotionSample(MediaTime SourceTime, WallTransform ReferenceToFrame, bool IsReliable);

/// <summary>What a track says about one source time.</summary>
public enum CameraMotionLookupKind
{
    /// <summary>Outside the analysed range: the track says nothing (the outline is drawn as without tracking).</summary>
    NoData,

    /// <summary>Inside the range, but no reliable estimate: the outline must not be shown.</summary>
    Hidden,

    /// <summary>A reliable transform from the reference frame.</summary>
    Aligned
}

public readonly record struct CameraMotionLookup(CameraMotionLookupKind Kind, WallTransform Transform)
{
    public static readonly CameraMotionLookup NoData = new(CameraMotionLookupKind.NoData, WallTransform.Identity);
    public static readonly CameraMotionLookup Hidden = new(CameraMotionLookupKind.Hidden, WallTransform.Identity);
}

/// <summary>
/// The camera's motion over a range of one video's source time, relative to a reference frame
/// (<see cref="ReferenceTime"/>): analysed frames (<see cref="Samples"/>, ascending source times) with the transform of
/// the wall from the reference frame to each of them. Between two reliable samples no further apart than
/// <see cref="MaxInterpolationGap"/> the transform is blended; a time next to an unreliable sample, in a longer gap, or
/// within <see cref="EdgeTolerance"/> of the range's ends without a reliable sample there is <see cref="CameraMotionLookupKind.Hidden"/>.
/// Keyed by source time (100 ns ticks), so trimming, slipping, a speed change or a split never move it against the
/// picture. Derived from the video alone — immutable, deterministic, presentation only.
/// </summary>
public sealed class CameraMotionTrack
{
    public static readonly MediaTime DefaultMaxInterpolationGap = MediaTime.FromSeconds(0.25);
    public static readonly MediaTime DefaultEdgeTolerance = MediaTime.FromSeconds(0.1);

    public CameraMotionTrack(MediaTime referenceTime, CameraMotionModel model, ImmutableArray<CameraMotionSample> samples,
        MediaTime? maxInterpolationGap = null, MediaTime? edgeTolerance = null)
    {
        if (samples.IsDefault) samples = ImmutableArray<CameraMotionSample>.Empty;
        for (var i = 1; i < samples.Length; i++)
        {
            if (samples[i].SourceTime <= samples[i - 1].SourceTime)
                throw new ArgumentException("The samples must have strictly ascending source times.", nameof(samples));
        }
        ReferenceTime = referenceTime;
        Model = model;
        Samples = samples;
        MaxInterpolationGap = maxInterpolationGap ?? DefaultMaxInterpolationGap;
        EdgeTolerance = edgeTolerance ?? DefaultEdgeTolerance;
    }

    public MediaTime ReferenceTime { get; }
    public CameraMotionModel Model { get; }
    public ImmutableArray<CameraMotionSample> Samples { get; }
    public MediaTime MaxInterpolationGap { get; }
    public MediaTime EdgeTolerance { get; }

    /// <summary>The share of unreliable samples (0 without samples).</summary>
    public double UnreliableFraction => Samples.IsEmpty ? 0 : Samples.Count(s => !s.IsReliable) / (double)Samples.Length;

    /// <summary>The transform from the reference frame to the frame at <paramref name="sourceTime"/>.</summary>
    public CameraMotionLookup At(MediaTime sourceTime)
    {
        if (Samples.IsEmpty) return CameraMotionLookup.NoData;
        var first = Samples[0];
        var last = Samples[^1];
        if (sourceTime < first.SourceTime)
            return sourceTime >= first.SourceTime - EdgeTolerance ? Of(first) : CameraMotionLookup.NoData;
        if (sourceTime > last.SourceTime)
            return sourceTime <= last.SourceTime + EdgeTolerance ? Of(last) : CameraMotionLookup.NoData;

        // The last sample at or before the time.
        int lo = 0, hi = Samples.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Samples[mid].SourceTime <= sourceTime) lo = mid;
            else hi = mid - 1;
        }
        var a = Samples[lo];
        if (a.SourceTime == sourceTime || lo == Samples.Length - 1) return Of(a);
        var b = Samples[lo + 1];
        if (!a.IsReliable || !b.IsReliable || b.SourceTime - a.SourceTime > MaxInterpolationGap) return CameraMotionLookup.Hidden;
        var t = (sourceTime - a.SourceTime).Ticks / (double)(b.SourceTime - a.SourceTime).Ticks;
        return new CameraMotionLookup(CameraMotionLookupKind.Aligned, WallTransform.Lerp(a.ReferenceToFrame, b.ReferenceToFrame, t));
    }

    /// <summary>
    /// Where a contour marked on the frame at <paramref name="markedAt"/> is on the frame at <paramref name="frameTime"/>:
    /// <c>T(frame) · T(marked)⁻¹</c>. <see cref="CameraMotionLookupKind.NoData"/> when the frame is outside the range (the
    /// contour is drawn where it was marked, as without tracking); <see cref="CameraMotionLookupKind.Hidden"/> when either
    /// estimate is missing or unreliable — the contour's place on that frame is unknown, so it is not shown.
    /// </summary>
    public CameraMotionLookup Map(MediaTime markedAt, MediaTime frameTime)
    {
        var frame = At(frameTime);
        if (frame.Kind != CameraMotionLookupKind.Aligned) return frame;
        var marked = At(markedAt);
        if (marked.Kind != CameraMotionLookupKind.Aligned) return CameraMotionLookup.Hidden;
        var transform = marked.Transform.Inverse().Then(frame.Transform);
        return transform.IsFinite ? new CameraMotionLookup(CameraMotionLookupKind.Aligned, transform) : CameraMotionLookup.Hidden;
    }

    private static CameraMotionLookup Of(CameraMotionSample sample) =>
        sample.IsReliable ? new CameraMotionLookup(CameraMotionLookupKind.Aligned, sample.ReferenceToFrame) : CameraMotionLookup.Hidden;
}
