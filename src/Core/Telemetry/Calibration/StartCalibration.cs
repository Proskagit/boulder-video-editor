using System.Globalization;

namespace AiVideoEditor.Core.Telemetry.Calibration;

/// <summary>
/// The key of a calibration record (D051 §5.1): the training's identity and the climb's full identity. The same key names
/// the climb's mark, its declined entry, its animation link and its record; it never depends on a video.
/// <see cref="Text"/> is its canonical form (fixed-width numbers), so its ordinal order is the order of the window's ties
/// (D051 §6.5.1).
/// </summary>
public readonly record struct CalibrationKey(TrainingActivity Training, ClimbIdentity Climb) : IComparable<CalibrationKey>
{
    public string Text => string.Create(CultureInfo.InvariantCulture,
        $"{Training.StartUtc.UtcTicks:D19}-{Training.EndUtc.UtcTicks:D19}-{Training.Manufacturer:D10}-{(Training.Product is { } p ? p.ToString("D10", CultureInfo.InvariantCulture) : "none")}" +
        $"/{Climb.ClimbKey:D10}-{Climb.ClimbNumber:D10}-{Climb.ClimbStartUtc.UtcTicks:D19}");

    public int CompareTo(CalibrationKey other) => string.CompareOrdinal(Text, other.Text);
}

/// <summary>The status of a calibration record (D051 §6.3).</summary>
public enum CalibrationRecordStatus
{
    /// <summary>An active, usable observation inside the bounds — a candidate for the window.</summary>
    Used,

    /// <summary>An active observation outside the bounds; <see cref="CalibrationRecord.Reason"/> says which side.</summary>
    Excluded,

    /// <summary>No active usable mark at the last save (hidden / declined, or clamped without a link): a tombstone, never
    /// in the window.</summary>
    Removed,
}

/// <summary>Why an observation is excluded (D051 §6.3).</summary>
public enum CalibrationExclusionReason
{
    /// <summary>Below −30 s.</summary>
    BelowBounds,

    /// <summary>Above +90 s.</summary>
    AboveBounds,
}

/// <summary>
/// One record of the global calibration (D051 §6.3), one per key, never trimmed to the window. <see cref="Value"/> is the
/// observation <c>c</c> in ticks; <see cref="FirstSeenAt"/> never changes; <see cref="PositionChangedAt"/> is copied from the
/// project and orders the window; <see cref="ValueChangedAt"/> is diagnostics only.
/// </summary>
public sealed record CalibrationRecord(
    CalibrationKey Key,
    long Value,
    CalibrationRecordStatus Status,
    CalibrationExclusionReason? Reason,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset PositionChangedAt,
    DateTimeOffset ValueChangedAt);

/// <summary>
/// The K3 estimate of <c>StartCorrection</c> (D051 §6.5, variant R): integers in ticks, every division rounded towards −∞,
/// no floating point. A function of the records only — any order, any grouping of saves gives the same value. The constants
/// are fixed by D051; <c>ClimbEndLag</c> never enters here (D051 §2).
/// </summary>
public static class StartCalibration
{
    /// <summary>The initial value <c>I</c> = 12 s (D051 §2.3).</summary>
    public const long InitialTicks = 120_000_000;

    /// <summary>The window <c>N</c>.</summary>
    public const int Window = 100;

    /// <summary>Below <c>MIN_N</c> usable records the estimate is <see cref="InitialTicks"/>.</summary>
    public const int MinimumCount = 20;

    /// <summary>The lower bound −30 s, inclusive.</summary>
    public const long LowerBoundTicks = -300_000_000;

    /// <summary>The upper bound +90 s, inclusive.</summary>
    public const long UpperBoundTicks = 900_000_000;

    /// <summary>The Huber clip <c>C</c> = 5 s.</summary>
    public const long ClipTicks = 50_000_000;

    /// <summary>The status an active observation gets by the bounds (D051 §6.3).</summary>
    public static (CalibrationRecordStatus Status, CalibrationExclusionReason? Reason) Classify(long value) =>
        value < LowerBoundTicks ? (CalibrationRecordStatus.Excluded, CalibrationExclusionReason.BelowBounds)
        : value > UpperBoundTicks ? (CalibrationRecordStatus.Excluded, CalibrationExclusionReason.AboveBounds)
        : (CalibrationRecordStatus.Used, null);

    /// <summary>The window (D051 §6.5.1): the last <see cref="Window"/> <c>used</c> records by <c>positionChangedAt</c>, then
    /// by key — computed afresh from all records.</summary>
    public static IReadOnlyList<CalibrationRecord> WindowOf(IEnumerable<CalibrationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var used = records.Where(r => r.Status == CalibrationRecordStatus.Used)
            .OrderBy(r => r.PositionChangedAt.UtcTicks)
            .ThenBy(r => r.Key)
            .ToList();
        return used.Count <= Window ? used : used.GetRange(used.Count - Window, Window);
    }

    /// <summary>The estimate (D051 §6.5): <c>I</c> below <see cref="MinimumCount"/>, else
    /// <c>I + ⌊min(n, N) · (Target − I) / N⌋</c>.</summary>
    public static long Estimate(IEnumerable<CalibrationRecord> records)
    {
        var window = WindowOf(records);
        var n = window.Count;
        if (n < MinimumCount) return InitialTicks;
        var target = Target(window.Select(r => r.Value).ToList());
        return InitialTicks + FloorDiv(Math.Min(n, Window) * (target - InitialTicks), Window);
    }

    /// <summary>
    /// The Huber location (D051 §6.5.3): <c>S(μ) = Σ clip(cᵢ − μ, −C, +C)</c>, non-increasing in <c>μ</c>;
    /// <c>L = min{μ : S(μ) ≤ 0}</c> and <c>U = max{μ : S(μ) ≥ 0}</c> by bisection in <c>[min c, max c]</c>;
    /// <c>Target = ⌊(L + U) / 2⌋</c>. <c>L = U + 1</c> when S has no integer zero (Target = the floor of the real root).
    /// </summary>
    public static long Target(IReadOnlyList<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) throw new ArgumentException("The target needs at least one value.", nameof(values));
        long min = values.Min(), max = values.Max();

        long lo = min, hi = max;                         // L: S(hi) ≤ 0 holds throughout
        while (lo < hi)
        {
            var mid = FloorDiv(lo + hi, 2);
            if (S(values, mid) <= 0) hi = mid; else lo = mid + 1;
        }
        var l = lo;

        lo = min; hi = max;                              // U: S(lo) ≥ 0 holds throughout
        while (lo < hi)
        {
            var mid = FloorDiv(lo + hi + 1, 2);
            if (S(values, mid) >= 0) lo = mid; else hi = mid - 1;
        }
        var u = lo;

        return FloorDiv(l + u, 2);
    }

    private static long S(IReadOnlyList<long> values, long mu)
    {
        long sum = 0;
        foreach (var c in values) sum += Math.Clamp(c - mu, -ClipTicks, ClipTicks);
        return sum;
    }

    /// <summary>Division rounded towards −∞ for a positive divisor (C# <c>/</c> truncates towards zero).</summary>
    internal static long FloorDiv(long a, long b)
    {
        var q = a / b;
        return a % b != 0 && (a < 0) != (b < 0) ? q - 1 : q;
    }
}
