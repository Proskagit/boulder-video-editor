using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Calibration;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>Whether the calibration file can be used (D051 §6.1).</summary>
public enum FitCalibrationFileState
{
    /// <summary>The file is read (or there is none yet) and may be written.</summary>
    Usable,

    /// <summary>The file couldn't be read (locked, no access); it is left as it is and not written.</summary>
    Unreadable,

    /// <summary>The file is of a newer format version: neither used nor overwritten (the estimate is <c>I</c>).</summary>
    NewerFormat,
}

/// <summary>The calibration as last read or written: the Beta flag (the collection of observations, on by default —
/// D051 §6.2), the derived estimate of <c>StartCorrection</c> in ticks with its version, the number of <c>used</c>
/// records, and whether the file can be used.</summary>
public sealed record FitCalibrationSnapshot(
    bool BetaEnabled,
    long EstimateTicks,
    int EstimateVersion,
    int UsedCount,
    FitCalibrationFileState FileState)
{
    /// <summary>No file yet: Beta on, the estimate <c>I</c> = 12 s, version 0, no records.</summary>
    public static FitCalibrationSnapshot Initial { get; } =
        new(true, StartCalibration.InitialTicks, 0, 0, FitCalibrationFileState.Usable);
}

/// <summary>The current estimate for new proposals, read without I/O (D051 §5.5): the last value the store read or wrote.
/// The commands that create marks freeze it into the mark.</summary>
public interface IStartCorrectionSource
{
    FitCalibrationSnapshot Current { get; }
}

/// <summary>
/// The global per-user calibration (D051 §6): one file in the configuration folder, its records the source of truth and
/// the estimate recomputed from them on every read and write. Shared by every running instance (a lock, a re-read before
/// every change, an atomic write).
/// </summary>
/// <remarks>
/// Every method does its file work off the calling thread and refreshes <see cref="IStartCorrectionSource.Current"/>.
/// A problem with the file never throws (it is logged): a change that couldn't be written returns false and leaves the
/// file as it was — a failed write never fails the project's save (D051 §6.4).
/// </remarks>
public interface IFitCalibrationStore : IStartCorrectionSource
{
    /// <summary>Reads the file (a damaged one is set aside) and returns the calibration.</summary>
    Task<FitCalibrationSnapshot> LoadAsync(CancellationToken ct = default);

    /// <summary>A save's synchronisation (D051 §6.4) for the project's trainings and their active usable marks. With
    /// Beta off nothing is written. The file is written only when something changed. False if it wasn't written although
    /// it should have been.</summary>
    Task<bool> SynchronizeAsync(IReadOnlySet<TrainingActivity> trainings, IReadOnlyCollection<CalibrationObservation> observations,
        CancellationToken ct = default);

    /// <summary>Turns the collection on or off (D051 §6.2); turning it off removes nothing. False if not written.</summary>
    Task<bool> SetBetaAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Removes every record (with Beta on or off); the estimate becomes <c>I</c>. False if not written.</summary>
    Task<bool> ResetAsync(CancellationToken ct = default);
}
