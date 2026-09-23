using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project;

/// <summary>
/// Holds the current in-memory <see cref="Core.Entities.Project"/> and mediates every
/// change to it, including New / Open / Save / Save As (project.json, see
/// <see cref="ProjectSerializer"/>).
/// </summary>
/// <remarks>
/// Dirty tracking: the project is dirty when the undo history is not at its save point
/// (<see cref="IUndoRedoService.IsAtSavePoint"/>) or when a change that isn't undoable
/// (a media import) happened since the last save. So Undo/Redo back to the saved state make
/// the project clean again, and New/Open (which clear the history) start clean.
/// Members are meant to be called from the UI thread; Open/Save do their file work in the
/// background and resume on the caller's context before touching the current project.
/// </remarks>
public sealed class ProjectService : IProjectService
{
    private readonly IUndoRedoService _undoRedo;
    private readonly ProjectFileStore _store;
    private readonly ILogger<ProjectService> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    // Non-undoable changes (media imports) since the project was created/opened, and how
    // many of them the last successful save included.
    private long _changeVersion;
    private long _savedChangeVersion;

    public Core.Entities.Project Current { get; private set; }

    public event EventHandler? ProjectChanged;
    public event EventHandler? MediaAssetsChanged;
    public event EventHandler? TimelineChanged;
    public event EventHandler? SaveStateChanged;
    public event EventHandler? ProjectSaved;

    public ProjectService(IUndoRedoService undoRedo, ILogger<ProjectService> logger)
        : this(undoRedo, logger, new ProjectFileStore())
    {
    }

    internal ProjectService(IUndoRedoService undoRedo, ILogger<ProjectService> logger, ProjectFileStore store)
    {
        _undoRedo = undoRedo;
        _logger = logger;
        _store = store;
        Current = NewProject("Untitled Project", null);
        _undoRedo.StateChanged += (_, _) => UpdateDirty();
        UpdateDirty();
    }

    public Core.Entities.Project CreateNew(string name, ProjectSettings? settings = null)
    {
        Replace(NewProject(name, settings));
        _logger.LogInformation("Created new project '{Name}'.", name);
        return Current;
    }

