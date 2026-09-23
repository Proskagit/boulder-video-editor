using AiVideoEditor.Core.Common;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public class FrameGridTests
{
    public static TheoryData<int, int> Rates => new()
    {
        { 24, 1 },
        { 25, 1 },
        { 30, 1 },
        { 24000, 1001 },
        { 30000, 1001 },
        { 60000, 1001 },
    };

    [Theory]
    [MemberData(nameof(Rates))]
    public void FrameToTicksToFrame_RoundTripsExactly(int numerator, int denominator)
    {
        var rate = new FrameRate(numerator, denominator);
        for (long n = 0; n <= 200_000; n++)
        {
            var t = MediaTime.FromFrame(n, rate);
            Assert.Equal(n, t.ToFrameFloor(rate));
            Assert.Equal(n, t.ToNearestFrame(rate));
            Assert.True(t.IsOnFrameGrid(rate));
        }
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void FrameStarts_AreStrictlyIncreasing_AndWithinHalfTickOfExactValue(int numerator, int denominator)
    {
        var rate = new FrameRate(numerator, denominator);
        var previous = MediaTime.FromFrame(0, rate);
        for (long n = 1; n <= 10_000; n++)
        {
            var t = MediaTime.FromFrame(n, rate);
            Assert.True(t > previous);

            // |ticks − exact| ≤ ½ tick  ⇔  |2·ticks·num − 2·n·10⁷·den| ≤ num
            var exactTimesNum2 = (Int128)2 * n * TimeSpan.TicksPerSecond * denominator;
            var diff = (Int128)2 * t.Ticks * numerator - exactTimesNum2;
            Assert.True(Int128.Abs(diff) <= numerator);
            previous = t;
        }
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void TimesBetweenFrames_FloorToContainingFrame(int numerator, int denominator)
    {
        var rate = new FrameRate(numerator, denominator);
        for (long n = 0; n < 5_000; n++)
        {
            var start = MediaTime.FromFrame(n, rate);
            var next = MediaTime.FromFrame(n + 1, rate);
            Assert.Equal(n, new MediaTime(next.Ticks - 1).ToFrameFloor(rate));
            Assert.Equal(n, new MediaTime(start.Ticks + 1).ToFrameFloor(rate));
            Assert.False(new MediaTime(start.Ticks + 1).IsOnFrameGrid(rate));
        }
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void SnapToFrame_PicksNearestBoundary(int numerator, int denominator)
    {
        var rate = new FrameRate(numerator, denominator);
        var a = MediaTime.FromFrame(100, rate);
        var b = MediaTime.FromFrame(101, rate);
        var justAfterA = new MediaTime(a.Ticks + (b.Ticks - a.Ticks) / 2 - 1);
        var justBeforeB = new MediaTime(b.Ticks - (b.Ticks - a.Ticks) / 2 + 1);

        Assert.Equal(a, justAfterA.SnapToFrame(rate));
        Assert.Equal(b, justBeforeB.SnapToFrame(rate));
    }

    [Fact]
    public void KnownValues_AreExact()
    {
        Assert.Equal(10_000_000, MediaTime.FromFrame(30, FrameRate.Fps30).Ticks);
        Assert.Equal(400_000, MediaTime.FromFrame(1, FrameRate.Fps25).Ticks);
        Assert.Equal(416_667, MediaTime.FromFrame(1, FrameRate.Fps24).Ticks);   // 416 666.67
        Assert.Equal(333_667, MediaTime.FromFrame(1, FrameRate.Ntsc30).Ticks);  // 333 666.67
        Assert.Equal(10_010_000, MediaTime.FromFrame(30, FrameRate.Ntsc30).Ticks); // exactly 1.001 s
        Assert.Equal(417_083, MediaTime.FromFrame(1, FrameRate.Ntsc24).Ticks);  // 417 083.33
        Assert.Equal(166_833, MediaTime.FromFrame(1, FrameRate.Ntsc60).Ticks);  // 166 833.33
    }

    [Fact]
    public void FrameSpanLength_CanVaryByOneTick_DependingOnStartFrame()
    {
        // Documents why edits must compute both edges from frame indices.
        var rate = FrameRate.Ntsc30;
        var lengths = Enumerable.Range(0, 3)
            .Select(start => MediaTime.FromFrame(start + 1, rate).Ticks - MediaTime.FromFrame(start, rate).Ticks)
            .Distinct()
            .ToList();
        Assert.Equal(2, lengths.Count);
        Assert.True(Math.Abs(lengths[0] - lengths[1]) == 1);
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void TenHourTimeline_ConvertsWithoutOverflow(int numerator, int denominator)
    {
        var rate = new FrameRate(numerator, denominator);
        var tenHours = MediaTime.FromTimeSpan(TimeSpan.FromHours(10));
        var frame = tenHours.ToFrameFloor(rate);
        Assert.True(MediaTime.FromFrame(frame, rate) <= tenHours);
        Assert.True(MediaTime.FromFrame(frame + 1, rate) > tenHours);
        Assert.Equal(frame, MediaTime.FromFrame(frame, rate).ToFrameFloor(rate));
    }
}
