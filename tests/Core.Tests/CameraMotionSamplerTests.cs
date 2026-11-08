using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Motion;
using Xunit;
using static AiVideoEditor.Core.Tests.CameraMotionEstimatorTests;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// R2 → E, E-1: which frames the analysis looks at. A still camera costs one frame every 0.1 s, as before; a jolt between
/// two analysed frames — which a blend of their transforms would put tens of pixels off the wall on the frames in between
/// (the audit's defect D1, 13–66 px on real clips) — makes the frames in between analysed too, so every frame's contour is
/// where a frame-by-frame analysis puts it; a lost track is analysed frame by frame where it was lost. Each estimate carries the
/// wall's speed since the previous analysed frame.
/// </summary>
public class CameraMotionSamplerTests
{
    private const double Fps = 30;
    private static MediaTime F(int frame) => MediaTime.FromSeconds(frame / Fps);

    /// <summary>The camera: still until frame 13, then a jolt — in one frame 30 px (480-px frame) to the right and 6 px down,
    /// where it stays. The analysed frames are 0, 3, …, 12, 15: a blend of 12 and 15 is 10 and 20 px off on frames 13 and 14.</summary>
    private static WallTransform Camera(int frame) => frame < 14 ? WallTransform.Identity : Similarity(1, 0, 30, 6);

    private static (CameraMotionTrack Track, CameraMotionSampler Sampler) Run(Func<int, WallTransform> camera, int frames, double densify)
    {
        var sampler = new CameraMotionSampler(new CameraMotionEstimator(Wall, F(0)), densifyPixels1920: densify);
        for (var i = 0; i <= frames; i++) sampler.Offer(Warp(Wall, camera(i)), F(i));
        return (sampler.Build(), sampler);
    }

    private static double ErrorPixels(CameraMotionTrack track, WallTransform truth, MediaTime time)
    {
        var lookup = track.At(time);
        Assert.Equal(CameraMotionLookupKind.Aligned, lookup.Kind);
        var worst = 0.0;
        foreach (var (u, v) in new[] { (0.2, 0.2), (0.8, 0.2), (0.5, 0.5), (0.2, 0.8), (0.8, 0.8) })
        {
            var (tx, ty) = truth.Apply(u * W, v * H);
            var (x, y) = lookup.Transform.Apply(u, v);
            worst = Math.Max(worst, Math.Sqrt((x * W - tx) * (x * W - tx) + (y * H - ty) * (y * H - ty)));
        }
        return worst;
    }

    [Fact]
    public void A_still_camera_is_analysed_once_every_tenth_of_a_second()
    {
        var (track, sampler) = Run(_ => WallTransform.Identity, frames: 30, CameraMotionSampler.DefaultDensifyPixels1920);
        Assert.Equal(0, sampler.DensifiedFrames);
        Assert.InRange(sampler.AnalysedFrames, 9, 11);
        Assert.All(track.Samples, s => Assert.True(s.IsReliable));
    }

    [Fact]
    public void A_jolt_between_two_analysed_frames_makes_the_frames_in_between_analysed()
    {
        var (dense, sampler) = Run(Camera, frames: 24, CameraMotionSampler.DefaultDensifyPixels1920);
        Assert.True(sampler.DensifiedFrames > 0, $"densified {sampler.DensifiedFrames}, analysed {sampler.AnalysedFrames}");
        // Every frame is where a frame-by-frame analysis puts it — or, the jolt itself (≈ 3700 px/s, a blurred frame in a
        // real video), hidden: never shown off the wall.
        var shown = 0;
        for (var i = 10; i <= 18; i++)
        {
            if (dense.At(F(i)).Kind == CameraMotionLookupKind.Hidden) continue;
            shown++;
            Assert.True(ErrorPixels(dense, Camera(i), F(i)) < 0.5, $"frame {i}: {ErrorPixels(dense, Camera(i), F(i))}");
        }
        Assert.True(shown >= 7, $"shown {shown}");

        // Without it (the analysis before E-1) the blend between the analysed frames is off the wall in between.
        var (sparse, before) = Run(Camera, frames: 24, double.PositiveInfinity);
        Assert.Equal(0, before.DensifiedFrames);
        var worst = Enumerable.Range(10, 9).Max(i => ErrorPixels(sparse, Camera(i), F(i)));
        Assert.True(worst > 5, $"worst {worst}");
    }

    [Fact]
    public void A_long_lost_stretch_is_searched_frame_by_frame_only_where_it_began()
    {
        // Frames 10–69: no wall (a featureless picture); a lost track. Only the interval where it was lost is densified,
        // then one analysis per interval — a long unreliable stretch must not cost a costly search on every frame.
        var blank = new GrayImage(W, H, new float[W * H]);
        var sampler = new CameraMotionSampler(new CameraMotionEstimator(Wall, F(0)));
        for (var i = 0; i <= 90; i++) sampler.Offer(i is >= 10 and < 70 ? blank : Wall, F(i));
        var track = sampler.Build();
        Assert.InRange(sampler.DensifiedFrames, 1, 8);
        Assert.InRange(sampler.AnalysedFrames, 30, 45);
        Assert.Equal(CameraMotionLookupKind.Hidden, track.At(F(40)).Kind);
        Assert.Equal(CameraMotionLookupKind.Aligned, track.At(F(85)).Kind);   // found again after it
    }

    [Fact]
    public void A_window_that_begins_before_the_reference_frame_builds_an_ascending_track()
    {
        var sampler = new CameraMotionSampler(new CameraMotionEstimator(Wall, F(9)));   // the holds marked at frame 9
        for (var i = 0; i <= 18; i++) sampler.Offer(Warp(Wall, Similarity(1, 0, 0.5 * i, 0)), F(i));
        var track = sampler.Build();
        Assert.Equal(track.Samples.OrderBy(s => s.SourceTime).Select(s => s.SourceTime), track.Samples.Select(s => s.SourceTime));
        Assert.Contains(track.Samples, s => s.SourceTime < F(9));
        Assert.Equal(WallTransform.Identity, track.At(F(9)).Transform);
    }

