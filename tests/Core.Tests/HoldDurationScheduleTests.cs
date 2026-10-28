using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// D048 §3.2 / §3.3: the Duration-mode schedule frame by frame — the appear phase <c>P = ⌊(8F + 5) / 10⌋</c>, the first hold
/// at <c>n0</c>, the spacing within one frame, every ramp finished by <c>n0 + P</c>, all at full opacity on
/// <c>[n0 + P, nE)</c>, none at <c>nE</c> (no fade after <c>T</c>) — for short and long animations and many holds; the
/// orders left to right and right to left.
/// </summary>
public class HoldDurationScheduleTests
{
    private static readonly FrameRate Rate = FrameRate.Ntsc30;

    private static ImmutableArray<NormalizedPoint> Square(double x, double y, double r = 0.01) => ImmutableArray.Create(
        new NormalizedPoint(x - r, y - r), new NormalizedPoint(x + r, y - r), new NormalizedPoint(x + r, y + r), new NormalizedPoint(x - r, y + r));

    private static ImmutableArray<Hold> Holds(int count) => Enumerable.Range(0, count)
        .Select(i => new Hold(Guid.NewGuid(), Square(0.1 + 0.8 * i / Math.Max(1, count), 0.9 - 0.8 * i / Math.Max(1, count)), default, default, MediaTime.Zero))
        .ToImmutableArray();

    private static HoldAnimation Animation(long frames) => new()
    {
        Mode = HoldAnimationMode.Duration, Duration = MediaTime.FromFrame(frames, Rate)
    };

    [Theory]
    [InlineData(150, 120)]
    [InlineData(60, 48)]
    [InlineData(4, 3)]
    [InlineData(3, 2)]
    [InlineData(2, 2)]
    [InlineData(1, 1)]
    public void The_appear_phase_is_80_percent_rounded_half_up(long frames, long appear) =>
        Assert.Equal(appear, HoldAnimationRule.AppearFrames(frames));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 20)]
    [InlineData(60, 3)]
    [InlineData(150, 1)]
    [InlineData(150, 20)]
    [InlineData(150, 100)]
    public void Every_hold_appears_in_the_first_80_percent_and_all_vanish_exactly_at_T(long frames, int count)
    {
        const long n0 = 1000;
        var holds = Holds(count);
        var schedule = HoldAnimationRule.Schedule(Animation(frames), holds, n0, Rate);
        var p = HoldAnimationRule.AppearFrames(frames);
        var end = n0 + frames;

        Assert.Equal(n0, schedule.AppearFrames.Min());                     // the first hold at n0
        var starts = schedule.AppearFrames.Order().ToArray();
        for (var r = 1; r < starts.Length; r++)
            Assert.InRange(starts[r] - starts[r - 1], (long)Math.Floor((double)p / count), (long)Math.Ceiling((double)p / count));
        Assert.True(schedule.AllVisibleFrame <= n0 + p);                    // every ramp finished before the final 20 %
        for (var frame = n0 + p; frame < end; frame++)
            Assert.All(schedule.OpacitiesAt(frame), o => Assert.Equal(1, o));
        Assert.All(schedule.OpacitiesAt(end), o => Assert.Equal(0, o));    // none at T, no fade
        Assert.All(schedule.OpacitiesAt(n0 - 1), o => Assert.Equal(0, o));
        Assert.Equal(end, schedule.EndFrame);
    }

    [Fact]
    public void Five_seconds_are_four_seconds_of_appearing_and_one_of_the_whole_route()
    {
        var schedule = HoldAnimationRule.Schedule(Animation(150), Holds(5), 0, Rate);
        Assert.True(schedule.AllVisibleFrame <= 120);
        Assert.True(schedule.OpacitiesAt(119).Any(o => o < 1) || schedule.AllVisibleFrame <= 119);
        Assert.Equal(150, schedule.EndFrame);
    }

    [Fact]
    public void The_duration_does_not_change_with_the_number_of_holds()
    {
        Assert.Equal(HoldAnimationRule.Schedule(Animation(150), Holds(3), 0, Rate).EndFrame,
            HoldAnimationRule.Schedule(Animation(150), Holds(30), 0, Rate).EndFrame);
    }

    [Fact]
    public void Left_to_right_and_right_to_left_rank_by_the_centres_x_ties_by_the_lower_hold_then_the_marking_order()
    {
        var holds = ImmutableArray.Create(
            new Hold(Guid.NewGuid(), Square(0.7, 0.5), default, default, MediaTime.Zero),
            new Hold(Guid.NewGuid(), Square(0.2, 0.5), default, default, MediaTime.Zero),
            new Hold(Guid.NewGuid(), Square(0.2, 0.3), default, default, MediaTime.Zero));
        Assert.Equal(new[] { 2, 0, 1 }, HoldAnimationRule.Ranks(holds, HoldAnimationOrder.LeftToRight).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, HoldAnimationRule.Ranks(holds, HoldAnimationOrder.RightToLeft).ToArray());
    }

    [Fact]
    public void A_duration_mode_without_a_positive_duration_or_with_an_unknown_mode_is_invalid()
    {
        Assert.NotNull(HoldMarkupRules.ValidateAnimation(new HoldAnimation { Mode = HoldAnimationMode.Duration }));
        Assert.NotNull(HoldMarkupRules.ValidateAnimation(new HoldAnimation { Mode = (HoldAnimationMode)7, Duration = MediaTime.FromSeconds(5) }));
        Assert.Null(HoldMarkupRules.ValidateAnimation(Animation(150)));
        Assert.Null(HoldMarkupRules.ValidateAnimation(new HoldAnimation()));   // the interval mode needs no duration
    }
}