    /// <summary>Every project starts with one video track (V1) and one audio track (A1).</summary>
    private static Core.Entities.Project NewProject(string name, ProjectSettings? settings)
    {
        var project = new Core.Entities.Project
        {
            Name = name,
            Settings = settings ?? new ProjectSettings()
        };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1", Order = 0 });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1", Order = 0 });
        return project;
    }

    public async Task<Core.Entities.Project> OpenAsync(string projectFolderPath, CancellationToken ct = default)
    {
        var folder = FullFolderPath(projectFolderPath);

        // Read, parse and validate into a separate object; the current project is untouched
        // until all of it has succeeded.
        var json = await _store.ReadAsync(ProjectFileStore.ProjectFilePath(folder), ct);
        var project = await Task.Run(() =>
        {
            var loaded = ProjectSerializer.Deserialize(json, folder);
            // Media state is known before anyone sees the project, so the first playback
            // snapshot built on ProjectChanged already shows missing files as offline.
            MarkMissingMedia(loaded);
            return loaded;
        }, ct);
        ct.ThrowIfCancellationRequested();

        Replace(project);
        var missing = project.MediaAssets.Count(a => a.IsMissing);
        _logger.LogInformation("Opened project '{Name}' from {Folder} ({Assets} media, {Missing} missing, {Clips} clips).",
            project.Name, folder, project.MediaAssets.Count, missing,
            project.Timeline.VideoTracks.Concat(project.Timeline.AudioTracks).Sum(t => t.Clips.Count));
        foreach (var asset in project.MediaAssets.Where(a => a.IsMissing))
            _logger.LogWarning("Media file is missing: {Path}", asset.FilePath);
        return project;
    }

    public async Task<Core.Entities.Project> RestoreRecoveryAsync(string recoveryFilePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilePath);

        // Same rule as Open: fully read and validated into a separate object first.
        var json = await _store.ReadAsync(recoveryFilePath, ct);
        var project = await Task.Run(() =>
        {
            var (loaded, _) = ProjectSerializer.DeserializeRecovery(json);
            MarkMissingMedia(loaded);
            return loaded;
        }, ct);
        ct.ThrowIfCancellationRequested();

        Replace(project, hasUnsavedChanges: true);
        _logger.LogInformation("Recovered project '{Name}' (folder {Folder}) from {File}.",
            project.Name, project.ProjectFolderPath ?? "none", recoveryFilePath);
        return project;
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        var folder = Current.ProjectFolderPath
            ?? throw new InvalidOperationException("The project hasn't been saved yet; use Save As.");
        return SaveToAsync(folder, Current.Name, ct);
    }

    public Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default)
    {
        var folder = FullFolderPath(projectFolderPath);
        var name = Path.GetFileName(folder);
        return SaveToAsync(folder, string.IsNullOrEmpty(name) ? Current.Name : name, ct);
    }

    private async Task SaveToAsync(string folder, string name, CancellationToken ct)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            // Snapshot on the caller's (UI) thread: the text, the history position and the
            // non-undoable change count all describe the same state.
            var project = Current;
            var position = _undoRedo.CurrentPosition;
            var changeVersion = _changeVersion;
            var json = ProjectSerializer.Serialize(project, folder, name);

            await _store.WriteAtomicAsync(ProjectFileStore.ProjectFilePath(folder), json, ct);

            _logger.LogInformation("Saved project '{Name}' to {Folder}.", name, folder);
            if (!ReferenceEquals(project, Current))
                return; // New/Open replaced the project while it was being written.

            project.ProjectFolderPath = folder;
            project.Name = name;
            _savedChangeVersion = changeVersion;
            _undoRedo.MarkSavePoint(position); // → UpdateDirty
            SaveStateChanged?.Invoke(this, EventArgs.Empty);
            ProjectSaved?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static string FullFolderPath(string projectFolderPath)
    {
        if (string.IsNullOrWhiteSpace(projectFolderPath))
            throw new ProjectFileException("No project folder was given.");
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectFolderPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectFileException($"\"{projectFolderPath}\" is not a valid folder path.", ex);
        }
    }

    /// <summary>Makes <paramref name="project"/> current with a fresh history. It is clean, unless
    /// <paramref name="hasUnsavedChanges"/> (a recovered project: nothing in its history, but its
    /// state isn't what is on disk).</summary>
    private void Replace(Core.Entities.Project project, bool hasUnsavedChanges = false)
    {
        Current = project;
        _changeVersion = hasUnsavedChanges ? 1 : 0;
        _savedChangeVersion = 0;
        _undoRedo.Clear(); // → UpdateDirty: clean

        ProjectChanged?.Invoke(this, EventArgs.Empty);
        NotifyMediaAssetsChanged();
        TimelineChanged?.Invoke(this, EventArgs.Empty);
        SaveStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDirty()
    {
        var dirty = !_undoRedo.IsAtSavePoint || _changeVersion != _savedChangeVersion;
        if (Current.IsDirty == dirty) return;
        Current.IsDirty = dirty;
        SaveStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <remarks>Runtime state only: never makes the project dirty and is not saved.</remarks>
    public IReadOnlyList<MediaAsset> DetectMissingMedia() => MarkMissingMedia(Current);

    private static IReadOnlyList<MediaAsset> MarkMissingMedia(Core.Entities.Project project)
    {
        var missing = new List<MediaAsset>();
        foreach (var asset in project.MediaAssets)
        {
            asset.IsMissing = !File.Exists(asset.FilePath);
            if (asset.IsMissing)
                missing.Add(asset);
        }
        return missing;
    }

    public MediaAddResult AddMediaAssets(IEnumerable<MediaAsset> assets)
    {
        var existingPaths = new HashSet<string>(
            Current.MediaAssets.Select(a => a.FilePath),
            StringComparer.OrdinalIgnoreCase);

        var added = new List<MediaAsset>();
        var duplicateCount = 0;

        foreach (var asset in assets)
        {
            if (!existingPaths.Add(asset.FilePath))
            {
                duplicateCount++;
                continue;
            }

            Current.MediaAssets.Add(asset);
            added.Add(asset);
        }

        if (added.Count > 0)
        {
            Current.ModifiedAt = DateTimeOffset.UtcNow;
            _changeVersion++;
            _logger.LogInformation("Added {Count} media asset(s) to the project ({Duplicates} duplicate(s) skipped).", added.Count, duplicateCount);
            UpdateDirty();
            NotifyMediaAssetsChanged();
        }

        return new MediaAddResult { Added = added, DuplicateCount = duplicateCount };
    }

    public void NotifyMediaAssetsChanged() => MediaAssetsChanged?.Invoke(this, EventArgs.Empty);

    public void NotifyTimelineChanged()
    {
        // Dirty state is derived from the undo history (UpdateDirty runs on its StateChanged,
        // right after the command has been pushed/popped).
        Current.ModifiedAt = DateTimeOffset.UtcNow;
        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }
}
