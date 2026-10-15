using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 21 (D036 §2): the schedule of the hold animation on timeline frames — the start frame from the source time (the
/// first frame showing it, exact at every rate and speed), the appear frames <c>n0 + Frames(r · Interval)</c> without
/// drift at 24 / 30 / 60 / 29.97 fps, the D025 ramps of the appear and disappear fades, "until clip end"; the order
/// bottom to top by the contour's centre (ties: left first, then the marking order) and the marking order.
/// </summary>
public class HoldAnimationRuleTests
{
    public static IEnumerable<object[]> Rates() => new[]
    {
        new object[] { 24, 1 }, new object[] { 30, 1 }, new object[] { 60, 1 }, new object[] { 30000, 1001 }
    };

    private static ImmutableArray<NormalizedPoint> Square(double x, double y, double r = 0.02) => ImmutableArray.Create(
        new NormalizedPoint(x - r, y - r), new NormalizedPoint(x + r, y - r), new NormalizedPoint(x + r, y + r), new NormalizedPoint(x - r, y + r));

    private static Hold H(double x, double y) => new(Guid.NewGuid(), Square(x, y), default, default, MediaTime.Zero);

    private static ImmutableArray<Hold> Holds(int count) =>
        Enumerable.Range(0, count).Select(i => H(0.5, 0.9 - i * 0.8 / Math.Max(1, count))).ToImmutableArray();   // marked bottom-up

    [Theory]
    [MemberData(nameof(Rates))]
    public void The_start_frame_is_the_first_frame_showing_the_start_source_time_at_every_speed(int num, int den)
    {
        var rate = new FrameRate(num, den);
        var clipStart = MediaTime.FromFrame(37, rate);
        var sourceIn = new MediaTime(12_345_678);
        foreach (var steps in new[] { 5, 10, 20, 27, 40, 80 })   // 0.25× … 4×
        {
            var speed = ClipSpeed.FromSteps(steps);
            long a = speed.Numerator, b = speed.Denominator;
            for (var n = 0L; n < 200; n += 7)
            {
                // The frame under the playhead → its source time → the same frame ("Set to playhead").
                var t = HoldAnimationRule.SourceTimeAt(clipStart, sourceIn, speed, n, rate);
                Assert.Equal(n, HoldAnimationRule.StartFrame(clipStart, sourceIn, speed, t, rate));

                // Brute force: t(n0) ≥ start > t(n0 − 1), with t(n) = SourceIn + (FromFrame(n) − S)·a/b exact.
                var start = new MediaTime(sourceIn.Ticks + n * 1_111_111);
                var n0 = HoldAnimationRule.StartFrame(clipStart, sourceIn, speed, start, rate);
                Int128 BT(long frame) => (Int128)b * sourceIn.Ticks + (Int128)a * (MediaTime.FromFrame(frame, rate).Ticks - clipStart.Ticks);
                Assert.True(BT(n0) >= (Int128)b * start.Ticks);
                Assert.True(BT(n0 - 1) < (Int128)b * start.Ticks);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Holds_appear_on_the_rounded_multiples_of_the_interval_without_drift(int num, int den)
    {
        var rate = new FrameRate(num, den);
        var animation = new HoldAnimation();   // 0.5 s, 0.25 s, 3 s, 0.5 s
        var holds = Holds(120);
        var schedule = HoldAnimationRule.Schedule(animation, holds, 100, rate);
        var ranks = HoldAnimationRule.Ranks(holds, animation.Order);
        for (var i = 0; i < holds.Length; i++)
        {
            // Rank r appears at n0 + round(r · 0.5 s · fps) — never r × a rounded interval.
            var expected = 100 + MediaTime.FromSeconds(ranks[i] * 0.5).ToNearestFrame(rate);
            Assert.Equal(expected, schedule.AppearFrames[i]);
        }
        Assert.Equal(100 + MediaTime.FromSeconds(119 * 0.5).ToNearestFrame(rate), schedule.LastAppearFrame);
        if (num == 30000) Assert.Equal(100 + 1783, schedule.LastAppearFrame);   // 59.5 s · 29.97 = 1783.2 (15 × 119 would be 1785)

        var fadeIn = MediaTime.FromSeconds(0.25).ToNearestFrame(rate);
        Assert.Equal(fadeIn, schedule.AppearFadeFrames);
        Assert.Equal(schedule.LastAppearFrame + fadeIn + MediaTime.FromSeconds(3).ToNearestFrame(rate), schedule.DisappearStartFrame);
        Assert.Equal(MediaTime.FromSeconds(0.5).ToNearestFrame(rate), schedule.DisappearFadeFrames);
    }

    [Fact]
    public void The_fades_are_the_D025_ramps_and_all_contours_go_together()
    {
        var rate = FrameRate.Fps24;
        var holds = Holds(3);
        var schedule = HoldAnimationRule.Schedule(new HoldAnimation(), holds, 0, rate);   // appear 0, 12, 24; fade 6
        Assert.Equal(new long[] { 0, 12, 24 }, schedule.AppearFrames.ToArray());
        Assert.Equal(6, schedule.AppearFadeFrames);

        Assert.Equal(0, schedule.OpacityOf(1, 11));
        for (var k = 0; k < 6; k++) Assert.Equal((k + 1) / 7.0, schedule.OpacityOf(1, 12 + k));
        Assert.Equal(1, schedule.OpacityOf(1, 18));
        Assert.Equal(1, schedule.OpacityOf(0, 25));                  // an appeared hold stays

        var d = schedule.DisappearStartFrame!.Value;
        Assert.Equal(24 + 6 + 72, d);
        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, schedule.OpacitiesAt(d - 1).ToArray());
        for (var j = 0; j < 12; j++)
            Assert.All(schedule.OpacitiesAt(d + j), o => Assert.Equal((12 - j) / 13.0, o));   // together
        Assert.All(schedule.OpacitiesAt(d + 12), o => Assert.Equal(0, o));
        Assert.Equal(d + 12, schedule.EndFrame);
    }

    [Fact]
    public void Zero_fades_are_instant_and_until_clip_end_keeps_every_contour()
    {
        var holds = Holds(2);
        var instant = HoldAnimationRule.Schedule(new HoldAnimation { AppearFade = MediaTime.Zero, DisappearFade = MediaTime.Zero }, holds, 10, FrameRate.Fps25);
        Assert.Equal(0, instant.OpacityOf(0, 9));
        Assert.Equal(1, instant.OpacityOf(0, 10));
        var d = instant.DisappearStartFrame!.Value;
        Assert.Equal(1, instant.OpacityOf(1, d - 1));
        Assert.Equal(0, instant.OpacityOf(1, d));

        var stays = HoldAnimationRule.Schedule(new HoldAnimation { StayUntilClipEnd = true }, holds, 10, FrameRate.Fps25);
        Assert.Null(stays.DisappearStartFrame);
        Assert.Null(stays.EndFrame);
        Assert.Equal(1, stays.OpacityOf(0, 1_000_000));
    }

    [Fact]
    public void Bottom_to_top_orders_by_the_centre_with_ties_left_first_then_marking_order()
    {
        var top = H(0.5, 0.2);
        var bottomRight = H(0.7, 0.8);
        var bottomLeft = H(0.3, 0.8);
        var same = H(0.3, 0.8);                                     // the same centre as bottomLeft, marked later
        var holds = ImmutableArray.Create(top, bottomRight, bottomLeft, same);

        // bottomLeft, then same (level and left: the marking order), bottomRight, top.
        Assert.Equal(new[] { 3, 2, 0, 1 }, HoldAnimationRule.Ranks(holds, HoldAnimationOrder.BottomToTop).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, HoldAnimationRule.Ranks(holds, HoldAnimationOrder.MarkingOrder).ToArray());
    }

    [Fact]
    public void The_centre_is_the_area_centroid_so_dense_vertices_on_one_side_do_not_move_it()
    {
        // A square with many vertices along its bottom edge: the vertex mean sinks, the centroid stays in the middle.
        var dense = ImmutableArray.CreateBuilder<NormalizedPoint>();
        dense.Add(new NormalizedPoint(0.4, 0.4));
        dense.Add(new NormalizedPoint(0.6, 0.4));
        dense.Add(new NormalizedPoint(0.6, 0.6));
        for (var i = 1; i < 50; i++) dense.Add(new NormalizedPoint(0.6 - i * 0.2 / 50, 0.6));
        dense.Add(new NormalizedPoint(0.4, 0.6));
        var c = HoldAnimationRule.Centre(dense.ToImmutable());
        Assert.Equal(0.5, c.X, 9);
        Assert.Equal(0.5, c.Y, 9);

        var flat = ImmutableArray.Create(new NormalizedPoint(0.1, 0.5), new NormalizedPoint(0.3, 0.5), new NormalizedPoint(0.2, 0.5));
        var mean = HoldAnimationRule.Centre(flat);                                      // no area: the mean of the vertices
        Assert.Equal(0.2, mean.X, 12);
        Assert.Equal(0.5, mean.Y, 12);
    }

    [Fact]
    public void A_corrected_contour_whose_centre_moved_changes_the_bottom_to_top_order()
    {
        var a = H(0.5, 0.7);
        var b = H(0.5, 0.6);
        Assert.Equal(new[] { 0, 1 }, HoldAnimationRule.Ranks(ImmutableArray.Create(a, b), HoldAnimationOrder.BottomToTop).ToArray());

        // b's contour corrected downwards (D035 keeps the id and the samples): its centre is now below a's.
        var corrected = new Hold(b.Id, Square(0.5, 0.75), b.SurfaceLab, b.WallLab, b.SourceTime);
        Assert.Equal(new[] { 1, 0 }, HoldAnimationRule.Ranks(ImmutableArray.Create(a, corrected), HoldAnimationOrder.BottomToTop).ToArray());
        Assert.Equal(new[] { 0, 1 }, HoldAnimationRule.Ranks(ImmutableArray.Create(a, corrected), HoldAnimationOrder.MarkingOrder).ToArray());
    }

    [Fact]
    public void The_limits_of_the_animation_are_validated()
    {
        Assert.Null(HoldMarkupRules.ValidateAnimation(new HoldAnimation()));
        Assert.NotNull(HoldMarkupRules.ValidateAnimation(new HoldAnimation { Interval = MediaTime.FromSeconds(0.04) }));
        Assert.Null(HoldMarkupRules.ValidateAnimation(new HoldAnimation { Interval = MediaTime.FromSeconds(0.05) }));
        Assert.NotNull(HoldMarkupRules.ValidateAnimation(new HoldAnimation { Interval = MediaTime.FromSeconds(10.001) }));
        Assert.NotNull(HoldMarkupRules.ValidateAnimation(new HoldAnimation { AppearFade = new MediaTime(-1) }));
        Assert.NotNull(HoldMarkupRules.ValidateAnimation(new HoldAnimation { DisappearFade = MediaTime.FromSeconds(5.01) }));
        Assert.NotNull(HoldMarkupRules.ValidateAnimation(new HoldAnimation { StayAfterLast = MediaTime.FromSeconds(61) }));
        Assert.NotNull(HoldMarkupRules.ValidateAnimation(new HoldAnimation { StartSourceTime = new MediaTime(-1) }));
        Assert.NotNull(HoldMarkupRules.ValidateAnimation(new HoldAnimation { Order = (HoldAnimationOrder)7 }));
        Assert.NotNull(HoldMarkupRules.Validate(new HoldMarkup { Animation = new HoldAnimation { Interval = MediaTime.Zero } }));
    }
}
