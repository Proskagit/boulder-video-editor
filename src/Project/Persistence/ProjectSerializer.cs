using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Telemetry;

namespace AiVideoEditor.Project.Persistence;

/// <summary>
/// Converts a <see cref="Core.Entities.Project"/> to and from project.json text (writes format
/// version 2, reads 1 and 2, see <see cref="ProjectFileDto"/>). Pure apart from the file-existence check
/// used to resolve media paths on load; reading and writing files is
/// <see cref="ProjectFileStore"/>'s job.
/// </summary>
/// <remarks>
/// <see cref="Deserialize"/> builds a completely new project and validates it before
/// returning, so a caller can keep the current project untouched when it throws.
/// It checks the structural invariants the file itself must satisfy (ids, references,
/// clip kinds, frame grid, overlaps). It does not compare clips with the source media on
/// disk — the files may have changed or be missing, which is a runtime condition
/// (offline media), not a damaged project.
/// </remarks>
public static class ProjectSerializer
{
    public const string FormatId = "AiVideoEditor.Project";
    /// <summary>14 since R2 → E (K3): a hold markup's "Follow the camera" off. 13 since Phase 35.5: the heart-rate overlay (a template kind with its look and corner). 12 since Phase 37
    /// (D051): the FIT marks and the declined climbs. 11 since Phase 35 (D049): the COROS training. 10 since D048: the route
    /// timing and the template anchor. 9 since Phase 29 (D045): templates.
    /// 8 since Phase 28 (D044): the transition library — Dip to black, Flash, Zoom and Push with its direction.
    /// 7 since Phase 27 (D043): bold, the outline, the background and the motions of text clips. 6 since Phase 21 (D036): the animation of the hold outlines. 5 since Phase 19 (D034): the hold markup of video clips. 4 since Phase 17 (D032): links of video and audio
    /// clips, a video clip's unlinked sound, audio clips of video files and crossfades on audio tracks. 3 since Phase 10
    /// (D025): clip fades and anchored transitions. 2 since Phase 7 Step 9: the clip speed is an exact fraction (D022).
    /// Version 1 files (speed must be 1) and version 2 files are read as projects without fades and transitions, version 3
    /// files without links, version 4 files without hold markup, version 5 files without the outline animation, version 6
    /// files without the text style and motion, version 7 files with dissolves only; all are saved as version 8. A newer
    /// version is refused, so an older build never drops what it doesn't know on a save.
    /// </summary>
    public const int CurrentFormatVersion = 18;
    public const string RecoveryFormatId = "AiVideoEditor.Recovery";

    /// <summary>The first version with fades and anchored transitions (D025).</summary>
    private const int FadesAndTransitionsVersion = 3;

    /// <summary>The first version with links, unlinked sound and audio clips of video files (D032).</summary>
    private const int AudioEditingVersion = 4;

    /// <summary>The first version with the hold markup of video clips (D034).</summary>
    private const int HoldMarkupVersion = 5;

    /// <summary>The first version with the animation of the hold outlines (D036).</summary>
    private const int HoldAnimationVersion = 6;

    /// <summary>The first version with the style and motion of text clips (D043).</summary>
    private const int TextStyleVersion = 7;

    /// <summary>The first version with the transition library: types beyond the dissolve and a push's direction (D044).</summary>
    private const int TransitionLibraryVersion = 8;

    /// <summary>The first version with templates (D045).</summary>
    private const int TemplatesVersion = 9;

    /// <summary>The first version with the duration mode and the new orders of the hold animation and the template ↔ clip
    /// anchor (D048). A v9 file is read with every animation in the interval mode and no anchor, and saved as v10.</summary>
    private const int RouteTimingVersion = 10;

    /// <summary>The first version with the COROS training (D049 §7): the FIT reference and its clock correction, a video's
    /// recording-zero override, the recording-time probe of the metadata. A v10 file is read without them and saved as v11.</summary>
    private const int TrainingVersion = 11;

    /// <summary>The first version with the FIT marks and the declined climbs of the training (D051 §5.3). A v11 file is read
    /// without them and saved as v12.</summary>
    private const int FitStartsVersion = 12;

    /// <summary>The first version with the heart-rate overlay (Phase 35.5): the template kind and its look and corner. A v12
    /// file has none and is saved as v13.</summary>
    private const int HeartRateOverlayVersion = 13;

    /// <summary>The first version with a hold markup's "Follow the camera" (R2 → E, K3): written only when off. A v13 file
    /// has every markup following the camera and is saved as v14.</summary>
    private const int FollowsCameraVersion = 14;

    /// <summary>The first version with several routes on a video clip (D053): each markup with its route id, the route of an
    /// anchored template and of a FIT link, the FIT climb of a route's green. A v14 file's markup is read as the clip's one
    /// route (its templates anchored to it; an anchored template of a clip without markup gets a route of its own, its green
    /// only) and is saved as v15.</summary>
    private const int RoutesVersion = 15;

    /// <summary>The first version with a green route area's heart rate (D055 §5): a «Боулдер» stores whether it shows the
    /// heart rate, its look, corner and heart colour. A v15 file's areas show it with the defaults and are saved as v16.</summary>
    private const int HeartRateAreaVersion = 16;

    /// <summary>The first version with the laid-out bounds of a FIT area (D056 §2). A v16 file's FIT areas have none (they
    /// are laid out again by a new <c>Z</c> only when their bounds are the rule's by the current time) and are saved as v17.</summary>
    private const int FitLayoutVersion = 17;

    /// <summary>The first version with a route area's computed start <c>T</c> (D056 §4). A v17 file's areas have none (an
    /// area with a Duration animation shows its end as <c>T</c>) and are saved as v18.</summary>
    private const int RouteStartVersion = 18;

    /// <summary>Whether a template of <paramref name="kind"/> in a file of <paramref name="formatVersion"/> carries the
    /// heart-rate look and corner: the overlay (v13), a «Боулдер» from v16 (D055 §5).</summary>
    private static bool HasHeartRateFields(TemplateKind? kind, int formatVersion) =>
        kind == TemplateKind.HeartRate || (kind == TemplateKind.Boulder && formatVersion >= HeartRateAreaVersion);

