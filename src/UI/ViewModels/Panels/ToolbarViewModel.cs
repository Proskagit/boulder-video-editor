using AiVideoEditor.Core.Common;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Top toolbar. New / Open / Save / Save As go through <see cref="ProjectFileWorkflow"/> (folder
/// picker, "save changes?" prompt, status messages); Export still just reports that it isn't
/// implemented yet. Import here calls the exact same <see cref="MediaImportWorkflow"/> as the
/// Media Browser's own Import button, so the two stay identical with no duplicated logic.
/// The project commands are async commands: while one runs it can't be started again.
/// </summary>
public sealed partial class ToolbarViewModel : ViewModelBase
{
    private readonly IUndoRedoService _undoRedoService;
    private readonly ProjectFileWorkflow _projectFiles;
    private readonly MediaImportWorkflow _importWorkflow;
    private readonly StatusService _status;

    public ToolbarViewModel(
        IUndoRedoService undoRedoService,
        ProjectFileWorkflow projectFiles,
        MediaImportWorkflow importWorkflow,
        StatusService status)
    {
        _undoRedoService = undoRedoService;
        _projectFiles = projectFiles;
        _importWorkflow = importWorkflow;
        _status = status;
        _undoRedoService.StateChanged += OnUndoRedoStateChanged;
    }

    private void OnUndoRedoStateChanged(object? sender, EventArgs e)
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private Task NewProject() => _projectFiles.NewProjectAsync();

    [RelayCommand]
    private Task Open() => _projectFiles.OpenProjectAsync();

    [RelayCommand]
    private Task Save() => _projectFiles.SaveAsync();

    [RelayCommand]
    private Task SaveAs() => _projectFiles.SaveAsAsync();

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _undoRedoService.Undo();

    private bool CanUndo() => _undoRedoService.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _undoRedoService.Redo();

    private bool CanRedo() => _undoRedoService.CanRedo;

    [RelayCommand]
    private Task ImportMedia() => _importWorkflow.RunAsync();

    [RelayCommand]
    private void Export() => _status.Report("Export isn't implemented yet (arrives in Phase 8).");
}
