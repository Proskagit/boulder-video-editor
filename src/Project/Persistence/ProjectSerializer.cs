using System.Text.Json;
using System.Text.Json.Serialization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Project.Persistence;

/// <summary>
/// Converts a <see cref="Core.Entities.Project"/> to and from project.json text (format
/// version 1, see <see cref="ProjectFileDto"/>). Pure apart from the file-existence check
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
    public const int CurrentFormatVersion = 1;
    public const string RecoveryFormatId = "AiVideoEditor.Recovery";

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

        var dto = Parse<ProjectFileDto>(json, "The project file is damaged and can't be opened.");
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
        const string damaged = "The recovery file is damaged and can't be used.";

        var dto = Parse<RecoveryFileDto>(json, damaged);
        if (dto is null || dto.Format != RecoveryFormatId)
            throw new ProjectFileException("This file is not an AI Video Editor recovery file.");
        if (dto.FormatVersion > CurrentFormatVersion)
            throw new ProjectFileException("This recovery file was written by a newer version of AI Video Editor and can't be used.");
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
            throw new ProjectFileException("This file is not an AI Video Editor project.");
        if (dto.FormatVersion > CurrentFormatVersion)
            throw new ProjectFileException("This project was saved by a newer version of AI Video Editor and can't be opened.");
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
            AudioSampleRate = p.Settings.AudioSampleRate
        },
        MediaAssets = p.MediaAssets.Select(a => ToDto(a, folder)).ToList(),
        Timeline = ToDto(p.Timeline),
        LastExportSettings = ToDto(p.LastExportSettings)
    };

    private static FrameRateDto ToDto(FrameRate r) => new() { Numerator = r.Numerator, Denominator = r.Denominator };

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
            Metadata = a.AnalysisStatus == MediaAnalysisStatus.Completed && a.Metadata is { } m ? ToDto(m) : null
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
        BitrateBps = m.BitrateBps
    };

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
        Transitions = t.Transitions.Select(x => new TransitionDto { Id = x.Id, TransitionTypeId = x.TransitionTypeId, DurationTicks = x.Duration.Ticks }).ToList()
    };

    private static ClipDto ToDto(Clip clip)
    {
        ClipDto dto = clip switch
        {
            VideoClip v => new VideoClipDto
            {
                PositionX = v.PositionX, PositionY = v.PositionY, Scale = v.Scale, RotationDegrees = v.RotationDegrees,
                Opacity = v.Opacity, Volume = v.Volume, IsMuted = v.IsMuted, Crop = ToDto(v.Crop)
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
                PositionX = t.PositionX, PositionY = t.PositionY, Scale = t.Scale, RotationDegrees = t.RotationDegrees, Opacity = t.Opacity
            },
            _ => throw new NotSupportedException($"Clip type {clip.GetType().Name} can't be saved.")
        };

        dto.Id = clip.Id;
        dto.TimelineStartTicks = clip.TimelineStart.Ticks;
        dto.DurationTicks = clip.Duration.Ticks;
        dto.Effects = clip.Effects.Select(ToDto).ToList();

        if (clip is MediaBackedClip media && dto is MediaBackedClipDto mediaDto)
        {
            mediaDto.MediaAssetId = media.MediaAssetId;
            mediaDto.SourceInTicks = media.SourceIn.Ticks;
            mediaDto.SourceOutTicks = media.SourceOut.Ticks;
            mediaDto.Speed = media.Speed;
        }

        return dto;
    }

    private static CropDto ToDto(CropRect c) => new() { Left = c.Left, Top = c.Top, Right = c.Right, Bottom = c.Bottom };

    private static EffectDto ToDto(Effect e) => new()
    {
        Id = e.Id,
        EffectTypeId = e.EffectTypeId,
        DisplayName = e.DisplayName,
        IsEnabled = e.IsEnabled,
        Parameters = e.Parameters.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value, Options))
    };

    private static ExportSettingsDto ToDto(ExportSettings e) => new()
    {
        OutputPath = e.OutputPath,
        Container = e.Container,
        VideoCodec = e.VideoCodec,
        AudioCodec = e.AudioCodec,
        Width = e.Width,
        Height = e.Height,
        FrameRate = e.FrameRate,
        VideoBitrateBps = e.VideoBitrateBps,
        AudioBitrateBps = e.AudioBitrateBps
    };

    // ---- DTO → entity (with validation) ---------------------------------------

    private static Core.Entities.Project FromDto(ProjectFileDto dto, string? folder, Func<string, bool> fileExists)
    {
        RequireId(dto.Id, "project");
        var settingsDto = dto.Settings ?? throw Damaged("project settings are missing");
        if (settingsDto.FrameWidth <= 0 || settingsDto.FrameHeight <= 0) throw Damaged("invalid frame size");
        if (settingsDto.AudioSampleRate <= 0) throw Damaged("invalid audio sample rate");
        var frameRate = ReadFrameRate(settingsDto.FrameRate) ?? throw Damaged("invalid project frame rate");

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
                AudioSampleRate = settingsDto.AudioSampleRate
            },
            Timeline = new Sequence(),
            LastExportSettings = FromDto(dto.LastExportSettings),
            IsDirty = false
        };

        var assets = new Dictionary<Guid, MediaAsset>();
        foreach (var assetDto in dto.MediaAssets ?? throw Damaged("media list is missing"))
        {
            var asset = FromDto(assetDto ?? throw Damaged("empty media entry"), folder, fileExists);
            if (!assets.TryAdd(asset.Id, asset)) throw Damaged("duplicate media id");
            project.MediaAssets.Add(asset);
        }

        project.Timeline = FromDto(dto.Timeline ?? throw Damaged("timeline is missing"), frameRate, assets);
        return project;
    }

    private static MediaAsset FromDto(MediaAssetDto dto, string? folder, Func<string, bool> fileExists)
    {
        RequireId(dto.Id, "media");
        if (string.IsNullOrWhiteSpace(dto.FilePath)) throw Damaged("media file path is missing");
        if (!Enum.IsDefined(dto.Kind)) throw Damaged("unknown media kind");

        var metadata = ReadMetadata(dto.Metadata);
        return new MediaAsset
        {
            Id = dto.Id,
            FilePath = ResolveMediaPath(dto.FilePath, dto.RelativePath, folder, fileExists),
            FileSizeBytes = dto.FileSizeBytes,
            Kind = dto.Kind,
            ImportedAt = dto.ImportedAt,
            ThumbnailPath = dto.ThumbnailPath,
            Metadata = metadata,
            AnalysisStatus = metadata is null ? MediaAnalysisStatus.Pending : MediaAnalysisStatus.Completed
        };
    }

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
            BitrateBps = dto.BitrateBps
        };
    }

    private static FrameRate? ReadFrameRate(FrameRateDto? dto) =>
        dto is { Numerator: > 0, Denominator: > 0 } ? new FrameRate(dto.Numerator, dto.Denominator) : null;

    private static Sequence FromDto(SequenceDto dto, FrameRate rate, IReadOnlyDictionary<Guid, MediaAsset> assets)
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

        var trackIds = new HashSet<Guid>();
        var clipIds = new HashSet<Guid>();
        foreach (var t in dto.VideoTracks ?? throw Damaged("video track list is missing"))
            sequence.VideoTracks.Add(FromDto(t ?? throw Damaged("empty track entry"), TrackType.Video, rate, assets, trackIds, clipIds));
        foreach (var t in dto.AudioTracks ?? throw Damaged("audio track list is missing"))
            sequence.AudioTracks.Add(FromDto(t ?? throw Damaged("empty track entry"), TrackType.Audio, rate, assets, trackIds, clipIds));

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

    private static Track FromDto(TrackDto dto, TrackType type, FrameRate rate, IReadOnlyDictionary<Guid, MediaAsset> assets,
        HashSet<Guid> trackIds, HashSet<Guid> clipIds)
    {
        RequireId(dto.Id, "track");
        if (!trackIds.Add(dto.Id)) throw Damaged("duplicate track id");

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
            var clip = FromDto(clipDto ?? throw Damaged("empty clip entry"), rate, assets);
            if (!clipIds.Add(clip.Id)) throw Damaged("duplicate clip id");
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
            track.Transitions.Add(new Transition { Id = x.Id, TransitionTypeId = x.TransitionTypeId, Duration = new MediaTime(x.DurationTicks) });
        }

        return track;
    }

    private static Clip FromDto(ClipDto dto, FrameRate rate, IReadOnlyDictionary<Guid, MediaAsset> assets)
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
                Crop = FromDto(v.Crop)
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
            _ => throw Damaged("unknown clip type")
        };

        clip.TimelineStart = start;
        clip.Duration = duration;

        if (clip is MediaBackedClip media && dto is MediaBackedClipDto mediaDto)
        {
            if (!assets.TryGetValue(mediaDto.MediaAssetId, out var asset)) throw Damaged("a clip refers to media that is not in the project");
            if (!KindMatches(clip, asset.Kind)) throw Damaged($"a clip doesn't match the kind of {asset.FileName}");
            if (!double.IsFinite(mediaDto.Speed) || mediaDto.Speed <= 0) throw Damaged("invalid clip speed");
            if (mediaDto.SourceInTicks < 0 || mediaDto.SourceOutTicks < mediaDto.SourceInTicks) throw Damaged("invalid clip source range");
            if (mediaDto.Speed == 1.0 && mediaDto.SourceOutTicks - mediaDto.SourceInTicks != mediaDto.DurationTicks)
                throw Damaged("a clip's source range doesn't match its duration");

            media.SourceIn = new MediaTime(mediaDto.SourceInTicks);
            media.SourceOut = new MediaTime(mediaDto.SourceOutTicks);
            media.Speed = mediaDto.Speed;
        }

        // Phase 7 properties: the same kinds and ranges the edit service accepts (NaN/infinite,
        // out-of-range, crop that leaves nothing, empty font, bad color → damaged).
        if (ClipPropertyValidator.ValidateCurrent(clip) is { } invalid)
            throw Damaged($"a clip has an invalid property: {invalid.TrimEnd('.')}");

        foreach (var e in dto.Effects ?? new List<EffectDto>())
            clip.Effects.Add(FromDto(e ?? throw Damaged("empty effect entry")));

        return clip;
    }

    private static bool KindMatches(Clip clip, MediaKind kind) => clip switch
    {
        VideoClip => kind == MediaKind.Video,
        AudioClip => kind == MediaKind.Audio,
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

    private static ExportSettings FromDto(ExportSettingsDto? dto)
    {
        if (dto is null) return new ExportSettings();
        if (!Enum.IsDefined(dto.Container) || !Enum.IsDefined(dto.VideoCodec) || !Enum.IsDefined(dto.AudioCodec))
            throw Damaged("unknown export format");
        return new ExportSettings
        {
            OutputPath = dto.OutputPath ?? string.Empty,
            Container = dto.Container,
            VideoCodec = dto.VideoCodec,
            AudioCodec = dto.AudioCodec,
            Width = dto.Width,
            Height = dto.Height,
            FrameRate = dto.FrameRate,
            VideoBitrateBps = dto.VideoBitrateBps,
            AudioBitrateBps = dto.AudioBitrateBps
        };
    }

    private static void RequireId(Guid id, string what)
    {
        if (id == Guid.Empty) throw Damaged($"{what} id is missing");
    }

    private static ProjectFileException Damaged(string reason, Exception? inner = null) =>
        new($"The project file is damaged and can't be opened ({reason}).", inner);
}
