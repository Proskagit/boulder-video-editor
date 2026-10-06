using System.Globalization;
using AiVideoEditor.Core.Common;

namespace AiVideoEditor.UI.Common;

/// <summary>A project frame rate as the user reads it: "25", "29.97", "23.976" (display only — never for arithmetic).</summary>
public static class RateFormat
{
    public static string Number(FrameRate rate) => rate.Denominator == 1
        ? rate.Numerator.ToString(CultureInfo.InvariantCulture)
        : Math.Round(rate.ToDouble(), 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>"25 FPS".</summary>
    public static string Fps(FrameRate rate) => $"{Number(rate)} FPS";
}
