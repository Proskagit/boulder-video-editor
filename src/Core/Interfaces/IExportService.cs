using AiVideoEditor.Core.Export;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Renders an <see cref="ExportJob"/> to an MP4 file (D023): an offline rendering of the Preview —
/// the same snapshot, composition (D018), frame selection (D009/D022) and audio placement (D013/D022).
/// </summary>
public interface IExportService
{
    /// <summary>True when the encoder backend (ffmpeg) can be used. Feeds
    /// <see cref="ExportPreflightEnvironment.EncoderAvailable"/>.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes <see cref="ExportJob.OutputPath"/>. Runs entirely off the calling thread; progress is
    /// reported via <paramref name="progress"/>. On success the file is complete (written to a temporary
    /// file first, then moved into place). A decode or encode failure throws <see cref="ExportException"/>,
    /// cancellation throws <see cref="OperationCanceledException"/>; in both cases no partial output is
    /// left and an existing file at the output path is untouched.
    /// </summary>
    Task ExportAsync(ExportJob job, IProgress<ExportProgress>? progress, CancellationToken ct = default);
}
