using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Interfaces;

public interface IProjectService
{
    Project Current { get; }

    event EventHandler? ProjectChanged;

    /// <summary>Raised whenever <see cref="Project.MediaAssets"/> changes — after
    /// <see cref="AddMediaAssets"/> adds anything, and implicitly whenever
    /// <see cref="ProjectChanged"/> fires too (a new/opened project has its own list).
    /// Media Browser listens to this instead of being pushed into directly, so it
    /// doesn't matter which command (Toolbar or Media Browser) triggered an import.</summary>
    event EventHandler? MediaAssetsChanged;

    Project CreateNew(string name, ProjectSettings? settings = null);

    Task<Project> OpenAsync(string projectFolderPath, CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);

    Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default);

    /// <summary>Checks every MediaAsset's FilePath and marks it IsMissing = true if not found.</summary>
    IReadOnlyList<MediaAsset> DetectMissingMedia();

    /// <summary>Adds newly-imported media to the current project, skipping any whose
    /// <see cref="MediaAsset.FilePath"/> is already present. This is the only place
    /// duplicate detection happens, since it's the only place that knows what's
    /// already in the project.</summary>
    MediaAddResult AddMediaAssets(IEnumerable<MediaAsset> assets);
}

/// <summary>Result of <see cref="IProjectService.AddMediaAssets"/>.</summary>
public sealed class MediaAddResult
{
    public IReadOnlyList<MediaAsset> Added { get; init; } = Array.Empty<MediaAsset>();

    /// <summary>How many of the given assets were skipped because a media asset with
    /// the same <see cref="MediaAsset.FilePath"/> was already in the project.</summary>
    public int DuplicateCount { get; init; }
}

public interface IAutosaveService
{
    void Start();
    void Stop();
    event EventHandler<string>? AutosaveCompleted;
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
