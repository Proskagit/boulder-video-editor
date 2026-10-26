using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Motion;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Hold tracking (R2 → E): the wall transform's algebra, the least-squares and RANSAC fits, and the track's lookup by
/// source time — interpolation in short gaps between reliable frames, hidden next to an unreliable frame or across a long
/// gap, nothing outside the analysed range, a contour carried from its own marking frame.
/// </summary>
public class CameraMotionTrackTests
{
    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);
    private static WallTransform Shift(double dx, double dy) => new(1, 0, dx, 0, 1, dy, 0, 0, 1);

    [Fact]
    public void Then_applies_the_first_transform_first_and_the_inverse_undoes_it()
    {
        var a = new WallTransform(1.02, -0.05, 3, 0.05, 1.02, -2, 0.0001, 0.0002, 1);
        var b = Shift(10, 20);
        var (x, y) = a.Then(b).Apply(5, 7);
        var (ax, ay) = a.Apply(5, 7);
        Assert.Equal(ax + 10, x, 9);
        Assert.Equal(ay + 20, y, 9);
        var (ix, iy) = a.Then(a.Inverse()).Apply(123, 45);
        Assert.Equal(123, ix, 9);
        Assert.Equal(45, iy, 9);
    }

    [Fact]
    public void A_pixel_transform_maps_normalized_points_like_the_pixels_they_stand_for()
    {
        var pixels = new WallTransform(0.98, 0.03, 12, -0.03, 0.98, -7, 0.00002, -0.00001, 1);
        var normalized = WallTransform.FromPixels(pixels, 960, 540);
        var (px, py) = pixels.Apply(0.3 * 960, 0.7 * 540);
        var (nx, ny) = normalized.Apply(0.3, 0.7);
        Assert.Equal(px / 960, nx, 12);
        Assert.Equal(py / 540, ny, 12);
    }

    [Fact]
    public void The_fits_recover_an_exact_similarity_and_homography()
    {
        var similarity = new WallTransform(1.05 * Math.Cos(0.03), -1.05 * Math.Sin(0.03), 15, 1.05 * Math.Sin(0.03), 1.05 * Math.Cos(0.03), -8, 0, 0, 1);
        var homography = new WallTransform(1.01, 0.02, 5, -0.01, 0.99, 3, 0.00005, -0.00003, 1);
        var from = Grid();
        var all = Enumerable.Range(0, from.Count).ToList();
        foreach (var truth in new[] { similarity, homography })
        {
            var to = from.Select(p => { var (x, y) = truth.Apply(p.X, p.Y); return new PointD(x, y); }).ToList();
            var fit = truth == similarity ? CameraMotionMath.FitSimilarity(from, to, all) : CameraMotionMath.FitHomography(from, to, all);
            Assert.NotNull(fit);
            AssertSame(truth, fit!.Value, from);
        }
    }

    [Fact]
    public void Ransac_ignores_a_third_of_wrong_correspondences()
    {
        var truth = new WallTransform(1.01, 0.02, 5, -0.01, 0.99, 3, 0.00005, -0.00003, 1);
        var from = Grid();
        var random = new Random(7);
        var to = from.Select((p, i) =>
        {
            if (i % 3 == 0) return new PointD(random.NextDouble() * 400, random.NextDouble() * 300);   // the climber
            var (x, y) = truth.Apply(p.X, p.Y);
            return new PointD(x, y);
        }).ToList();
        var fit = CameraMotionMath.Ransac(from, to, CameraMotionModel.Homography, 1.0, 300, 1, 10);
        Assert.NotNull(fit);
        AssertSame(truth, fit!.Transform, from);
        Assert.InRange(fit.InlierCount, from.Count * 2 / 3 - 1, from.Count * 2 / 3 + 1);
    }

    [Fact]
    public void Ransac_is_deterministic()
    {
        var from = Grid();
        var random = new Random(3);
        var to = from.Select(p => new PointD(p.X + 2 + random.NextDouble(), p.Y - 1 + random.NextDouble())).ToList();
        var a = CameraMotionMath.Ransac(from, to, CameraMotionModel.Similarity, 1.0, 100, 42, 5)!;
        var b = CameraMotionMath.Ransac(from, to, CameraMotionModel.Similarity, 1.0, 100, 42, 5)!;
        Assert.Equal(a.Transform, b.Transform);
        Assert.Equal(a.Inliers, b.Inliers);
    }

    [Fact]
    public void Between_two_reliable_samples_the_transform_is_blended()
    {
        var track = Track((0.0, Shift(0, 0), true), (0.1, Shift(0.02, 0), true));
        var at = track.At(S(0.05));
        Assert.Equal(CameraMotionLookupKind.Aligned, at.Kind);
        Assert.Equal(0.51, at.Transform.Apply(0.5, 0.5).X, 9);
        Assert.Equal(0.52, track.At(S(0.1)).Transform.Apply(0.5, 0.5).X, 9);
    }

    [Fact]
    public void Next_to_an_unreliable_sample_or_across_a_long_gap_the_contour_is_hidden()
    {
        var track = Track((0.0, Shift(0, 0), true), (0.1, Shift(0.01, 0), false), (0.2, Shift(0.02, 0), true), (1.0, Shift(0.05, 0), true));
        Assert.Equal(CameraMotionLookupKind.Hidden, track.At(S(0.05)).Kind);
        Assert.Equal(CameraMotionLookupKind.Hidden, track.At(S(0.1)).Kind);
        Assert.Equal(CameraMotionLookupKind.Hidden, track.At(S(0.5)).Kind);     // 0.8 s gap > 0.25 s
        Assert.Equal(CameraMotionLookupKind.Aligned, track.At(S(0.2)).Kind);
    }

    [Fact]
    public void Outside_the_range_the_track_says_nothing_but_its_edges_reach_a_little_further()
    {
        var track = Track((1.0, Shift(0.01, 0), true), (1.1, Shift(0.02, 0), true));
        Assert.Equal(CameraMotionLookupKind.NoData, track.At(S(0.5)).Kind);
        Assert.Equal(CameraMotionLookupKind.NoData, track.At(S(2)).Kind);
        Assert.Equal(CameraMotionLookupKind.Aligned, track.At(S(0.95)).Kind);
        Assert.Equal(CameraMotionLookupKind.Aligned, track.At(S(1.15)).Kind);
    }

    [Fact]
    public void A_contour_is_carried_from_its_own_marking_frame()
    {
        var track = Track((0.0, Shift(0, 0), true), (0.1, Shift(0.05, 0), true), (0.2, Shift(0.10, 0), true));
        var mapped = track.Map(S(0.1), S(0.2));   // marked where the wall was at +0.05, shown where it is at +0.10
        Assert.Equal(CameraMotionLookupKind.Aligned, mapped.Kind);
        Assert.Equal(0.55, mapped.Transform.Apply(0.5, 0.5).X, 9);
        Assert.Equal(CameraMotionLookupKind.NoData, track.Map(S(0.1), S(5)).Kind);
    }

    [Fact]
    public void Samples_must_ascend()
    {
        Assert.Throws<ArgumentException>(() => Track((0.1, WallTransform.Identity, true), (0.1, WallTransform.Identity, true)));
    }

    private static CameraMotionTrack Track(params (double Seconds, WallTransform Transform, bool Reliable)[] samples) =>
        new(MediaTime.Zero, CameraMotionModel.Similarity,
            samples.Select(s => new CameraMotionSample(S(s.Seconds), s.Transform, s.Reliable)).ToImmutableArray());

    private static List<PointD> Grid()
    {
        var list = new List<PointD>();
        for (var y = 10; y < 300; y += 29)
        for (var x = 10; x < 400; x += 37)
            list.Add(new PointD(x, y));
        return list;
    }

    private static void AssertSame(WallTransform expected, WallTransform actual, IEnumerable<PointD> points)
    {
        foreach (var p in points)
        {
            var (ex, ey) = expected.Apply(p.X, p.Y);
            var (ax, ay) = actual.Apply(p.X, p.Y);
            Assert.Equal(ex, ax, 6);
            Assert.Equal(ey, ay, 6);
        }
    }
}
