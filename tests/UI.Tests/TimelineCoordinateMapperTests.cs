using AiVideoEditor.Core.Common;
using AiVideoEditor.UI.Common;
using Xunit;

namespace AiVideoEditor.UI.Tests;

public class TimelineCoordinateMapperTests
{
    [Fact]
    public void TimeToX_And_XToTime_AreInverse_AtTickPrecision()
    {
        var time = MediaTime.FromSeconds(12.345);
        var x = TimelineCoordinateMapper.TimeToX(time, 60);
        Assert.Equal(12.345 * 60, x, precision: 6);
        Assert.Equal(time, TimelineCoordinateMapper.XToTime(x, 60));
    }

    [Theory]
    [InlineData(24, 1)]
    [InlineData(30000, 1001)]
    [InlineData(60000, 1001)]
    public void XToFrameTime_AlwaysLandsOnFrameGrid_AndNeverBeforeZero(int num, int den)
    {
        var rate = new FrameRate(num, den);
        foreach (var x in new[] { -50.0, 0, 0.4, 13.37, 999.99, 123456.7 })
        {
            var time = TimelineCoordinateMapper.XToFrameTime(x, 87.5, rate);
            Assert.True(time.IsOnFrameGrid(rate));
            Assert.True(time >= MediaTime.Zero);
        }
    }

    [Fact]
    public void XToFrameTime_PicksNearestFrame()
    {
        var rate = FrameRate.Fps25; // 40 ms frames; at 100 px/s one frame = 4 px
        Assert.Equal(MediaTime.FromFrame(2, rate), TimelineCoordinateMapper.XToFrameTime(9.9, 100, rate));
        Assert.Equal(MediaTime.FromFrame(3, rate), TimelineCoordinateMapper.XToFrameTime(10.1, 100, rate));
    }

    [Fact]
    public void ScrollOffsetForAnchor_KeepsAnchorAtSameViewportPosition()
    {
        var anchor = MediaTime.FromSeconds(10);
        var offset = TimelineCoordinateMapper.ScrollOffsetForAnchor(anchor, 200, 120);
        Assert.Equal(10 * 120 - 200, offset, precision: 6);
        Assert.Equal(0, TimelineCoordinateMapper.ScrollOffsetForAnchor(MediaTime.FromSeconds(1), 500, 120));
    }

    [Fact]
    public void ClampZoom_BoundsByMinimumAndFramesPerPixel()
    {
        Assert.Equal(TimelineCoordinateMapper.MinPixelsPerSecond, TimelineCoordinateMapper.ClampZoom(0.1, FrameRate.Fps30));
        Assert.Equal(30 * TimelineCoordinateMapper.MaxPixelsPerFrame, TimelineCoordinateMapper.ClampZoom(1e9, FrameRate.Fps30));
        Assert.Equal(100, TimelineCoordinateMapper.ClampZoom(100, FrameRate.Fps30));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(60)]
    [InlineData(1200)]
    public void RulerInterval_KeepsLabelsApart(double pixelsPerSecond)
    {
        var interval = TimelineCoordinateMapper.RulerIntervalTicks(pixelsPerSecond);
        var spacing = interval * pixelsPerSecond / TimeSpan.TicksPerSecond;
        Assert.True(spacing >= TimelineCoordinateMapper.MinRulerSpacingPixels);
    }
}
