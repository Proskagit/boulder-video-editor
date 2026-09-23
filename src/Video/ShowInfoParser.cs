using System.Globalization;
using System.Text.RegularExpressions;
using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Video;

/// <summary>One frame as reported by ffmpeg's <c>showinfo</c> filter: its PTS (null for
/// NOPTS), the time base in force for it, and the frame size written to stdout.</summary>
internal readonly record struct ShowInfoFrame(long Index, long? Pts, TimeBase TimeBase, int Width, int Height);

/// <summary>
/// Parses ffmpeg stderr lines produced by <c>showinfo</c>. Internal to the FFmpeg CLI
/// adapter — the format of these lines never leaves this assembly.
/// <code>
/// [Parsed_showinfo_2 @ 0x..] config in time_base: 1/90000, frame_rate: 25/1
/// [Parsed_showinfo_2 @ 0x..] n:   0 pts: 133200 pts_time:1.48 duration: 3600 ... s:256x144 ...
/// </code>
/// </summary>
internal sealed partial class ShowInfoParser
{
    private TimeBase? _timeBase;

    [GeneratedRegex(@"^\[Parsed_showinfo_\d+ @ [^\]]+\] config in time_base: (\d+)/(\d+)")]
    private static partial Regex ConfigRegex();

    [GeneratedRegex(@"^\[Parsed_showinfo_\d+ @ [^\]]+\] n:\s*(\d+)\s+pts:\s*(-?\d+|NOPTS)\s.*?\ss:(\d+)x(\d+)\s")]
    private static partial Regex FrameRegex();

    /// <summary>Returns the frame described by <paramref name="line"/>, or null for any other line.</summary>
    /// <exception cref="FormatException">A frame line arrived before any time base, or a value is invalid.</exception>
    public ShowInfoFrame? Parse(string line)
    {
        if (!line.StartsWith("[Parsed_showinfo_", StringComparison.Ordinal))
            return null;

        var config = ConfigRegex().Match(line);
        if (config.Success)
        {
            var num = long.Parse(config.Groups[1].Value, CultureInfo.InvariantCulture);
            var den = long.Parse(config.Groups[2].Value, CultureInfo.InvariantCulture);
            if (num <= 0 || den <= 0)
                throw new FormatException($"showinfo reported an invalid time base: {num}/{den}.");
            _timeBase = new TimeBase(num, den);
            return null;
        }

        var frame = FrameRegex().Match(line);
        if (!frame.Success)
            return null;

        if (_timeBase is not { } timeBase)
            throw new FormatException("showinfo reported a frame before its time base.");

        var ptsText = frame.Groups[2].Value;
        long? pts = ptsText == "NOPTS" ? null : long.Parse(ptsText, CultureInfo.InvariantCulture);
        return new ShowInfoFrame(
            long.Parse(frame.Groups[1].Value, CultureInfo.InvariantCulture),
            pts,
            timeBase,
            int.Parse(frame.Groups[3].Value, CultureInfo.InvariantCulture),
            int.Parse(frame.Groups[4].Value, CultureInfo.InvariantCulture));
    }
}
