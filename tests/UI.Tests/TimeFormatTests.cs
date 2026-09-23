using AiVideoEditor.Core.Common;
using AiVideoEditor.UI.Common;
using Xunit;

namespace AiVideoEditor.UI.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData(24, 1, 24)]
    [InlineData(25, 1, 25)]
    [InlineData(30, 1, 30)]
    [InlineData(24000, 1001, 24)]
    [InlineData(30000, 1001, 30)]
    [InlineData(60000, 1001, 60)]
    public void NominalFps_RoundsToIntegerTimecodeRate(int num, int den, long expected)
    {
        Assert.Equal(expected, TimeFormat.NominalFps(new FrameRate(num, den)));
    }

    [Fact]
    public void Timecode_CountsFramesOnTheExactGrid()
    {
        var rate = FrameRate.Fps25;
        Assert.Equal("00:00:00:00", TimeFormat.ToTimecode(MediaTime.Zero, rate));
        Assert.Equal("00:00:01:05", TimeFormat.ToTimecode(MediaTime.FromFrame(30, rate), rate));
        Assert.Equal("01:00:00:00", TimeFormat.ToTimecode(MediaTime.FromFrame(25 * 3600, rate), rate));
    }

    [Fact]
    public void Timecode_AtNtsc_IsNonDropFrame()
    {
        var rate = FrameRate.Ntsc30;
        // Frame 30 is "one timecode second" even though it is 1.001 s of real time.
        Assert.Equal("00:00:01:00", TimeFormat.ToTimecode(MediaTime.FromFrame(30, rate), rate));
        Assert.Equal("00:00:00:29", TimeFormat.ToTimecode(MediaTime.FromFrame(29, rate), rate));
        // One tick before frame 30 still shows frame 29.
        Assert.Equal("00:00:00:29", TimeFormat.ToTimecode(new MediaTime(MediaTime.FromFrame(30, rate).Ticks - 1), rate));
    }
}