    /// <summary>A push's direction in the file (D044 §9).</summary>
    private static readonly IReadOnlyDictionary<TransitionDirection, string> DirectionNames = new Dictionary<TransitionDirection, string>
    {
        [TransitionDirection.FromRight] = "fromRight",
        [TransitionDirection.FromLeft] = "fromLeft",
        [TransitionDirection.FromTop] = "fromTop",
        [TransitionDirection.FromBottom] = "fromBottom"
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    /// <summary>Serializes <paramref name="project"/> as it would be stored in
    /// <paramref name="projectFolderPath"/> (used for the media paths relative to it).
    /// Does not modify the project.</summary>
    public static string Serialize(Core.Entities.Project project, string projectFolderPath) =>
        Serialize(project, projectFolderPath, project.Name);

    /// <summary>As <see cref="Serialize(Core.Entities.Project, string)"/>, storing
    /// <paramref name="name"/> as the project name (Save As names the project after its
    /// folder, but only once the save has succeeded).</summary>
    internal static string Serialize(Core.Entities.Project project, string projectFolderPath, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(projectFolderPath);
        var dto = ToDto(project, Path.GetFullPath(projectFolderPath));
        dto.Name = name;
        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>Reads and validates project.json text. The returned project has
    /// <see cref="Core.Entities.Project.ProjectFolderPath"/> set to
    /// <paramref name="projectFolderPath"/>, is not dirty, and its media assets without
    /// usable metadata are <see cref="MediaAnalysisStatus.Pending"/>.</summary>
    /// <param name="fileExists">Existence check used to choose between a media file's
    /// saved absolute path and its path relative to the project folder; defaults to
    /// <see cref="File.Exists(string)"/>.</param>
    /// <exception cref="ProjectFileException">The text is not a valid project of a
    /// supported format version.</exception>
    public static Core.Entities.Project Deserialize(string json, string projectFolderPath, Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrEmpty(projectFolderPath);
        var folder = Path.GetFullPath(projectFolderPath);

        var dto = Parse<ProjectFileDto>(json, Messages.ProjectFile_DamagedPlain);
        CheckHeader(dto);
        return FromDto(dto!, folder, fileExists ?? File.Exists);
    }

    // ---- recovery (autosave) files -----------------------------------------------

    /// <summary>Serializes an autosave recovery file: <paramref name="project"/> exactly as it
    /// is now (same project DTO as project.json, including the unsaved name) wrapped with
    /// <paramref name="info"/>. Does not modify the project.</summary>
    public static string SerializeRecovery(Core.Entities.Project project, RecoveryInfo info)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(info);
        var folder = info.ProjectFolderPath is null ? null : Path.GetFullPath(info.ProjectFolderPath);
        var dto = new RecoveryFileDto
        {
            Format = RecoveryFormatId,
            FormatVersion = CurrentFormatVersion,
            ProjectFolderPath = folder,
            AutosavedAt = info.AutosavedAt,
            ProcessId = info.ProcessId,
            ProcessStartTime = info.ProcessStartTime,
            Project = ToDto(project, folder)
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>Reads a recovery file. The project inside goes through exactly the same
    /// validation as <see cref="Deserialize"/>; its <see cref="Core.Entities.Project.ProjectFolderPath"/>
    /// is the folder it belonged to (null if it had never been saved).</summary>
    /// <exception cref="ProjectFileException">Not a valid recovery file.</exception>
    public static (Core.Entities.Project Project, RecoveryInfo Info) DeserializeRecovery(string json, Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var damaged = Messages.ProjectFile_RecoveryDamaged;

        var dto = Parse<RecoveryFileDto>(json, damaged);
        if (dto is null || dto.Format != RecoveryFormatId)
            throw new ProjectFileException(Messages.ProjectFile_NotRecovery);
        if (dto.FormatVersion > CurrentFormatVersion)
            throw new ProjectFileException(Messages.ProjectFile_RecoveryNewer);
        if (dto.FormatVersion < 1 || dto.Project is null)
            throw new ProjectFileException(damaged);

        string? folder;
        try
        {
            folder = string.IsNullOrWhiteSpace(dto.ProjectFolderPath) ? null : Path.GetFullPath(dto.ProjectFolderPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectFileException(damaged, ex);
        }

        CheckHeader(dto.Project);
        var project = FromDto(dto.Project, folder, fileExists ?? File.Exists);
        return (project, new RecoveryInfo(folder, dto.AutosavedAt, dto.ProcessId, dto.ProcessStartTime));
    }

    private static T? Parse<T>(string json, string damagedMessage) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or OverflowException)
        {
            throw new ProjectFileException(damagedMessage, ex);
        }
    }

    private static void CheckHeader(ProjectFileDto? dto)
    {
        if (dto is null || dto.Format != FormatId)
            throw new ProjectFileException(Messages.ProjectFile_NotProject);
        if (dto.FormatVersion > CurrentFormatVersion)
            throw new ProjectFileException(Messages.ProjectFile_Newer);
        if (dto.FormatVersion < 1)
            throw Damaged("unknown format version");
    }

    // ---- entity → DTO ----------------------------------------------------------

    private static ProjectFileDto ToDto(Core.Entities.Project p, string? folder) => new()
    {
        Format = FormatId,
        FormatVersion = CurrentFormatVersion,
        Id = p.Id,
        Name = p.Name,
        CreatedAt = p.CreatedAt,
        ModifiedAt = p.ModifiedAt,
        Settings = new ProjectSettingsDto
        {
            FrameWidth = p.Settings.FrameWidth,
            FrameHeight = p.Settings.FrameHeight,
            FrameRate = ToDto(p.Settings.FrameRate),
            IsFrameRateLocked = p.Settings.IsFrameRateLocked,
            AudioSampleRate = p.Settings.AudioSampleRate,
            Export = ToDto(p.Settings.Export)
        },
        MediaAssets = p.MediaAssets.Select(a => ToDto(a, folder)).ToList(),
        Timeline = ToDto(p.Timeline),
        // LastExportSettings is session state (D023): never written.
        Training = p.Training is { } t ? ToDto(t, folder) : null
    };

    private static TrainingDto ToDto(TrainingReference t, string? folder)
    {
        var absolute = Path.GetFullPath(t.FilePath);
        var relative = folder is null ? null : Path.GetRelativePath(folder, absolute);
        return new TrainingDto
        {
            Id = t.Id,
            FilePath = absolute,
            RelativePath = relative is null || Path.IsPathRooted(relative) ? null : relative,
            FileSizeBytes = t.FileSizeBytes,
            Sha256 = t.Sha256,
            Activity = new TrainingActivityDto
            {
                StartUtc = t.Activity.StartUtc.ToUniversalTime(),
                EndUtc = t.Activity.EndUtc.ToUniversalTime(),
                Manufacturer = t.Activity.Manufacturer,
                Product = t.Activity.Product
            },
            ClockCorrectionTicks = t.ClockCorrection.Ticks,
            FitMarks = t.Marks.Count == 0 ? null : t.Marks.Select(ToDto).ToList(),
            DeclinedClimbs = t.DeclinedClimbs.Count == 0
                ? null
                : t.DeclinedClimbs.Select(d => new DeclinedClimbDto
                {
                    Climb = ToDto(d.Climb), FormerMark = d.FormerMark is { } m ? ToDto(m) : null
                }).ToList()
        };
    }

    private static ClimbIdentityDto ToDto(ClimbIdentity c) =>
        new() { Key = c.ClimbKey, Number = c.ClimbNumber, StartUtc = c.ClimbStartUtc.ToUniversalTime() };

    private static FitMarkDto ToDto(FitMark m) => new()
    {
        Climb = ToDto(m.Climb),
        AutoStartUtc = m.AutoStartUtc.ToUniversalTime(),
        StartCorrectionTicks = m.StartCorrectionTicks,
        CalibrationVersion = m.CalibrationVersion,
        MediaAssetId = m.MediaAssetId,
        Position = m.Position.Kind switch
        {
            FitMarkPositionKind.Proposed => "proposed",
            FitMarkPositionKind.Clamped => "clamped",
            _ => "moved"
        },
        SourceTimeTicks = m.Position.SourceTime?.Ticks,
        PositionChangedAt = m.PositionChangedAt.ToUniversalTime(),
        LinkedClipId = m.LinkedClipId,
        LinkedRouteId = m.LinkedClipId is null ? null : m.LinkedRouteId
    };

    private static FrameRateDto ToDto(FrameRate r) => new() { Numerator = r.Numerator, Denominator = r.Denominator };

    /// <summary>Null (not written) for the default settings (D028, Step 13.3).</summary>
    private static ExportEncodingDto? ToDto(ExportEncoding e) => e == ExportEncoding.Default
        ? null
        : new() { Quality = e.Quality.ToString(), Preset = e.Preset.ToString(), AudioBitrateKbps = e.AudioBitrateKbps };

    private static MediaAssetDto ToDto(MediaAsset a, string? folder)
    {
        var absolute = Path.GetFullPath(a.FilePath);
        var relative = folder is null ? null : Path.GetRelativePath(folder, absolute);
        return new MediaAssetDto
        {
            Id = a.Id,
            FilePath = absolute,
            // GetRelativePath returns the absolute path unchanged when the file is on another volume.
            RelativePath = relative is null || Path.IsPathRooted(relative) ? null : relative,
            FileSizeBytes = a.FileSizeBytes,
            Kind = a.Kind,
            ImportedAt = a.ImportedAt,
            ThumbnailPath = a.ThumbnailPath,
            // Only a completed analysis is worth keeping; anything else is redone after Open.
            Metadata = a.AnalysisStatus == MediaAnalysisStatus.Completed && a.Metadata is { } m ? ToDto(m) : null,
            RecordingZeroOverrideUtc = a.RecordingZeroOverrideUtc?.ToUniversalTime()
        };
    }

    private static MediaMetadataDto ToDto(MediaMetadata m) => new()
    {
        DurationTicks = m.Duration.Ticks,
        Width = m.Width,
        Height = m.Height,
        DisplayRotation = m.DisplayRotation,
        DisplayWidth = m.DisplayWidth,
        DisplayHeight = m.DisplayHeight,
        FrameRate = m.FrameRate is { } fr ? ToDto(fr) : null,
        AvgFrameRate = m.AvgFrameRate is { } afr ? ToDto(afr) : null,
        StartTimeTicks = m.StartTime?.Ticks,
        VideoCodec = m.VideoCodec,
        AudioCodec = m.AudioCodec,
        AudioChannels = m.AudioChannels,
        AudioSampleRate = m.AudioSampleRate,
        BitrateBps = m.BitrateBps,
        PixelFormat = m.PixelFormat,
        ColorTransfer = m.ColorTransfer,
        ColorPrimaries = m.ColorPrimaries,
        ColorMatrix = m.ColorMatrix,
        ColorRange = m.ColorRange,
        MasteringMaxLuminance = m.MasteringMaxLuminance,
        MaxContentLightLevel = m.MaxContentLightLevel,
        DolbyVisionProfile = m.DolbyVisionProfile,
        DolbyVisionCompatibility = m.DolbyVisionCompatibility,
        HasHdrVivid = m.HasHdrVivid ? true : null,
        RecordingTimeProbe = m.RecordingTimeProbe is { } probe ? ToDto(probe) : null
    };

    private static RecordingTimeProbeDto ToDto(RecordingTimeProbe probe) => probe.IsFound
        ? new() { Status = "found", Tag = probe.Tag, Origin = probe.Origin == RecordingTimeTagOrigin.Format ? "format" : "videoStream" }
        : new() { Status = "absent" };

    private static SequenceDto ToDto(Sequence s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        VideoTracks = s.VideoTracks.Select(ToDto).ToList(),
        AudioTracks = s.AudioTracks.Select(ToDto).ToList(),
        Markers = s.Markers.Select(m => new MarkerDto { Id = m.Id, PositionTicks = m.Position.Ticks, Label = m.Label, ColorHex = m.ColorHex }).ToList(),
        PlayheadTicks = s.PlayheadPosition.Ticks,
        ZoomPixelsPerSecond = s.ZoomPixelsPerSecond,
        SnappingEnabled = s.SnappingEnabled
    };

    private static TrackDto ToDto(Track t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Order = t.Order,
        IsMuted = t.IsMuted,
        IsHidden = t.IsHidden,
        IsLocked = t.IsLocked,
        Clips = t.Clips.Select(ToDto).ToList(),
        Transitions = t.Transitions.Select(x => new TransitionDto
        {
            Id = x.Id, TransitionTypeId = x.TransitionTypeId, DurationTicks = x.Duration.Ticks,
            LeftClipId = x.LeftClipId, RightClipId = x.RightClipId,
            Direction = x.Direction is { } direction ? DirectionNames[direction] : null
        }).ToList()
    };

    private static ClipDto ToDto(Clip clip)
    {
        ClipDto dto = clip switch
        {
            VideoClip v => new VideoClipDto
            {
                PositionX = v.PositionX, PositionY = v.PositionY, Scale = v.Scale, RotationDegrees = v.RotationDegrees,
                Opacity = v.Opacity, Volume = v.Volume, IsMuted = v.IsMuted, Crop = ToDto(v.Crop),
                IsAudioDetached = v.IsAudioDetached,
                Routes = v.Routes.IsDefaultOrEmpty ? null : v.Routes.Select(r => (HoldMarkupDto?)ToDto(r)).ToList()
            },
            AudioClip a => new AudioClipDto { Volume = a.Volume, IsMuted = a.IsMuted },
            ImageClip i => new ImageClipDto
            {
                PositionX = i.PositionX, PositionY = i.PositionY, Scale = i.Scale, RotationDegrees = i.RotationDegrees,
                Opacity = i.Opacity, Crop = ToDto(i.Crop)
            },
            TextClip t => new TextClipDto
            {
                Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, ColorHex = t.ColorHex, Alignment = t.Alignment,
                PositionX = t.PositionX, PositionY = t.PositionY, Scale = t.Scale, RotationDegrees = t.RotationDegrees, Opacity = t.Opacity,
                Bold = t.Bold ? true : null,
                Outline = t.Outline == TextOutline.Default ? null
                    : new TextOutlineDto { Enabled = t.Outline.IsEnabled, ColorHex = t.Outline.ColorHex, Thickness = t.Outline.Thickness },
                Background = t.Background == TextBackground.Default ? null
                    : new TextBackgroundDto { Enabled = t.Background.IsEnabled, ColorHex = t.Background.ColorHex, Opacity = t.Background.Opacity },
                MotionIn = ToDto(t.MotionIn),
                MotionOut = ToDto(t.MotionOut)
            },
            TemplateClip p => new TemplateClipDto
            {
                Template = p.Kind, CounterStartTicks = p.CounterStart.Ticks, Date = p.Date, Route = p.Route, Grade = p.Grade,
                Status = p.Status, AccentHex = p.AccentHex, Title = p.Title, Subtitle = p.Subtitle, StatusAtTicks = p.StatusAt.Ticks,
                AnchorClipId = p.AnchorClipId,
                AnchorRouteId = p.AnchorClipId is null ? null : p.AnchorRouteId,
                FitClimb = p.AnchorClipId is not null && p.FitClimb is { } fitClimb ? ToDto(fitClimb) : null,
                FitLayout = p.AnchorClipId is not null && p.FitClimb is not null && p.FitLayout is { } layout
                    ? new FitAreaLayoutDto { SourceStartTicks = layout.SourceStart.Ticks, SourceEndTicks = layout.SourceEnd.Ticks, StatusAtTicks = layout.StatusAt.Ticks }
                    : null,
                RouteStartTicks = p.AnchorClipId is not null && p.RouteStart is { } routeStart ? routeStart.Ticks : null,
                Look = p.Kind is TemplateKind.HeartRate or TemplateKind.Boulder ? p.Look : null,
                Corner = p.Kind is TemplateKind.HeartRate or TemplateKind.Boulder ? p.Corner : null,
                ShowHeartRate = p.Kind == TemplateKind.Boulder ? p.ShowHeartRate : null,
                HeartColorHex = p.Kind == TemplateKind.Boulder ? p.HeartHex : null
            },
            _ => throw new NotSupportedException($"Clip type {clip.GetType().Name} can't be saved.")
        };

        dto.Id = clip.Id;
        dto.TimelineStartTicks = clip.TimelineStart.Ticks;
        dto.DurationTicks = clip.Duration.Ticks;
        dto.FadeInTicks = clip.FadeIn.Ticks;
        dto.FadeOutTicks = clip.FadeOut.Ticks;
        dto.Effects = clip.Effects.Select(ToDto).ToList();

        if (clip is MediaBackedClip media && dto is MediaBackedClipDto mediaDto)
        {
            mediaDto.MediaAssetId = media.MediaAssetId;
            mediaDto.SourceInTicks = media.SourceIn.Ticks;
            mediaDto.SourceOutTicks = media.SourceOut.Ticks;
            mediaDto.SpeedRatio = new SpeedDto { Numerator = media.Speed.Numerator, Denominator = media.Speed.Denominator };
            mediaDto.LinkId = media.LinkId;
        }

        return dto;
    }

    private static HoldMarkupDto ToDto(HoldMarkup m) => new()
    {
        Id = m.Id,
        StrokeWidth1080 = m.StrokeWidth1080, ColorMode = m.ColorMode, ColorHex = m.ColorHex, Keyline = m.Keyline,
        KeylineActive = m.KeylineActive, ModelId = m.ModelId,
        FollowsCamera = m.FollowsCamera ? null : false,
        Holds = m.Holds.Select(h => (HoldDto?)new HoldDto
        {
            Id = h.Id,
            Points = h.Points.SelectMany(p => new[] { p.X, p.Y }).ToList(),
            SurfaceLab = ToDto(h.SurfaceLab), WallLab = ToDto(h.WallLab), SourceTimeTicks = h.SourceTime.Ticks
        }).ToList(),
        Animation = m.Animation is { } a
            ? new HoldAnimationDto
            {
                Order = a.Order, StartSourceTimeTicks = a.StartSourceTime.Ticks, IntervalTicks = a.Interval.Ticks,
                AppearFadeTicks = a.AppearFade.Ticks, StayAfterLastTicks = a.StayAfterLast.Ticks,
                StayUntilClipEnd = a.StayUntilClipEnd, DisappearFadeTicks = a.DisappearFade.Ticks,
                // D048: written only for the duration mode, so an interval-mode animation is saved as in v9.
                Mode = a.Mode == HoldAnimationMode.Duration ? a.Mode : null,
                DurationTicks = a.Mode == HoldAnimationMode.Duration ? a.Duration.Ticks : null
            }
            : null
    };

    private static OkLabDto ToDto(OkLabColor c) => new() { L = c.L, A = c.A, B = c.B };

    private static CropDto ToDto(CropRect c) => new() { Left = c.Left, Top = c.Top, Right = c.Right, Bottom = c.Bottom };

    private static EffectDto ToDto(Effect e) => new()
    {
        Id = e.Id,
        EffectTypeId = e.EffectTypeId,
        DisplayName = e.DisplayName,
        IsEnabled = e.IsEnabled,
        Parameters = e.Parameters.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value, Options))
    };

