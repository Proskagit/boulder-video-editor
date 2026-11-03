using System.Globalization;
using System.Text.RegularExpressions;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>
/// The one parser of a video's recording-time tag (D049 §3): invariant culture, no assumed zone. Only an ISO-8601 date-time
/// with a <c>T</c> and with <c>Z</c> or a numeric offset (<c>±hh:mm</c> / <c>±hhmm</c>) is read, as the instant it states;
/// no designator, another layout or a value before 2000-01-01 (the container epochs 1904 / 1970) is «unrecognised» —
/// never read as UTC. A designator is only the file's <em>claim</em> that the value is camera time C. The raw text is
/// never replaced by the parsed instant (RT1).
/// </summary>
public static partial class RecordingTimeTag
{
    /// <summary>The earliest instant read as a real recording time.</summary>
    public static readonly DateTimeOffset Earliest = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?(Z|[+-]\d{2}:?\d{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Iso8601();

    /// <summary>The instant the tag states (offset 0), or null when it is unrecognised. Fractions beyond the 100 ns tick
    /// are cut off, never rounded.</summary>
    public static DateTimeOffset? Parse(string? tag)
    {
        if (tag is null || Iso8601().Match(tag) is not { Success: true } m) return null;
        try
        {
            var offset = m.Groups[8].Value is "Z" ? TimeSpan.Zero : Offset(m.Groups[8].Value);
            var fraction = m.Groups[7].Success ? long.Parse(m.Groups[7].Value.PadRight(7, '0')[..7], CultureInfo.InvariantCulture) : 0;
            var local = new DateTime(Int(m, 1), Int(m, 2), Int(m, 3), Int(m, 4), Int(m, 5), Int(m, 6), DateTimeKind.Unspecified)
                .AddTicks(fraction);
            var instant = new DateTimeOffset(local, offset).ToUniversalTime();
            return instant >= Earliest ? instant : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;   // a day, hour or offset out of range (e.g. 2026-02-30, 24:00, +15:00)
        }
    }

    private static int Int(Match m, int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    private static TimeSpan Offset(string text)
    {
        var digits = text.Replace(":", "", StringComparison.Ordinal);
        var minutes = int.Parse(digits[1..3], CultureInfo.InvariantCulture) * 60 + int.Parse(digits[3..5], CultureInfo.InvariantCulture);
        return TimeSpan.FromMinutes(digits[0] == '-' ? -minutes : minutes);
    }
}
