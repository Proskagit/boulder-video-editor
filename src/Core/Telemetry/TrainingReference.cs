using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>The identity of a recorded activity, read from its FIT file (D049 §2, §9): the session's start and end in watch
/// time W and the device. Two files of the same activity (e.g. two exports from the COROS app) share it.</summary>
public readonly record struct TrainingActivity(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Manufacturer, int? Product)
{
    public static TrainingActivity Of(ClimbingWorkout workout) =>
        new(workout.StartUtc, workout.EndUtc, workout.Manufacturer, workout.Product);
}

/// <summary>Whether the project's FIT file can be used now (D049 §9). Runtime state: never saved, never dirty.</summary>
public enum TrainingAvailability
{
    /// <summary>Not resolved yet (just opened, the file being read).</summary>
    Unresolved,

    /// <summary>The file is there, byte for byte the one referenced.</summary>
    Available,

    /// <summary>The file is there with other bytes but the same activity (e.g. a new export): used.</summary>
    Changed,

    /// <summary>No file at the absolute or the relative path.</summary>
    Missing,

    /// <summary>The file is a valid training of another activity: not used.</summary>
    OtherActivity,

    /// <summary>The file cannot be read or parsed: not used.</summary>
    Unreadable,
}

/// <summary>
/// The project's training (Phase 35, D049 §2, §7–§9): a reference to its source FIT file — never a copy of the telemetry —
/// and the training's clock correction <see cref="ClockCorrection"/> = W − C (watch time minus camera time, one value for
/// the whole training; <c>w = c + ClockCorrection</c>). <see cref="Workout"/> and <see cref="Availability"/> are runtime
/// state of the referenced file.
/// </summary>
public sealed class TrainingReference
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Absolute path at the time of saving (the relative path is derived on save, as for media — D014).</summary>
    public required string FilePath { get; set; }

    public required long FileSizeBytes { get; set; }

    /// <summary>SHA-256 of the file's bytes, lower-case hex (64 characters).</summary>
    public required string Sha256 { get; set; }

    public required TrainingActivity Activity { get; set; }

    /// <summary>W − C, signed. The watch ahead of the phone by 2 s → +2 s.</summary>
    public MediaTime ClockCorrection { get; set; }

    /// <summary>The parsed file, when <see cref="Availability"/> is <see cref="TrainingAvailability.Available"/> or
    /// <see cref="TrainingAvailability.Changed"/>.</summary>
    public ClimbingWorkout? Workout { get; set; }

    public TrainingAvailability Availability { get; set; } = TrainingAvailability.Unresolved;

    public bool IsUsable => Workout is not null &&
        Availability is TrainingAvailability.Available or TrainingAvailability.Changed;
}
