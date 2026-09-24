namespace AiVideoEditor.Core.Common;

/// <summary>
/// A reversible unit of change to the project state. Every mutation that a user can
/// trigger (move clip, trim, delete, add text, change a property, ...) should be
/// expressed as an <see cref="IUndoableCommand"/> rather than mutating state directly,
/// so it can participate in Undo/Redo.
/// </summary>
public interface IUndoableCommand
{
    /// <summary>Short human-readable description, e.g. "Move Clip", "Trim Left".</summary>
    string Description { get; }

    void Execute();

    void Undo();
}

/// <summary>
/// A command that can absorb the command executed right after it into one Undo step (e.g.
/// consecutive changes of the same property of the same clip while a value is being spun or
/// typed). Merging never mutates either command: it builds a new one, so a history position
/// handed out before the merge (see <see cref="IUndoRedoService.CurrentPosition"/>) can never
/// stand for the merged state.
/// </summary>
public interface IMergeableCommand : IUndoableCommand
{
    /// <summary>
    /// Called by <see cref="IUndoRedoService"/> after <paramref name="next"/> has executed, when
    /// this command is on top of the undo stack. Returns false when the two must stay separate
    /// steps. Otherwise <paramref name="merged"/> is a command whose Execute has the effect of this
    /// one followed by <paramref name="next"/> and whose Undo restores the state before this one —
    /// or null when the two cancel out exactly (the step disappears from the history).
    /// </summary>
    bool TryMerge(IUndoableCommand next, out IUndoableCommand? merged);
}

/// <summary>
/// Maintains the undo/redo stacks and is the single entry point through which
/// project-mutating commands are applied. Not "reload the whole project" style undo:
/// each command undoes exactly the change it made.
/// </summary>
public interface IUndoRedoService
{
    bool CanUndo { get; }
    bool CanRedo { get; }

    event EventHandler? StateChanged;

    /// <summary>Executes <paramref name="command"/> and records it. If the command on top of the
    /// undo stack is an <see cref="IMergeableCommand"/> that accepts it, the two become one step —
    /// but never when that top command is the save point or right after an Undo (the redo stack
    /// is not empty), so the saved state and undo boundaries the user created stay reachable.</summary>
    void Execute(IUndoableCommand command);

    void Undo();

    void Redo();

    /// <summary>Empties both stacks. The empty history becomes the save point (a new or
    /// just-opened project is clean).</summary>
    void Clear();

    /// <summary>Opaque token for the current position in the history: equal tokens mean
    /// the same project state as far as undoable changes are concerned. Capture it when
    /// the state is snapshotted for saving, and pass it to <see cref="MarkSavePoint"/> once
    /// the save has actually succeeded (edits made while the file was being written then
    /// correctly count as unsaved).</summary>
    object CurrentPosition { get; }

    /// <summary>Records <paramref name="position"/> (from <see cref="CurrentPosition"/>) as
    /// the saved state and raises <see cref="StateChanged"/>.</summary>
    void MarkSavePoint(object position);

    /// <summary>True when Undo/Redo have brought the history back to the save point. Becomes
    /// permanently false once the save point is discarded (a new command executed while the
    /// save point was on the redo stack), until the next <see cref="MarkSavePoint"/> or
    /// <see cref="Clear"/>.</summary>
    bool IsAtSavePoint { get; }
}
