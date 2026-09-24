using AiVideoEditor.Core.Common;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public class UndoRedoMergeTests
{
    private sealed class Box
    {
        public int Value;
    }

    /// <summary>Sets a key's value; merges with a later SetValue of the same key.</summary>
    private sealed class SetValue(Box box, string key, int before, int after) : IMergeableCommand
    {
        public string Key { get; } = key;
        public int Before { get; } = before;
        public int After { get; } = after;
        public string Description => $"Set {Key}";
        public void Execute() => box.Value = After;
        public void Undo() => box.Value = Before;

        public bool TryMerge(IUndoableCommand next, out IUndoableCommand? merged)
        {
            merged = null;
            if (next is not SetValue n || n.Key != Key || n.Before != After) return false;
            if (n.After != Before) merged = new SetValue(box, Key, Before, n.After);
            return true;
        }
    }

    private readonly UndoRedoService _s = new();
    private readonly Box _box = new();

    private void Set(int value, string key = "a") => _s.Execute(new SetValue(_box, key, _box.Value, value));

    private int Steps()
    {
        var steps = 0;
        while (_s.CanUndo) { _s.Undo(); steps++; }
        for (var i = 0; i < steps; i++) _s.Redo();
        return steps;
    }

    [Fact]
    public void Consecutive_mergeable_commands_become_one_step_that_undoes_to_the_first_before()
    {
        Set(1);
        Set(2);
        Set(3);

        Assert.Equal(3, _box.Value);
        Assert.Equal(1, Steps());

        _s.Undo();
        Assert.Equal(0, _box.Value);
        Assert.False(_s.CanUndo);

        _s.Redo();
        Assert.Equal(3, _box.Value);
    }

    [Fact]
    public void Commands_the_top_rejects_stay_separate_steps()
    {
        Set(1, "a");
        Set(2, "b");
        Set(3, "a");

        Assert.Equal(3, Steps());
    }

    [Fact]
    public void A_non_mergeable_top_command_is_never_merged_into()
    {
        _s.Execute(new NoOp());
        Set(1);
        Set(2);

        Assert.Equal(2, Steps());
    }

    [Fact]
    public void Merge_replaces_the_top_with_a_new_instance()
    {
        Set(1);
        var before = _s.CurrentPosition;

        Set(2);

        Assert.NotSame(before, _s.CurrentPosition);
        var top = Assert.IsType<SetValue>(_s.CurrentPosition);
        Assert.Equal((0, 2), (top.Before, top.After));
    }

    [Fact]
    public void Save_point_command_is_never_merged_into()
    {
        Set(1);
        _s.MarkSavePoint(_s.CurrentPosition);

        Set(2);
        Set(3);

        Assert.False(_s.IsAtSavePoint);
        Assert.Equal(2, Steps()); // saved step + one merged step after it

        _s.Undo();
        Assert.Equal(1, _box.Value);
        Assert.True(_s.IsAtSavePoint);
    }

    [Fact]
    public void Position_captured_before_a_merge_never_marks_the_merged_state_as_saved()
    {
        // Save snapshotted the history while the value was 1, then the edit continued.
        Set(1);
        var capturedForSave = _s.CurrentPosition;
        Set(2);

        _s.MarkSavePoint(capturedForSave); // the write finished with value 1

        Assert.False(_s.IsAtSavePoint);
    }

    [Fact]
    public void Edits_that_cancel_out_remove_the_step_and_return_to_the_save_point()
    {
        Set(5);
        _s.MarkSavePoint(_s.CurrentPosition);

        Set(6);
        Assert.False(_s.IsAtSavePoint);

        Set(5);
        Assert.Equal(5, _box.Value);
        Assert.True(_s.IsAtSavePoint);
        Assert.Equal(1, Steps());
    }

    [Fact]
    public void Cancelling_out_on_an_empty_history_leaves_it_empty_and_clean()
    {
        Set(1);
        Set(0);

        Assert.False(_s.CanUndo);
        Assert.True(_s.IsAtSavePoint);
    }

    [Fact]
    public void No_merge_right_after_undo()
    {
        Set(1);
        Set(2, "b");
        _s.Undo(); // b is on the redo stack; top is a(0→1)

        Set(3); // same key as the top, but the user just stepped back: a new step

        Assert.False(_s.CanRedo);
        Assert.Equal(2, Steps());
        _s.Undo();
        Assert.Equal(1, _box.Value);
    }

    [Fact]
    public void Merge_raises_StateChanged()
    {
        Set(1);
        var raised = 0;
        _s.StateChanged += (_, _) => raised++;

        Set(2);

        Assert.Equal(1, raised);
    }

    private sealed class NoOp : IUndoableCommand
    {
        public string Description => "No-op";
        public void Execute() { }
        public void Undo() { }
    }
}
