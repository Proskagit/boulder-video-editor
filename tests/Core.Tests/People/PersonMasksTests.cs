using System.Collections.Immutable;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.People;
using Xunit;
using static AiVideoEditor.Core.Tests.People.PeopleTestData;

namespace AiVideoEditor.Core.Tests.People;

/// <summary>D061 §4: the people's masks — one separate mask per person, stored compressed, read back exactly, blended between
/// the analysed frames, absent where nothing was analysed — and the outline hidden where a person stands in front.</summary>
public class PersonMasksTests
{
    private const int W = 40, H = 30;

    private static byte[] Rect(int x0, int y0, int x1, int y1, byte value = 255)
    {
        var grid = new byte[W * H];
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++) grid[y * W + x] = value;
        return grid;
    }

    private static PersonMaskSample Sample(double seconds, params (int Id, byte[] Grid)[] people) =>
        new(S(seconds), people.Select(p => MaskCodec.Entry(p.Id, p.Grid, W, H)).OfType<PersonMaskEntry>().ToImmutableArray());

    [Fact]
    public void A_mask_is_stored_and_read_back_exactly()
    {
        var grid = Rect(5, 4, 17, 20, 200);
        var entry = MaskCodec.Entry(3, grid, W, H)!;
        Assert.Equal((5, 4, 12, 16), (entry.X, entry.Y, entry.Width, entry.Height));
        var track = new PersonMaskTrack(W, H, S(0.1), ImmutableArray.Create(new PersonMaskSample(S(1), ImmutableArray.Create(entry))), "test");
        Assert.Equal(grid, track.CoverageAt(S(1))!.Coverage);
    }

    [Fact]
    public void An_empty_mask_has_no_entry()
    {
        Assert.Null(MaskCodec.Entry(1, new byte[W * H], W, H));
    }

    [Fact]
    public void Each_person_keeps_a_separate_mask_and_the_union_covers_both()
    {
        var track = new PersonMaskTrack(W, H, S(0.1), ImmutableArray.Create(Sample(1, (1, Rect(0, 0, 10, 10)), (2, Rect(20, 0, 30, 10)))), "test");
        var people = track.PeopleAt(S(1));
        Assert.Equal(2, people.Count);
        Assert.Equal(255, people.Single(p => p.PersonId == 1).Mask.At(0.1, 0.1));
        Assert.Equal(0, people.Single(p => p.PersonId == 1).Mask.At(0.6, 0.1));
        Assert.Equal(255, people.Single(p => p.PersonId == 2).Mask.At(0.6, 0.1));
        var union = track.CoverageAt(S(1))!;
        Assert.Equal(255, union.At(0.1, 0.1));
        Assert.Equal(255, union.At(0.6, 0.1));
        Assert.Equal(new[] { 1, 2 }, track.PersonIds);
    }

    [Fact]
    public void Between_two_analysed_frames_the_masks_are_blended()
    {
        var track = new PersonMaskTrack(W, H, S(0.1), ImmutableArray.Create(Sample(1.0, (1, Rect(0, 0, 10, 10))), Sample(1.1, (1, Rect(0, 0, 20, 10)))), "test");
        var mid = track.CoverageAt(S(1.05))!;
        Assert.Equal(255, mid.At(0.1, 0.1));     // in both
        Assert.InRange(mid.At(0.4, 0.1), 120, 135);   // in one: half
    }

    [Fact]
    public void Far_from_any_analysed_frame_there_is_no_mask()
    {
        var track = new PersonMaskTrack(W, H, S(0.1), ImmutableArray.Create(Sample(1.0, (1, Rect(0, 0, 10, 10))), Sample(3.0, (1, Rect(0, 0, 10, 10)))), "test");
        Assert.Null(track.CoverageAt(S(2.0)));
        Assert.NotNull(track.CoverageAt(S(1.05)));
        Assert.Null(track.CoverageAt(S(10)));
        Assert.Empty(track.PeopleAt(S(2.0)));
    }

    [Fact]
    public void The_outline_is_hidden_under_a_person_and_kept_elsewhere()
    {
        // A horizontal contour across the frame; a person covers its left half.
        var mask = new OcclusionMask(W, H, Rect(0, 0, 20, 30));
        var outline = new OutlineDraw(Guid.NewGuid(), Affine2D.Identity, 1,
            ImmutableArray.Create(ImmutableArray.Create(new PointD(4, 150), new PointD(396, 150), new PointD(396, 152), new PointD(4, 152))),
            new RectD(0, 0, 400, 300), 4, 1, "#FF0000", null) { Occlusion = mask, OcclusionRect = new RectD(0, 0, 400, 300) };
        var groups = OutlineOcclusion.Groups(outline).ToList();
        var segments = groups.SelectMany(g => g.Segments.Select(s => (g.Opacity, s))).ToList();
        Assert.All(segments.Where(s => s.Opacity >= 1), s => Assert.True(Math.Min(s.s.A.X, s.s.B.X) >= 195, $"{s.s}"));   // fully drawn only right
        Assert.Contains(segments, s => s.s.A.X >= 300 || s.s.B.X >= 300);   // the right part is there
        Assert.DoesNotContain(segments, s => Math.Max(s.s.A.X, s.s.B.X) < 180);   // nothing under the person
    }

    [Fact]
    public void Without_a_mask_every_edge_is_drawn_in_its_hold_group()
    {
        var outline = new OutlineDraw(Guid.NewGuid(), Affine2D.Identity, 1,
            ImmutableArray.Create(ImmutableArray.Create(new PointD(0, 0), new PointD(10, 0), new PointD(10, 10))),
            new RectD(0, 0, 400, 300), 4, 1, "#FF0000", null);
        var group = Assert.Single(OutlineOcclusion.Groups(outline));
        Assert.Equal(1, group.Opacity);
        Assert.Equal(3, group.Segments.Length);
    }

    [Fact]
    public void A_fading_hold_keeps_its_own_opacity_under_the_visibility()
    {
        var mask = new OcclusionMask(W, H, new byte[W * H]);
        var outline = new OutlineDraw(Guid.NewGuid(), Affine2D.Identity, 1,
            ImmutableArray.Create(ImmutableArray.Create(new PointD(10, 10), new PointD(50, 10), new PointD(50, 50))),
            new RectD(0, 0, 400, 300), 4, 1, "#FF0000", null)
        {
            PolygonOpacities = ImmutableArray.Create(0.5), Occlusion = mask, OcclusionRect = new RectD(0, 0, 400, 300)
        };
        Assert.All(OutlineOcclusion.Groups(outline), g => Assert.Equal(0.5, g.Opacity));
    }

    [Fact]
    public void The_visibility_ramp_is_smooth_and_monotone()
    {
        Assert.Equal(1, OutlineOcclusion.Visibility(0));
        Assert.Equal(1, OutlineOcclusion.Visibility(OutlineOcclusion.RampStart));
        Assert.Equal(0, OutlineOcclusion.Visibility(OutlineOcclusion.RampEnd));
        Assert.Equal(0.5, OutlineOcclusion.Visibility((OutlineOcclusion.RampStart + OutlineOcclusion.RampEnd) / 2), 9);
        var previous = 1.0;
        for (var c = 0.0; c <= 1; c += 0.01)
        {
            var v = OutlineOcclusion.Visibility(c);
            Assert.True(v <= previous + 1e-12);
            previous = v;
        }
    }
}
