using System.Globalization;

namespace AiVideoEditor.Core.Common;

/// <summary>
/// Exact rational length of one timestamp unit of a media stream, in seconds
/// (e.g. 1/90000, 1/12800, 1/1000). Backend-neutral: any decoder reports frame
/// timestamps as integer counts of its stream's time base.
/// </summary>
public readonly struct TimeBase : IEquatable<TimeBase>
{
    public long Numerator { get; }
    public long Denominator { get; }

    public TimeBase(long numerator, long denominator)
    {
        if (numerator <= 0) throw new ArgumentOutOfRangeException(nameof(numerator), "Time base numerator must be positive.");
        if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator), "Time base denominator must be positive.");
        Numerator = numerator;
        Denominator = denominator;
    }

    public bool IsValid => Numerator > 0 && Denominator > 0;

    public bool Equals(TimeBase other) => Numerator == other.Numerator && Denominator == other.Denominator;
    public override bool Equals(object? obj) => obj is TimeBase other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
    public static bool operator ==(TimeBase a, TimeBase b) => a.Equals(b);
    public static bool operator !=(TimeBase a, TimeBase b) => !a.Equals(b);

    public override string ToString() =>
        $"{Numerator.ToString(CultureInfo.InvariantCulture)}/{Denominator.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// A decoded frame's presentation timestamp exactly as the source stream reports it:
/// <c>Pts · TimeBase</c> seconds on the file's own clock (not yet relative to the
/// file's start time). Never converted through floating point.
/// </summary>
public readonly record struct SourceTimestamp(long Pts, TimeBase TimeBase);
