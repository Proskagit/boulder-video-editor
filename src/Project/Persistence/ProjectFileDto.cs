using System.Text.Json;
using System.Text.Json.Serialization;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Project.Persistence;

// On-disk shape of project.json (format version 3; 1 and 2 are still read). Kept separate from the runtime
// entities so the file format only changes deliberately. Conventions:
// - every MediaTime is a long of 100 ns ticks (never seconds);
// - every FrameRate is an exact { numerator, denominator } pair;
// - runtime/UI state (IsSelected, IsDirty, IsMissing, analysis progress) is not stored, nor is session state
//   such as LastExportSettings (D023; a "lastExportSettings" of older files is ignored like any unknown property);
// - the export settings saved with the project are "settings.export" (D028, optional in v3, see ProjectSettingsDto);
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

    /// <summary>Added in Phase 13 (D028, Step 13.3) without a format version change (optional): how the project is
    /// encoded on export. Written only when it differs from <see cref="ExportEncoding.Default"/>, so a project with the
    /// default settings is saved exactly as before; absent (or null) reads as the default. Not to be confused with the
    /// ignored top-level <c>lastExportSettings</c> of Phases 6–8.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExportEncodingDto? Export { get; set; }
}

/// <summary>Every field is required when the object is present. <see cref="Quality"/> and <see cref="Preset"/> are the
/// exact names of <see cref="ExportQuality"/> / <see cref="ExportSpeedPreset"/> (e.g. "High", "Medium"): kept as strings
/// so that only those names are accepted — no numbers, other spellings or combinations.</summary>
internal sealed class ExportEncodingDto
{
    public string? Quality { get; set; }
    public string? Preset { get; set; }
    public int? AudioBitrateKbps { get; set; }
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

    /// <summary>Coded picture size.</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>Added in Phase 7 without a format version change (optional): display orientation
    /// (clockwise 0/90/180/270, null = not a right-angle rotation) and the size of the frames the
    /// decoder delivers. Metadata written before them has a coded size but no display size; it is
    /// kept, and the media is probed again when it is available.</summary>
    public int? DisplayRotation { get; set; }
    public int? DisplayWidth { get; set; }
    public int? DisplayHeight { get; set; }
    public FrameRateDto? FrameRate { get; set; }
    public FrameRateDto? AvgFrameRate { get; set; }
    public long? StartTimeTicks { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioSampleRate { get; set; }
    public long? BitrateBps { get; set; }

    /// <summary>Added in Phase 18 (D033 HQ2, T2) without a format version change (optional, written only when known):
    /// the stream's colour as ffprobe names it. A video analysed since Phase 18 always has <see cref="PixelFormat"/>;
    /// metadata without it was saved before and is probed again in the background (not dirty). The bit depth is not
    /// stored: it follows from the pixel format.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PixelFormat { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorTransfer { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorPrimaries { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorMatrix { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorRange { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MasteringMaxLuminance { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxContentLightLevel { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DolbyVisionProfile { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DolbyVisionCompatibility { get; set; }

    /// <summary>Written only when true.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasHdrVivid { get; set; }
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

    /// <summary>Format v3 (D025): the fades. Ignored in v1 / v2 files, which have none.</summary>
    public long FadeInTicks { get; set; }
    public long FadeOutTicks { get; set; }

    public List<EffectDto>? Effects { get; set; }
}

internal abstract class MediaBackedClipDto : ClipDto
{
    public Guid MediaAssetId { get; set; }
    public long SourceInTicks { get; set; }
    public long SourceOutTicks { get; set; }

    /// <summary>Format v1 only: the speed as a number, always 1 in files the editor could write.
    /// Not written from v2 on (see <see cref="SpeedRatio"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Speed { get; set; }

    /// <summary>Format v2: the exact speed as a reduced fraction (D022).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SpeedDto? SpeedRatio { get; set; }

    /// <summary>Format v4 (D032 R2): the link of a video and an audio clip; written only when set. Ignored in older
    /// files, which have none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LinkId { get; set; }
}

internal sealed class SpeedDto
{
    public long Numerator { get; set; }
    public long Denominator { get; set; }
}

internal sealed class VideoClipDto : MediaBackedClipDto
{
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double Scale { get; set; }
    public double RotationDegrees { get; set; }
    public double Opacity { get; set; }
    public double Volume { get; set; }