    // ---- DTO → entity (with validation) ---------------------------------------

    private static Core.Entities.Project FromDto(ProjectFileDto dto, string? folder, Func<string, bool> fileExists)
    {
        RequireId(dto.Id, "project");
        var settingsDto = dto.Settings ?? throw Damaged("project settings are missing");
        if (settingsDto.FrameWidth <= 0 || settingsDto.FrameHeight <= 0) throw Damaged("invalid frame size");
        if (settingsDto.AudioSampleRate <= 0) throw Damaged("invalid audio sample rate");
        var frameRate = ReadFrameRate(settingsDto.FrameRate) ?? throw Damaged("invalid project frame rate");
        var export = ReadExportEncoding(settingsDto.Export);

        var project = new Core.Entities.Project
        {
            Id = dto.Id,
            Name = dto.Name ?? throw Damaged("project name is missing"),
            ProjectFolderPath = folder,
            CreatedAt = dto.CreatedAt,
            ModifiedAt = dto.ModifiedAt,
            Settings = new ProjectSettings
            {
                FrameWidth = settingsDto.FrameWidth,
                FrameHeight = settingsDto.FrameHeight,
                FrameRate = frameRate,
                IsFrameRateLocked = settingsDto.IsFrameRateLocked,
                AudioSampleRate = settingsDto.AudioSampleRate,
                Export = export
            },
            Timeline = new Sequence(),
            // LastExportSettings starts empty: session state (D023); a "lastExportSettings" of an older file is ignored.
            IsDirty = false
        };

        var assets = new Dictionary<Guid, MediaAsset>();
        foreach (var assetDto in dto.MediaAssets ?? throw Damaged("media list is missing"))
        {
            var asset = FromDto(assetDto ?? throw Damaged("empty media entry"), folder, fileExists, dto.FormatVersion);
            if (!assets.TryAdd(asset.Id, asset)) throw Damaged("duplicate media id");
            project.MediaAssets.Add(asset);
        }

        project.Timeline = FromDto(dto.Timeline ?? throw Damaged("timeline is missing"), frameRate, assets, dto.FormatVersion);
        if (dto.FormatVersion >= TrainingVersion && dto.Training is { } training)
        {
            project.Training = ReadTraining(training, folder, fileExists);
            if (dto.FormatVersion >= FitStartsVersion)
                ReadFitMarks(training, project.Training, assets, project.Timeline);
        }
        return project;
    }

