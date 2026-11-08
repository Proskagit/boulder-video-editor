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
/// similarity is not accurate enough — never assumed for all clips). A frame is reliable only when
/// <see cref="CameraMotionReliability"/> accepts its estimate; an unreliable frame never shows a contour.
/// <para>A frame is estimated (<see cref="Estimate"/>) and then committed (<see cref="Commit"/>), so a caller can look at an
/// estimate before it becomes part of the track (<see cref="CameraMotionSampler"/> analyses the frames in between when the
/// camera moved too far); <see cref="Add"/> does both.</para>
/// </summary>
public sealed class CameraMotionEstimator
{
    /// <summary>The working thresholds on the model's error at the wall points, in pixels of a 1920-wide frame (the
    /// research's hypothesis, about one default stroke on a 1080p canvas — docs/PHASE_HOLD_TRACKING.md 6.4; not yet a
    /// threshold agreed with the product owner). They choose the model of the clip.</summary>
    public const double MedianLimit1920 = 3.0;
    public const double P95Limit1920 = 6.0;

    /// <summary>The inlier distance of RANSAC, pixels of a 1920-wide frame.</summary>
    public const double InlierThreshold1920 = 3.0;

    public const int MinInliers = 30;
    public const double MinInlierShare = 0.3;

    /// <summary>The grid the coverage of the inliers is measured on (<see cref="FrameEstimate.Coverage"/>).</summary>
    public const int CoverageGrid = 4;

    private readonly GrayImage[] _reference;
    private readonly PointD[] _corners;
    private readonly MediaTime _referenceTime;
    private readonly List<FrameEstimate> _frames = new();
    private readonly CameraMotionReliability _reliability;
    private readonly int _cornerCells;
    private WallTransform? _previous;

