using System.Globalization;

namespace AiVideoEditor.Core.Common;

/// <summary>
/// Playback speed of a video or audio clip (D022): an exact multiple of 0.05 from 0.25× to 4×,
/// i.e. <c>k/20</c> with <c>5 ≤ k ≤ 80</c>. Never a floating-point factor; <see cref="Numerator"/> /
/// <see cref="Denominator"/> is the reduced fraction. <c>default</c> is 1× (<see cref="Normal"/>).
/// </summary>
public readonly struct ClipSpeed : IEquatable<ClipSpeed>
{
    /// <summary>Speeds are counted in steps of 1/20 (0.05×).</summary>
    public const int StepsPerUnit = 20;
    public const int MinSteps = 5;   // 0.25×
    public const int MaxSteps = 80;  // 4×

    public static readonly ClipSpeed Normal = default;
    public static readonly ClipSpeed Min = new(MinSteps);
    public static readonly ClipSpeed Max = new(MaxSteps);

    // Stored as the offset from 1× so that default(ClipSpeed) is 1×.
    private readonly int _offset;

    private ClipSpeed(int steps) => _offset = steps - StepsPerUnit;

    /// <summary><c>k</c> in <c>k/20</c>.</summary>
    public int Steps => _offset + StepsPerUnit;

    public int Numerator => Steps / Gcd(Steps, StepsPerUnit);
    public int Denominator => StepsPerUnit / Gcd(Steps, StepsPerUnit);

    public bool IsNormal => _offset == 0;

    /// <summary>The speed of <paramref name="steps"/> twentieths; false outside 5…80.</summary>
    public static bool TryFromSteps(int steps, out ClipSpeed speed)
    {
        speed = steps is >= MinSteps and <= MaxSteps ? new ClipSpeed(steps) : Normal;
        return steps is >= MinSteps and <= MaxSteps;
    }

    /// <summary>The speed of <paramref name="steps"/> twentieths (5…80).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0.25×…4×.</exception>
    public static ClipSpeed FromSteps(int steps) =>
        TryFromSteps(steps, out var speed) ? speed : throw new ArgumentOutOfRangeException(nameof(steps), steps, "Speed must be 5…80 twentieths.");

    /// <summary>The speed equal to <paramref name="numerator"/>/<paramref name="denominator"/>;
    /// false unless it is exactly a multiple of 0.05 in range.</summary>
    public static bool TryFromRatio(long numerator, long denominator, out ClipSpeed speed)
    {
        speed = Normal;
        if (numerator <= 0 || denominator <= 0) return false;
        var scaled = (Int128)numerator * StepsPerUnit;
        if (scaled % denominator != 0) return false;
        var steps = scaled / denominator;
        return steps <= MaxSteps && TryFromSteps((int)steps, out speed);
    }

    /// <summary>The speed equal to <paramref name="value"/> (e.g. 1.35); false unless exactly a
    /// multiple of 0.05 in range.</summary>
    public static bool TryFromDecimal(decimal value, out ClipSpeed speed)
    {
        speed = Normal;
        var scaled = value * StepsPerUnit;
        return scaled == decimal.Truncate(scaled) && scaled is >= MinSteps and <= MaxSteps
            && TryFromSteps((int)scaled, out speed);
    }

    public decimal ToDecimal() => Steps / (decimal)StepsPerUnit;

    public bool Equals(ClipSpeed other) => _offset == other._offset;
    public override bool Equals(object? obj) => obj is ClipSpeed other && Equals(other);
    public override int GetHashCode() => _offset;
    public static bool operator ==(ClipSpeed a, ClipSpeed b) => a.Equals(b);
    public static bool operator !=(ClipSpeed a, ClipSpeed b) => !a.Equals(b);

    public override string ToString() => ToDecimal().ToString("0.00", CultureInfo.InvariantCulture) + "×";

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}

/// <summary>
/// Timing of a clip at a speed other than 1× (D022). A clip of <c>N</c> project frames consumes
/// <see cref="SourceLength"/> = <c>⌊N · s · 10⁷ · rateDen / rateNum⌋</c> ticks of its source (one floor,
/// exact integer arithmetic, independent of where the clip is). The timing invariant is
/// <c>SourceLength(N) ≤ SourceOut − SourceIn &lt; SourceLength(N + 1)</c>, i.e.
/// <c>N = FramesFor(SourceOut − SourceIn)</c>: the clip is as long as the selected source range allows
/// in whole frames, and a speed change keeps the source range. Clips at 1× keep their existing exact
/// rule (<c>SourceOut − SourceIn</c> = duration, D014), which is not changed by any of this.
/// </summary>
public static class SpeedTiming
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>Source ticks consumed by <paramref name="frames"/> project frames at <paramref name="speed"/>.</summary>
    public static MediaTime SourceLength(long frames, ClipSpeed speed, FrameRate rate)
    {
        var (a, b) = Factors(speed, rate);
        return new MediaTime(checked((long)FloorDiv(frames * a, b)));
    }

    /// <summary>The largest frame count whose <see cref="SourceLength"/> fits in <paramref name="length"/>
    /// (0 when not even one frame fits).</summary>
    public static long FramesFor(MediaTime length, ClipSpeed speed, FrameRate rate)
    {
        if (length < MediaTime.Zero) return 0;
        var (a, b) = Factors(speed, rate);
        // ⌊N·a/b⌋ ≤ L  ⇔  N·a < (L + 1)·b  ⇔  N ≤ ((L + 1)·b − 1) / a
        return checked((long)(((Int128)(length.Ticks + 1) * b - 1) / a));
    }

    /// <summary>True when <paramref name="frames"/> frames are exactly what the source range allows.</summary>
    public static bool Fits(long frames, MediaTime sourceLength, ClipSpeed speed, FrameRate rate) =>
        frames >= 1 && FramesFor(sourceLength, speed, rate) == frames;

    /// <summary>Source ticks per frame as the exact fraction a/b: s · 10⁷ · rateDen / rateNum.</summary>
    private static (Int128 A, Int128 B) Factors(ClipSpeed speed, FrameRate rate)
    {
        if (!rate.IsValid) throw new ArgumentException("Invalid frame rate.", nameof(rate));
        return ((Int128)speed.Numerator * TicksPerSecond * rate.Denominator, (Int128)speed.Denominator * rate.Numerator);
    }

    private static Int128 FloorDiv(Int128 a, Int128 b)
    {
        var q = a / b;
        return (a % b != 0) && ((a < 0) != (b < 0)) ? q - 1 : q;
    }
}
