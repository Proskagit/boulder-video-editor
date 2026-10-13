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

    /// <summary>Kept for compatibility of project files; not used and never filled (D024 Step 9.4, PO-5): a
    /// thumbnail's cache file follows from the asset id and its source file (<c>IThumbnailService</c>).</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>The file is not at <see cref="FilePath"/>: set when a project is opened or recovered and updated by every
    /// re-check during the session (<c>IProjectService.RecheckMediaAsync</c>, D026 §2). Runtime state, never saved.</summary>
    public bool IsMissing { get; set; }

    /// <summary>A background probe of saved metadata (D019 display size, D033 T3 colour) is queued or running for this
    /// asset; the asset stays <see cref="MediaAnalysisStatus.Completed"/> meanwhile. The export preflight waits for it
    /// only when the asset's colour is still unknown (<see cref="MediaMetadata.NeedsColorProbe"/>). Runtime state,
    /// never saved.</summary>
    public bool IsMetadataRefreshing { get; set; }

    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Probed technical metadata for a <see cref="MediaAsset"/>, produced by the FFmpeg backend.</summary>
public sealed class MediaMetadata
{
    public MediaTime Duration { get; set; }

    /// <summary>Encoded (coded) picture width of the video/image stream, as stored in the file.</summary>
    public int? Width { get; set; }

    /// <summary>Encoded (coded) picture height of the video/image stream, as stored in the file.</summary>
    public int? Height { get; set; }

    /// <summary>
    /// Orientation from the file's display matrix (or EXIF orientation of an image): the clockwise
    /// rotation, 0/90/180/270, that the decoder applies so frames come out upright. Null when the
    /// orientation is not a plain right-angle rotation (odd angle, mirrored matrix) or unknown.
    /// </summary>
    public int? DisplayRotation { get; set; }

    /// <summary>Width of the frames as the decoder actually delivers them (after its automatic
    /// rotation). This is the picture size composition works with (D018). Null when unknown.</summary>
    public int? DisplayWidth { get; set; }

    /// <summary>Height of the frames as the decoder actually delivers them. Null when unknown.</summary>
    public int? DisplayHeight { get; set; }

    /// <summary>True for metadata that has a coded picture size but no display size — probed
    /// before orientation was read (projects saved before Phase 7). Such media is probed again
    /// when it is available.</summary>
    public bool NeedsDisplaySizeProbe => Width is not null && Height is not null && (DisplayWidth is null || DisplayHeight is null);
    /// <summary>Exact rational rate reported by ffprobe (r_frame_rate); null if unknown.</summary>
    public FrameRate? FrameRate { get; set; }
    /// <summary>Exact rational average rate of the video stream (avg_frame_rate); null if
    /// unknown. Nominal source frame length for playback sampling (D009) — frame timing
    /// itself always comes from decoded PTS. Not used for the project rate (D007).</summary>
    public FrameRate? AvgFrameRate { get; set; }
    /// <summary>Container start time (format start_time): the origin of source time, so
    /// source time = PTS − StartTime for every stream. Null if unknown (treated as zero).</summary>
    public MediaTime? StartTime { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioSampleRate { get; set; }
    public long? BitrateBps { get; set; }

    // Colour of the video / image stream (D033 T1), as ffprobe names it. Null when the file does not state it.

    /// <summary>The decoded pixel format, e.g. <c>yuv420p10le</c>. Every stream analysed since Phase 18 has one: its
    /// absence on video metadata marks metadata saved before colour was probed (<see cref="NeedsColorProbe"/>).</summary>
    public string? PixelFormat { get; set; }

    /// <summary>Bits per component, from <see cref="PixelFormat"/>; null when unknown.</summary>
    public int? BitDepth { get; set; }

    /// <summary>Transfer characteristics, e.g. <c>bt709</c>, <c>arib-std-b67</c> (HLG), <c>smpte2084</c> (PQ).</summary>
    public string? ColorTransfer { get; set; }

    /// <summary>Colour primaries, e.g. <c>bt709</c>, <c>bt2020</c>.</summary>
    public string? ColorPrimaries { get; set; }

    /// <summary>Matrix coefficients (ffprobe's <c>color_space</c>), e.g. <c>bt709</c>, <c>bt2020nc</c>.</summary>
    public string? ColorMatrix { get; set; }

    /// <summary><c>tv</c> (limited) or <c>pc</c> (full).</summary>
    public string? ColorRange { get; set; }

    /// <summary>The mastering display's maximum luminance in nit (HDR10 static metadata); null when absent.</summary>
    public double? MasteringMaxLuminance { get; set; }

    /// <summary>MaxCLL in nit (content light level); null when absent. 0 means "not set" in the file.</summary>
    public int? MaxContentLightLevel { get; set; }

    /// <summary>The Dolby Vision profile of the stream's configuration record; null when there is none. Shown only: the
    /// dynamic metadata is not applied (D033 HQ1).</summary>
    public int? DolbyVisionProfile { get; set; }

    /// <summary>The stream carries HDR Vivid (CUVA) dynamic metadata. Shown only: not applied (D033 HQ1).</summary>
    public bool HasHdrVivid { get; set; }

    /// <summary>True for picture metadata saved before Phase 18 — a coded size but no pixel format. Such video is probed
    /// again in the background when it is available (D033 T3).</summary>
    public bool NeedsColorProbe => Width is not null && Height is not null && PixelFormat is null;
}
