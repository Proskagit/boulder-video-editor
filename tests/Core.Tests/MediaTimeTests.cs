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
}
