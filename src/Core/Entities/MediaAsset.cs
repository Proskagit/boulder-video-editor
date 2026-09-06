using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Entities;

public enum MediaKind
{
    Video,
    Audio,
    Image
}

/// <summary>
/// A media file that has been imported into the project's Media Browser.
/// The project stores a reference to the original file path; the file itself
/// is not necessarily copied into the project folder (see project layout in docs).
/// </summary>
public sealed class MediaAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Absolute path to the original source file on disk.</summary>
    public required string FilePath { get; set; }

    public string FileName => Path.GetFileName(FilePath);

    public MediaKind Kind { get; set; }

    /// <summary>Null until FFmpeg metadata probing has completed.</summary>
    public MediaMetadata? Metadata { get; set; }

    /// <summary>Cached thumbnail path (relative to project cache folder), if generated.</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>Set when the file could not be located at <see cref="FilePath"/> on project load.</summary>
    public bool IsMissing { get; set; }

    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Probed technical metadata for a <see cref="MediaAsset"/>, produced by the FFmpeg backend.</summary>
public sealed class MediaMetadata
{
    public MediaTime Duration { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? FrameRate { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioSampleRate { get; set; }
    public long? BitrateBps { get; set; }
}
