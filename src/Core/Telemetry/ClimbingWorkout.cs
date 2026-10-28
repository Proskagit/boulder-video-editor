using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>One heart-rate sample of a training: the absolute time (UTC) and beats per minute.</summary>
public readonly record struct HeartRateSample(DateTimeOffset Utc, int Bpm);

/// <summary>A span of absolute time, <c>[Start, End)</c>, both in UTC.</summary>
public readonly record struct WorkoutInterval(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;

    public bool Contains(DateTimeOffset time) => time >= Start && time < End;

    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => Start < end && start < End;
}

/// <summary>What a split of the training is, as the watch recorded it (FIT <c>split_type</c>).</summary>
public enum WorkoutSegmentKind
{
    /// <summary>A climb: an attempt on a route (FIT <c>climb_active</c>).</summary>
    Climb,

    /// <summary>Rest between climbs (FIT <c>climb_rest</c>).</summary>
    Rest,

    /// <summary>Any other split type; kept so the training's structure is complete.</summary>
    Other,
}

/// <summary>
/// One split of the training. Its start and end are whole seconds (FIT <c>date_time</c>); its elapsed time has the
/// watch's millisecond precision. The average and maximum heart rate are the watch's own values for the split.
/// </summary>
public sealed record WorkoutSegment(
    int Index,
    WorkoutSegmentKind Kind,
    int? ClimbNumber,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    MediaTime Elapsed,
    int? AverageHeartRate,
    int? MaxHeartRate)
{
    public WorkoutInterval Interval => new(StartUtc, EndUtc);
}

/// <summary>A non-fatal finding of the import: data that was left out, with how many items it concerned.</summary>
public enum TrainingFileWarningKind
{
    /// <summary>Samples whose time did not advance past the previous one were left out.</summary>
    NonMonotonicSamples,

    /// <summary>Heart-rate values outside 25–250 bpm were left out.</summary>
    ImplausibleHeartRate,

    /// <summary>Splits whose end precedes their start were left out.</summary>
    InvalidSegments,

    /// <summary>The local-time offset the watch stored is not a whole quarter-hour within ±14 h; it is ignored.</summary>
    UnusableUtcOffset,
}

public readonly record struct TrainingFileWarning(TrainingFileWarningKind Kind, int Count);

/// <summary>
/// A climbing training imported from a COROS watch's FIT export: the absolute times of the recording, the heart rate
/// sampled by the watch, the timer's pauses and the splits (climbs and rests). All times are UTC; <see cref="UtcOffset"/>
/// is only for showing local time. Nothing here is bound to a video yet.
/// </summary>
public sealed class ClimbingWorkout
{
    public required string? DeviceName { get; init; }

    /// <summary>The FIT manufacturer id (COROS = 294).</summary>
    public required int Manufacturer { get; init; }

    public required int? Product { get; init; }

    /// <summary>The start of the recording (the session's start time).</summary>
    public required DateTimeOffset StartUtc { get; init; }

    /// <summary>The end of the recording (the session's timestamp).</summary>
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>The offset of the watch's local time from UTC at the recording, when the file states a usable one.</summary>
    public required TimeSpan? UtcOffset { get; init; }

    /// <summary>The total elapsed time, pauses included (session, millisecond precision).</summary>
    public required MediaTime ElapsedTime { get; init; }

    /// <summary>The timer time, pauses excluded (session, millisecond precision).</summary>
    public required MediaTime TimerTime { get; init; }

    /// <summary>FIT <c>sport</c> / <c>sub_sport</c>; rock climbing / bouldering is 31 / 69.</summary>
    public required int? Sport { get; init; }

    public required int? SubSport { get; init; }

    public required int? AverageHeartRate { get; init; }

    public required int? MaxHeartRate { get; init; }

    /// <summary>Heart-rate samples in strictly increasing time.</summary>
    public required IReadOnlyList<HeartRateSample> HeartRate { get; init; }

    /// <summary>The timer's pauses (from a stop to the next start), in time order.</summary>
    public required IReadOnlyList<WorkoutInterval> Pauses { get; init; }

    /// <summary>The splits in file order.</summary>
    public required IReadOnlyList<WorkoutSegment> Segments { get; init; }

    public required IReadOnlyList<TrainingFileWarning> Warnings { get; init; }

    public WorkoutInterval Recording => new(StartUtc, EndUtc);

    public bool IsBouldering => Sport == 31 && SubSport == 69;

    public IEnumerable<WorkoutSegment> Climbs => Segments.Where(s => s.Kind == WorkoutSegmentKind.Climb);
}
