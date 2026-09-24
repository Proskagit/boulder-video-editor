using AiVideoEditor.Core.Common;
using Xunit;

namespace AiVideoEditor.Video.Tests;

public class ShowInfoParserTests
{
    // Lines as printed by ffmpeg 9.0.1 (trimmed after the fields we read).
    private const string Config = "[Parsed_showinfo_2 @ 000001de6fd66e00] config in time_base: 1/90000, frame_rate: 25/1";
    private const string ConfigOut = "[Parsed_showinfo_2 @ 000001de6fd66e00] config out time_base: 0/0, frame_rate: 0/0";
    private const string Frame0 = "[Parsed_showinfo_2 @ 000001de6fd66e00] n:   0 pts: 824400 pts_time:9.16    duration:   3600 duration_time:0.04    fmt:bgra cl:unspecified sar:1/1 s:256x144 i:P iskey:1 type:I";

    [Fact]
    public void ParsesFrameWithTheTimeBaseInForce()
    {
        var parser = new ShowInfoParser();
        Assert.Null(parser.Parse(Config));
        Assert.Null(parser.Parse(ConfigOut)); // "config out" must not replace the input time base

        var frame = parser.Parse(Frame0);

        Assert.Equal(new ShowInfoFrame(0, 824400, new TimeBase(1, 90000), 256, 144), frame);
    }

    [Fact]
    public void ParsesNegativeAndMissingPts()
    {
        var parser = new ShowInfoParser();
        parser.Parse(Config);

        Assert.Equal(-1800, parser.Parse(Frame0.Replace("pts: 824400", "pts: -1800"))!.Value.Pts);
        Assert.Null(parser.Parse(Frame0.Replace("pts: 824400", "pts:NOPTS"))!.Value.Pts);
    }

    [Fact]
    public void IgnoresOtherLines_AndRejectsFramesBeforeTheTimeBase()
    {
        var parser = new ShowInfoParser();
        Assert.Null(parser.Parse("Input #0, mpegts, from 'b25.ts':"));
        Assert.Null(parser.Parse("[Parsed_showinfo_2 @ 000001de6fd66e00]   side data - ..."));
        Assert.Throws<FormatException>(() => parser.Parse(Frame0));
    }

    [Fact]
    public void ConfigLineGluedToAPrecedingMessage_StillSetsTheTimeBase()
    {
        // ffmpeg 9 with a 45° display matrix: its warning has no trailing newline, so showinfo's
        // config line loses its own "[Parsed_showinfo_…]" prefix.
        var parser = new ShowInfoParser();
        Assert.Null(parser.Parse("If you want to help, upload a sample of this file to https://streams.videolan.org/upload/ " +
            "and contact the ffmpeg-devel mailing list. (ffmpeg-devel@ffmpeg.org)config in time_base: 1/12800, frame_rate: 25/1"));

        var frame = parser.Parse("[Parsed_showinfo_2 @ 000001619e633000] n:   0 pts:      0 pts_time:0       duration:    512 " +
            "duration_time:0.04    fmt:bgra cl:unspecified sar:1/1 s:320x180 i:P iskey:1 type:I ");

        Assert.Equal(new TimeBase(1, 12800), frame!.Value.TimeBase);
        Assert.Equal((320, 180), (frame.Value.Width, frame.Value.Height));
    }

    [Fact]
    public void ConfigOutAnywhere_IsNotATimeBase()
    {
        var parser = new ShowInfoParser();
        Assert.Null(parser.Parse("something config out time_base: 0/0, frame_rate: 0/0"));
        Assert.Throws<FormatException>(() => parser.Parse(
            "[Parsed_showinfo_2 @ 0x1] n:   0 pts:      0 pts_time:0 fmt:bgra s:320x180 i:P "));
    }
}
