using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Entities;

public enum MediaKind
{
    Video,
    Audio,
    Image
}

/// <summary>Where a <see cref="MediaAsset"/> is in the metadata-analysis pipeline.</summary>
public enum MediaAnalysisStatus
{
    /// <summary>Imported but analysis hasn't started yet.</summary>
    Pending,

    /// <summary>Analysis is currently running in the background.</summary>
    Analyzing,

    /// <summary>Analysis finished and <see cref="MediaAsset.Metadata"/> is populated.</summary>
    Completed,

    /// <summary>Analysis finished unsuccessfully — see <see cref="MediaAsset.AnalysisError"/>.
    /// The asset itself remains a valid imported file; only its metadata is missing.</summary>
    Failed
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

    /// <summary>File extension including the leading dot (e.g. ".mp4"), lower-cased.</summary>
    public string FileExtension => Path.GetExtension(FilePath).ToLowerInvariant();

    /// <summary>Size of the source file in bytes, captured at import time.</summary>
    public long FileSizeBytes { get; set; }

    public MediaKind Kind { get; set; }

    /// <summary>Null until metadata analysis has completed successfully.</summary>
    public MediaMetadata? Metadata { get; set; }

    public MediaAnalysisStatus AnalysisStatus { get; set; } = MediaAnalysisStatus.Pending;

    /// <summary>Human-readable reason analysis failed, set only when
    /// <see cref="AnalysisStatus"/> is <see cref="MediaAnalysisStatus.Failed"/>.</summary>
    public string? AnalysisError { get; set; }

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
    /// <summary>Exact rational rate reported by ffprobe (r_frame_rate); null if unknown.</summary>
    public FrameRate? FrameRate { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioSampleRate { get; set; }
    public long? BitrateBps { get; set; }
}
