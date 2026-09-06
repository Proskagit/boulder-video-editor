namespace AiVideoEditor.Core.Common;

public sealed class UndoRedoService : IUndoRedoService
{
    private readonly Stack<IUndoableCommand> _undoStack = new();
    private readonly Stack<IUndoableCommand> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event EventHandler? StateChanged;

    public void Execute(IUndoableCommand command)
    {
        command.Execute();
        _undoStack.Push(command);
        _redoStack.Clear();
        RaiseStateChanged();
    }

    public void Undo()
    {
        if (!_undoStack.TryPop(out var command)) return;
        command.Undo();
        _redoStack.Push(command);
        RaiseStateChanged();
    }

    public void Redo()
    {
        if (!_redoStack.TryPop(out var command)) return;
        command.Execute();
        _undoStack.Push(command);
        RaiseStateChanged();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
