using AiVideoEditor.Core.Common;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Top toolbar. New / Open / Save / Save As go through <see cref="ProjectFileWorkflow"/> (folder
/// picker, "save changes?" prompt, status messages); Export goes through <see cref="ExportWorkflow"/>.
/// Import here calls the exact same <see cref="MediaImportWorkflow"/> as the
/// Media Browser's own Import button, so the two stay identical with no duplicated logic.
/// The project commands are async commands: while one runs it can't be started again. While an export
/// runs (<see cref="EditingLock"/>) every command here is disabled. <see cref="Recent"/> is the <c>Recent ▾</c> drop-down
/// next to Open (D026 §6), absent when not given. "Project Settings…" (D028, Step 13.6) opens the canvas / frame-rate dialog
/// through <see cref="ProjectSettingsWorkflow"/>; its tooltip shows the current settings.
/// </summary>
public sealed partial class ToolbarViewModel : ViewModelBase
{
    private readonly IUndoRedoService _undoRedoService;
    private readonly ProjectFileWorkflow _projectFiles;
    private readonly MediaImportWorkflow _importWorkflow;
    private readonly StatusService _status;
    private readonly ExportWorkflow? _exportWorkflow;
    private readonly EditingLock _editingLock;
    private readonly ProjectSettingsWorkflow? _projectSettings;

    /// <param name="exportWorkflow">Without it Export reports that exporting is unavailable.</param>
    /// <param name="editingLock">The app's shared lock; a private one when not given.</param>
    /// <param name="recentProjects">The <c>Recent ▾</c> drop-down; without it the button is hidden.</param>
    /// <param name="projectSettings">Project Settings…; without it the command reports that it is unavailable.</param>
    public ToolbarViewModel(
        IUndoRedoService undoRedoService,
        ProjectFileWorkflow projectFiles,
        MediaImportWorkflow importWorkflow,
        StatusService status,
        ExportWorkflow? exportWorkflow = null,
        EditingLock? editingLock = null,
        RecentProjectsViewModel? recentProjects = null,
        ProjectSettingsWorkflow? projectSettings = null)
    {
        _projectSettings = projectSettings;
        if (_projectSettings is not null)
            _projectSettings.SummaryChanged += (_, _) => OnPropertyChanged(nameof(ProjectSettingsToolTip));
        Recent = recentProjects;
        _undoRedoService = undoRedoService;
        _projectFiles = projectFiles;
        _importWorkflow = importWorkflow;
        _status = status;
        _exportWorkflow = exportWorkflow;
        _editingLock = editingLock ?? new EditingLock();
        _undoRedoService.StateChanged += OnUndoRedoStateChanged;
        _editingLock.PropertyChanged += (_, _) => NotifyAllCommands();
        if (_exportWorkflow is not null)
            _exportWorkflow.IsRunningChanged += (_, _) => ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The <c>Recent ▾</c> drop-down, or null.</summary>
    public RecentProjectsViewModel? Recent { get; }

    private bool CanEdit() => !_editingLock.IsLocked;

    /// <summary>"Project Settings — 1920 × 1080 · 25 FPS".</summary>
    public string ProjectSettingsToolTip => _projectSettings is null
        ? "Project Settings"
        : $"Project Settings — {_projectSettings.Summary}";

    private void NotifyAllCommands()
    {
        NewProjectCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        ImportMediaCommand.NotifyCanExecuteChanged();
        ProjectSettingsCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    private void OnUndoRedoStateChanged(object? sender, EventArgs e)
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task NewProject() => _projectFiles.NewProjectAsync();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task Open() => _projectFiles.OpenProjectAsync();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task Save() => _projectFiles.SaveAsync();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task SaveAs() => _projectFiles.SaveAsAsync();

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _undoRedoService.Undo();

    private bool CanUndo() => CanEdit() && _undoRedoService.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _undoRedoService.Redo();

    private bool CanRedo() => CanEdit() && _undoRedoService.CanRedo;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task ImportMedia() => _importWorkflow.RunAsync();

    /// <summary>Project Settings… (D028, Step 13.6): the canvas size and the frame rate; disabled during an export.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ProjectSettings()
    {
        if (_projectSettings is null)
        {
            _status.Report("Project settings are not available.");
            return;
        }
        await _projectSettings.RunAsync();
    }

    /// <summary>Export (Phase 8 Step 7): preflight, output file, progress window — see <see cref="ExportWorkflow"/>.
    /// Not available while an export runs or unwinds.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        if (_exportWorkflow is null)
        {
            _status.Report("Exporting is not available.");
            return;
        }
        await _exportWorkflow.RunAsync();
    }

    private bool CanExport() => CanEdit() && _exportWorkflow is not { IsRunning: true };
}
