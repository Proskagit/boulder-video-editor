using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Interfaces;

public interface IProjectService
{
    Project Current { get; }

    event EventHandler? ProjectChanged;

    Project CreateNew(string name, ProjectSettings? settings = null);

    Task<Project> OpenAsync(string projectFolderPath, CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);

    Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default);

    /// <summary>Checks every MediaAsset's FilePath and marks it IsMissing = true if not found.</summary>
    IReadOnlyList<MediaAsset> DetectMissingMedia();
}

public interface IAutosaveService
{
    void Start();
    void Stop();
    event EventHandler<string>? AutosaveCompleted;
}

public interface IMediaImportService
{
    Task<MediaAsset> ImportAsync(string filePath, CancellationToken ct = default);

    Task<IReadOnlyList<MediaAsset>> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default);

    /// <summary>File extensions (without dot) that the Media Browser will accept, derived from FFmpeg support.</summary>
    IReadOnlySet<string> SupportedExtensions { get; }
}

public interface IThumbnailService
{
    /// <summary>Returns a cached thumbnail path if present, otherwise generates one asynchronously.</summary>
    Task<string> GetOrCreateThumbnailAsync(MediaAsset asset, CancellationToken ct = default);
}

/// <summary>Drives the preview player and stays in sync with the timeline playhead.</summary>
public interface IPlaybackService
{
    bool IsPlaying { get; }
    Common.MediaTime CurrentTime { get; }
    Common.MediaTime Duration { get; }
    double Volume { get; set; }
    bool IsMuted { get; set; }

    event EventHandler? PlaybackStateChanged;
    event EventHandler<Common.MediaTime>? TimeChanged;

    void Play();
    void Pause();
    void Stop();
    void Seek(Common.MediaTime position);
    void StepFrame(int deltaFrames);
}