    /// <summary>The FIT marks and declined climbs (D051 §5.2–§5.4): one active mark per climb, the declined climbs disjoint
    /// from them, each link to an existing video clip with a <c>Duration</c> hold animation, no clip linked twice, no link
    /// on a declined climb; every mark on a video of the project with a position of a valid kind. A climb missing from the
    /// FIT is not a damage — the file may be unavailable.</summary>
    private static void ReadFitMarks(TrainingDto dto, TrainingReference training, IReadOnlyDictionary<Guid, MediaAsset> assets,
        Sequence timeline)
    {
        var videoClips = timeline.VideoTracks.Concat(timeline.AudioTracks).SelectMany(t => t.Clips).OfType<VideoClip>()
            .ToDictionary(c => c.Id);
        var climbs = new HashSet<ClimbIdentity>();
        var linkedClips = new HashSet<(Guid Clip, Guid Route)>();
        foreach (var markDto in dto.FitMarks ?? [])
        {
            var mark = ReadFitMark(markDto ?? throw Damaged("empty FIT mark entry"), assets);
            if (!climbs.Add(mark.Climb)) throw Damaged("two active FIT marks for one climb");
            if (mark.LinkedClipId is { } clipId)
            {
                if (!videoClips.TryGetValue(clipId, out var clip)) throw Damaged("a FIT mark is linked to a missing clip");
                // D053: the link names a route of the clip (none named: the clip's single route).
                if (FitMarkRules.LinkedRoute(clip, mark.LinkedRouteId) is not { Animation.Mode: HoldAnimationMode.Duration } linkedRoute)
                    throw Damaged("a FIT mark is linked to a clip without a duration hold animation");
                if (!linkedClips.Add((clipId, linkedRoute.Id))) throw Damaged("one animation is linked to two FIT climbs");
            }
            training.Marks.Add(mark);
        }
        foreach (var declinedDto in dto.DeclinedClimbs ?? [])
        {
            if (declinedDto?.Climb is not { } climbDto) throw Damaged("a declined climb has no identity");
            var climb = ReadClimb(climbDto);
            if (!climbs.Add(climb)) throw Damaged("a declined climb is also active or declined twice");
            FitMark? former = null;
            if (declinedDto.FormerMark is { } formerDto)
            {
                former = ReadFitMark(formerDto, assets);
                if (former.Climb != climb) throw Damaged("a declined climb keeps the mark of another climb");
                if (former.LinkedClipId is not null) throw Damaged("a declined climb keeps a link");
            }
            training.DeclinedClimbs.Add(new DeclinedClimb(climb, former));
        }
    }

    private static FitAreaLayout ReadFitLayout(FitAreaLayoutDto dto) =>
        dto.SourceEndTicks > dto.SourceStartTicks && dto.StatusAtTicks >= 0
            ? new FitAreaLayout(new MediaTime(dto.SourceStartTicks), new MediaTime(dto.SourceEndTicks), new MediaTime(dto.StatusAtTicks))
            : throw Damaged("a FIT area's layout is invalid");

    private static ClimbIdentity ReadClimb(ClimbIdentityDto dto) => new(dto.Key, dto.Number, dto.StartUtc.ToUniversalTime());

    private static FitMark ReadFitMark(FitMarkDto dto, IReadOnlyDictionary<Guid, MediaAsset> assets)
    {
        var climb = ReadClimb(dto.Climb ?? throw Damaged("a FIT mark has no climb"));
        if (!assets.TryGetValue(dto.MediaAssetId, out var asset) || asset.Kind != MediaKind.Video)
            throw Damaged("a FIT mark is not on a video of the project");
        if (dto.CalibrationVersion < 0) throw Damaged("invalid FIT mark calibration version");
        var source = dto.SourceTimeTicks is { } ticks ? new MediaTime(ticks) : (MediaTime?)null;
        var position = dto.Position switch
        {
            "proposed" => new FitMarkPosition(FitMarkPositionKind.Proposed, source),
            "clamped" => new FitMarkPosition(FitMarkPositionKind.Clamped, source),
            "moved" => new FitMarkPosition(FitMarkPositionKind.Moved, source),
            _ => throw Damaged("unknown FIT mark position")
        };
        if (!position.IsValid) throw Damaged("a FIT mark's position does not match its kind");
        if (source is { } s && s < MediaTime.Zero) throw Damaged("negative FIT mark position");
        return new FitMark(climb, dto.AutoStartUtc.ToUniversalTime(), dto.StartCorrectionTicks, dto.CalibrationVersion,
            dto.MediaAssetId, position, dto.PositionChangedAt.ToUniversalTime(), dto.LinkedClipId,
            dto.LinkedClipId is null ? null : dto.LinkedRouteId);
    }

