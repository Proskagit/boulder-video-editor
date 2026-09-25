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

    /// <summary>Stored in project.json but not used: playback and export always run at
    /// 48 kHz stereo (<see cref="Playback.AudioFormat"/>, D013, D023).</summary>
    public int AudioSampleRate { get; set; } = 48000;
}

public enum ExportContainer { Mp4 }
public enum ExportVideoCodec { H264 }
public enum ExportAudioCodec { Aac }

/// <summary>
/// What the last export used (D023): session-only state of the project — never saved (not in project.json
/// or recovery files; an older file's value is ignored), and updating it never makes the project dirty
/// and never enters undo/redo. The output format itself is fixed (<see cref="Export.ExportFormat"/>):
/// size and frame rate come from <see cref="ProjectSettings"/>, quality is constant.
/// </summary>
public sealed class ExportSettings
{
    public string OutputPath { get; set; } = string.Empty;
    public ExportContainer Container { get; set; } = ExportContainer.Mp4;
    public ExportVideoCodec VideoCodec { get; set; } = ExportVideoCodec.H264;
    public ExportAudioCodec AudioCodec { get; set; } = ExportAudioCodec.Aac;
}
