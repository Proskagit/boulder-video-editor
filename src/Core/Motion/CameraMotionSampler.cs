using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Motion;

/// <summary>
/// Chooses which decoded frames the <see cref="CameraMotionEstimator"/> analyses (R2 → E, E-1). Frames arrive in time
/// order. One frame every <see cref="Interval"/> is analysed; the frames in between are kept. When the wall moved more than
/// <see cref="DensifyPixels1920"/> between two analysed frames — or the track was lost there — the kept frames are
/// analysed too, in order, and the later frame again from the last of them. A track already lost at the previous analysed
/// frame is not densified again: only where it was lost is searched frame by frame, so a long unreliable stretch costs one
/// analysis per interval (each a costly search), not one per frame. So a contour drawn between two analysed frames
/// is never further than about that distance from where a frame-by-frame analysis would put it (the track blends the
/// transforms of neighbouring analysed frames), while a still or slowly moving camera costs no more than before.
/// <para>A jolt that goes and comes back within one interval is not seen (both ends agree); the interval is short enough
/// that such a jolt is at most a few frames long.</para>
/// </summary>
public sealed class CameraMotionSampler
{
    public static readonly MediaTime DefaultInterval = MediaTime.FromSeconds(0.1);

    /// <summary>The version of the whole analysis — the estimator, this sampler and the reliability rule. A cached track
    /// (<see cref="Interfaces.ICameraMotionCache"/>) of another version is never used: raise it with any change that can
    /// change a track. 1 — the analysis merged in PR #31; 2 — R2 → E completion (E-1 densify, E-2 reliability); 3 — a lost stretch densified only where it began.</summary>
    public const int AnalysisVersion = 3;

    /// <summary>Below the smallest error a contour is judged by (docs/R2E_CAMERA_TRACKING_AUDIT.md, E-1).</summary>
    public const double DefaultDensifyPixels1920 = 2.0;

    private readonly CameraMotionEstimator _estimator;
    private readonly List<(GrayImage Frame, MediaTime Time)> _between = new();
    private MediaTime? _next;
    private MediaTime? _committedAt;
    private bool _lastFits = true;

    /// <param name="interval">The regular spacing of the analysed frames; zero analyses every frame.</param>
    /// <param name="densifyPixels1920">The motion between two analysed frames above which the frames in between are analysed
    /// too; infinity never analyses them.</param>
    public CameraMotionSampler(CameraMotionEstimator estimator, MediaTime? interval = null, double densifyPixels1920 = DefaultDensifyPixels1920)
    {
        _estimator = estimator;
        Interval = interval ?? DefaultInterval;
        DensifyPixels1920 = densifyPixels1920;
    }

    public MediaTime Interval { get; }
    public double DensifyPixels1920 { get; }

    /// <summary>The frames analysed because the camera moved fast (for reports).</summary>
    public int DensifiedFrames { get; private set; }

    /// <summary>Every frame analysed, the regular ones included.</summary>
    public int AnalysedFrames { get; private set; }

    /// <summary>Offers the next decoded frame (ascending source times).</summary>
    public void Offer(GrayImage frame, MediaTime time)
    {
        if (time == _estimator.ReferenceTime)
        {
            _estimator.Add(frame, time);   // the identity, and the start of the next estimate
            _committedAt = time;
            _lastFits = true;
            _between.Clear();
            _next = time + Interval;
            return;
        }
        if (_next is { } next && time < next)
        {
            if (!double.IsPositiveInfinity(DensifyPixels1920)) _between.Add((frame, time));
            return;
        }

        var previous = _estimator.Previous;
        var estimate = With(_estimator.Estimate(frame, time), previous);
        AnalysedFrames++;
        var moved = previous is { } p && estimate.Fits && _estimator.Displacement1920(p, estimate.Homography) > DensifyPixels1920;
        if (_between.Count > 0 && (moved || (!estimate.Fits && _lastFits)))
        {
            foreach (var (between, at) in _between)
            {
                var before = _estimator.Previous;
                Commit(With(_estimator.Estimate(between, at), before));
                DensifiedFrames++;
                AnalysedFrames++;
            }
            var last = _estimator.Previous;
            estimate = With(_estimator.Estimate(frame, time), last);
            AnalysedFrames++;
        }
        Commit(estimate);
        _lastFits = estimate.Fits;
        _between.Clear();
        _next = time + Interval;
    }

    public CameraMotionTrack Build() => _estimator.Build();

    private void Commit(CameraMotionEstimator.FrameEstimate estimate)
    {
        _estimator.Commit(estimate);
        if (estimate.Fits) _committedAt = estimate.SourceTime;
    }

    /// <summary>The estimate with the wall's speed since <paramref name="previous"/> (the last committed fit).</summary>
    private CameraMotionEstimator.FrameEstimate With(CameraMotionEstimator.FrameEstimate estimate, WallTransform? previous)
    {
        if (!estimate.Fits || previous is not { } p || _committedAt is not { } at || estimate.SourceTime <= at) return estimate;
        var seconds = (estimate.SourceTime - at).Ticks / (double)TimeSpan.TicksPerSecond;
        return estimate with { Speed1920 = _estimator.Displacement1920(p, estimate.Homography) / seconds };
    }
}