    /// <summary>The training reference (D049 §7): every member required; the path resolved as a media path (D014). The file
    /// itself is read after Open (its availability is runtime state).</summary>
    private static TrainingReference ReadTraining(TrainingDto dto, string? folder, Func<string, bool> fileExists)
    {
        RequireId(dto.Id, "training");
        if (string.IsNullOrWhiteSpace(dto.FilePath)) throw Damaged("training file path is missing");
        if (dto.FileSizeBytes < 0) throw Damaged("invalid training file size");
        if (dto.Sha256 is not { Length: 64 } sha || !sha.All(Uri.IsHexDigit)) throw Damaged("invalid training file hash");
        var activity = dto.Activity ?? throw Damaged("training activity is missing");
        if (activity.EndUtc < activity.StartUtc) throw Damaged("training activity ends before it starts");

        return new TrainingReference
        {
            Id = dto.Id,
            FilePath = ResolveMediaPath(dto.FilePath, dto.RelativePath, folder, fileExists),
            FileSizeBytes = dto.FileSizeBytes,
            Sha256 = sha.ToLowerInvariant(),
            Activity = new TrainingActivity(activity.StartUtc.ToUniversalTime(), activity.EndUtc.ToUniversalTime(),
                activity.Manufacturer, activity.Product),
            ClockCorrection = new MediaTime(dto.ClockCorrectionTicks)
        };
    }

    private static MediaAsset FromDto(MediaAssetDto dto, string? folder, Func<string, bool> fileExists, int formatVersion)
    {
        RequireId(dto.Id, "media");
        if (string.IsNullOrWhiteSpace(dto.FilePath)) throw Damaged("media file path is missing");
        if (!Enum.IsDefined(dto.Kind)) throw Damaged("unknown media kind");

        var hasTraining = formatVersion >= TrainingVersion;
        var metadata = ReadMetadata(dto.Metadata);
        if (metadata is not null && hasTraining && dto.Metadata!.RecordingTimeProbe is { } probe)
            metadata.RecordingTimeProbe = ReadRecordingTimeProbe(probe);
        var zeroOverride = hasTraining ? dto.RecordingZeroOverrideUtc : null;
        if (zeroOverride is not null && dto.Kind != MediaKind.Video) throw Damaged("recording-zero override on a non-video media");
        return new MediaAsset
        {
            Id = dto.Id,
            FilePath = ResolveMediaPath(dto.FilePath, dto.RelativePath, folder, fileExists),
            FileSizeBytes = dto.FileSizeBytes,
            Kind = dto.Kind,
            ImportedAt = dto.ImportedAt,
            ThumbnailPath = dto.ThumbnailPath,
            Metadata = metadata,
            AnalysisStatus = metadata is null ? MediaAnalysisStatus.Pending : MediaAnalysisStatus.Completed,
            RecordingZeroOverrideUtc = zeroOverride?.ToUniversalTime()
        };
    }

    /// <summary>A well-formed probe (D049 §7): "found" with a tag and an origin, or "absent" with neither.</summary>
    private static RecordingTimeProbe ReadRecordingTimeProbe(RecordingTimeProbeDto dto) => dto.Status switch
    {
        "found" when !string.IsNullOrEmpty(dto.Tag) && dto.Origin is "format" or "videoStream" =>
            RecordingTimeProbe.Found(dto.Tag!, dto.Origin == "format" ? RecordingTimeTagOrigin.Format : RecordingTimeTagOrigin.VideoStream),
        "absent" when dto.Tag is null && dto.Origin is null => RecordingTimeProbe.Absent,
        _ => throw Damaged("invalid recording-time probe")
    };

