namespace AiVideoEditor.Core.Common;

/// <summary>
/// Several commands applied and undone as one Undo/Redo step (e.g. deleting a
/// multi-clip selection). Executes children in order and undoes them in reverse.
/// Atomic: if a child throws during <see cref="Execute"/>, the children that
/// already ran are undone before the exception propagates, so a failed composite
/// leaves no partial change behind (and <see cref="UndoRedoService"/> never
/// pushes it, since Execute threw).
/// </summary>
public sealed class CompositeCommand : IUndoableCommand
{
    private readonly IReadOnlyList<IUndoableCommand> _commands;

    public CompositeCommand(string description, IEnumerable<IUndoableCommand> commands)
    {
        Description = description;
        _commands = commands.ToList();
        if (_commands.Count == 0)
            throw new ArgumentException("A composite command needs at least one command.", nameof(commands));
    }

    public string Description { get; }

    public IReadOnlyList<IUndoableCommand> Commands => _commands;

    public void Execute()
    {
        var executed = 0;
        try
        {
            for (; executed < _commands.Count; executed++)
                _commands[executed].Execute();
        }
        catch
        {
            for (var i = executed - 1; i >= 0; i--)
                _commands[i].Undo();
            throw;
        }
    }

    public void Undo()
    {
        for (var i = _commands.Count - 1; i >= 0; i--)
            _commands[i].Undo();
    }
}
