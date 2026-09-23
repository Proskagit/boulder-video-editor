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

    /// <summary>Raised when the current project's <see cref="Project.IsDirty"/>,
    /// <see cref="Project.Name"/> or <see cref="Project.ProjectFolderPath"/> may have changed:
    /// after an edit, Undo/Redo, a media import, a successful save, New or Open.</summary>
    event EventHandler? SaveStateChanged;

    /// <summary>Replaces the current project with an empty one and resets the undo history
    /// (the new project is clean).</summary>
    Project CreateNew(string name, ProjectSettings? settings = null);

    /// <summary>Loads project.json from <paramref name="projectFolderPath"/>. The file is read
    /// and fully validated before anything changes; only then does the loaded project replace
    /// the current one (undo history reset, project clean, events raised on the caller's
    /// context).</summary>
    /// <exception cref="ProjectFileException">The project couldn't be read or is invalid; the
    /// current project and its undo history are unchanged.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing changed.</exception>
    Task<Project> OpenAsync(string projectFolderPath, CancellationToken ct = default);

    /// <summary>Saves the current project to its <see cref="Project.ProjectFolderPath"/>.
    /// On success the saved state becomes the save point: <see cref="Project.IsDirty"/> is
    /// false until the next change, and again whenever Undo/Redo return to that state.</summary>
    /// <exception cref="InvalidOperationException">The project has never been saved — use
    /// <see cref="SaveAsAsync"/>.</exception>
    /// <exception cref="ProjectFileException">Writing failed; the file on disk, the save point
    /// and the project's folder and name are unchanged.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the file was replaced;
    /// nothing changed.</exception>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>Saves the current project as project.json in <paramref name="projectFolderPath"/>
    /// (created if needed). On success that folder becomes the project's folder, the project
    /// takes the folder's name, and the saved state becomes the save point. Failure and
    /// cancellation change nothing, exactly as for <see cref="SaveAsync"/>.</summary>
    Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default);

    /// <summary>Raised after Save / Save As has written project.json successfully (after
    /// <see cref="SaveStateChanged"/>). <see cref="Project.IsDirty"/> is then false unless the
    /// project was edited while it was being written.</summary>
    event EventHandler? ProjectSaved;

    /// <summary>Makes the project in an autosave recovery file the current project. The file
    /// goes through the same full validation as <see cref="OpenAsync"/> and replaces the current
    /// project only if that succeeds. The recovered project belongs to its original folder (none
    /// if it had never been saved) and has unsaved changes until it is saved.</summary>
    /// <exception cref="ProjectFileException">The recovery file couldn't be read or is invalid;
    /// nothing changed.</exception>
    Task<Project> RestoreRecoveryAsync(string recoveryFilePath, CancellationToken ct = default);

    /// <summary>Checks every MediaAsset's FilePath and marks it IsMissing = true if not found.</summary>
    IReadOnlyList<MediaAsset> DetectMissingMedia();

    /// <summary>Adds newly-imported media to the current project, skipping any whose
    /// <see cref="MediaAsset.FilePath"/> is already present. This is the only place
    /// duplicate detection happens, since it's the only place that knows what's
    /// already in the project.</summary>
    MediaAddResult AddMediaAssets(IEnumerable<MediaAsset> assets);

    /// <summary>Raises <see cref="MediaAssetsChanged"/> without otherwise changing
    /// project state. Lets code that mutates a <see cref="MediaAsset"/> in place
    /// (e.g. metadata analysis filling in <see cref="MediaAsset.Metadata"/> after
    /// it was already added) signal bound UI to refresh, the same way an actual
    /// add does.</summary>
    void NotifyMediaAssetsChanged();

    /// <summary>Raised after any change to <see cref="Project.Timeline"/> (tracks, clips)
    /// or the project frame rate — including Undo/Redo of such a change.</summary>
    event EventHandler? TimelineChanged;

    /// <summary>Raises <see cref="TimelineChanged"/>. Called by timeline commands from both
    /// Execute and Undo. Whether the project is dirty follows the undo history's save point
    /// (see <see cref="Common.IUndoRedoService.IsAtSavePoint"/>), not this call.</summary>
    void NotifyTimelineChanged();
}

/// <summary>Result of <see cref="IProjectService.AddMediaAssets"/>.</summary>
public sealed class MediaAddResult
{
    public IReadOnlyList<MediaAsset> Added { get; init; } = Array.Empty<MediaAsset>();

    /// <summary>How many of the given assets were skipped because a media asset with
    /// the same <see cref="MediaAsset.FilePath"/> was already in the project.</summary>
    public int DuplicateCount { get; init; }
}

public interface IThumbnailService
{
    /// <summary>Returns a cached thumbnail path if present, otherwise generates one asynchronously.</summary>
    Task<string> GetOrCreateThumbnailAsync(MediaAsset asset, CancellationToken ct = default);
}

