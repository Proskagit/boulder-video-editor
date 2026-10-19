using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Localization;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// The UI side of the project's life on disk: New / Open / Save / Save As / Close with the
/// folder picker and the "save changes?" prompt, analysis of media that still needs it after
/// Open, the startup offer to recover autosaved work, and autosave at shutdown. Every failure
/// is reported in the status bar and leaves the current project as it was. A successful Open, Save As and Recover (of a
/// project with a folder) puts the project first in the recent-projects list (D026 §6); nothing else changes the list.
/// </summary>
public sealed class ProjectFileWorkflow
{
    private readonly IProjectService _projectService;
    private readonly MediaAnalysisCoordinator _analysisCoordinator;
    private readonly IAutosaveService _autosave;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _picker;
    private readonly StatusService _status;
    private readonly ILogger<ProjectFileWorkflow> _logger;
    private readonly IThumbnailCacheLocation? _thumbnailCache;
    private readonly IWaveformCacheLocation? _waveformCache;
    private readonly IRecentProjectsStore? _recentProjects;

    public ProjectFileWorkflow(
        IProjectService projectService,
        MediaAnalysisCoordinator analysisCoordinator,
        IAutosaveService autosave,
        IDialogService dialogs,
        IFilePickerService picker,
        StatusService status,
        ILogger<ProjectFileWorkflow> logger,
        IThumbnailCacheLocation? thumbnailCache = null,
        IWaveformCacheLocation? waveformCache = null,
        IRecentProjectsStore? recentProjects = null)
    {
        _projectService = projectService;
        _analysisCoordinator = analysisCoordinator;
        _autosave = autosave;
        _dialogs = dialogs;
        _picker = picker;
        _status = status;
        _logger = logger;
        _thumbnailCache = thumbnailCache;
        _waveformCache = waveformCache;
        _recentProjects = recentProjects;
    }

    // ---- New / Open (interactive) ---------------------------------------------------

    /// <summary>New: asks about unsaved changes first, then replaces the project with an empty
    /// one. Returns false if cancelled.</summary>
    public async Task<bool> NewProjectAsync()
    {
        var previousId = _projectService.Current.Id;
        var decision = await ConfirmUnsavedChangesAsync(Strings.Unsaved_ActionNew);
        if (decision == UnsavedChangesDecision.Cancel) return false;

        _projectService.CreateNew(Messages.Project_DefaultName); // also resets the undo history
        if (decision == UnsavedChangesDecision.DontSave)
            await DiscardRecoveryQuietlyAsync(previousId);
        _status.Report(Strings.Project_Created);
        return true;
    }

    /// <summary>Open: picks a project folder, asks about unsaved changes, then opens it. Returns
    /// false if cancelled or if the project couldn't be opened (the current project is then kept,
    /// including its unsaved changes and their recovery file).</summary>
    public async Task<bool> OpenProjectAsync()
    {
        var folder = await _picker.PickFolderAsync(new FolderPickerRequest
        {
            Title = Strings.Project_OpenPickerTitle,
            StartFolder = ParentOf(_projectService.Current.ProjectFolderPath)
        });
        if (folder is null) return false;

        return await OpenFolderAsync(folder);
    }

    /// <summary>Opens the project in <paramref name="projectFolderPath"/> as Open does once a folder is picked: asks
    /// about unsaved changes, then opens it. Used by Open and by the recent-projects list (D026 §6). Returns false if
    /// cancelled or if the project couldn't be opened (the current project is then kept, including its unsaved changes
    /// and their recovery file; the recent-projects list is changed only by a successful open).</summary>
    public async Task<bool> OpenFolderAsync(string projectFolderPath)
    {
        var previousId = _projectService.Current.Id;
        var decision = await ConfirmUnsavedChangesAsync(Strings.Unsaved_ActionOpen);
        if (decision == UnsavedChangesDecision.Cancel) return false;

        if (!await OpenAsync(projectFolderPath)) return false;
        if (decision == UnsavedChangesDecision.DontSave)
            await DiscardRecoveryQuietlyAsync(previousId);
        return true;
    }

    // ---- Save / Save As -------------------------------------------------------------

    /// <summary>Save: to the project's folder, or through Save As if it has never been saved.
    /// Returns true only if project.json was actually written.</summary>
    public Task<bool> SaveAsync()
    {
        BeforeSave?.Invoke(this, EventArgs.Empty);
        return _projectService.Current.ProjectFolderPath is null ? SaveAsAsync() : SaveToCurrentFolderAsync();
    }

    /// <summary>Raised before Save, Save As and the unsaved-changes prompt (New / Open / Close): a running Preview gesture
    /// is cancelled first, so the save point is never set inside a gesture (D038 §4).</summary>
    public event EventHandler? BeforeSave;

    private async Task<bool> SaveToCurrentFolderAsync()
    {
        try
        {
            await _projectService.SaveAsync();
        }
        catch (ProjectFileException ex)
        {
            _logger.LogWarning(ex, "Save failed.");
            _status.Report(string.Format(Strings.Project_SaveFailed, _projectService.Current.Name, ex.Message));
            return false;
        }

        _status.Report(SavedMessage(string.Format(Strings.Project_Saved, _projectService.Current.Name)));
        return true;
    }

