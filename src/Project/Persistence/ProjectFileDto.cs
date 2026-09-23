using System.Text.Json;
using System.Text.Json.Serialization;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Project.Persistence;

// On-disk shape of project.json (format version 1). Kept separate from the runtime
// entities so the file format only changes deliberately. Conventions:
// - every MediaTime is a long of 100 ns ticks (never seconds);
// - every FrameRate is an exact { numerator, denominator } pair;
// - runtime/UI state (IsSelected, IsDirty, IsMissing, analysis progress) is not stored;
// - track type is implied by the list a track is in (videoTracks / audioTracks).

internal sealed class ProjectFileDto
{
    /// <summary>Identifies the file as an AI Video Editor project.</summary>
    public string? Format { get; set; }
    public int FormatVersion { get; set; }

    public Guid Id { get; set; }
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }

    public ProjectSettingsDto? Settings { get; set; }
    public List<MediaAssetDto>? MediaAssets { get; set; }
    public SequenceDto? Timeline { get; set; }
    public ExportSettingsDto? LastExportSettings { get; set; }
}

internal sealed class FrameRateDto
{
    public int Numerator { get; set; }
    public int Denominator { get; set; }
}

internal sealed class ProjectSettingsDto
{
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
    public FrameRateDto? FrameRate { get; set; }
    public bool IsFrameRateLocked { get; set; }
    public int AudioSampleRate { get; set; }
}

internal sealed class MediaAssetDto
{
    public Guid Id { get; set; }

    /// <summary>Absolute path at the time of saving.</summary>
    public string? FilePath { get; set; }

    /// <summary>Path relative to the project folder, or null when the file is on another
    /// volume. Used when the absolute path no longer exists (project moved with its media).</summary>
    public string? RelativePath { get; set; }

    public long FileSizeBytes { get; set; }
    public MediaKind Kind { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public string? ThumbnailPath { get; set; }

    /// <summary>ffprobe result; null if analysis hadn't completed successfully.</summary>
    public MediaMetadataDto? Metadata { get; set; }
}

internal sealed class MediaMetadataDto
{
    public long DurationTicks { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public FrameRateDto? FrameRate { get; set; }
    public FrameRateDto? AvgFrameRate { get; set; }
    public long? StartTimeTicks { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioSampleRate { get; set; }
    public long? BitrateBps { get; set; }
}

internal sealed class SequenceDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public List<TrackDto>? VideoTracks { get; set; }
    public List<TrackDto>? AudioTracks { get; set; }
    public List<MarkerDto>? Markers { get; set; }
    public long PlayheadTicks { get; set; }
    public double ZoomPixelsPerSecond { get; set; }
    public bool SnappingEnabled { get; set; }
}

internal sealed class TrackDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public int Order { get; set; }
    public bool IsMuted { get; set; }
    public bool IsHidden { get; set; }
    public bool IsLocked { get; set; }
    public List<ClipDto>? Clips { get; set; }
    public List<TransitionDto>? Transitions { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(VideoClipDto), "video")]
[JsonDerivedType(typeof(AudioClipDto), "audio")]
[JsonDerivedType(typeof(ImageClipDto), "image")]
[JsonDerivedType(typeof(TextClipDto), "text")]
internal abstract class ClipDto
{
    public Guid Id { get; set; }
    public long TimelineStartTicks { get; set; }
    public long DurationTicks { get; set; }
    public List<EffectDto>? Effects { get; set; }
}

internal abstract class MediaBackedClipDto : ClipDto
{
    public Guid MediaAssetId { get; set; }
    public long SourceInTicks { get; set; }
    public long SourceOutTicks { get; set; }
    public double Speed { get; set; }
}

internal sealed class VideoClipDto : MediaBackedClipDto
{
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double Scale { get; set; }
    public double RotationDegrees { get; set; }
    public double Opacity { get; set; }
    public double Volume { get; set; }
    public CropDto? Crop { get; set; }
}

internal sealed class AudioClipDto : MediaBackedClipDto
{
    public double Volume { get; set; }
    public bool IsMuted { get; set; }
}

internal sealed class ImageClipDto : MediaBackedClipDto
{
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double Scale { get; set; }
    public double RotationDegrees { get; set; }
    public double Opacity { get; set; }
    public CropDto? Crop { get; set; }
}

internal sealed class TextClipDto : ClipDto
{
    public string? Text { get; set; }
    public string? FontFamily { get; set; }
    public double FontSize { get; set; }
    public string? ColorHex { get; set; }
    public TextAlignment Alignment { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double Scale { get; set; }
    public double RotationDegrees { get; set; }
    public double Opacity { get; set; }
}

internal sealed class CropDto
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }
}

internal sealed class EffectDto
{
    public Guid Id { get; set; }
    public string? EffectTypeId { get; set; }
    public string? DisplayName { get; set; }
    public bool IsEnabled { get; set; }
    public Dictionary<string, JsonElement>? Parameters { get; set; }
}

internal sealed class TransitionDto
{
    public Guid Id { get; set; }
    public string? TransitionTypeId { get; set; }
    public long DurationTicks { get; set; }
}

internal sealed class MarkerDto
{
    public Guid Id { get; set; }
    public long PositionTicks { get; set; }
    public string? Label { get; set; }
    public string? ColorHex { get; set; }
}

internal sealed class ExportSettingsDto
{
    public string? OutputPath { get; set; }
    public ExportContainer Container { get; set; }
    public ExportVideoCodec VideoCodec { get; set; }
    public ExportAudioCodec AudioCodec { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public long? VideoBitrateBps { get; set; }
    public long? AudioBitrateBps { get; set; }
}

/// <summary>Autosave recovery file: the project exactly as it was at autosave time, plus
/// where it belongs and which process wrote it.</summary>
internal sealed class RecoveryFileDto
{
    public string? Format { get; set; }
    public int FormatVersion { get; set; }

    /// <summary>Folder of the project the recovery belongs to; null if it had never been saved.</summary>
    public string? ProjectFolderPath { get; set; }
    public DateTimeOffset AutosavedAt { get; set; }
    public int ProcessId { get; set; }
    public DateTimeOffset? ProcessStartTime { get; set; }
    public ProjectFileDto? Project { get; set; }
}
