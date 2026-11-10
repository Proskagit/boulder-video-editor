namespace AiVideoEditor.Core.Common;

/// <summary>
/// D058 §3: one edit of several clips as one Undo / Redo step — the children run in order and are undone in reverse
/// (as <see cref="CompositeCommand"/>). Two group edits with the same <see cref="MergeKey"/> (the property and the clips)
/// in a row merge into one step, as consecutive changes of one field of one clip do (D017): a slider dragged over a group
/// is undone at once.
/// </summary>
public sealed class GroupEditCommand : IMergeableCommand
{
    private readonly IReadOnlyList<IUndoableCommand> _commands;

    public GroupEditCommand(string description, string? mergeKey, IEnumerable<IUndoableCommand> commands)
    {
        Description = description;
        MergeKey = mergeKey;
        _commands = commands.ToList();
        if (_commands.Count == 0)
            throw new ArgumentException("A group edit needs at least one command.", nameof(commands));
    }

    public string Description { get; }

    /// <summary>The property and the clips it changed; null never merges.</summary>
    public string? MergeKey { get; }

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

    public bool TryMerge(IUndoableCommand next, out IUndoableCommand? merged)
    {
        if (MergeKey is not null && next is GroupEditCommand group && group.MergeKey == MergeKey)
        {
            merged = new GroupEditCommand(Description, MergeKey, _commands.Concat(group._commands));
            return true;
        }
        merged = null;
        return false;
    }
}
