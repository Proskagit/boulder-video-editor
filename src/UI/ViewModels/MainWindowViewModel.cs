using AiVideoEditor.Core.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// Phase 0 shell view model. It intentionally does very little yet: its job right
/// now is to prove that DI, logging and the Core services are wired correctly end
/// to end. The real docked layout (Media Browser / Preview / Inspector / Timeline)
/// is built in Phase 1.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IUndoRedoService _undoRedoService;
    private readonly ILogger<MainWindowViewModel> _logger;

    [ObservableProperty]
    private string _statusMessage = "Ready.";

    [ObservableProperty]
    private int _demoCounter;

    public string Title => "AI Video Editor";

    public MainWindowViewModel(IUndoRedoService undoRedoService, ILogger<MainWindowViewModel> logger)
    {
        _undoRedoService = undoRedoService;
        _logger = logger;
        _undoRedoService.StateChanged += (_, _) => UndoCommand.NotifyCanExecuteChanged();
        _logger.LogInformation("Application shell initialized (Phase 0 skeleton).");
    }

    [RelayCommand]
    private void IncrementDemo()
    {
        // A trivial undoable command, standing in for real editing operations
        // (move clip, trim, add text, ...) until Phase 3 introduces them.
        _undoRedoService.Execute(new IncrementCounterCommand(this));
        StatusMessage = $"Demo counter is now {DemoCounter}.";
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _undoRedoService.Undo();

    private bool CanUndo() => _undoRedoService.CanUndo;

    private sealed class IncrementCounterCommand(MainWindowViewModel owner) : IUndoableCommand
    {
        public string Description => "Increment demo counter";

        public void Execute() => owner.DemoCounter++;

        public void Undo() => owner.DemoCounter--;
    }
}