    public CameraMotionEstimator(GrayImage reference, MediaTime referenceTime, CameraMotionReliability? reliability = null)
    {
        _reference = reference.Pyramid(FeatureTracker.Levels);
        _corners = FeatureTracker.Corners(reference);
        _referenceTime = referenceTime;
        _reliability = reliability ?? CameraMotionReliability.Default;
        Width = reference.Width;
        Height = reference.Height;
        _cornerCells = Cells(_corners, _ => true).Count(c => c);
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The number of wall corners found on the reference frame.</summary>
    public int CornerCount => _corners.Length;

    public MediaTime ReferenceTime => _referenceTime;

    /// <summary>Pixels of the analysed frame per pixel of a 1920-wide frame.</summary>
    public double PixelScale => Width / 1920.0;

    /// <summary>The homography (pixels of the analysed frame) of the last committed frame that fitted — the start of the
    /// next estimate; null before any.</summary>
    public WallTransform? Previous => _previous;

    /// <summary>Adds the frame at <paramref name="sourceTime"/> (the same size as the reference): <see cref="Estimate"/>
    /// then <see cref="Commit"/>. Frames may come in any order; the previous frame's estimate is the next one's start, so
    /// feeding them in time order tracks best.</summary>
    public void Add(GrayImage frame, MediaTime sourceTime)
    {
        if (sourceTime == _referenceTime)
        {
            _previous = WallTransform.Identity;   // the reference itself is the identity: the best start for the next frame
            return;
        }
        Commit(Estimate(frame, sourceTime));
    }

    /// <summary>The estimate of the frame at <paramref name="sourceTime"/>, started from <see cref="Previous"/> (or from a
    /// coarse search when it is lost or there is none); not yet part of the track.</summary>
    public FrameEstimate Estimate(GrayImage frame, MediaTime sourceTime)
    {
        if (frame.Width != Width || frame.Height != Height)
            throw new ArgumentException("The frame must have the reference frame's size.", nameof(frame));
        var pyramid = frame.Pyramid(FeatureTracker.Levels);
        var estimate = _previous is { } previous ? Estimate(pyramid, sourceTime, previous, restarted: false) : null;
        if (estimate is not { Fits: true })
        {
            // Lost (or the first frame): a coarse shift; when that fails too, a coarse zoom and shift (a lens switch).
            var shift = FeatureTracker.CoarseShift(_reference, pyramid);
            estimate = Estimate(pyramid, sourceTime, new WallTransform(1, 0, shift.X, 0, 1, shift.Y, 0, 0, 1), restarted: true);
            if (!estimate.Fits)
                estimate = Estimate(pyramid, sourceTime, FeatureTracker.CoarseZoomShift(_reference, pyramid), restarted: true);
        }
        return estimate;
    }

    /// <summary>Makes <paramref name="estimate"/> part of the track; a fitting estimate becomes the next one's start.</summary>
    public void Commit(FrameEstimate estimate)
    {
        if (estimate.SourceTime == _referenceTime) return;
        _frames.Add(estimate);
        if (estimate.Fits) _previous = estimate.Homography;
    }

    /// <summary>How far (pixels of a 1920-wide frame) the wall moved between two homographies of analysed-frame pixels: the
    /// largest displacement of the frame's centre and of four points 10 % in from its corners.</summary>
    public double Displacement1920(WallTransform a, WallTransform b)
    {
        var worst = 0.0;
        foreach (var (u, v) in ControlPoints)
        {
            var (ax, ay) = a.Apply(u * Width, v * Height);
            var (bx, by) = b.Apply(u * Width, v * Height);
            worst = Math.Max(worst, Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)));
        }
        return worst / PixelScale;
    }

    private static readonly (double U, double V)[] ControlPoints = [(0.5, 0.5), (0.1, 0.1), (0.9, 0.1), (0.1, 0.9), (0.9, 0.9)];

    /// <summary>The track of every frame committed so far (plus the reference itself, the identity), with the model chosen
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
            var pixels = model == CameraMotionModel.Similarity ? f.Similarity : f.Homography;
            samples.Add(new CameraMotionSample(f.SourceTime, WallTransform.FromPixels(pixels, Width, Height),
                _reliability.IsReliable(f, model, PixelScale)));
        }
        // The window may begin before the reference frame (a hold marked later than the contours appear).
        return new CameraMotionTrack(_referenceTime, model, samples.OrderBy(s => s.SourceTime).ToImmutableArray());
    }

    /// <summary>The per-frame diagnostics (for tests and reports).</summary>
    public IReadOnlyList<FrameEstimate> Frames => _frames;

    private FrameEstimate Estimate(GrayImage[] pyramid, MediaTime time, WallTransform guess, bool restarted)
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
        if (homography is null) return FrameEstimate.Failed(time, from.Count, restarted);
        var similarity = CameraMotionMath.Ransac(from, to, CameraMotionModel.Similarity, threshold, 200, seed + 1, 2);

        // Both models are judged on the same wall points: the homography's inliers.
        var homographyResiduals = CameraMotionMath.Residuals(homography.Transform, from, to, homography.Inliers);
        var similarityResiduals = similarity is null
            ? Array.Empty<double>()
            : CameraMotionMath.Residuals(similarity.Transform, from, to, homography.Inliers);
        return new FrameEstimate(time, true, from.Count, homography.InlierCount, homography.Transform,
            similarity?.Transform ?? homography.Transform,
            CameraMotionMath.Quantile(similarityResiduals, 0.5), CameraMotionMath.Quantile(similarityResiduals, 0.95),
            CameraMotionMath.Quantile(homographyResiduals, 0.5), CameraMotionMath.Quantile(homographyResiduals, 0.95))
        {
            Coverage = Coverage(from, homography.Inliers),
            Restarted = restarted
        };
    }

    /// <summary>Of the <see cref="CoverageGrid"/> × <see cref="CoverageGrid"/> cells of the reference frame that have wall
    /// corners at all, the share holding at least one inlier: a fit supported by one part of the wall says little about the
    /// rest of it (a false lock on a hand-held clip), while a cell without texture (a plain ceiling) is not held against it.</summary>
    private double Coverage(List<PointD> from, bool[] inliers) =>
        _cornerCells == 0 ? 0 : Cells(from, i => inliers[i]).Count(c => c) / (double)_cornerCells;

    private bool[] Cells(IReadOnlyList<PointD> points, Func<int, bool> counts)
    {
        var cells = new bool[CoverageGrid * CoverageGrid];
        for (var i = 0; i < points.Count; i++)
        {
            if (!counts(i)) continue;
            var cx = Math.Clamp((int)(points[i].X / Width * CoverageGrid), 0, CoverageGrid - 1);
            var cy = Math.Clamp((int)(points[i].Y / Height * CoverageGrid), 0, CoverageGrid - 1);
            cells[cy * CoverageGrid + cx] = true;
        }
        return cells;
    }

    /// <summary>One analysed frame: the fits (pixels of the analysed frame), their errors on the wall points, and what the
    /// reliability of the frame is judged by.</summary>
    public sealed record FrameEstimate(MediaTime SourceTime, bool Fits, int Tracked, int Inliers, WallTransform Homography,
        WallTransform Similarity, double SimilarityMedian, double SimilarityP95, double HomographyMedian, double HomographyP95)
    {
        /// <summary>Of the reference frame's grid cells with wall corners, the share with an inlier (0..1).</summary>
        public double Coverage { get; init; }

        /// <summary>The estimate did not start from the previous frame's: the track had been lost (or this is the first frame).</summary>
        public bool Restarted { get; init; }

        /// <summary>How fast the wall moved since the previously committed frame (pixels of a 1920-wide frame per second);
        /// set by <see cref="CameraMotionSampler"/>, 0 when unknown.</summary>
        public double Speed1920 { get; init; }

        public static FrameEstimate Failed(MediaTime time, int tracked, bool restarted = true) =>
            new(time, false, tracked, 0, WallTransform.Identity, WallTransform.Identity,
                double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity)
            {
                Restarted = restarted
            };
    }
}