    /// <summary>Save As: picks (or creates) a folder, confirms replacing another project that is
    /// already there, and saves. Cancelling or a failure changes nothing. Returns true only if
    /// project.json was actually written.</summary>
    public async Task<bool> SaveAsAsync()
    {
        BeforeSave?.Invoke(this, EventArgs.Empty);
        var current = _projectService.Current;
        var folder = await _picker.PickFolderAsync(new FolderPickerRequest
        {
            Title = Strings.Project_SaveAsPickerTitle,
            StartFolder = ParentOf(current.ProjectFolderPath)
        });
        if (folder is null) return false;

        var isCurrentFolder = current.ProjectFolderPath is { } own &&
                              string.Equals(Path.GetFullPath(own), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
        if (!isCurrentFolder && File.Exists(Path.Combine(folder, "project.json")))
        {
            var replace = await _dialogs.AskAsync(new DialogRequest
            {
                Title = Strings.Project_ReplaceTitle,
                Message = string.Format(Strings.Project_ReplaceQuestion, folder, current.Name),
                Buttons = new[] { Strings.Common_Replace, Strings.Common_Cancel }
            });
            if (replace != 0) return false;
        }

        try
        {
            await _projectService.SaveAsAsync(folder);
        }
        catch (ProjectFileException ex)
        {
            _logger.LogWarning(ex, "Save As to {Folder} failed.", folder);
            _status.Report(string.Format(Strings.Project_SaveFailed, current.Name, ex.Message));
            return false;
        }

        _status.Report(SavedMessage(string.Format(Strings.Project_SavedTo, _projectService.Current.Name, folder)));
        // The folder written, also when Open replaced the project while it was being saved (project.json is there).
        var savedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        await RememberRecentAsync(savedFolder, Path.GetFileName(savedFolder) is { Length: > 0 } name ? name : current.Name);
        return true;
    }

    private string SavedMessage(string message) => _projectService.Current.IsDirty
        ? string.Format(Strings.Project_ChangesWhileSaving, message)
        : message;

    // ---- Unsaved changes -------------------------------------------------------------

    /// <summary>If the project has unsaved changes, asks Save / Don't Save / Cancel before
    /// <paramref name="action"/>. Save that doesn't complete (picker cancelled, write failed) counts
    /// as Cancel; if the project was edited while it was being saved, the question is asked again.</summary>
    public async Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string action)
    {
        BeforeSave?.Invoke(this, EventArgs.Empty);
        while (_projectService.Current.IsDirty)
        {
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = Strings.Unsaved_Title,
                Message = string.Format(Strings.Unsaved_Question, _projectService.Current.Name, action),
                Buttons = new[] { Strings.Common_Save, Strings.Common_DontSave, Strings.Common_Cancel }
            });

