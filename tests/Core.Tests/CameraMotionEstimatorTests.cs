using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Motion;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Hold tracking (R2 → E): the camera-motion estimate on a synthetic wall (random holds on a speckled panel) moved by known
/// transforms — the error at the wall stays under half a pixel, a "climber" covering part of the wall is ignored, the
/// clip's model is the similarity when it is enough and the homography when the camera tilts, and a frame that shows
/// another picture is unreliable (its contours are hidden, never drawn off the holds).
/// </summary>
public class CameraMotionEstimatorTests
{
    internal const int W = 480, H = 270;
    internal static readonly GrayImage Wall = SyntheticWall(W, H, seed: 11);
    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    [Fact]
    public void A_shifted_rotated_and_zoomed_camera_is_recovered_as_a_similarity()
    {
        var estimator = new CameraMotionEstimator(Wall, S(0));
        var truths = new List<WallTransform>();
        for (var i = 1; i <= 6; i++)
        {
            var truth = Similarity(1 + 0.008 * i, 0.004 * i, 3.5 * i, -2.0 * i);
            truths.Add(truth);
            estimator.Add(Warp(Wall, truth), S(0.1 * i));
        }
        var track = estimator.Build();
        Assert.Equal(CameraMotionModel.Similarity, track.Model);
        for (var i = 1; i <= 6; i++)
            AssertWithin(truths[i - 1], track.At(S(0.1 * i)), 0.5);
        Assert.True(track.Samples.All(s => s.IsReliable));
    }

    [Fact]
    public void A_tilted_camera_needs_the_homography()
    {
        var estimator = new CameraMotionEstimator(Wall, S(0));
        var truth = new WallTransform(1.02, 0.03, 6, -0.02, 1.0, 4, 0.00025, 0.0002, 1);
        estimator.Add(Warp(Wall, truth), S(0.1));
        var track = estimator.Build();
        Assert.Equal(CameraMotionModel.Homography, track.Model);
        AssertWithin(truth, track.At(S(0.1)), 0.5);
    }

    [Fact]
    public void A_climber_in_front_of_the_wall_does_not_move_the_estimate()
    {
        var estimator = new CameraMotionEstimator(Wall, S(0));
        var truth = Similarity(1.01, 0.01, 7, 3);
        var frame = Warp(Wall, truth);
        var pixels = (float[])frame.Pixels.Clone();
        for (var y = 60; y < 260; y++)        // a body: a large patch of other texture over a third of the wall
        for (var x = 150; x < 300; x++)
            pixels[y * W + x] = 128 + 60 * MathF.Sin(x * 0.7f) * MathF.Cos(y * 0.3f);
        estimator.Add(new GrayImage(W, H, pixels), S(0.1));
        var track = estimator.Build();
        AssertWithin(truth, track.At(S(0.1)), 0.5);
    }

    [Fact]
    public void A_sudden_zoom_out_like_a_lens_switch_is_recovered()
    {
        var estimator = new CameraMotionEstimator(Wall, S(0));
        var truth = Similarity(0.65, 0.0, 6, -4);   // the wall shrinks to 65 % in one tenth of a second
        estimator.Add(Warp(Wall, truth), S(0.1));
        var track = estimator.Build();
        AssertWithin(truth, track.At(S(0.1)), 0.75);
    }

    [Fact]
    public void A_frame_of_another_picture_is_unreliable()
    {
        var estimator = new CameraMotionEstimator(Wall, S(0));
        estimator.Add(SyntheticWall(W, H, seed: 99), S(0.1));
        var track = estimator.Build();
        Assert.Equal(CameraMotionLookupKind.Hidden, track.At(S(0.1)).Kind);
    }

    [Fact]
    public void The_reference_frame_is_the_identity_and_the_estimate_is_deterministic()
    {
        CameraMotionTrack Run()
        {
            var e = new CameraMotionEstimator(Wall, S(1));
            e.Add(Warp(Wall, Similarity(1.01, 0.0, 4, 2)), S(1.1));
            return e.Build();
        }
        var a = Run();
        var b = Run();
        Assert.Equal(a.Samples.ToArray(), b.Samples.ToArray());
        Assert.Equal(WallTransform.Identity, a.At(S(1)).Transform);
    }

    internal static WallTransform Similarity(double scale, double angle, double tx, double ty)
    {
        // About the frame's centre, then shifted (pixels).
        double c = scale * Math.Cos(angle), s = scale * Math.Sin(angle), cx = W / 2.0, cy = H / 2.0;
        return new WallTransform(c, -s, cx - c * cx + s * cy + tx, s, c, cy - s * cx - c * cy + ty, 0, 0, 1);
    }

    /// <summary>The frame the camera sees after <paramref name="pixelTransform"/>: frame(x) = wall(T⁻¹ x).</summary>
    internal static GrayImage Warp(GrayImage source, WallTransform pixelTransform)
    {
        var inverse = pixelTransform.Inverse();
        var pixels = new float[W * H];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var (sx, sy) = inverse.Apply(x, y);
            pixels[y * W + x] = source.Sample(sx, sy);
        }
        return new GrayImage(W, H, pixels);
    }

    internal static GrayImage SyntheticWall(int w, int h, int seed)
    {
        var random = new Random(seed);
        var pixels = new float[w * h];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = 200 + (float)random.NextDouble() * 6;   // the panel
        for (var k = 0; k < 900; k++)                                                                  // speckles
        {
            int x = random.Next(w), y = random.Next(h);
            pixels[y * w + x] = 60;
        }
        for (var k = 0; k < 70; k++)                                                                   // holds
        {
            double cx = random.NextDouble() * w, cy = random.NextDouble() * h, rx = 4 + random.NextDouble() * 12, ry = 4 + random.NextDouble() * 10;
            var value = (float)(30 + random.NextDouble() * 120);
            for (var y = Math.Max(0, (int)(cy - ry)); y < Math.Min(h, (int)(cy + ry) + 1); y++)
            for (var x = Math.Max(0, (int)(cx - rx)); x < Math.Min(w, (int)(cx + rx) + 1); x++)
            {
                double dx = (x - cx) / rx, dy = (y - cy) / ry;
                if (dx * dx + dy * dy <= 1) pixels[y * w + x] = value + (float)(10 * dx);
            }
        }
        // A light blur, like a camera.
        var blurred = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            float sum = 0;
            var n = 0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int xx = Math.Clamp(x + dx, 0, w - 1), yy = Math.Clamp(y + dy, 0, h - 1);
                sum += pixels[yy * w + xx];
                n++;
            }
            blurred[y * w + x] = sum / n;
        }
        return new GrayImage(w, h, blurred);
    }

    private static void AssertWithin(WallTransform pixelTruth, CameraMotionLookup lookup, double pixels)
    {
        Assert.Equal(CameraMotionLookupKind.Aligned, lookup.Kind);
        var normalizedTruth = WallTransform.FromPixels(pixelTruth, W, H);
        for (var y = 0.15; y < 0.9; y += 0.2)
        for (var x = 0.15; x < 0.9; x += 0.2)
        {
            var (tx, ty) = normalizedTruth.Apply(x, y);
            var (ax, ay) = lookup.Transform.Apply(x, y);
            var error = Math.Sqrt((tx - ax) * W * (tx - ax) * W + (ty - ay) * H * (ty - ay) * H);
            Assert.True(error <= pixels, $"error {error:0.###} px at ({x:0.##}, {y:0.##})");
        }
    }
}
