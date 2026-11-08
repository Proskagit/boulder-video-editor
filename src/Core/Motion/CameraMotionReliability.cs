namespace AiVideoEditor.Core.Motion;

/// <summary>
/// Whether one analysed frame's estimate is reliable enough to draw contours by it. The thresholds are in pixels of a
/// 1920-wide frame. Every limit must hold: enough consistent wall points, a large enough share of the tracked ones, the
/// chosen model's error on them within <see cref="MedianLimit1920"/> / <see cref="P95Limit1920"/>, the inliers spread over
/// at least <see cref="MinCoverage"/> of the frame, and the wall's speed since the previous analysed frame at most
/// <see cref="MaxSpeed1920"/> (a fast motion blurs the picture: the tracked points then sit where the blur puts them).
/// <para>The defaults are PROVISIONAL (R2 → E, E-2 / E-4, 2026-11-08): chosen on the real-video measurement of the
/// completion (docs/PHASE_HOLD_TRACKING.md §19.4) so that no shown frame of the reference set is more than 3 px (1920-px
/// frame) off its holds — the error bound recommended to the product owner and NOT YET APPROVED (DECISIONS.md D052). They are
/// final only once the owner approves the bound; a change of any value raises <see cref="CameraMotionSampler.AnalysisVersion"/>.</para>
/// </summary>
public sealed record CameraMotionReliability
{
    public int MinInliers { get; init; } = CameraMotionEstimator.MinInliers;
    public double MinInlierShare { get; init; } = CameraMotionEstimator.MinInlierShare;
    public double MedianLimit1920 { get; init; } = CameraMotionEstimator.MedianLimit1920;
    public double P95Limit1920 { get; init; } = CameraMotionEstimator.P95Limit1920;

    /// <summary>The least share of the textured grid cells with an inlier (<see cref="CameraMotionEstimator.FrameEstimate.Coverage"/>);
    /// 0 — not checked. Provisional 0.75: it removes the false locks of the hand-held clip (a fit on one part of the wall).</summary>
    public double MinCoverage { get; init; } = 0.75;

    /// <summary>The largest speed of the wall (pixels of a 1920-wide frame per second); infinity — not checked. Provisional
    /// 2000 (≈ 67 px per frame at 30 fps): above it the picture is too blurred for the tracked points to be trusted.</summary>
    public double MaxSpeed1920 { get; init; } = 2000;

    public static readonly CameraMotionReliability Default = new();

    /// <summary>The rule before R2 → E's E-2 (no coverage or speed limit) — for comparisons and reports only.</summary>
    public static readonly CameraMotionReliability BeforeE2 = new() { MinCoverage = 0, MaxSpeed1920 = double.PositiveInfinity };

    public bool IsReliable(CameraMotionEstimator.FrameEstimate f, CameraMotionModel model, double pixelScale)
    {
        if (!f.Fits) return false;
        var (median, p95) = model == CameraMotionModel.Similarity ? (f.SimilarityMedian, f.SimilarityP95) : (f.HomographyMedian, f.HomographyP95);
        return f.Inliers >= MinInliers && f.Inliers >= MinInlierShare * f.Tracked &&
               median <= MedianLimit1920 * pixelScale && p95 <= P95Limit1920 * pixelScale &&
               f.Coverage >= MinCoverage && f.Speed1920 <= MaxSpeed1920;
    }
}
