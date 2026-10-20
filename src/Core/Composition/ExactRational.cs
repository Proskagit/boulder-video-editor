using System.Numerics;

namespace AiVideoEditor.Core.Composition;

/// <summary>
/// Exact rational number over <see cref="BigInteger"/>. Every finite double converts exactly, so
/// geometric predicates (does a layer cover the canvas?) can be decided without rounding.
/// </summary>
internal readonly struct ExactRational : IComparable<ExactRational>
{
    private readonly BigInteger _num;
    private readonly BigInteger _den; // > 0 once constructed through Create

    private ExactRational(BigInteger num, BigInteger den)
    {
        _num = num;
        _den = den;
    }

    public static readonly ExactRational Zero = new(BigInteger.Zero, BigInteger.One);

    public static ExactRational Create(BigInteger num, BigInteger den)
    {
        if (den.IsZero) throw new DivideByZeroException();
        if (den.Sign < 0) { num = -num; den = -den; }
        var gcd = BigInteger.GreatestCommonDivisor(num, den);
        return gcd.IsOne || gcd.IsZero ? new ExactRational(num, den) : new ExactRational(num / gcd, den / gcd);
    }

    /// <summary>The exact value of <paramref name="value"/> (mantissa · 2^exponent).</summary>
    public static ExactRational FromDouble(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Only finite values are exact rationals.");
        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = bits < 0;
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xF_FFFF_FFFF_FFFFL;
        if (exponent == 0) exponent = 1; else mantissa |= 1L << 52;
        exponent -= 1075;

        BigInteger num = mantissa;
        if (negative) num = -num;
        return exponent >= 0 ? Create(num << exponent, BigInteger.One) : Create(num, BigInteger.One << -exponent);
    }

    public static implicit operator ExactRational(int value) => new(value, BigInteger.One);

    public static ExactRational operator +(ExactRational a, ExactRational b) => Create(a._num * b._den + b._num * a._den, a._den * b._den);
    public static ExactRational operator -(ExactRational a, ExactRational b) => Create(a._num * b._den - b._num * a._den, a._den * b._den);
    public static ExactRational operator *(ExactRational a, ExactRational b) => Create(a._num * b._num, a._den * b._den);
    public static ExactRational operator /(ExactRational a, ExactRational b) => Create(a._num * b._den, a._den * b._num);

    public int CompareTo(ExactRational other) => (_num * other._den).CompareTo(other._num * _den);

    public static bool operator <=(ExactRational a, ExactRational b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ExactRational a, ExactRational b) => a.CompareTo(b) >= 0;
    public static bool operator <(ExactRational a, ExactRational b) => a.CompareTo(b) < 0;
    public static bool operator >(ExactRational a, ExactRational b) => a.CompareTo(b) > 0;

    public static ExactRational Min(ExactRational a, ExactRational b) => a <= b ? a : b;
    public static ExactRational Max(ExactRational a, ExactRational b) => a >= b ? a : b;

    /// <summary>The smallest double not below this value.</summary>
    public double ToDoubleAtLeast()
    {
        var d = ApproximateDouble();
        while (FromDouble(d) < this) d = Math.BitIncrement(d);
        while (FromDouble(Math.BitDecrement(d)) >= this) d = Math.BitDecrement(d);
        return d;
    }

    /// <summary>The largest double not above this value.</summary>
    public double ToDoubleAtMost()
    {
        var d = ApproximateDouble();
        while (FromDouble(d) > this) d = Math.BitDecrement(d);
        while (FromDouble(Math.BitIncrement(d)) <= this) d = Math.BitIncrement(d);
        return d;
    }

    /// <summary>Within a few units in the last place: the quotient taken to 62 significant bits, then scaled — never through
    /// a double numerator or denominator, which could overflow.</summary>
    private double ApproximateDouble()
    {
        if (_num.IsZero) return 0;
        var magnitude = BigInteger.Abs(_num);
        var shift = 62 - (magnitude.GetBitLength() - _den.GetBitLength());
        var quotient = shift >= 0 ? (magnitude << (int)shift) / _den : magnitude / (_den << (int)-shift);
        var value = Math.ScaleB((double)quotient, (int)-shift);
        return _num.Sign < 0 ? -value : value;
    }
}
