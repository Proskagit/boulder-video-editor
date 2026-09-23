using AiVideoEditor.Core.Common;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public class UndoRedoSavePointTests
{
    private sealed class NoOp(string name) : IUndoableCommand
    {
        public string Description => name;
        public void Execute() { }
        public void Undo() { }
    }

    private static void Run(UndoRedoService s, string name = "x") => s.Execute(new NoOp(name));

    [Fact]
    public void Fresh_history_is_at_the_save_point()
    {
        Assert.True(new UndoRedoService().IsAtSavePoint);
    }

    [Fact]
    public void Execute_leaves_and_undo_returns_to_the_save_point()
    {
        var s = new UndoRedoService();

        Run(s);
        Assert.False(s.IsAtSavePoint);

        s.Undo();
        Assert.True(s.IsAtSavePoint);

        s.Redo();
        Assert.False(s.IsAtSavePoint);
    }

    [Fact]
    public void Save_point_in_the_middle_is_reached_by_undo_and_redo()
    {
        var s = new UndoRedoService();
        Run(s, "a");
        Run(s, "b");
        s.MarkSavePoint(s.CurrentPosition);
        Assert.True(s.IsAtSavePoint);

        s.Undo();
        Assert.False(s.IsAtSavePoint);
        s.Undo();
        Assert.False(s.IsAtSavePoint);
        s.Redo();
        Assert.False(s.IsAtSavePoint);
        s.Redo();
        Assert.True(s.IsAtSavePoint);

        Run(s, "c");
        Assert.False(s.IsAtSavePoint);
        s.Undo();
        Assert.True(s.IsAtSavePoint);
    }

    [Fact]
    public void Save_point_discarded_from_the_redo_stack_is_unreachable()
    {
        var s = new UndoRedoService();
        Run(s, "a");
        s.MarkSavePoint(s.CurrentPosition);
        s.Undo();

        Run(s, "b"); // drops "a" (the save point) from the redo stack

        Assert.False(s.IsAtSavePoint);
        s.Undo();
        Assert.False(s.IsAtSavePoint); // empty history ≠ saved state (which had "a" applied)
        s.Redo();
        Assert.False(s.IsAtSavePoint);
    }

    [Fact]
    public void Save_point_at_empty_history_survives_new_commands()
    {
        var s = new UndoRedoService();
        Run(s, "a");
        s.Undo();
        Run(s, "b");

        s.Undo();

        Assert.True(s.IsAtSavePoint); // nothing applied = the state a fresh project was saved in
    }

    [Fact]
    public void Clear_makes_the_empty_history_the_save_point()
    {
        var s = new UndoRedoService();
        Run(s, "a");
        s.MarkSavePoint(s.CurrentPosition);
        Run(s, "b");

        s.Clear();

        Assert.True(s.IsAtSavePoint);
        Run(s, "c");
        Assert.False(s.IsAtSavePoint);
    }

    [Fact]
    public void Earlier_captured_position_marks_the_state_that_was_saved()
    {
        var s = new UndoRedoService();
        Run(s, "a");
        var saved = s.CurrentPosition;
        Run(s, "edited while saving");

        s.MarkSavePoint(saved);

        Assert.False(s.IsAtSavePoint);
        s.Undo();
        Assert.True(s.IsAtSavePoint);
    }

    [Fact]
    public void Position_is_stable_across_undo_and_redo()
    {
        var s = new UndoRedoService();
        Run(s, "a");
        var p = s.CurrentPosition;

        s.Undo();
        Assert.NotSame(p, s.CurrentPosition);
        s.Redo();

        Assert.Same(p, s.CurrentPosition);
    }

    [Fact]
    public void MarkSavePoint_raises_StateChanged()
    {
        var s = new UndoRedoService();
        Run(s);
        var raised = 0;
        s.StateChanged += (_, _) => raised++;

        s.MarkSavePoint(s.CurrentPosition);

        Assert.Equal(1, raised);
    }
}
