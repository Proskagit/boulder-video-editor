using AiVideoEditor.Core.Common;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Top toolbar. Everything except Undo/Redo is a placeholder command in Phase 1 —
/// New/Open/Save/Import/Export don't do anything real yet (no Project/Media/Export
/// subsystem behind them), so they just log an informational message. They are
/// still real, disable-able commands wired through the ViewModel, not code-behind
/// click handlers, so wiring in the real behavior in later phases is a one-line
/// change here rather than touching the view.
/// </summary>
public sealed partial class ToolbarViewModel : ViewModelBase
{
    private readonly IUndoRedoService _undoRedoService;
    private readonly ILogger<ToolbarViewModel> _logger;

    public ToolbarViewModel(IUndoRedoService undoRedoService, ILogger<ToolbarViewModel> logger)
    {
        _undoRedoService = undoRedoService;
        _logger = logger;
        _undoRedoService.StateChanged += OnUndoRedoStateChanged;
    }

    private void OnUndoRedoStateChanged(object? sender, EventArgs e)
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void NewProject() => _logger.LogInformation("New Project requested (not implemented until Phase 5).");

    [RelayCommand]
    private void Open() => _logger.LogInformation("Open Project requested (not implemented until Phase 5).");

    [RelayCommand]
    private void Save() => _logger.LogInformation("Save Project requested (not implemented until Phase 5).");

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _undoRedoService.Undo();

    private bool CanUndo() => _undoRedoService.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _undoRedoService.Redo();

    private bool CanRedo() => _undoRedoService.CanRedo;

    [RelayCommand]
    private void ImportMedia() => _logger.LogInformation("Import Media requested (not implemented until Phase 2).");

    [RelayCommand]
    private void Export() => _logger.LogInformation("Export requested (not implemented until Phase 7).");
}
