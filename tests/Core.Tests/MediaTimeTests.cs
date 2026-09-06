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

    [Theory]
    [InlineData(0, 30, 0)]
    [InlineData(30, 30, 1)]
    [InlineData(29, 30, 0)] // 29/30s should floor to frame 0
    [InlineData(60, 30, 2)]
    public void ToFrame_MatchesExpectedFrameIndex(long milliseconds, double fps, long expectedFrame)
    {
        var t = MediaTime.FromSeconds(milliseconds / 1000.0);
        Assert.Equal(expectedFrame, t.ToFrame(fps));
    }

    [Fact]
    public void FromFrame_ThenToFrame_IsStable_AcrossDifferentFrameRates()
    {
        var t = MediaTime.FromFrame(10, 24);
        Assert.Equal(10, t.ToFrame(24));
    }

    [Fact]
    public void Addition_And_Comparison_Operators_Work()
    {
        var a = MediaTime.FromSeconds(1);
        var b = MediaTime.FromSeconds(2);
        Assert.True(a < b);
        Assert.Equal(MediaTime.FromSeconds(3), a + b);
    }
}
