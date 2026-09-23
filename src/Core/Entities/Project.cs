namespace AiVideoEditor.Core.Entities;

/// <summary>
/// Root aggregate for a single editing project. This is what gets serialized to
/// project.json. Source media files are referenced, not embedded (see MediaAsset).
/// </summary>
public sealed class Project
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Untitled Project";

    /// <summary>Folder on disk containing project.json, media/, cache/, thumbnails/.</summary>
    public string? ProjectFolderPath { get; set; }

    public ProjectSettings Settings { get; set; } = new();

    public List<MediaAsset> MediaAssets { get; } = new();

    public Sequence Timeline { get; set; } = new();

    public ExportSettings LastExportSettings { get; set; } = new();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsDirty { get; set; }
}

public sealed class ProjectSettings
{
    public int FrameWidth { get; set; } = 1920;
    public int FrameHeight { get; set; } = 1080;
    /// <summary>Timeline frame grid. Exact rational rate — see <see cref="Common.FrameRate"/>.</summary>
    public Common.FrameRate FrameRate { get; set; } = Common.FrameRate.Default;

    /// <summary>False while <see cref="FrameRate"/> is the provisional default. Set when
    /// the first video clip is added to the timeline, which fixes the frame rate for
    /// the rest of the project; only undoing that add clears it again.</summary>
    public bool IsFrameRateLocked { get; set; }
    public int AudioSampleRate { get; set; } = 48000;
}

public enum ExportContainer { Mp4 }
public enum ExportVideoCodec { H264 }
public enum ExportAudioCodec { Aac }

public sealed class ExportSettings
{
    public string OutputPath { get; set; } = string.Empty;
    public ExportContainer Container { get; set; } = ExportContainer.Mp4;
    public ExportVideoCodec VideoCodec { get; set; } = ExportVideoCodec.H264;
    public ExportAudioCodec AudioCodec { get; set; } = ExportAudioCodec.Aac;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double FrameRate { get; set; } = 30;
    public long? VideoBitrateBps { get; set; }
    public long? AudioBitrateBps { get; set; } = 192_000;
}
