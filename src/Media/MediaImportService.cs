using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Media;

/// <summary>
/// Validates file paths against the supported extension list and turns the valid
/// ones into <see cref="MediaAsset"/> instances, reading only what's needed to
/// populate the Phase 2 fields (file size). No FFmpeg, no decoding, no thumbnails —
/// those are later phases; <see cref="MediaAsset.Metadata"/> is left null here.
/// </summary>
public sealed class MediaImportService : IMediaImportService
{
    private static readonly IReadOnlyDictionary<string, MediaKind> ExtensionToKind =
        new Dictionary<string, MediaKind>(StringComparer.OrdinalIgnoreCase)
        {
            // Video
            [".mp4"] = MediaKind.Video,
            [".mov"] = MediaKind.Video,
            [".mkv"] = MediaKind.Video,
            [".webm"] = MediaKind.Video,
            [".avi"] = MediaKind.Video,

            // Audio
            [".mp3"] = MediaKind.Audio,
            [".wav"] = MediaKind.Audio,
            [".flac"] = MediaKind.Audio,
            [".aac"] = MediaKind.Audio,
            [".m4a"] = MediaKind.Audio,

            // Image
            [".png"] = MediaKind.Image,
            [".jpg"] = MediaKind.Image,
            [".jpeg"] = MediaKind.Image,
            [".webp"] = MediaKind.Image,
            [".bmp"] = MediaKind.Image,
        };

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(ExtensionToKind.Keys, StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<MediaImportService> _logger;

    public MediaImportService(ILogger<MediaImportService> logger)
    {
        _logger = logger;
    }

    public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        var imported = new List<MediaAsset>();
        var unsupported = new List<string>();
        var missing = new List<string>();

        foreach (var path in filePaths)
        {
            ct.ThrowIfCancellationRequested();

            if (!ExtensionToKind.TryGetValue(Path.GetExtension(path), out var kind))
            {
                unsupported.Add(path);
                continue;
            }

            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(path);
                if (!fileInfo.Exists)
                {
                    missing.Add(path);
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A file that vanished, got locked, or has an invalid path between
                // being picked and being processed is a missing/invalid import, not
                // a crash — see Phase 2 spec: "must not crash on invalid files".
                _logger.LogWarning(ex, "Could not read file info for '{Path}'; treating as missing.", path);
                missing.Add(path);
                continue;
            }

            imported.Add(new MediaAsset
            {
                FilePath = fileInfo.FullName,
                Kind = kind,
                FileSizeBytes = fileInfo.Length
            });
        }

        _logger.LogInformation(
            "Media import batch: {Imported} imported, {Unsupported} unsupported, {Missing} missing.",
            imported.Count, unsupported.Count, missing.Count);

        return Task.FromResult(new MediaImportBatchResult
        {
            Imported = imported,
            UnsupportedFiles = unsupported,
            MissingFiles = missing
        });
    }
}