    /// <summary>Added in Phase 7 without a format version change: absent in older v1 files,
    /// which then load unmuted.</summary>
    public bool IsMuted { get; set; }

    public CropDto? Crop { get; set; }

    /// <summary>Format v4 (D032 R2): the clip's sound was unlinked into an audio clip; written only when true.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsAudioDetached { get; set; }

    /// <summary>Format v5 (D034): the clip's hold markup; written only when set. Ignored in older files, which have none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HoldMarkupDto? HoldMarkup { get; set; }
}

/// <summary>Format v5 (D034 §6): a video clip's hold markup. Every member is required when the markup is present.</summary>
internal sealed class HoldMarkupDto
{
    public double? StrokeWidth1080 { get; set; }
    public HoldColorMode? ColorMode { get; set; }
    public string? ColorHex { get; set; }
    public KeylineMode? Keyline { get; set; }
    public bool? KeylineActive { get; set; }
    public string? ModelId { get; set; }
    public List<HoldDto?>? Holds { get; set; }

    /// <summary>Format v6 (D036): the outline animation; written only when on. Ignored in older files, which have none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HoldAnimationDto? Animation { get; set; }
}

/// <summary>Format v6 (D036 §2): the appear / disappear animation; times in ticks — the start a source time, the rest
/// timeline durations. Every member is required when the animation is present.</summary>
internal sealed class HoldAnimationDto
{
    public HoldAnimationOrder? Order { get; set; }
    public long? StartSourceTimeTicks { get; set; }
    public long? IntervalTicks { get; set; }
    public long? AppearFadeTicks { get; set; }
    public long? StayAfterLastTicks { get; set; }
    public bool? StayUntilClipEnd { get; set; }
    public long? DisappearFadeTicks { get; set; }
}

/// <summary>One hold: the contour as a flat list <c>[x0, y0, x1, y1, …]</c> of normalized source coordinates, the colour
/// samples (OKLab) and the markup frame's source time in ticks.</summary>
internal sealed class HoldDto
{
    public Guid Id { get; set; }
    public List<double>? Points { get; set; }
    public OkLabDto? SurfaceLab { get; set; }
    public OkLabDto? WallLab { get; set; }
    public long? SourceTimeTicks { get; set; }
}

internal sealed class OkLabDto
{
    public double? L { get; set; }
    public double? A { get; set; }
    public double? B { get; set; }
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

    // Format v7 (D043 §5): optional, written only when the text has them (a text like an older version's is saved as
    // before apart from formatVersion). Every member of an object is required when the object is present.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Bold { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextOutlineDto? Outline { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextBackgroundDto? Background { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextMotionDto? MotionIn { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextMotionDto? MotionOut { get; set; }
}

/// <summary>Format v7: «Обводка» — on / off, the colour, the thickness (× the font size).</summary>
internal sealed class TextOutlineDto
{
    public bool? Enabled { get; set; }
    public string? ColorHex { get; set; }
    public double? Thickness { get; set; }
}

/// <summary>Format v7: «Подложка» — on / off, the colour, the opacity.</summary>
internal sealed class TextBackgroundDto
{
    public bool? Enabled { get; set; }
    public string? ColorHex { get; set; }
    public double? Opacity { get; set; }
}

/// <summary>Format v7: an appearance / disappearance motion; its duration is the clip's fade.</summary>
internal sealed class TextMotionDto
{
    public TextMotionKind? Kind { get; set; }
    public TextMotionSide? Side { get; set; }
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

    /// <summary>Format v3 (D025): the clips left and right of the cut. v1 / v2 transitions had no anchor; they are
    /// dropped on load.</summary>
    public Guid LeftClipId { get; set; }
    public Guid RightClipId { get; set; }

    /// <summary>Format v8 (D044 §9): the side a push's incoming clip comes from — <c>fromRight</c>, <c>fromLeft</c>,
    /// <c>fromTop</c> or <c>fromBottom</c>; written for a push only, ignored in a file older than v8.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Direction { get; set; }
}

internal sealed class MarkerDto
{
    public Guid Id { get; set; }
    public long PositionTicks { get; set; }
    public string? Label { get; set; }
    public string? ColorHex { get; set; }
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
