using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 19 Step 19.4 (D034 §1, §5, §6): which masks a candidate offers (> 2 % of the frame hidden, best score first, none
/// when all are hidden); the outer contour of a mask (largest component, holes ignored, a start pixel joining two
/// branches, a single pixel); the colour samples of a hold (shadows and highlights trimmed, the other holds out of the
/// wall ring, a hold thinner than the erosion).
/// </summary>
public class HoldMaskTests
{
    private static HoldMask Mask(int w, int h, double score, Func<int, int, bool> inside)
    {
        var p = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            if (inside(x, y)) p[y * w + x] = 1;
        return new HoldMask(w, h, p, score);
    }

    [Fact]
    public void Masks_over_two_percent_of_the_frame_are_hidden_and_the_best_score_comes_first()
    {
        var small = Mask(100, 100, 0.6, (x, y) => x < 10 && y < 10);      // 1 %
        var big = Mask(100, 100, 0.95, (x, y) => x < 50);                  // 50 %: hidden despite the score
        var better = Mask(100, 100, 0.8, (x, y) => x < 12 && y < 15);      // 1.8 %
        var set = new HoldMaskSet(ImmutableArray.Create(small, big, better));
        Assert.Equal(new[] { 2, 0 }, HoldCandidateRules.Visible(set));
        Assert.Equal(2, HoldCandidateRules.Choose(set));

        var walls = new HoldMaskSet(ImmutableArray.Create(big, big, Mask(100, 100, 0.3, (x, y) => y < 3)));   // 3 %
        Assert.Empty(HoldCandidateRules.Visible(walls));
        Assert.Null(HoldCandidateRules.Choose(walls));                     // no candidate at all
        Assert.Null(HoldCandidateRules.Choose(new HoldMaskSet(ImmutableArray.Create(Mask(10, 10, 0.9, (_, _) => false)))));
    }

    [Fact]
    public void The_contour_of_a_rectangle_is_its_four_corners_through_the_pixel_centres()
    {
        var m = Mask(20, 10, 1, (x, y) => x >= 4 && x <= 13 && y >= 2 && y <= 7);
        var contour = MaskContour.Outer(m.Pixels, 20, 10);
        Assert.Equal(4, contour.Length);
        Assert.Contains(new NormalizedPoint(4.5 / 20, 2.5 / 10), contour);
        Assert.Contains(new NormalizedPoint(13.5 / 20, 7.5 / 10), contour);
    }

    [Fact]
    public void Only_the_largest_component_and_its_outer_contour_count_a_donuts_hole_is_ignored()
    {
        // A ring (outer radius 20, inner 8) and a small blob elsewhere.
        var m = Mask(100, 60, 1, (x, y) =>
        {
            var d = Math.Sqrt((x - 40) * (x - 40) + (y - 30) * (y - 30));
            return (d <= 20 && d >= 8) || (x > 90 && y > 50);
        });
        var contour = MaskContour.Outer(m.Pixels, 100, 60);
        Assert.All(contour, p => Assert.InRange(Math.Sqrt(Math.Pow(p.X * 100 - 40.5, 2) + Math.Pow(p.Y * 60 - 30.5, 2)), 17.5, 21.5));
        Assert.DoesNotContain(contour, p => p.X * 100 > 85);
    }

    [Fact]
    public void A_start_pixel_joining_two_branches_still_traces_the_whole_outline()
    {
        // A "V": the topmost-leftmost pixel joins a branch going down-left and one going down-right.
        var m = Mask(41, 25, 1, (x, y) => y <= 20 && (Math.Abs(x - (20 - y)) <= 1 || Math.Abs(x - (20 + y)) <= 1) && !(y == 0 && x != 20));
        var traced = MaskContour.TraceOuter(MaskContour.LargestComponent(m.Pixels, 41, 25).Mask, 41, 25);
        Assert.Contains(traced, p => p.X <= 1);        // the left arm's tip
        Assert.Contains(traced, p => p.X >= 39);       // the right arm's tip
    }

    [Fact]
    public void A_single_pixel_gets_a_tiny_square_and_an_empty_mask_no_contour()
    {
        var contour = MaskContour.Outer(Mask(10, 10, 1, (x, y) => x == 5 && y == 5).Pixels, 10, 10);
        Assert.Equal(4, contour.Length);
        Assert.Empty(MaskContour.Outer(new byte[100], 10, 10));
    }

    private static DecodedFrame Frame(int w, int h, Func<int, int, (byte R, byte G, byte B)> color)
    {
        var p = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (r, g, b) = color(x, y);
            var i = (y * w + x) * 4;
            (p[i], p[i + 1], p[i + 2], p[i + 3]) = (b, g, r, 255);
        }
        return new DecodedFrame(w, h, w * 4, p, new SourceTimestamp(0, new TimeBase(1, 25)));
    }

    [Fact]
    public void The_samples_are_the_surface_and_the_wall_with_shadows_and_highlights_trimmed()
    {
        const int w = 200, h = 200;
        bool InHold(int x, int y) => (x - 100) * (x - 100) + (y - 100) * (y - 100) <= 40 * 40;
        var frame = Frame(w, h, (x, y) =>
        {
            if (InHold(x, y)) return (x % 23 == 0) ? ((byte)255, (byte)255, (byte)255) : (y % 31 == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)200, (byte)30, (byte)40));
            return (230, 220, 200);   // the wall
        });
        var mask = Mask(w, h, 1, InHold).Pixels;
        var (surface, wall) = HoldColorSampler.Sample(frame, mask, Array.Empty<IReadOnlyList<NormalizedPoint>>());
        Assert.True(surface.DistanceTo(RouteColor.SrgbToOkLab(200, 30, 40)) < 0.01, $"surface {surface}");
        Assert.True(wall.DistanceTo(RouteColor.SrgbToOkLab(230, 220, 200)) < 0.01, $"wall {wall}");
    }

    [Fact]
    public void The_other_holds_are_left_out_of_the_wall_ring()
    {
        const int w = 200, h = 100;
        bool InHold(int x, int y) => x >= 80 && x < 120 && y >= 30 && y < 70;
        bool InNeighbour(int x, int y) => x >= 120 && x < 200;   // a big blue hold right next to it
        var frame = Frame(w, h, (x, y) => InHold(x, y) ? ((byte)200, (byte)30, (byte)40) : InNeighbour(x, y) ? ((byte)20, (byte)40, (byte)220) : ((byte)230, (byte)220, (byte)200));
        var mask = Mask(w, h, 1, InHold).Pixels;
        var neighbour = new IReadOnlyList<NormalizedPoint>[]
        {
            new[] { new NormalizedPoint(120.0 / w, 0), new NormalizedPoint(1, 0), new NormalizedPoint(1, 1), new NormalizedPoint(120.0 / w, 1) }
        };
        var (_, withExclusion) = HoldColorSampler.Sample(frame, mask, neighbour);
        Assert.True(withExclusion.DistanceTo(RouteColor.SrgbToOkLab(230, 220, 200)) < 0.01);
    }

    [Fact]
    public void A_hold_thinner_than_the_erosion_uses_its_whole_mask()
    {
        var frame = Frame(50, 50, (x, y) => y == 25 ? ((byte)10, (byte)200, (byte)10) : ((byte)240, (byte)240, (byte)240));
        var mask = Mask(50, 50, 1, (x, y) => y == 25 && x > 5 && x < 45).Pixels;
        var (surface, _) = HoldColorSampler.Sample(frame, mask, Array.Empty<IReadOnlyList<NormalizedPoint>>());
        Assert.True(surface.DistanceTo(RouteColor.SrgbToOkLab(10, 200, 10)) < 0.01);
    }
}
