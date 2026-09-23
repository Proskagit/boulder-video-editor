using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// The UI side of the project's life on disk: New / Open / Save / Save As / Close with the
/// folder picker and the "save changes?" prompt, analysis of media that still needs it after
/// Open, the startup offer to recover autosaved work, and autosave at shutdown. Every failure
/// is reported in the status bar and leaves the current project as it was.
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

    public ProjectFileWorkflow(
        IProjectService projectService,
        MediaAnalysisCoordinator analysisCoordinator,
        IAutosaveService autosave,
        IDialogService dialogs,
        IFilePickerService picker,
        StatusService status,
        ILogger<ProjectFileWorkflow> logger)
    {
        _projectService = projectService;
        _analysisCoordinator = analysisCoordinator;
        _autosave = autosave;
        _dialogs = dialogs;
        _picker = picker;
        _status = status;
        _logger = logger;
    }

    // ---- New / Open (interactive) ---------------------------------------------------

    /// <summary>New: asks about unsaved changes first, then replaces the project with an empty
    /// one. Returns false if cancelled.</summary>
    public async Task<bool> NewProjectAsync()
    {
        var previousId = _projectService.Current.Id;
        var decision = await ConfirmUnsavedChangesAsync("creating a new project");
        if (decision == UnsavedChangesDecision.Cancel) return false;

        _projectService.CreateNew("Untitled Project"); // also resets the undo history
        if (decision == UnsavedChangesDecision.DontSave)
            await DiscardRecoveryQuietlyAsync(previousId);
        _status.Report("New project created.");
        return true;
    }

    /// <summary>Open: picks a project folder, asks about unsaved changes, then opens it. Returns
    /// false if cancelled or if the project couldn't be opened (the current project is then kept,
    /// including its unsaved changes and their recovery file).</summary>
    public async Task<bool> OpenProjectAsync()
    {
        var folder = await _picker.PickFolderAsync(new FolderPickerRequest
        {
            Title = "Open Project — select the project folder",
            StartFolder = ParentOf(_projectService.Current.ProjectFolderPath)
        });
        if (folder is null) return false;

        var previousId = _projectService.Current.Id;
        var decision = await ConfirmUnsavedChangesAsync("opening another project");
        if (decision == UnsavedChangesDecision.Cancel) return false;

        if (!await OpenAsync(folder)) return false;
        if (decision == UnsavedChangesDecision.DontSave)
            await DiscardRecoveryQuietlyAsync(previousId);
        return true;
    }

    // ---- Save / Save As -------------------------------------------------------------

    /// <summary>Save: to the project's folder, or through Save As if it has never been saved.
    /// Returns true only if project.json was actually written.</summary>
    public Task<bool> SaveAsync() =>
        _projectService.Current.ProjectFolderPath is null ? SaveAsAsync() : SaveToCurrentFolderAsync();

    private async Task<bool> SaveToCurrentFolderAsync()
    {
        try
        {
            await _projectService.SaveAsync();
        }
        catch (ProjectFileException ex)
        {
            _logger.LogWarning(ex, "Save failed.");
            _status.Report($"Couldn't save \"{_projectService.Current.Name}\". {ex.Message}");
            return false;
        }

        _status.Report(SavedMessage($"Saved project \"{_projectService.Current.Name}\"."));
        return true;
    }

    /// <summary>Save As: picks (or creates) a folder, confirms replacing another project that is
    /// already there, and saves. Cancelling or a failure changes nothing. Returns true only if
    /// project.json was actually written.</summary>
    public async Task<bool> SaveAsAsync()
    {
        var current = _projectService.Current;
        var folder = await _picker.PickFolderAsync(new FolderPickerRequest
        {
            Title = "Save Project As — select or create a folder for the project",
            StartFolder = ParentOf(current.ProjectFolderPath)
        });
        if (folder is null) return false;

        var isCurrentFolder = current.ProjectFolderPath is { } own &&
                              string.Equals(Path.GetFullPath(own), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
        if (!isCurrentFolder && File.Exists(Path.Combine(folder, "project.json")))
        {
            var replace = await _dialogs.AskAsync(new DialogRequest
            {
                Title = "Replace project?",
                Message = $"The folder \"{folder}\" already contains a project. Replace it with \"{current.Name}\"?",
                Buttons = new[] { "Replace", "Cancel" }
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
            _status.Report($"Couldn't save \"{current.Name}\". {ex.Message}");
            return false;
        }

        _status.Report(SavedMessage($"Saved project \"{_projectService.Current.Name}\" to {folder}."));
        return true;
    }

    private string SavedMessage(string message) => _projectService.Current.IsDirty
        ? message + " Changes made while saving are not saved yet."
        : message;

    // ---- Unsaved changes -------------------------------------------------------------

    /// <summary>If the project has unsaved changes, asks Save / Don't Save / Cancel before
    /// <paramref name="action"/>. Save that doesn't complete (picker cancelled, write failed) counts
    /// as Cancel; if the project was edited while it was being saved, the question is asked again.</summary>
    public async Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string action)
    {
        while (_projectService.Current.IsDirty)
        {
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = "Unsaved changes",
                Message = $"Do you want to save the changes to \"{_projectService.Current.Name}\" before {action}?\n\n" +
                          "If you don't save, your changes will be lost.",
                Buttons = new[] { "Save", "Don't Save", "Cancel" }
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
            _status.Report($"Couldn't open the project. {ex.Message}");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        AnalyseWhereNeeded(project);
        _status.Report($"Opened project \"{project.Name}\".{MissingSuffix(project)}");
        return true;
    }

    /// <summary>Called once the main window is shown: offers to recover work autosaved by a
    /// session that didn't end normally, then starts autosave. Never throws — a problem with
    /// recovery files must not prevent the editor from starting.</summary>
    public async Task StartSessionAsync()
    {
        try
        {
            var scan = await _autosave.FindRecoveryAsync();
            var setAside = scan.DamagedFiles switch
            {
                0 => "",
                1 => " 1 recovery file couldn't be used and was set aside.",
                _ => $" {scan.DamagedFiles} recovery files couldn't be used and were set aside."
            };

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
            _status.Report($"Couldn't recover \"{candidate.ProjectName}\". {ex.Message}{suffix}");
            return;
        }

        AnalyseWhereNeeded(project);
        _status.Report($"Recovered unsaved changes to \"{project.Name}\" — save the project to keep them.{MissingSuffix(project)}{suffix}");
    }

    private async Task OfferRecoveryAsync(RecoveryCandidate candidate, string suffix)
    {
        var where = candidate.ProjectFolderPath is null
            ? "The project had not been saved yet."
            : $"Project folder: {candidate.ProjectFolderPath}";
        var choice = await _dialogs.AskAsync(new DialogRequest
        {
            Title = "Recover unsaved work",
            Message = $"AI Video Editor didn't close normally. Unsaved changes to \"{candidate.ProjectName}\" " +
                      $"were autosaved at {candidate.AutosavedAt.LocalDateTime:g}.\n{where}\n\n" +
                      "Recover them now? Discarded changes can't be restored.",
            Buttons = new[] { "Recover", "Discard", "Not now" }
        });

        switch (choice)
        {
            case 0:
                await OnRecoverAsync(candidate, suffix);
                break;
            case 1:
                await _autosave.DiscardRecoveryAsync(candidate);
                _status.Report($"Discarded the autosaved changes to \"{candidate.ProjectName}\".{suffix}");
                break;
            default:
                _status.Report($"Autosaved changes to \"{candidate.ProjectName}\" were kept; you'll be asked again next time.{suffix}");
                break;
        }
    }

    /// <summary>Called when the main window is about to close: asks about unsaved changes
    /// (Cancel keeps the window open and autosave running), then shuts autosave down — a saved or
    /// clean project leaves no recovery file, "Don't Save" removes it. Returns true when the window
    /// may close.</summary>
    public async Task<bool> PrepareToCloseAsync()
    {
        var decision = await ConfirmUnsavedChangesAsync("closing");
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

    private void AnalyseWhereNeeded(Core.Entities.Project project)
    {
        // Saved metadata is reused; only media without it (and present on disk) is analysed.
        var queued = _analysisCoordinator.QueueWhereNeeded(project.MediaAssets);
        if (queued > 0)
            _logger.LogInformation("Analysing {Count} media file(s) without saved metadata.", queued);
    }

    private static string MissingSuffix(Core.Entities.Project project) => project.MediaAssets.Count(a => a.IsMissing) switch
    {
        0 => "",
        1 => " 1 media file is missing and is shown as offline.",
        var n => $" {n} media files are missing and are shown as offline."
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
