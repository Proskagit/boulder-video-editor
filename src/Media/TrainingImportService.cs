using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Media;

/// <summary>
/// Reads a COROS FIT export from disk and parses it with <see cref="CorosWorkoutParser"/> (Phase Coros C1). The file is
/// read whole (a training export is small) after a size check, and parsed off the caller's thread. Every failure is a
/// <see cref="TrainingFileException"/> with its reason: a missing file, an I/O error and an oversized file included.
/// </summary>
public sealed class TrainingImportService : ITrainingImportService
{
    /// <summary>16 MiB. The real 1 h 37 min export is 54 kB; a whole day of 1 s records stays below 5 MB, so a larger
    /// file is not a training export.</summary>
    public const long DefaultMaxFileBytes = 16L * 1024 * 1024;

    private readonly ILogger<TrainingImportService> _logger;
    private readonly long _maxFileBytes;

    public TrainingImportService(ILogger<TrainingImportService> logger, long maxFileBytes = DefaultMaxFileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        _logger = logger;
        _maxFileBytes = maxFileBytes;
    }

    public async Task<ClimbingWorkout> ImportAsync(string filePath, CancellationToken ct = default)
    {
        var bytes = await ReadAsync(filePath, ct);
        var workout = await Task.Run(() => CorosWorkoutParser.Parse(bytes), ct);

        _logger.LogInformation("Training imported from '{Path}': {Device}, {Start:u} – {End:u}, {Samples} heart-rate samples, {Climbs} climbs, {Warnings} warnings.",
            filePath, workout.DeviceName, workout.StartUtc, workout.EndUtc, workout.HeartRate.Count, workout.Climbs.Count(),
            workout.Warnings.Count);
        return workout;
    }

    private async Task<byte[]> ReadAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
                throw new TrainingFileException(TrainingFileError.FileNotFound, $"'{filePath}' does not exist.");
            if (info.Length > _maxFileBytes)
                throw new TrainingFileException(TrainingFileError.TooLarge, $"'{filePath}' is {info.Length} bytes, more than {_maxFileBytes}.");

            var bytes = await File.ReadAllBytesAsync(filePath, ct);
            // The file may have grown between the check and the read.
            if (bytes.LongLength > _maxFileBytes)
                throw new TrainingFileException(TrainingFileError.TooLarge, $"'{filePath}' is {bytes.LongLength} bytes, more than {_maxFileBytes}.");
            return bytes;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new TrainingFileException(TrainingFileError.FileNotFound, $"'{filePath}' does not exist.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new TrainingFileException(TrainingFileError.Unreadable, $"'{filePath}' could not be read.", ex);
        }
    }
}