    /// <summary>The saved absolute path wins if the file is there; otherwise the path
    /// relative to the project folder if that file exists (project moved together with its
    /// media); otherwise the absolute path is kept and the asset will show as missing.</summary>
    private static string ResolveMediaPath(string savedPath, string? relativePath, string? folder, Func<string, bool> fileExists)
    {
        string absolute;
        try
        {
            absolute = folder is null ? Path.GetFullPath(savedPath) : Path.GetFullPath(savedPath, folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Damaged("invalid media file path", ex);
        }

        if (fileExists(absolute) || folder is null || string.IsNullOrWhiteSpace(relativePath))
            return absolute;

        try
        {
            var candidate = Path.GetFullPath(relativePath, folder);
            return fileExists(candidate) ? candidate : absolute;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return absolute;
        }
    }

    /// <summary>Metadata is a cache of ffprobe output: if it is internally inconsistent it is
    /// dropped (the asset is analysed again) rather than rejecting the whole project.</summary>
    private static MediaMetadata? ReadMetadata(MediaMetadataDto? dto)
    {
        if (dto is null || dto.DurationTicks < 0) return null;
        if (dto.FrameRate is not null && ReadFrameRate(dto.FrameRate) is null) return null;
        if (dto.AvgFrameRate is not null && ReadFrameRate(dto.AvgFrameRate) is null) return null;
        if (dto.DisplayRotation is { } rotation && rotation is not (0 or 90 or 180 or 270)) return null;
        if ((dto.DisplayWidth is null) != (dto.DisplayHeight is null)) return null;
        if (dto.DisplayWidth is <= 0 || dto.DisplayHeight is <= 0) return null;

        return new MediaMetadata
        {
            Duration = new MediaTime(dto.DurationTicks),
            Width = dto.Width,
            Height = dto.Height,
            DisplayRotation = dto.DisplayRotation,
            DisplayWidth = dto.DisplayWidth,
            DisplayHeight = dto.DisplayHeight,
            FrameRate = ReadFrameRate(dto.FrameRate),
            AvgFrameRate = ReadFrameRate(dto.AvgFrameRate),
            StartTime = dto.StartTimeTicks is { } st ? new MediaTime(st) : null,
            VideoCodec = dto.VideoCodec,
            AudioCodec = dto.AudioCodec,
            AudioChannels = dto.AudioChannels,
            AudioSampleRate = dto.AudioSampleRate,
            BitrateBps = dto.BitrateBps,
            PixelFormat = dto.PixelFormat,
            BitDepth = HdrColor.BitDepthOf(dto.PixelFormat),
            ColorTransfer = dto.ColorTransfer,
            ColorPrimaries = dto.ColorPrimaries,
            ColorMatrix = dto.ColorMatrix,
            ColorRange = dto.ColorRange,
            MasteringMaxLuminance = dto.MasteringMaxLuminance,
            MaxContentLightLevel = dto.MaxContentLightLevel,
            DolbyVisionProfile = dto.DolbyVisionProfile,
            DolbyVisionCompatibility = dto.DolbyVisionCompatibility,
            HasHdrVivid = dto.HasHdrVivid == true
        };
    }

    private static FrameRate? ReadFrameRate(FrameRateDto? dto) =>
        dto is { Numerator: > 0, Denominator: > 0 } ? new FrameRate(dto.Numerator, dto.Denominator) : null;

    /// <summary>The export settings of <c>settings.export</c> (D028, Step 13.3): absent or null = the default; when
    /// present, every field must be one of the offered values (EX-4: anything else is a damaged file, as D014).</summary>
    private static ExportEncoding ReadExportEncoding(ExportEncodingDto? dto)
    {
        if (dto is null) return ExportEncoding.Default;
        var encoding = new ExportEncoding(
            ReadName<ExportQuality>(dto.Quality, "export quality"),
            ReadName<ExportSpeedPreset>(dto.Preset, "export speed preset"),
            dto.AudioBitrateKbps ?? throw Damaged("export audio bitrate is missing"));
        if (encoding.Validate() is not null) throw Damaged("invalid export settings");
        return encoding;
    }

    /// <summary>The <typeparamref name="T"/> value whose name is exactly <paramref name="name"/>.</summary>
    private static T ReadName<T>(string? name, string what) where T : struct, Enum
    {
        if (name is null) throw Damaged($"{what} is missing");
        foreach (var value in Enum.GetValues<T>())
            if (string.Equals(value.ToString(), name, StringComparison.Ordinal)) return value;
        throw Damaged($"unknown {what}");
    }

    private static Sequence FromDto(SequenceDto dto, FrameRate rate, IReadOnlyDictionary<Guid, MediaAsset> assets, int formatVersion)
    {
        RequireId(dto.Id, "timeline");
        if (dto.PlayheadTicks < 0) throw Damaged("negative playhead position");
        if (!double.IsFinite(dto.ZoomPixelsPerSecond) || dto.ZoomPixelsPerSecond <= 0) throw Damaged("invalid timeline zoom");

        var sequence = new Sequence
        {
            Id = dto.Id,
            Name = dto.Name ?? string.Empty,
            PlayheadPosition = new MediaTime(dto.PlayheadTicks),
            ZoomPixelsPerSecond = dto.ZoomPixelsPerSecond,
            SnappingEnabled = dto.SnappingEnabled
        };

        var ids = new TimelineIds();
        foreach (var t in dto.VideoTracks ?? throw Damaged("video track list is missing"))
            sequence.VideoTracks.Add(FromDto(t ?? throw Damaged("empty track entry"), TrackType.Video, rate, assets, ids, formatVersion));
        foreach (var t in dto.AudioTracks ?? throw Damaged("audio track list is missing"))
            sequence.AudioTracks.Add(FromDto(t ?? throw Damaged("empty track entry"), TrackType.Audio, rate, assets, ids, formatVersion));

        // Transitions are checked once every track and clip is known (a clip problem is reported as such first).
        foreach (var track in sequence.VideoTracks.Concat(sequence.AudioTracks))
            if (TransitionRules.ValidateTrack(track, rate) is { } invalid)
                throw Damaged($"a transition on track {track.Name} is invalid: {invalid.TrimEnd('.')}");
        if (TransitionRules.ValidateFillPeaks(sequence.VideoTracks) is { } tooClose)   // D044 §4
            throw Damaged($"the transitions are invalid: {tooClose.TrimEnd('.')}");

        if (LinkRules.Validate(sequence) is { } invalidLink)
            throw Damaged($"a link is invalid: {invalidLink.TrimEnd('.')}");

        // D048 §3.5: an anchor to a missing or non-video clip is cleared (the template stays free), not a damaged file.
        var videos = sequence.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().ToDictionary(c => c.Id);
        var greenOnly = new Dictionary<Guid, Guid>();   // a clip without markup: its templates' route (a new id)
        foreach (var template in sequence.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>())
        {
            if (template.AnchorClipId is { } anchor && !videos.ContainsKey(anchor)) template.AnchorClipId = null;
            if (template.AnchorClipId is not { } clipId) { template.AnchorRouteId = null; template.FitClimb = null; template.FitLayout = null; template.RouteStart = null; continue; }
            if (formatVersion >= RoutesVersion) continue;   // a route without markup is a route with its green only (D053)
            // D053: a file before v15 had one markup per clip — its templates belong to it; the templates of a clip without
            // markup (its markup deleted, D048 §3.9) belong to one route of their own, its green only.
            var video = videos[clipId];
            template.AnchorRouteId = video.Routes.IsDefaultOrEmpty
                ? (greenOnly.TryGetValue(clipId, out var own) ? own : greenOnly[clipId] = Guid.NewGuid())
                : video.Routes[0].Id;
        }

        var markerIds = new HashSet<Guid>();
        foreach (var m in dto.Markers ?? new List<MarkerDto>())
        {
            if (m is null) throw Damaged("empty marker entry");
            RequireId(m.Id, "marker");
            if (!markerIds.Add(m.Id)) throw Damaged("duplicate marker id");
            if (m.PositionTicks < 0) throw Damaged("negative marker position");
            sequence.Markers.Add(new Marker { Id = m.Id, Position = new MediaTime(m.PositionTicks), Label = m.Label ?? string.Empty, ColorHex = m.ColorHex ?? string.Empty });
        }

        return sequence;
    }

    /// <summary>Ids already used in the timeline (each must be unique within the file).</summary>
    private sealed class TimelineIds
    {
        public HashSet<Guid> Tracks { get; } = new();
        public HashSet<Guid> Clips { get; } = new();
        public HashSet<Guid> Transitions { get; } = new();
    }

    private static Track FromDto(TrackDto dto, TrackType type, FrameRate rate, IReadOnlyDictionary<Guid, MediaAsset> assets,
        TimelineIds ids, int formatVersion)
    {
        RequireId(dto.Id, "track");
        if (!ids.Tracks.Add(dto.Id)) throw Damaged("duplicate track id");

        var track = new Track
        {
            Id = dto.Id,
            Type = type,
            Name = dto.Name ?? string.Empty,
            Order = dto.Order,
            IsMuted = dto.IsMuted,
            IsHidden = dto.IsHidden,
            IsLocked = dto.IsLocked
        };

        var clips = new List<Clip>();
        foreach (var clipDto in dto.Clips ?? throw Damaged("clip list is missing"))
        {
            var clip = FromDto(clipDto ?? throw Damaged("empty clip entry"), rate, assets, formatVersion);
            if (!ids.Clips.Add(clip.Id)) throw Damaged("duplicate clip id");
            if (clip is AudioClip != (type == TrackType.Audio))
                throw Damaged($"a clip is on the wrong kind of track ({track.Name})");
            clips.Add(clip);
        }

        clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        for (var i = 1; i < clips.Count; i++)
            if (clips[i - 1].TimelineEnd > clips[i].TimelineStart)
                throw Damaged($"clips overlap on track {track.Name}");
        track.Clips.AddRange(clips);

        foreach (var x in dto.Transitions ?? new List<TransitionDto>())
        {
            if (x is null) throw Damaged("empty transition entry");
            RequireId(x.Id, "transition");
            if (string.IsNullOrEmpty(x.TransitionTypeId)) throw Damaged("transition type is missing");
            if (x.DurationTicks < 0) throw Damaged("negative transition duration");

            // v1 / v2 transitions had no anchor (and nothing could create them): the project is read without them.
            if (formatVersion < FadesAndTransitionsVersion) continue;

            if (!ids.Transitions.Add(x.Id)) throw Damaged("duplicate transition id");
            if (formatVersion < AudioEditingVersion && x.TransitionTypeId != TransitionRules.CrossDissolve)
                throw Damaged("unknown transition type");
            // Before v8 a track had only its default type (D025, D032); the library's types and the direction are v8's.
            if (formatVersion < TransitionLibraryVersion && x.TransitionTypeId is not (TransitionRules.CrossDissolve or TransitionRules.Crossfade))
                throw Damaged("unknown transition type");
            TransitionDirection? direction = null;
            if (formatVersion >= TransitionLibraryVersion && x.Direction is not null)
            {
                var named = DirectionNames.Where(d => d.Value == x.Direction).ToList();
                if (named.Count == 0) throw Damaged("unknown transition direction");
                direction = named[0].Key;
            }
            // D045: a template takes no transition.
            if (clips.Any(c => c is TemplateClip && (c.Id == x.LeftClipId || c.Id == x.RightClipId)))
                throw Damaged("a transition next to a template");
            track.Transitions.Add(new Transition
            {
                Id = x.Id, TransitionTypeId = x.TransitionTypeId, Duration = new MediaTime(x.DurationTicks),
                LeftClipId = x.LeftClipId, RightClipId = x.RightClipId, Direction = direction
            });
        }

        return track;
    }

    private static Clip FromDto(ClipDto dto, FrameRate rate, IReadOnlyDictionary<Guid, MediaAsset> assets, int formatVersion)
    {
        RequireId(dto.Id, "clip");
        var start = new MediaTime(dto.TimelineStartTicks);
        var duration = new MediaTime(dto.DurationTicks);
        if (start < MediaTime.Zero) throw Damaged("a clip starts before the beginning of the timeline");
        if (duration <= MediaTime.Zero) throw Damaged("a clip has no duration");
        if (!start.IsOnFrameGrid(rate) || !(start + duration).IsOnFrameGrid(rate))
            throw Damaged("a clip is not aligned to the project frame grid");

        Clip clip = dto switch
        {
            VideoClipDto v => new VideoClip
            {
                Id = v.Id, MediaAssetId = v.MediaAssetId, PositionX = v.PositionX, PositionY = v.PositionY, Scale = v.Scale,
                RotationDegrees = v.RotationDegrees, Opacity = v.Opacity, Volume = v.Volume, IsMuted = v.IsMuted,
                Crop = FromDto(v.Crop),
                // v1–v3 have no unlinked sound (D032).
                IsAudioDetached = formatVersion >= AudioEditingVersion && v.IsAudioDetached,
                // v1–v4 have no hold markup (D034); v5–v14 one markup — the clip's one route; v15 the routes (D053).
                Routes = ReadRoutes(v, formatVersion)
            },
            AudioClipDto a => new AudioClip { Id = a.Id, MediaAssetId = a.MediaAssetId, Volume = a.Volume, IsMuted = a.IsMuted },
            ImageClipDto i => new ImageClip
            {
                Id = i.Id, MediaAssetId = i.MediaAssetId, PositionX = i.PositionX, PositionY = i.PositionY, Scale = i.Scale,
                RotationDegrees = i.RotationDegrees, Opacity = i.Opacity, Crop = FromDto(i.Crop)
            },
            TextClipDto t => new TextClip
            {
                Id = t.Id, Text = t.Text ?? string.Empty, FontFamily = t.FontFamily ?? string.Empty, FontSize = t.FontSize,
                ColorHex = t.ColorHex ?? string.Empty, Alignment = Enum.IsDefined(t.Alignment) ? t.Alignment : throw Damaged("unknown text alignment"),
                PositionX = t.PositionX, PositionY = t.PositionY, Scale = t.Scale, RotationDegrees = t.RotationDegrees, Opacity = t.Opacity
            },
            // v1–v8 have no templates (D045).
            TemplateClipDto p when formatVersion >= TemplatesVersion => new TemplateClip
            {
                Id = p.Id,
                Kind = p.Template is { } kind && Enum.IsDefined(kind) && (kind != TemplateKind.HeartRate || formatVersion >= HeartRateOverlayVersion)
                    ? kind : throw Damaged("unknown template"),
                CounterStart = p.CounterStartTicks >= 0 ? new MediaTime(p.CounterStartTicks) : throw Damaged("a template's counter is negative"),
                Date = p.Date ?? throw Damaged("a template field is missing"),
                Route = p.Route ?? throw Damaged("a template field is missing"),
                Grade = p.Grade ?? throw Damaged("a template field is missing"),
                Status = p.Status is { } status && Enum.IsDefined(status) ? status : throw Damaged("unknown template status"),
                AccentHex = p.AccentHex ?? throw Damaged("a template field is missing"),
                Title = p.Title ?? throw Damaged("a template field is missing"),
                Subtitle = p.Subtitle ?? throw Damaged("a template field is missing"),
                StatusAt = p.StatusAtTicks is not { } statusAt ? throw Damaged("a template field is missing")
                    : statusAt >= 0 ? new MediaTime(statusAt) : throw Damaged("a template's status moment is negative"),
                // v9 has no anchor (D048); a dangling one is cleared once every clip is known (FromDto(SequenceDto)).
                AnchorClipId = formatVersion >= RouteTimingVersion ? p.AnchorClipId : null,
                AnchorRouteId = formatVersion >= RoutesVersion ? p.AnchorRouteId : null,
                FitClimb = formatVersion >= RoutesVersion && p.FitClimb is { } fitClimb ? ReadClimb(fitClimb) : null,
                FitLayout = formatVersion >= FitLayoutVersion && p.FitClimb is not null && p.FitLayout is { } layout ? ReadFitLayout(layout) : null,
                RouteStart = formatVersion >= RouteStartVersion && p.RouteStartTicks is { } routeStartTicks ? new MediaTime(routeStartTicks) : null,
                // Phase 35.5: the heart-rate overlay's look and corner are required on it, absent on the other kinds.
                // D055 §5: from v16 a «Боулдер» has them too (required); an older one gets the defaults.
                Look = !HasHeartRateFields(p.Template, formatVersion) ? HeartRateLook.Plate
                    : p.Look is { } look && Enum.IsDefined(look) ? look : throw Damaged("a heart-rate overlay's look is missing"),
                Corner = !HasHeartRateFields(p.Template, formatVersion) ? OverlayCorner.BottomRight
                    : p.Corner is { } corner && Enum.IsDefined(corner) ? corner : throw Damaged("a heart-rate overlay's corner is missing"),
                ShowHeartRate = p.Template != TemplateKind.Boulder || formatVersion < HeartRateAreaVersion ||
                    (p.ShowHeartRate ?? throw Damaged("a route area's heart rate is missing")),
                HeartHex = p.Template != TemplateKind.Boulder || formatVersion < HeartRateAreaVersion ? TemplateRules.HeartRateAccentHex
                    : p.HeartColorHex ?? throw Damaged("a route area's heart colour is missing")
            },
            _ => throw Damaged("unknown clip type")
        };

        clip.TimelineStart = start;
        clip.Duration = duration;

        if (clip is TextClip textClip && dto is TextClipDto textDto) ReadTextStyle(textClip, textDto, formatVersion);

        // v1 / v2 have no fades (D025).
        if (formatVersion >= FadesAndTransitionsVersion)
        {
            if (dto.FadeInTicks < 0 || dto.FadeOutTicks < 0) throw Damaged("a clip has a negative fade");
            clip.FadeIn = new MediaTime(dto.FadeInTicks);
            clip.FadeOut = new MediaTime(dto.FadeOutTicks);
        }

        if (clip is MediaBackedClip media && dto is MediaBackedClipDto mediaDto)
        {
            if (!assets.TryGetValue(mediaDto.MediaAssetId, out var asset)) throw Damaged("a clip refers to media that is not in the project");
            if (!KindMatches(clip, asset.Kind, formatVersion)) throw Damaged($"a clip doesn't match the kind of {asset.FileName}");
            var speed = ReadSpeed(mediaDto, formatVersion);
            if (clip is ImageClip && !speed.IsNormal) throw Damaged("an image clip has a speed");
            if (mediaDto.SourceInTicks < 0 || mediaDto.SourceOutTicks < mediaDto.SourceInTicks) throw Damaged("invalid clip source range");

            media.SourceIn = new MediaTime(mediaDto.SourceInTicks);
            media.SourceOut = new MediaTime(mediaDto.SourceOutTicks);
            media.Speed = speed;
            // v1–v3 have no links (D032).
            if (formatVersion >= AudioEditingVersion && mediaDto.LinkId is { } linkId)
            {
                if (linkId == Guid.Empty) throw Damaged("a clip has an empty link id");
                media.LinkId = linkId;
            }

            // v1: the source range is exactly the duration (the only thing v1 could hold). v2: the
            // duration is the whole number of frames the source range allows at the clip's speed —
            // for 1× that includes the exact case and a source tail shorter than one frame left by a
            // speed change back to 1× (D022).
            var fits = formatVersion == 1
                ? mediaDto.SourceOutTicks - mediaDto.SourceInTicks == mediaDto.DurationTicks
                : SpeedTiming.Fits(clip.TimelineEnd.ToFrameFloor(rate) - start.ToFrameFloor(rate), media.SourceOut - media.SourceIn, speed, rate);
            if (!fits) throw Damaged("a clip's source range doesn't match its duration");
        }

        // Phase 7 properties: the same kinds and ranges the edit service accepts (NaN/infinite,
        // out-of-range, crop that leaves nothing, empty font, bad color → damaged).
        if (ClipPropertyValidator.ValidateCurrent(clip) is { } invalid)
            throw Damaged($"a clip has an invalid property: {invalid.TrimEnd('.')}");

        foreach (var e in dto.Effects ?? new List<EffectDto>())
            clip.Effects.Add(FromDto(e ?? throw Damaged("empty effect entry")));

        return clip;
    }

    /// <summary>v1: the speed number, which must be exactly 1 (the editor couldn't write anything
    /// else). v2: the exact fraction, a multiple of 0.05 from 0.25× to 4×.</summary>
    private static ClipSpeed ReadSpeed(MediaBackedClipDto dto, int formatVersion)
    {
        if (formatVersion == 1)
        {
            if (dto.Speed != 1.0) throw Damaged("a clip speed other than 1 in a version 1 project");
            return ClipSpeed.Normal;
        }

        if (dto.Speed is not null) throw Damaged("a clip speed in the version 1 form");
        if (dto.SpeedRatio is not { } ratio || !ClipSpeed.TryFromRatio(ratio.Numerator, ratio.Denominator, out var speed))
            throw Damaged("invalid clip speed");
        return speed;
    }

    /// <summary>The asset kind a clip may refer to; from v4 on an audio clip may hold the sound of a video file (D032 R1).</summary>
    private static bool KindMatches(Clip clip, MediaKind kind, int formatVersion) => clip switch
    {
        VideoClip => kind == MediaKind.Video,
        AudioClip => kind == MediaKind.Audio || (kind == MediaKind.Video && formatVersion >= AudioEditingVersion),
        ImageClip => kind == MediaKind.Image,
        _ => false
    };

    private static CropRect FromDto(CropDto? c) => c is null ? CropRect.None : new CropRect(c.Left, c.Top, c.Right, c.Bottom);

    private static Effect FromDto(EffectDto dto)
    {
        RequireId(dto.Id, "effect");
        if (string.IsNullOrEmpty(dto.EffectTypeId)) throw Damaged("effect type is missing");
        return new Effect
        {
            Id = dto.Id,
            EffectTypeId = dto.EffectTypeId,
            DisplayName = dto.DisplayName ?? string.Empty,
            IsEnabled = dto.IsEnabled,
            Parameters = (dto.Parameters ?? new()).ToDictionary(kv => kv.Key, kv => ReadParameter(kv.Value))
        };
    }

    /// <summary>JSON scalars come back as string / long / double / bool / null; anything
    /// structured stays a (detached) <see cref="JsonElement"/>.</summary>
    private static object? ReadParameter(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        _ => e.Clone()
    };

    private static void RequireId(Guid id, string what)
    {
        if (id == Guid.Empty) throw Damaged($"{what} id is missing");
    }

    /// <summary>D053: the routes of a video clip — v15 its route list (every id present and distinct), v5–v14 its one markup
    /// with a new id, older none.</summary>
    private static ImmutableArray<HoldMarkup> ReadRoutes(VideoClipDto v, int formatVersion)
    {
        if (formatVersion < HoldMarkupVersion) return ImmutableArray<HoldMarkup>.Empty;
        if (formatVersion < RoutesVersion)
            // The one route of an older file takes its clip's id: the same file opened twice gives the same route.
            return v.HoldMarkup is { } single ? ImmutableArray.Create(FromDto(single, formatVersion) with { Id = v.Id }) : ImmutableArray<HoldMarkup>.Empty;
        if (v.Routes is null) return ImmutableArray<HoldMarkup>.Empty;
        var routes = ImmutableArray.CreateBuilder<HoldMarkup>(v.Routes.Count);
        var ids = new HashSet<Guid>();
        foreach (var dto in v.Routes)
        {
            var route = FromDto(dto ?? throw Damaged("empty route entry"), formatVersion);
            if (!ids.Add(route.Id)) throw Damaged("two routes of one clip have the same id");
            routes.Add(route);
        }
        return routes.MoveToImmutable();
    }

    /// <summary>A hold markup (D034 §6): every member required, the samples finite, the rules of
    /// <see cref="HoldMarkupRules"/>; anything else is a damaged file.</summary>
    private static HoldMarkup FromDto(HoldMarkupDto dto, int formatVersion)
    {
        var holds = new List<Hold>();
        foreach (var h in dto.Holds ?? throw Damaged("a hold markup has no hold list"))
        {
            if (h is null) throw Damaged("empty hold entry");
            var flat = h.Points ?? throw Damaged("a hold has no contour");
            if (flat.Count % 2 != 0) throw Damaged("a hold's contour has an odd number of coordinates");
            var points = ImmutableArray.CreateBuilder<NormalizedPoint>(flat.Count / 2);
            for (var i = 0; i < flat.Count; i += 2) points.Add(new NormalizedPoint(flat[i], flat[i + 1]));
            holds.Add(new Hold(h.Id, points.MoveToImmutable(),
                ReadLab(h.SurfaceLab, "surface"), ReadLab(h.WallLab, "wall"),
                new MediaTime(h.SourceTimeTicks ?? throw Damaged("a hold has no markup frame time"))));
        }

        var markup = new HoldMarkup
        {
            StrokeWidth1080 = dto.StrokeWidth1080 ?? throw Damaged("a hold markup has no outline width"),
            ColorMode = dto.ColorMode ?? throw Damaged("a hold markup has no colour mode"),
            ColorHex = dto.ColorHex ?? throw Damaged("a hold markup has no colour"),
            Keyline = dto.Keyline ?? throw Damaged("a hold markup has no keyline mode"),
            KeylineActive = dto.KeylineActive ?? throw Damaged("a hold markup has no keyline state"),
            ModelId = dto.ModelId ?? throw Damaged("a hold markup has no model id"),
            Holds = holds.ToImmutableArray(),
            Animation = formatVersion >= HoldAnimationVersion && dto.Animation is { } animation ? FromDto(animation, formatVersion) : null,
            FollowsCamera = formatVersion < FollowsCameraVersion || (dto.FollowsCamera ?? true),
            // D053: a route's id is required since v15; an older file's markup gets a new one.
            Id = formatVersion < RoutesVersion ? Guid.NewGuid() : dto.Id ?? throw Damaged("a route has no id")
        };
        if (HoldMarkupRules.Validate(markup) is { } error) throw Damaged($"invalid hold markup: {error.TrimEnd('.')}");
        return markup;

        static OkLabColor ReadLab(OkLabDto? lab, string what)
        {
            if (lab?.L is not { } l || lab.A is not { } a || lab.B is not { } b) throw Damaged($"a hold has no {what} colour sample");
            return new OkLabColor(l, a, b);
        }
    }

    /// <summary>The outline animation (D036 §2): every member required; the limits are checked with the markup. v10 (D048):
    /// the optional mode and duration (both or neither) and the orders left to right / right to left.</summary>
    private static HoldAnimation FromDto(HoldAnimationDto dto, int formatVersion) => new()
    {
        Order = dto.Order is { } order
            ? order is HoldAnimationOrder.LeftToRight or HoldAnimationOrder.RightToLeft && formatVersion < RouteTimingVersion
                ? throw Damaged("unknown hold animation order") : order
            : throw Damaged("a hold animation has no order"),
        Mode = formatVersion < RouteTimingVersion || dto.Mode is null ? HoldAnimationMode.Interval
            : dto.Mode is { } mode && Enum.IsDefined(mode) ? mode : throw Damaged("unknown hold animation mode"),
        Duration = formatVersion < RouteTimingVersion || dto.Mode is null
            ? (dto.DurationTicks is null || formatVersion < RouteTimingVersion ? MediaTime.Zero : throw Damaged("a hold animation has a duration without its mode"))
            : new MediaTime(dto.DurationTicks ?? throw Damaged("a hold animation has no duration")),
        StartSourceTime = new MediaTime(dto.StartSourceTimeTicks ?? throw Damaged("a hold animation has no start")),
        Interval = new MediaTime(dto.IntervalTicks ?? throw Damaged("a hold animation has no interval")),
        AppearFade = new MediaTime(dto.AppearFadeTicks ?? throw Damaged("a hold animation has no appear fade")),
        StayAfterLast = new MediaTime(dto.StayAfterLastTicks ?? throw Damaged("a hold animation has no time after the last hold")),
        StayUntilClipEnd = dto.StayUntilClipEnd ?? throw Damaged("a hold animation has no stay mode"),
        DisappearFade = new MediaTime(dto.DisappearFadeTicks ?? throw Damaged("a hold animation has no disappear fade"))
    };

    /// <summary>A text's motion: none is not written.</summary>
    private static TextMotionDto? ToDto(TextMotion motion) => motion == TextMotion.None
        ? null
        : new TextMotionDto { Kind = motion.Kind, Side = motion.Side };

    /// <summary>Format v7 (D043 §5): the text's style and motions; absent members are none. Older versions have none.</summary>
    private static void ReadTextStyle(TextClip clip, TextClipDto dto, int formatVersion)
    {
        if (formatVersion < TextStyleVersion) return;
        clip.Bold = dto.Bold ?? false;
        if (dto.Outline is { } o)
            clip.Outline = new TextOutline(o.Enabled ?? throw Damaged("a text outline has no state"),
                o.ColorHex ?? throw Damaged("a text outline has no colour"), o.Thickness ?? throw Damaged("a text outline has no thickness"));
        if (dto.Background is { } b)
            clip.Background = new TextBackground(b.Enabled ?? throw Damaged("a text background has no state"),
                b.ColorHex ?? throw Damaged("a text background has no colour"), b.Opacity ?? throw Damaged("a text background has no opacity"));
        clip.MotionIn = FromDto(dto.MotionIn);
        clip.MotionOut = FromDto(dto.MotionOut);
    }

    private static TextMotion FromDto(TextMotionDto? dto) => dto is null
        ? TextMotion.None
        : new TextMotion(dto.Kind ?? throw Damaged("a text motion has no kind"), dto.Side ?? throw Damaged("a text motion has no side"));

    private static ProjectFileException Damaged(string reason, Exception? inner = null) =>
        new(string.Format(Messages.ProjectFile_Damaged, reason), inner);
}
