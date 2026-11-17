using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Audio;

/// <summary>
/// D064 §1: one volume point of a clip's sound — its <see cref="Time"/> in the clip's source time (a move, Trim, Slip and
/// Speed keep it on the same sound) and its <see cref="Gain"/>, linear 0–2, relative to the clip's volume (the points shape the
/// clip's volume, the clip's volume moves the whole curve). <see cref="Id"/> identifies it in the editor.
/// </summary>
public sealed record VolumePoint(Guid Id, MediaTime Time, double Gain);

/// <summary>
/// D064 §1 / §3: a clip's volume points — sorted, distinct times and ids, at least one, every gain within
/// [<see cref="MinGain"/>, <see cref="MaxGain"/>]. Immutable and equal by value (replaced as a whole by every edit); read from
/// any thread (the device thread, the export). <see cref="At(MediaTime)"/> is the one evaluation the Preview, the export and the
/// editor share.
/// </summary>
public sealed class VolumeEnvelope : IEquatable<VolumeEnvelope>
{
    public const double MinGain = 0;
    public const double MaxGain = ClipPropertyLimits.MaxVolume;

    private readonly long[] _ticks;
    private readonly double[] _gains;

    public VolumeEnvelope(ImmutableArray<VolumePoint> points)
    {
        Points = points.IsDefault ? ImmutableArray<VolumePoint>.Empty : points;
        _ticks = Points.Select(p => p.Time.Ticks).ToArray();
        _gains = Points.Select(p => p.Gain).ToArray();
    }

    public ImmutableArray<VolumePoint> Points { get; }

    public int Count => Points.Length;

    /// <summary>The points sorted; null when there are none (no curve — the clip's volume alone).</summary>
    public static VolumeEnvelope? Of(IEnumerable<VolumePoint> points)
    {
        var sorted = points.OrderBy(p => p.Time).ToImmutableArray();
        return sorted.IsEmpty ? null : new VolumeEnvelope(sorted);
    }

    /// <summary>Whether <paramref name="points"/> are what a project accepts: at least one, sorted, distinct times and ids,
    /// every gain finite and within the limits.</summary>
    public static bool AreValid(ImmutableArray<VolumePoint> points)
    {
        if (points.IsDefaultOrEmpty) return false;
        var ids = new HashSet<Guid>();
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (p is null || !ids.Add(p.Id)) return false;
            if (i > 0 && points[i - 1].Time >= p.Time) return false;
            if (!double.IsFinite(p.Gain) || p.Gain < MinGain || p.Gain > MaxGain) return false;
        }
        return true;
    }

    /// <summary>The gain at source time <paramref name="time"/> (D064 §3).</summary>
    public double At(MediaTime time) => AtTicks(time.Ticks);

    /// <summary>
    /// The gain at source time <paramref name="ticks"/>: before the first point its gain, after the last the last's; between two
    /// the gain moves evenly in dB — <c>a·(b/a)^f</c>, the same as <c>10^(lerp(dBa, dBb, f)/20)</c> — and linearly in amplitude
    /// when an end is 0. Exactly a point's gain on its own time.
    /// </summary>
    public double AtTicks(long ticks)
    {
        var t = _ticks;
        if (t.Length == 0) return 1;
        if (ticks <= t[0]) return _gains[0];
        if (ticks >= t[^1]) return _gains[^1];
        int lo = 0, hi = t.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (t[mid] <= ticks) lo = mid; else hi = mid;
        }
        var f = (ticks - t[lo]) / (double)(t[hi] - t[lo]);
        return Interpolate(_gains[lo], _gains[hi], f);
    }

    /// <summary>The gain between two points at share <paramref name="f"/>: even in dB, linear in amplitude with a 0 end.</summary>
    public static double Interpolate(double a, double b, double f)
    {
        if (f <= 0) return a;
        if (f >= 1) return b;
        if (a == b) return a;
        if (a <= 0 || b <= 0) return a + (b - a) * f;
        return a * Math.Pow(b / a, f);
    }

    public VolumePoint? Find(Guid id)
    {
        foreach (var point in Points)
            if (point.Id == id) return point;
        return null;
    }

    /// <summary>These points with <paramref name="point"/> added or — the same id — replaced, sorted; null when another point
    /// already lies at its time.</summary>
    public VolumeEnvelope? With(VolumePoint point)
    {
        if (Points.Any(p => p.Id != point.Id && p.Time == point.Time)) return null;
        return Of(Points.Where(p => p.Id != point.Id).Append(point));
    }

    /// <summary>These points without the one of <paramref name="id"/>; null when none is left.</summary>
    public VolumeEnvelope? Without(Guid id) => Of(Points.Where(p => p.Id != id));

    /// <summary>Every point's gain × <paramref name="factor"/>, clamped to the limits (the ducking baked into the points).</summary>
    public VolumeEnvelope Scaled(double factor) =>
        new(Points.Select(p => p with { Gain = Math.Clamp(p.Gain * factor, MinGain, MaxGain) }).ToImmutableArray());

    public bool Equals(VolumeEnvelope? other) =>
        other is not null && (ReferenceEquals(this, other) || Points.SequenceEqual(other.Points));

    public override bool Equals(object? obj) => Equals(obj as VolumeEnvelope);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var point in Points) hash.Add(point);
        return hash.ToHashCode();
    }

    public static bool operator ==(VolumeEnvelope? a, VolumeEnvelope? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(VolumeEnvelope? a, VolumeEnvelope? b) => !(a == b);
}
