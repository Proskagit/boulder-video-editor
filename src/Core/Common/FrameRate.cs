using System.Globalization;

namespace AiVideoEditor.Core.Common;

/// <summary>
/// An exact rational frame rate: <see cref="Numerator"/> frames every
/// <see cref="Denominator"/> seconds (e.g. 30000/1001 for NTSC "29.97").
/// Always stored reduced, so equal rates compare equal. All frame-index ↔ time
/// conversions go through <see cref="MediaTime.FromFrame(long, FrameRate)"/> and
/// friends, which use integer arithmetic only — never <see cref="ToDouble"/>.
/// </summary>
public readonly struct FrameRate : IEquatable<FrameRate>
{
    public static readonly FrameRate Fps24 = new(24, 1);
    public static readonly FrameRate Fps25 = new(25, 1);
    public static readonly FrameRate Fps30 = new(30, 1);
    public static readonly FrameRate Fps60 = new(60, 1);
    public static readonly FrameRate Ntsc24 = new(24000, 1001);
    public static readonly FrameRate Ntsc30 = new(30000, 1001);
    public static readonly FrameRate Ntsc60 = new(60000, 1001);

    /// <summary>Project fallback when no video has determined the frame rate yet.</summary>
    public static FrameRate Default => Fps30;

    public int Numerator { get; }
    public int Denominator { get; }

    public FrameRate(int numerator, int denominator)
    {
        if (numerator <= 0) throw new ArgumentOutOfRangeException(nameof(numerator), "Frame rate numerator must be positive.");
        if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator), "Frame rate denominator must be positive.");

        var gcd = Gcd(numerator, denominator);
        Numerator = numerator / gcd;
        Denominator = denominator / gcd;
    }

    /// <summary>False for <c>default(FrameRate)</c>, which has no meaningful value.</summary>
    public bool IsValid => Numerator > 0 && Denominator > 0;

    /// <summary>Approximate frames per second, for display only. Never use this for
    /// time or frame arithmetic.</summary>
    public double ToDouble() => (double)Numerator / Denominator;

    /// <summary>Parses "30000/1001" or "25" (the forms ffprobe reports). Returns false
    /// for anything else, including ffprobe's "0/0" for unknown rates and decimal
    /// strings like "29.97", which cannot be represented exactly.</summary>
    public static bool TryParse(string? text, out FrameRate rate)
    {
        rate = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Trim().Split('/');
        if (parts.Length is not (1 or 2))
            return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator))
            return false;

        var denominator = 1;
        if (parts.Length == 2 &&
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out denominator))
            return false;

        if (numerator <= 0 || denominator <= 0)
            return false;

        rate = new FrameRate(numerator, denominator);
        return true;
    }

    public bool Equals(FrameRate other) => Numerator == other.Numerator && Denominator == other.Denominator;
    public override bool Equals(object? obj) => obj is FrameRate other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
    public static bool operator ==(FrameRate a, FrameRate b) => a.Equals(b);
    public static bool operator !=(FrameRate a, FrameRate b) => !a.Equals(b);

    public override string ToString() => Denominator == 1
        ? Numerator.ToString(CultureInfo.InvariantCulture)
        : $"{Numerator.ToString(CultureInfo.InvariantCulture)}/{Denominator.ToString(CultureInfo.InvariantCulture)}";

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
