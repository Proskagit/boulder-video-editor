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

    void Clear();
}
