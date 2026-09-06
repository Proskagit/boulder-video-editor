using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Interfaces;

public interface IExportService
{
    /// <summary>
    /// Renders the given sequence to a single output file according to
    /// <paramref name="settings"/>. Runs entirely on a background task; progress
    /// is reported via <paramref name="progress"/> and the UI thread is never blocked.
    /// </summary>
    Task ExportAsync(
        Sequence sequence,
        IReadOnlyList<MediaAsset> mediaAssets,
        ExportSettings settings,
        IProgress<EngineProgress> progress,
        CancellationToken ct = default);
}
