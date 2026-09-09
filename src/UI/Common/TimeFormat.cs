using AiVideoEditor.Core.Common;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// Formats <see cref="MediaTime"/> for display. Kept separate from the domain
/// type itself (whose own ToString() is a precise hh:mm:ss.fff form) because
/// different panels want different granularity — the timeline ruler wants
/// short "m:ss" labels, while a future frame-accurate readout might want more.
/// </summary>
public static class TimeFormat
{
    /// <summary>"m:ss" — used by the Preview transport and the timeline ruler.</summary>
    public static string ToShortString(MediaTime time)
    {
        var span = time.ToTimeSpan();
        var totalMinutes = (int)span.TotalMinutes;
        return $"{totalMinutes}:{span.Seconds:D2}";
    }
}
