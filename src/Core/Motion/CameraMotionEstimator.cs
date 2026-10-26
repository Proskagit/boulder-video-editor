using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;

namespace AiVideoEditor.Core.Motion;

/// <summary>
/// Estimates the camera's motion relative to one reference frame (where the holds were marked), frame by frame, then
/// builds a <see cref="CameraMotionTrack"/>. Per frame: the reference's wall corners are tracked into the frame (pyramidal
/// Lucas–Kanade from the previous estimate, or from a coarse shift when there is none), and a similarity and a homography
/// are fitted with RANSAC — the climber, a passer-by or a lost point are outliers. The model of the whole clip is the
/// similarity when its error on the wall points stays within <see cref="MedianLimit1920"/> / <see cref="P95Limit1920"/> on
/// every analysed frame, otherwise the homography (the owner's decision: similarity first, the homography only when the
/// similarity is not accurate enough — never assumed for all clips). A frame is reliable only with enough consistent
/// points and the chosen model within those limits; an unreliable frame never shows a contour.
/// </summary>
public sealed class CameraMotionEstimator
{
    /// <summary>The working thresholds on the model's error at the wall points, in pixels of a 1920-wide frame (the
    /// research's hypothesis, about one default stroke on a 1080p canvas — docs/PHASE_HOLD_TRACKING.md 6.4; not yet a
    /// threshold agreed with the product owner).</summary>
    public const double MedianLimit1920 = 3.0;
    public const double P95Limit1920 = 6.0;

    /// <summary>The inlier distance of RANSAC, pixels of a 1920-wide frame.</summary>
    public const double InlierThreshold1920 = 3.0;

    public const int MinInliers = 30;
    public const double MinInlierShare = 0.3;

    private readonly GrayImage[] _reference;
    private readonly PointD[] _corners;
    private readonly MediaTime _referenceTime;
    private readonly List<FrameEstimate> _frames = new();
    private WallTransform? _previous;

    public CameraMotionEstimator(GrayImage reference, MediaTime referenceTime)
    {
        _reference = reference.Pyramid(FeatureTracker.Levels);
        _corners = FeatureTracker.Corners(reference);
        _referenceTime = referenceTime;
        Width = reference.Width;
        Height = reference.Height;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The number of wall corners found on the reference frame.</summary>
    public int CornerCount => _corners.Length;

    private double PixelScale => Width / 1920.0;

    /// <summary>Adds the frame at <paramref name="sourceTime"/> (the same size as the reference). Frames may come in any
    /// order; the previous frame's estimate is the next one's start, so feeding them in time order tracks best.</summary>
    public void Add(GrayImage frame, MediaTime sourceTime)
    {
        if (frame.Width != Width || frame.Height != Height)
            throw new ArgumentException("The frame must have the reference frame's size.", nameof(frame));
        if (sourceTime == _referenceTime) return;   // the reference itself is the identity
        var pyramid = frame.Pyramid(FeatureTracker.Levels);
        var estimate = _previous is { } previous ? Estimate(pyramid, sourceTime, previous) : null;
        if (estimate is not { Fits: true })
        {
            // Lost (or the first frame): a coarse shift; when that fails too, a coarse zoom and shift (a lens switch).
            var shift = FeatureTracker.CoarseShift(_reference, pyramid);
            estimate = Estimate(pyramid, sourceTime, new WallTransform(1, 0, shift.X, 0, 1, shift.Y, 0, 0, 1));
            if (!estimate.Fits)
                estimate = Estimate(pyramid, sourceTime, FeatureTracker.CoarseZoomShift(_reference, pyramid));
        }
        _frames.Add(estimate);
        if (estimate.Fits) _previous = estimate.Homography;
    }

    /// <summary>The track of every frame added so far (plus the reference itself, the identity), with the model chosen
    /// for the whole clip.</summary>
    public CameraMotionTrack Build()
    {
        double median = MedianLimit1920 * PixelScale, p95 = P95Limit1920 * PixelScale;
        var fitted = _frames.Where(f => f.Fits).ToList();
        var model = fitted.All(f => f.SimilarityMedian <= median && f.SimilarityP95 <= p95)
            ? CameraMotionModel.Similarity
            : CameraMotionModel.Homography;

        var samples = new List<CameraMotionSample> { new(_referenceTime, WallTransform.Identity, true) };
        foreach (var f in _frames)
        {
            if (!f.Fits)
            {
                samples.Add(new CameraMotionSample(f.SourceTime, WallTransform.Identity, false));
                continue;
            }
            var similarity = model == CameraMotionModel.Similarity;
            var (m, p) = similarity ? (f.SimilarityMedian, f.SimilarityP95) : (f.HomographyMedian, f.HomographyP95);
            var reliable = f.Inliers >= MinInliers && f.Inliers >= MinInlierShare * f.Tracked && m <= median && p <= p95;
            var pixels = similarity ? f.Similarity : f.Homography;
            samples.Add(new CameraMotionSample(f.SourceTime, WallTransform.FromPixels(pixels, Width, Height), reliable));
        }
        return new CameraMotionTrack(_referenceTime, model, samples.OrderBy(s => s.SourceTime).ToImmutableArray());
    }

    /// <summary>The per-frame diagnostics (for tests and reports).</summary>
    public IReadOnlyList<FrameEstimate> Frames => _frames;

    private FrameEstimate Estimate(GrayImage[] pyramid, MediaTime time, WallTransform guess)
    {
        var tracked = FeatureTracker.Track(_reference, pyramid, _corners, guess);
        var from = new List<PointD>();
        var to = new List<PointD>();
        for (var i = 0; i < tracked.Length; i++)
        {
            if (tracked[i] is not { } point) continue;
            from.Add(_corners[i]);
            to.Add(point);
        }
        var threshold = InlierThreshold1920 * PixelScale;
        var seed = unchecked((int)(time.Ticks ^ (time.Ticks >> 32)));
        var homography = CameraMotionMath.Ransac(from, to, CameraMotionModel.Homography, threshold, 400, seed, MinInliers);
        if (homography is null) return FrameEstimate.Failed(time, from.Count);
        var similarity = CameraMotionMath.Ransac(from, to, CameraMotionModel.Similarity, threshold, 200, seed + 1, 2);

        // Both models are judged on the same wall points: the homography's inliers.
        var homographyResiduals = CameraMotionMath.Residuals(homography.Transform, from, to, homography.Inliers);
        var similarityResiduals = similarity is null
            ? Array.Empty<double>()
            : CameraMotionMath.Residuals(similarity.Transform, from, to, homography.Inliers);
        return new FrameEstimate(time, true, from.Count, homography.InlierCount, homography.Transform,
            similarity?.Transform ?? homography.Transform,
            CameraMotionMath.Quantile(similarityResiduals, 0.5), CameraMotionMath.Quantile(similarityResiduals, 0.95),
            CameraMotionMath.Quantile(homographyResiduals, 0.5), CameraMotionMath.Quantile(homographyResiduals, 0.95));
    }

    /// <summary>One analysed frame: the fits (pixels of the analysed frame) and their errors on the wall points.</summary>
    public sealed record FrameEstimate(MediaTime SourceTime, bool Fits, int Tracked, int Inliers, WallTransform Homography,
        WallTransform Similarity, double SimilarityMedian, double SimilarityP95, double HomographyMedian, double HomographyP95)
    {
        public static FrameEstimate Failed(MediaTime time, int tracked) =>
            new(time, false, tracked, 0, WallTransform.Identity, WallTransform.Identity,
                double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
    }
}
