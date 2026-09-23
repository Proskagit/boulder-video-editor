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
}