            switch (choice)
            {
                case 0:
                    if (!await SaveAsync()) return UnsavedChangesDecision.Cancel;
                    if (!_projectService.Current.IsDirty) return UnsavedChangesDecision.Saved;
                    continue; // edited during the save: those changes are unsaved — ask again
                case 1:
                    return UnsavedChangesDecision.DontSave;
                default:
                    return UnsavedChangesDecision.Cancel;
            }
        }

        return UnsavedChangesDecision.NoChanges;
    }

    private async Task DiscardRecoveryQuietlyAsync(Guid projectId)
    {
        try
        {
            await _autosave.DiscardRecoveryAsync(projectId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't remove the recovery file of discarded changes.");
        }
    }

    private static string? ParentOf(string? folder) => folder is null ? null : Path.GetDirectoryName(folder);

    // ---- Open (non-interactive) ---------------------------------------------------------

    /// <summary>Opens the project in <paramref name="projectFolderPath"/> without asking anything.
    /// Returns false (with a status message) if it couldn't be opened; the current project is
    /// then unchanged.</summary>
    public async Task<bool> OpenAsync(string projectFolderPath, CancellationToken ct = default)
    {
        Core.Entities.Project project;
        try
        {
            project = await _projectService.OpenAsync(projectFolderPath, ct);
        }
        catch (ProjectFileException ex)
        {
            _logger.LogWarning(ex, "Couldn't open project from {Folder}.", projectFolderPath);
            _status.Report(string.Format(Strings.Project_OpenFailed, ex.Message));
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        AnalyseWhereNeeded(project);
        _status.Report(string.Format(Strings.Project_Opened, project.Name) + MissingSuffix(project));
        await RememberRecentAsync(project.ProjectFolderPath!, project.Name);
        return true;
    }

    /// <summary>Called once the main window is shown: removes the thumbnail and waveform caches of unsaved projects
    /// that can't come back (before the recovery offer, so a recoverable project keeps them), offers to
    /// recover work autosaved by a session that didn't end normally, then starts autosave. Never throws — a
    /// problem with recovery files or caches must not prevent the editor from starting.</summary>
    public async Task StartSessionAsync()
    {
        try
        {
            if (_thumbnailCache is not null)
                await _thumbnailCache.CleanUpUnsavedAsync();
            if (_waveformCache is not null)
                await _waveformCache.CleanUpUnsavedAsync();

            var scan = await _autosave.FindRecoveryAsync();
            var setAside = scan.DamagedFiles == 0 ? "" : " " + Plural.Format(scan.DamagedFiles, Strings.Recovery_SetAside);

            if (scan.Candidate is { } candidate)
                await OfferRecoveryAsync(candidate, setAside);
            else if (setAside.Length > 0)
                _status.Report(setAside.TrimStart());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking for recovery files failed.");
        }
        finally
        {
            _autosave.Start();
        }
    }

    private async Task OnRecoverAsync(RecoveryCandidate candidate, string suffix)
    {
        Core.Entities.Project project;
        try
        {
            project = await _projectService.RestoreRecoveryAsync(candidate.FilePath);
        }
        catch (ProjectFileException ex)
        {
            _logger.LogWarning(ex, "Couldn't recover from {File}.", candidate.FilePath);
            _status.Report(string.Format(Strings.Recovery_Failed, candidate.ProjectName, ex.Message) + suffix);
            return;
        }

        AnalyseWhereNeeded(project);
        _status.Report(string.Format(Strings.Recovery_Recovered, project.Name) + MissingSuffix(project) + suffix);
        if (project.ProjectFolderPath is { } folder)
            await RememberRecentAsync(folder, project.Name); // also when the folder is gone: listed as unavailable
    }

    private async Task OfferRecoveryAsync(RecoveryCandidate candidate, string suffix)
    {
        var where = candidate.ProjectFolderPath is null
            ? Strings.Recovery_NotSavedYet
            : string.Format(Strings.Recovery_Folder, candidate.ProjectFolderPath);
        var choice = await _dialogs.AskAsync(new DialogRequest
        {
            Title = Strings.Recovery_Title,
            Message = string.Format(Strings.Recovery_Question, candidate.ProjectName, candidate.AutosavedAt.LocalDateTime.ToString("g"), where),
            Buttons = new[] { Strings.Recovery_Recover, Strings.Recovery_Discard, Strings.Recovery_NotNow }
        });

        switch (choice)
        {
            case 0:
                await OnRecoverAsync(candidate, suffix);
                break;
            case 1:
                await _autosave.DiscardRecoveryAsync(candidate);
                _status.Report(string.Format(Strings.Recovery_Discarded, candidate.ProjectName) + suffix);
                break;
            default:
                _status.Report(string.Format(Strings.Recovery_Kept, candidate.ProjectName) + suffix);
                break;
        }
    }

    /// <summary>Called when the main window is about to close: asks about unsaved changes
    /// (Cancel keeps the window open and autosave running), then shuts autosave down — a saved or
    /// clean project leaves no recovery file, "Don't Save" removes it. Returns true when the window
    /// may close.</summary>
    public async Task<bool> PrepareToCloseAsync()
    {
        var decision = await ConfirmUnsavedChangesAsync(Strings.Unsaved_ActionClose);
        if (decision == UnsavedChangesDecision.Cancel) return false;

        try
        {
            await _autosave.ShutdownAsync(keepUnsavedChanges: decision != UnsavedChangesDecision.DontSave);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autosave shutdown failed.");
        }
        return true;
    }

    /// <summary>Puts the project first in the recent-projects list. Never fails the operation that succeeded: the
    /// store logs what it couldn't write, and nothing is shown in the status bar.</summary>
    private async Task RememberRecentAsync(string folder, string name)
    {
        if (_recentProjects is null) return;
        try
        {
            await _recentProjects.AddAsync(folder, name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't add {Folder} to the recent projects.", folder);
        }
    }

    private void AnalyseWhereNeeded(Core.Entities.Project project)
    {
        // Saved metadata is reused; only media without it (and present on disk) is analysed, and
        // metadata saved before orientation was probed is refreshed in the background.
        var queued = _analysisCoordinator.QueueWhereNeeded(project.MediaAssets);
        if (queued > 0)
            _logger.LogInformation("Analysing {Count} media file(s) without saved metadata.", queued);
    }

    private static string MissingSuffix(Core.Entities.Project project) => project.MediaAssets.Count(a => a.IsMissing) switch
    {
        0 => "",
        var n => " " + Plural.Format(n, Strings.Media_MissingSuffix)
    };
}

/// <summary>Outcome of <see cref="ProjectFileWorkflow.ConfirmUnsavedChangesAsync"/>.</summary>
public enum UnsavedChangesDecision
{
    /// <summary>There was nothing to save.</summary>
    NoChanges,
    /// <summary>The user chose Save and the project was saved.</summary>
    Saved,
    /// <summary>The user chose to discard the changes.</summary>
    DontSave,
    /// <summary>The user cancelled (or chose Save and the save didn't happen).</summary>
    Cancel
}
