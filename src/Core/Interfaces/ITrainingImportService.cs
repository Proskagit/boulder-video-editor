using AiVideoEditor.Core.Telemetry;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Reads a training file from disk and parses it (Phase Coros C1): the COROS FIT activity export becomes a
/// <see cref="ClimbingWorkout"/>. Only reads — what the workout is used for, and how long it is kept, is the caller's.
/// </summary>
public interface ITrainingImportService
{
    /// <summary>The file extension of a training file, with the leading dot.</summary>
    const string FitExtension = ".fit";

    /// <exception cref="TrainingFileException">The file was refused; <see cref="TrainingFileException.Error"/> says why
    /// (also a missing, unreadable or oversized file).</exception>
    Task<ClimbingWorkout> ImportAsync(string filePath, CancellationToken ct = default);
}
