using AiVideoEditor.Core.Common;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public class FrameRateTests
{
    [Theory]
    [InlineData("24", 24, 1)]
    [InlineData("25", 25, 1)]
    [InlineData("30/1", 30, 1)]
    [InlineData("24000/1001", 24000, 1001)]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("60000/1001", 60000, 1001)]
    [InlineData("60/2", 30, 1)] // reduced
    public void TryParse_Accepts_IntegerAndRationalForms(string text, int numerator, int denominator)
    {
        Assert.True(FrameRate.TryParse(text, out var rate));
        Assert.Equal(numerator, rate.Numerator);
        Assert.Equal(denominator, rate.Denominator);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0/0")]
    [InlineData("30/0")]
    [InlineData("0/1")]
    [InlineData("-30/1")]
    [InlineData("29.97")]
    [InlineData("30/1/1")]
    [InlineData("abc")]
    public void TryParse_Rejects_UnknownOrInexactForms(string? text)
    {
        Assert.False(FrameRate.TryParse(text, out _));
    }

    [Fact]
    public void EqualRates_CompareEqual_AfterReduction()
    {
        Assert.Equal(FrameRate.Fps30, new FrameRate(60, 2));
        Assert.NotEqual(FrameRate.Fps30, FrameRate.Ntsc30);
    }

    [Fact]
    public void Default_IsThirtyFps_AndDefaultStructIsInvalid()
    {
        Assert.Equal(FrameRate.Fps30, FrameRate.Default);
        Assert.False(default(FrameRate).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaTime.FromFrame(1, default));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(30, 0)]
    public void Constructor_Rejects_NonPositiveParts(int numerator, int denominator)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRate(numerator, denominator));
    }
}
