using AiVideoEditor.Core.Common;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// Formats <see cref="MediaTime"/> for display. Kept separate from the domain
/// type itself (whose own ToString() is a precise hh:mm:ss.fff form) because
/// different panels want different granularity.
/// </summary>
public static class TimeFormat
{
    /// <summary>"m:ss" — used by the timeline ruler and the Media Browser.</summary>
    public static string ToShortString(MediaTime time)
    {
        var span = time.ToTimeSpan();
        var totalMinutes = (int)span.TotalMinutes;
        return $"{totalMinutes}:{span.Seconds:D2}";
    }

    /// <summary>
    /// Non-drop-frame timecode "HH:MM:SS:FF" at the given frame rate. The frame index
    /// comes from the exact frame grid; it is then counted in whole "timecode seconds"
    /// of the nominal integer rate (30 for 29.97, 24 for 23.976, 60 for 59.94). For
    /// NTSC rates this is standard NDF behaviour: timecode runs 0.1% slower than the
    /// wall clock (1 real hour ≈ 00:59:56:12 at 29.97).
    /// </summary>
    public static string ToTimecode(MediaTime time, FrameRate rate)
    {
        var frame = Math.Max(0, time.ToFrameFloor(rate));
        var nominal = NominalFps(rate);
        var frames = frame % nominal;
        var totalSeconds = frame / nominal;
        return $"{totalSeconds / 3600:D2}:{totalSeconds / 60 % 60:D2}:{totalSeconds % 60:D2}:{frames:D2}";
    }

    /// <summary>Integer frames-per-second used for timecode counting: round(num / den).</summary>
    public static long NominalFps(FrameRate rate) => Math.Max(1, ((long)rate.Numerator + rate.Denominator / 2) / rate.Denominator);
}
