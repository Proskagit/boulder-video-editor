namespace AiVideoEditor.Core.Common;

/// <summary>
/// Represents a precise point in time or a duration on the timeline.
/// Internally stored as 100-nanosecond ticks (same resolution as <see cref="TimeSpan"/>),
/// completely independent of any UI frame rate. Frame-based positions are derived
/// on demand for a given <see cref="FrameRate"/> via <see cref="FromFrame"/> /
/// <see cref="ToFrameFloor"/> / <see cref="ToNearestFrame"/>.
/// </summary>
/// <remarks>
/// Frame grid: frame <c>n</c> starts at <c>G(n) = round_half_up(n · 10⁷ · den / num)</c>
/// ticks, computed in integer arithmetic. Most frame rates (24, 30, 30000/1001, ...)
/// don't divide 10⁷ ticks evenly, so a frame boundary is rounded to the nearest
/// tick — the conversions are defined so that <c>ToFrameFloor(FromFrame(n)) == n</c>
/// exactly for every <c>n</c>. A consequence: the tick length of "k frames" can
/// differ by one tick depending on the starting frame, so frame-based edits must
/// compute both edges from frame indices rather than adding a fixed duration.
/// </remarks>
public readonly struct MediaTime : IEquatable<MediaTime>, IComparable<MediaTime>
{
    public static readonly MediaTime Zero = new(0);

    /// <summary>Ticks at 100-nanosecond resolution (identical unit to <see cref="TimeSpan.Ticks"/>).</summary>
    public long Ticks { get; }

    public MediaTime(long ticks) => Ticks = ticks;

    public static MediaTime FromSeconds(double seconds) => new((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    public static MediaTime FromTimeSpan(TimeSpan span) => new(span.Ticks);

    /// <summary>Start of frame <paramref name="frame"/> on the <paramref name="rate"/> grid
    /// (see type remarks). Exact integer arithmetic.</summary>
    public static MediaTime FromFrame(long frame, FrameRate rate)
    {
        EnsureValid(rate);
        // ticks = round_half_up(frame * TicksPerSecond * den / num)
        Int128 numerator = (Int128)frame * TimeSpan.TicksPerSecond * rate.Denominator;
        Int128 denominator = rate.Numerator;
        return new MediaTime(checked((long)FloorDiv(2 * numerator + denominator, 2 * denominator)));
    }

    public double TotalSeconds => Ticks / (double)TimeSpan.TicksPerSecond;

    public TimeSpan ToTimeSpan() => new(Ticks);

    /// <summary>Index of the frame containing this time: the largest <c>n</c> with
    /// <c>FromFrame(n) &lt;= this</c>. Exact inverse of <see cref="FromFrame"/>.</summary>
    public long ToFrameFloor(FrameRate rate)
    {
        EnsureValid(rate);
        // Estimate from the exact rational frame duration, then correct by at most one
        // frame for the tick rounding applied in FromFrame.
        var n = checked((long)FloorDiv((Int128)Ticks * rate.Numerator, (Int128)TimeSpan.TicksPerSecond * rate.Denominator));
        while (FromFrame(n + 1, rate).Ticks <= Ticks) n++;
        while (FromFrame(n, rate).Ticks > Ticks) n--;
        return n;
    }

    /// <summary>Smallest frame index whose start is at or after this time: <see cref="ToFrameFloor"/> when
    /// this time is a frame boundary, otherwise one more. A span <c>[S, E)</c> covers frames
    /// <c>[S.ToFrameCeiling, E.ToFrameCeiling)</c>; playback's last frame and the export's frame count
    /// derive from it.</summary>
    public long ToFrameCeiling(FrameRate rate)
    {
        var floor = ToFrameFloor(rate);
        return FromFrame(floor, rate) == this ? floor : floor + 1;
    }

    /// <summary>Index of the frame boundary nearest to this time (ties go to the later frame).</summary>
    public long ToNearestFrame(FrameRate rate)
    {
        var floor = ToFrameFloor(rate);
        var before = Ticks - FromFrame(floor, rate).Ticks;
        var after = FromFrame(floor + 1, rate).Ticks - Ticks;
        return after <= before ? floor + 1 : floor;
    }

    /// <summary>This time moved to the nearest frame boundary on the <paramref name="rate"/> grid.</summary>
    public MediaTime SnapToFrame(FrameRate rate) => FromFrame(ToNearestFrame(rate), rate);

    /// <summary>True when this time is exactly a frame boundary on the <paramref name="rate"/> grid.</summary>
    public bool IsOnFrameGrid(FrameRate rate) => FromFrame(ToFrameFloor(rate), rate) == this;

    private static void EnsureValid(FrameRate rate)
    {
        if (!rate.IsValid) throw new ArgumentOutOfRangeException(nameof(rate), "Frame rate is not initialized.");
    }

    private static Int128 FloorDiv(Int128 a, Int128 b)
    {
        var q = a / b;
        return (a % b != 0) && ((a < 0) != (b < 0)) ? q - 1 : q;
    }

    public static MediaTime operator +(MediaTime a, MediaTime b) => new(a.Ticks + b.Ticks);
    public static MediaTime operator -(MediaTime a, MediaTime b) => new(a.Ticks - b.Ticks);
    public static bool operator <(MediaTime a, MediaTime b) => a.Ticks < b.Ticks;
    public static bool operator >(MediaTime a, MediaTime b) => a.Ticks > b.Ticks;
    public static bool operator <=(MediaTime a, MediaTime b) => a.Ticks <= b.Ticks;
    public static bool operator >=(MediaTime a, MediaTime b) => a.Ticks >= b.Ticks;
    public static bool operator ==(MediaTime a, MediaTime b) => a.Ticks == b.Ticks;
    public static bool operator !=(MediaTime a, MediaTime b) => a.Ticks != b.Ticks;

    public int CompareTo(MediaTime other) => Ticks.CompareTo(other.Ticks);
    public bool Equals(MediaTime other) => Ticks == other.Ticks;
    public override bool Equals(object? obj) => obj is MediaTime other && Equals(other);
    public override int GetHashCode() => Ticks.GetHashCode();
    public override string ToString() => ToTimeSpan().ToString(@"hh\:mm\:ss\.fff");
}
