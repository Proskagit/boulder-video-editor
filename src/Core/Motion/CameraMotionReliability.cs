namespace AiVideoEditor.Core.Motion;

/// <summary>
/// Whether one analysed frame's estimate is reliable enough to draw contours by it. The thresholds are in pixels of a
/// 1920-wide frame. Every limit must hold: enough consistent wall points, a large enough share of the tracked ones, the
/// chosen model's error on them within <see cref="MedianLimit1920"/> / <see cref="P95Limit1920"/>, the inliers spread over
/// at least <see cref="MinCoverage"/> of the frame, and the wall's speed since the previous analysed frame at most
/// <see cref="MaxSpeed1920"/> (a fast motion blurs the picture: the tracked points then sit where the blur puts them).
/// <para><b>The accuracy bound — approved by the product owner on 2026-11-08 (DECISIONS.md D052): a shown contour is at
/// most <see cref="ErrorBound1920"/> = 3 px off its hold, in pixels of a 1920-wide frame</b> (on a 1080p canvas the contour's
/// own displacement; at any other analysis or canvas size the same share of the frame width — every pixel limit here is
/// scaled by <c>pixelScale</c> = analysis width / 1920, and the speed is measured at 1920). The true error of a frame is not
/// observable while editing, so a frame is unreliable when its own measured error — the chosen model's median residual on
/// the wall points — exceeds the bound, or when a sign that the measured error cannot be trusted shows: too few consistent
/// points, a fit on one part of the wall (<see cref="MinCoverage"/>) or a blurred, fast-moving picture
/// (<see cref="MaxSpeed1920"/>). The last two values are calibrated on the reference set (16 clips of 27.09 and the HDR
/// pilot, every frame of 34 windows, independent SIFT check; docs/PHASE_HOLD_TRACKING.md §19.4) as the loosest rule under
/// which no shown frame is more than the bound off (max 3.00 px). A change of any value raises
/// <see cref="CameraMotionSampler.AnalysisVersion"/>.</para>
/// </summary>
public sealed record CameraMotionReliability
{
    /// <summary>The approved accuracy bound: the largest offset of a shown contour from its hold, pixels of a 1920-wide
    /// frame (D052, 2026-11-08).</summary>
    public const double ErrorBound1920 = 3.0;

    public int MinInliers { get; init; } = CameraMotionEstimator.MinInliers;
    public double MinInlierShare { get; init; } = CameraMotionEstimator.MinInlierShare;
    /// <summary>The largest median residual of the chosen model on the wall points — the frame's measured error: the bound.</summary>
    public double MedianLimit1920 { get; init; } = ErrorBound1920;
    public double P95Limit1920 { get; init; } = CameraMotionEstimator.P95Limit1920;

    /// <summary>The least share of the textured grid cells with an inlier (<see cref="CameraMotionEstimator.FrameEstimate.Coverage"/>);
    /// 0 — not checked. 0.75, calibrated to the bound: it removes the false locks of the hand-held clip (a fit on one part
    /// of the wall).</summary>
    public double MinCoverage { get; init; } = 0.75;

    /// <summary>The largest speed of the wall (pixels of a 1920-wide frame per second); infinity — not checked. 2000,
    /// calibrated to the bound (≈ 67 px per frame at 30 fps): above it the picture is too blurred for the tracked points to
    /// be trusted.</summary>
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
