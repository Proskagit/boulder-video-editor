using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Top toolbar. New Project and Import Media are real (Phase 2); Open/Save/Export
/// stay enabled but just report a clear "not implemented yet" status message
/// instead of pretending to work — no fake persistence, per the Phase 2 rules.
/// Import here calls the exact same <see cref="MediaImportWorkflow"/> as the Media
/// Browser's own Import button, so the two stay identical with no duplicated logic.
/// </summary>
public sealed partial class ToolbarViewModel : ViewModelBase
{
    private readonly IUndoRedoService _undoRedoService;
    private readonly IProjectService _projectService;
    private readonly MediaImportWorkflow _importWorkflow;
    private readonly StatusService _status;

    public ToolbarViewModel(
        IUndoRedoService undoRedoService,
        IProjectService projectService,
        MediaImportWorkflow importWorkflow,
        StatusService status)
    {
        _undoRedoService = undoRedoService;
        _projectService = projectService;
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
    private void NewProject()
    {
        _projectService.CreateNew("Untitled Project");
        _undoRedoService.Clear();
        _status.Report("New project created");
    }

    [RelayCommand]
    private void Open() => _status.Report("Opening saved projects isn't implemented yet (arrives in Phase 6).");

    [RelayCommand]
    private void Save() => _status.Report("Saving projects isn't implemented yet (arrives in Phase 6).");

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
