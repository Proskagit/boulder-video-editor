using AiVideoEditor.Core.Common;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public class MediaTimeTests
{
    [Fact]
    public void FromSeconds_And_TotalSeconds_RoundTrip()
    {
        var t = MediaTime.FromSeconds(12.5);
        Assert.Equal(12.5, t.TotalSeconds, precision: 6);
    }

    [Fact]
    public void Addition_And_Comparison_Operators_Work()
    {
        var a = MediaTime.FromSeconds(1);
        var b = MediaTime.FromSeconds(2);
        Assert.True(a < b);
        Assert.Equal(MediaTime.FromSeconds(3), a + b);
    }

    [Theory]
    [InlineData(24000, 1001)]
    [InlineData(30000, 1001)]
    [InlineData(25, 1)]
    public void ToFrameCeiling_is_the_first_frame_starting_at_or_after_the_time(int num, int den)
    {
        var rate = new FrameRate(num, den);
        var random = new Random(num);
        for (var i = 0; i < 2_000; i++)
        {
            var time = new MediaTime(random.NextInt64(0, 36_000_000_000));
            var ceiling = time.ToFrameCeiling(rate);
            Assert.True(MediaTime.FromFrame(ceiling, rate) >= time);
            Assert.True(ceiling == 0 || MediaTime.FromFrame(ceiling - 1, rate) < time);
        }
        foreach (var n in new long[] { 0, 1, 1001, 107_892 })
        {
            var boundary = MediaTime.FromFrame(n, rate);
            Assert.Equal(n, boundary.ToFrameCeiling(rate));
            Assert.Equal(n + 1, (boundary + new MediaTime(1)).ToFrameCeiling(rate));
        }
    }
}
