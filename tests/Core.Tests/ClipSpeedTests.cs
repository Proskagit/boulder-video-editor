using System.Numerics;
using AiVideoEditor.Core.Common;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>D022: the speed value (exact multiples of 0.05 from 0.25× to 4×) and the timing rule
/// SourceLength(N) = ⌊N·s·10⁷/R⌋, FramesFor = its inverse.</summary>
public class ClipSpeedTests
{
    public static readonly TheoryData<decimal, int, int> Speeds = new()
    {
        { 0.25m, 1, 4 }, { 0.50m, 1, 2 }, { 0.75m, 3, 4 }, { 0.95m, 19, 20 }, { 1.00m, 1, 1 },
        { 1.05m, 21, 20 }, { 1.35m, 27, 20 }, { 2.00m, 2, 1 }, { 4.00m, 4, 1 }
    };

    [Theory]
    [MemberData(nameof(Speeds))]
    public void Multiples_of_0_05_in_range_are_exact_reduced_fractions(decimal value, int numerator, int denominator)
    {
        Assert.True(ClipSpeed.TryFromDecimal(value, out var speed));
        Assert.Equal((numerator, denominator), (speed.Numerator, speed.Denominator));
        Assert.Equal(value, speed.ToDecimal());
        Assert.True(ClipSpeed.TryFromRatio(numerator * 3L, denominator * 3L, out var fromRatio));
        Assert.Equal(speed, fromRatio);
        Assert.Equal(value == 1m, speed.IsNormal);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.24)]
    [InlineData(0.33)]
    [InlineData(1.001)]
    [InlineData(4.05)]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Other_values_are_not_a_speed(double value)
    {
        Assert.False(ClipSpeed.TryFromDecimal((decimal)value, out var speed));
        Assert.Equal(ClipSpeed.Normal, speed);
    }

    [Fact]
    public void Ratios_that_are_not_on_the_grid_or_not_positive_are_rejected()
    {
        Assert.False(ClipSpeed.TryFromRatio(1, 3, out _));
        Assert.False(ClipSpeed.TryFromRatio(9, 2, out _));
        Assert.False(ClipSpeed.TryFromRatio(0, 1, out _));
        Assert.False(ClipSpeed.TryFromRatio(1, 0, out _));
        Assert.False(ClipSpeed.TryFromRatio(-1, -2, out _));
    }

    [Fact]
    public void Default_is_normal_speed()
    {
        Assert.Equal(ClipSpeed.Normal, default(ClipSpeed));
        Assert.Equal(20, default(ClipSpeed).Steps);
        Assert.Equal((1, 1), (default(ClipSpeed).Numerator, default(ClipSpeed).Denominator));
        Assert.Equal("1.35×", Speed(1.35m).ToString());
    }

    // --- SourceLength / FramesFor -------------------------------------------------------------------

    private static ClipSpeed Speed(decimal value) => ClipSpeed.TryFromDecimal(value, out var s) ? s : throw new ArgumentException();

    private static readonly FrameRate[] Rates =
    {
        FrameRate.Fps24, FrameRate.Fps25, FrameRate.Fps30, FrameRate.Fps60, FrameRate.Ntsc24, FrameRate.Ntsc30, FrameRate.Ntsc60
    };

    /// <summary>Reference: ⌊N · k/20 · 10⁷ · den / num⌋ in BigInteger.</summary>
    private static long RefLength(long frames, ClipSpeed speed, FrameRate rate) =>
        (long)BigInteger.Divide((BigInteger)frames * speed.Steps * 10_000_000 * rate.Denominator, (BigInteger)20 * rate.Numerator);

    [Fact]
    public void SourceLength_is_the_floor_of_the_exact_value_for_every_speed_and_rate()
    {
        foreach (var rate in Rates)
            for (var k = ClipSpeed.MinSteps; k <= ClipSpeed.MaxSteps; k++)
            {
                Assert.True(ClipSpeed.TryFromSteps(k, out var speed));
                foreach (var frames in new long[] { 0, 1, 2, 3, 7, 29, 30, 1001, 30_000, 7_654_321 })
                    Assert.Equal(RefLength(frames, speed, rate), SpeedTiming.SourceLength(frames, speed, rate).Ticks);
            }
    }

    [Fact]
    public void FramesFor_is_the_largest_frame_count_that_fits()
    {
        foreach (var rate in Rates)
            foreach (var k in new[] { 5, 10, 19, 20, 21, 27, 40, 80 })
            {
                Assert.True(ClipSpeed.TryFromSteps(k, out var speed));
                foreach (var ticks in new long[] { 0, 1, 99_999, 333_333, 333_334, 400_000, 10_000_000, 60_000_000, 123_456_789 })
                {
                    var length = new MediaTime(ticks);
                    var n = SpeedTiming.FramesFor(length, speed, rate);
                    Assert.True(RefLength(n, speed, rate) <= ticks, $"{rate} {speed} {ticks}: {n} frames too long");
                    Assert.True(RefLength(n + 1, speed, rate) > ticks, $"{rate} {speed} {ticks}: {n + 1} frames would fit");
                }
                // A length produced by SourceLength maps back to exactly that frame count.
                foreach (var frames in new long[] { 1, 2, 111, 150, 1001 })
                    Assert.Equal(frames, SpeedTiming.FramesFor(SpeedTiming.SourceLength(frames, speed, rate), speed, rate));
            }
        Assert.Equal(0, SpeedTiming.FramesFor(new MediaTime(-5), ClipSpeed.Max, FrameRate.Fps25));
    }

    [Fact]
    public void Worked_example_at_25_fps()
    {
        // 6.0 s of source at 1.35× → ⌊60 000 000 / 540 000⌋ = 111 frames (4.44 s); 112 would need 60 480 000.
        var speed = Speed(1.35m);
        Assert.Equal(540_000, SpeedTiming.SourceLength(1, speed, FrameRate.Fps25).Ticks);
        Assert.Equal(111, SpeedTiming.FramesFor(new MediaTime(60_000_000), speed, FrameRate.Fps25));
        Assert.True(SpeedTiming.Fits(111, new MediaTime(60_000_000), speed, FrameRate.Fps25));
        Assert.False(SpeedTiming.Fits(110, new MediaTime(60_000_000), speed, FrameRate.Fps25));
        Assert.False(SpeedTiming.Fits(0, MediaTime.Zero, speed, FrameRate.Fps25));
        // 1× at 25 fps is exact: 150 frames.
        Assert.Equal(150, SpeedTiming.FramesFor(new MediaTime(60_000_000), ClipSpeed.Normal, FrameRate.Fps25));
    }

    [Fact]
    public void Existing_1x_clips_satisfy_the_same_invariant_at_every_rate()
    {
        // A 1× clip's source length is its grid duration FromFrame(E) − FromFrame(S) (tick-rounded, D006).
        foreach (var rate in Rates)
            foreach (var start in new long[] { 0, 1, 2, 7, 1000, 12_345 })
                foreach (var frames in new long[] { 1, 2, 3, 29, 30, 1001 })
                {
                    var length = MediaTime.FromFrame(start + frames, rate) - MediaTime.FromFrame(start, rate);
                    Assert.True(SpeedTiming.Fits(frames, length, ClipSpeed.Normal, rate), $"{rate} {start}+{frames}");
                }
    }
}
