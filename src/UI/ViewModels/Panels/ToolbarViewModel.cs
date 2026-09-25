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
/// runs (<see cref="EditingLock"/>) every command here is disabled.
/// </summary>
public sealed partial class ToolbarViewModel : ViewModelBase
{
    private readonly IUndoRedoService _undoRedoService;
    private readonly ProjectFileWorkflow _projectFiles;
    private readonly MediaImportWorkflow _importWorkflow;
    private readonly StatusService _status;
    private readonly ExportWorkflow? _exportWorkflow;
    private readonly EditingLock _editingLock;

    /// <param name="exportWorkflow">Without it Export reports that exporting is unavailable.</param>
    /// <param name="editingLock">The app's shared lock; a private one when not given.</param>
    public ToolbarViewModel(
        IUndoRedoService undoRedoService,
        ProjectFileWorkflow projectFiles,
        MediaImportWorkflow importWorkflow,
        StatusService status,
        ExportWorkflow? exportWorkflow = null,
        EditingLock? editingLock = null)
    {
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

    private bool CanEdit() => !_editingLock.IsLocked;

    private void NotifyAllCommands()
    {
        NewProjectCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        ImportMediaCommand.NotifyCanExecuteChanged();
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
