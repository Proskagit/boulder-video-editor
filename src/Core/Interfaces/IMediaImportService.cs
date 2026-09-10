using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Validates and imports media files from disk into <see cref="MediaAsset"/>
/// instances. This service only looks at the filesystem and the file's
/// extension — it has no notion of "the current project" and does not do
/// duplicate detection, since that requires knowing what's already imported.
/// See <see cref="IProjectService.AddMediaAssets"/> for that step.
/// </summary>
public interface IMediaImportService
{
    Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default);

    /// <summary>File extensions this service recognizes (with leading dot, e.g. ".mp4"),
    /// used both to validate files and to build file-picker filters.</summary>
    IReadOnlySet<string> SupportedExtensions { get; }
}

/// <summary>
/// Result of importing a batch of file paths, categorized so the caller can build
/// an accurate status message ("3 imported, 2 unsupported") without re-deriving why
/// anything was skipped.
/// </summary>
public sealed class MediaImportBatchResult
{
    public IReadOnlyList<MediaAsset> Imported { get; init; } = Array.Empty<MediaAsset>();

    /// <summary>Paths that don't match any <see cref="IMediaImportService.SupportedExtensions"/> entry.</summary>
    public IReadOnlyList<string> UnsupportedFiles { get; init; } = Array.Empty<string>();

    /// <summary>Paths that no longer exist on disk at import time.</summary>
    public IReadOnlyList<string> MissingFiles { get; init; } = Array.Empty<string>();
}
