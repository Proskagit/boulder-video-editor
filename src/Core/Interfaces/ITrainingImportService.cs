using AiVideoEditor.Core.Telemetry;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>A training file read from disk: the parsed workout and the identity of the bytes (D049 §9).</summary>
public sealed record TrainingFile(ClimbingWorkout Workout, long FileSizeBytes, string Sha256);

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

    /// <summary>As <see cref="ImportAsync"/>, with the file's size and SHA-256 (lower-case hex) for the reference kept in
    /// the project (D049 §9).</summary>
    /// <exception cref="TrainingFileException">As <see cref="ImportAsync"/>.</exception>
    Task<TrainingFile> LoadAsync(string filePath, CancellationToken ct = default);
}
