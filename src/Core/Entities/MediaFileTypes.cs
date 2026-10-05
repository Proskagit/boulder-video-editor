namespace AiVideoEditor.Core.Entities;

/// <summary>
/// The media kinds of the supported file extensions — the one table the import (Phase 2) and the relink (D026 §3) use
/// to tell what kind of media a file is before (or without) probing it.
/// </summary>
public static class MediaFileTypes
{
    public static IReadOnlyDictionary<string, MediaKind> ByExtension { get; } =
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

    /// <summary>The kind of a file by its extension; null when the extension is not supported.</summary>
    public static MediaKind? KindOf(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var kind) ? kind : null;
}
