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
/// Maintains the undo/redo stacks and is the single entry point through which
/// project-mutating commands are applied. Not "reload the whole project" style undo:
/// each command undoes exactly the change it made.
/// </summary>
public interface IUndoRedoService
{
    bool CanUndo { get; }
    bool CanRedo { get; }

    event EventHandler? StateChanged;

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
