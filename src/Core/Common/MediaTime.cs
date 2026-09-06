namespace AiVideoEditor.Core.Common;

/// <summary>
/// Represents a precise point in time or a duration on the timeline.
/// Internally stored as 100-nanosecond ticks (same resolution as <see cref="TimeSpan"/>),
/// completely independent of any UI frame rate. Frame-based positions are derived
/// on demand via <see cref="ToFrame"/> / <see cref="FromFrame"/> for a given FPS.
/// </summary>
public readonly struct MediaTime : IEquatable<MediaTime>, IComparable<MediaTime>
{
    public static readonly MediaTime Zero = new(0);

    /// <summary>Ticks at 100-nanosecond resolution (identical unit to <see cref="TimeSpan.Ticks"/>).</summary>
    public long Ticks { get; }

    public MediaTime(long ticks) => Ticks = ticks;

    public static MediaTime FromSeconds(double seconds) => new((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    public static MediaTime FromTimeSpan(TimeSpan span) => new(span.Ticks);

    /// <summary>Creates a MediaTime from a frame index at a given (possibly fractional) frame rate.</summary>
    public static MediaTime FromFrame(long frame, double fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        return FromSeconds(frame / fps);
    }

    public double TotalSeconds => Ticks / (double)TimeSpan.TicksPerSecond;

    public TimeSpan ToTimeSpan() => new(Ticks);

    /// <summary>Converts to a frame index for a given frame rate. Result is floored, matching typical NLE behavior.</summary>
    public long ToFrame(double fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        return (long)Math.Floor(TotalSeconds * fps + 1e-9);
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