    [Fact]
    public void Every_estimate_carries_the_walls_speed_since_the_previous_analysed_frame()
    {
        var estimator = new CameraMotionEstimator(Wall, F(0));
        var sampler = new CameraMotionSampler(estimator);
        for (var i = 0; i <= 24; i++) sampler.Offer(Warp(Wall, Camera(i)), F(i));
        var still = estimator.Frames.Where(f => f.SourceTime < F(12)).ToList();
        Assert.All(still, f => Assert.InRange(f.Speed1920, 0, 15));        // still camera: noise only
        // The jolt: ≈ 30.6 px of a 480-px frame in one frame = 122 px of a 1920-px frame × 30 frames per second.
        Assert.Contains(estimator.Frames, f => f.Speed1920 > 2000);
    }

    [Fact]
    public void The_reliability_rule_hides_a_frame_moving_too_fast_or_fitted_on_one_part_of_the_wall()
    {
        var f = new CameraMotionEstimator.FrameEstimate(F(1), true, 200, 180, WallTransform.Identity, WallTransform.Identity, 0.2, 0.5, 0.2, 0.5)
        {
            Coverage = 1, Speed1920 = 900
        };
        var rule = CameraMotionReliability.Default;
        Assert.True(rule.IsReliable(f, CameraMotionModel.Similarity, 0.25));
        Assert.False(rule.IsReliable(f with { Speed1920 = 2100 }, CameraMotionModel.Similarity, 0.25));   // blurred
        Assert.False(rule.IsReliable(f with { Coverage = 0.5 }, CameraMotionModel.Similarity, 0.25));     // a false lock
        Assert.True(CameraMotionReliability.BeforeE2.IsReliable(f with { Speed1920 = 2100, Coverage = 0.5 }, CameraMotionModel.Similarity, 0.25));
        Assert.False(rule.IsReliable(f with { Inliers = 20 }, CameraMotionModel.Similarity, 0.25));      // the limits before E-2 stay
    }

    [Theory]
    [InlineData(480)]
    [InlineData(960)]
    [InlineData(1920)]
    [InlineData(3840)]
    public void A_frame_whose_measured_error_exceeds_the_approved_3_px_bound_is_unreliable_at_any_analysis_width(int width)
    {
        // The bound is 3 px of a 1920-wide frame (D052): the residuals are in the analysis frame's pixels, so the same share
        // of the width is the limit at every analysis size.
        Assert.Equal(3.0, CameraMotionReliability.ErrorBound1920);
        var scale = width / 1920.0;
        CameraMotionEstimator.FrameEstimate Error(double px1920) =>
            new(F(1), true, 200, 180, WallTransform.Identity, WallTransform.Identity,
                px1920 * scale, 1.5 * px1920 * scale, px1920 * scale, 1.5 * px1920 * scale) { Coverage = 1, Speed1920 = 100 };
        var rule = CameraMotionReliability.Default;
        foreach (var model in new[] { CameraMotionModel.Similarity, CameraMotionModel.Homography })
        {
            Assert.True(rule.IsReliable(Error(2.9), model, scale));
            Assert.True(rule.IsReliable(Error(3.0), model, scale));
            Assert.False(rule.IsReliable(Error(3.1), model, scale));
            Assert.False(rule.IsReliable(Error(6.0), model, scale));
        }
    }

    [Fact]
    public void The_walls_speed_and_the_tracks_reliability_are_the_same_at_any_analysis_width()
    {
        // The jolt analysed at the test size and at twice it: the wall's speed (pixels of a 1920-wide frame) and what is
        // shown or hidden are the same.
        CameraMotionEstimator Run(int factor)
        {
            var estimator = new CameraMotionEstimator(Scale(Wall, factor), F(0));
            var sampler = new CameraMotionSampler(estimator);
            for (var i = 0; i <= 24; i++) sampler.Offer(Scale(Warp(Wall, Camera(i)), factor), F(i));
            return estimator;
        }
        var small = Run(1);
        var large = Run(2);
        Assert.Equal(2 * small.Width, large.Width);
        var jolt = small.Frames.First(f => f.Speed1920 > 2000).SourceTime;
        var ratio = large.Frames.First(f => f.SourceTime == jolt).Speed1920 / small.Frames.First(f => f.SourceTime == jolt).Speed1920;
        Assert.InRange(ratio, 0.9, 1.1);
        var (a, b) = (small.Build(), large.Build());
        for (var i = 0; i <= 24; i++) Assert.Equal(a.At(F(i)).Kind, b.At(F(i)).Kind);
    }

    /// <summary>The image enlarged <paramref name="factor"/> times (bilinear); 1 — the image itself.</summary>
    private static GrayImage Scale(GrayImage image, int factor)
    {
        if (factor == 1) return image;
        int w = image.Width * factor, h = image.Height * factor;
        var pixels = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            double sx = Math.Min(image.Width - 1.001, x / (double)factor), sy = Math.Min(image.Height - 1.001, y / (double)factor);
            int x0 = (int)sx, y0 = (int)sy;
            double fx = sx - x0, fy = sy - y0;
            pixels[y * w + x] = (float)((1 - fx) * (1 - fy) * image[x0, y0] + fx * (1 - fy) * image[x0 + 1, y0] +
                                        (1 - fx) * fy * image[x0, y0 + 1] + fx * fy * image[x0 + 1, y0 + 1]);
        }
        return new GrayImage(w, h, pixels);
    }
}
