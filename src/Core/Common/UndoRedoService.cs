namespace AiVideoEditor.Core.Common;

public sealed class UndoRedoService : IUndoRedoService
{
    /// <summary>History position of an empty undo stack.</summary>
    private static readonly object EmptyHistory = new();

    private readonly Stack<IUndoableCommand> _undoStack = new();
    private readonly Stack<IUndoableCommand> _redoStack = new();

    // A position is identified by the command on top of the undo stack: every executed
    // command is a distinct instance, so once a command is dropped from the redo stack its
    // position can never come back, and a save point pointing at it is unreachable.
    private object _savePoint = EmptyHistory;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public object CurrentPosition => _undoStack.TryPeek(out var top) ? top : EmptyHistory;

    public bool IsAtSavePoint => ReferenceEquals(CurrentPosition, _savePoint);

    public event EventHandler? StateChanged;

    public void Execute(IUndoableCommand command)
    {
        command.Execute();

        // Merging replaces the top command with a new instance (or removes it when the two
        // cancel out), so a position captured before the merge no longer matches. The save
        // point itself is never merged into: the saved state must stay reachable by Undo/Redo.
        if (_redoStack.Count == 0
            && _undoStack.TryPeek(out var top)
            && !ReferenceEquals(top, _savePoint)
            && top is IMergeableCommand mergeable
            && mergeable.TryMerge(command, out var merged))
        {
            _undoStack.Pop();
            if (merged is not null) _undoStack.Push(merged);
        }
        else
        {
            _undoStack.Push(command);
        }

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
        _savePoint = EmptyHistory;
        RaiseStateChanged();
    }

    public void MarkSavePoint(object position)
    {
        ArgumentNullException.ThrowIfNull(position);
        _savePoint = position;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
