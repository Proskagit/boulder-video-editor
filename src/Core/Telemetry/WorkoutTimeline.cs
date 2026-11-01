namespace AiVideoEditor.Core.Telemetry;

/// <summary>Queries on a <see cref="ClimbingWorkout"/> at absolute instants.</summary>
public static class WorkoutTimeline
{
    /// <summary>The longest time a sample's value is held. The watch samples once a second; a longer silence is a gap in
    /// the data and shows no heart rate rather than a stale one.</summary>
    public static readonly TimeSpan DefaultMaxSampleAge = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The heart rate at <paramref name="utc"/>: the latest sample at or before it, provided it is at most
    /// <paramref name="maxSampleAge"/> old, the instant lies inside the recording and outside the timer's pauses.
    /// Null otherwise — never an interpolated or extrapolated value.
    /// </summary>
    public static int? HeartRateAt(ClimbingWorkout workout, DateTimeOffset utc, TimeSpan? maxSampleAge = null)
    {
        if (utc < workout.StartUtc || utc > workout.EndUtc) return null;
        if (workout.Pauses.Any(p => p.Contains(utc))) return null;

        var samples = workout.HeartRate;
        int lo = 0, hi = samples.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (samples[mid].Utc <= utc)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0) return null;
        var sample = samples[found];
        return utc - sample.Utc <= (maxSampleAge ?? DefaultMaxSampleAge) ? sample.Bpm : null;
    }

    /// <summary>The splits that overlap <c>[start, end)</c>, in file order.</summary>
    public static IEnumerable<WorkoutSegment> SegmentsOverlapping(ClimbingWorkout workout, DateTimeOffset start, DateTimeOffset end) =>
        workout.Segments.Where(s => s.Interval.Overlaps(start, end));
}
