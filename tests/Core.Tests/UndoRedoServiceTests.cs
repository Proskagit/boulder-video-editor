using AiVideoEditor.Core.Common;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public class UndoRedoServiceTests
{
    /// <summary>Appends/removes a marker in a shared log so tests can see exact order.</summary>
    private sealed class LogCommand(List<string> log, string name, bool throwOnExecute = false) : IUndoableCommand
    {
        public string Description => name;

        public void Execute()
        {
            if (throwOnExecute) throw new InvalidOperationException(name);
            log.Add("+" + name);
        }

        public void Undo() => log.Add("-" + name);
    }

    [Fact]
    public void Execute_Undo_Redo_RunInOrder()
    {
        var log = new List<string>();
        var service = new UndoRedoService();

        service.Execute(new LogCommand(log, "a"));
        service.Execute(new LogCommand(log, "b"));
        service.Undo();
        service.Undo();
        service.Redo();

        Assert.Equal(new[] { "+a", "+b", "-b", "-a", "+a" }, log);
        Assert.True(service.CanUndo);
        Assert.True(service.CanRedo);
    }

    [Fact]
    public void Execute_ClearsRedoStack()
    {
        var log = new List<string>();
        var service = new UndoRedoService();

        service.Execute(new LogCommand(log, "a"));
        service.Undo();
        service.Execute(new LogCommand(log, "b"));

        Assert.False(service.CanRedo);
    }

    [Fact]
    public void UndoRedo_OnEmptyStacks_AreNoOps()
    {
        var service = new UndoRedoService();
        var raised = 0;
        service.StateChanged += (_, _) => raised++;

        service.Undo();
        service.Redo();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void StateChanged_RaisedForEveryChange()
    {
        var log = new List<string>();
        var service = new UndoRedoService();
        var raised = 0;
        service.StateChanged += (_, _) => raised++;

        service.Execute(new LogCommand(log, "a"));
        service.Undo();
        service.Redo();
        service.Clear();

        Assert.Equal(4, raised);
        Assert.False(service.CanUndo);
        Assert.False(service.CanRedo);
    }

    [Fact]
    public void FailedExecute_IsNotPushed()
    {
        var log = new List<string>();
        var service = new UndoRedoService();

        Assert.Throws<InvalidOperationException>(() => service.Execute(new LogCommand(log, "bad", throwOnExecute: true)));

        Assert.False(service.CanUndo);
        Assert.Empty(log);
    }

    [Fact]
    public void Composite_ExecutesInOrder_UndoesInReverse()
    {
        var log = new List<string>();
        var service = new UndoRedoService();

        service.Execute(new CompositeCommand("ab", new[] { new LogCommand(log, "a"), new LogCommand(log, "b") }));
        service.Undo();
        service.Redo();

        Assert.Equal(new[] { "+a", "+b", "-b", "-a", "+a", "+b" }, log);
    }

    [Fact]
    public void Composite_RollsBack_WhenAChildFails()
    {
        var log = new List<string>();
        var service = new UndoRedoService();
        var composite = new CompositeCommand("abc", new[]
        {
            new LogCommand(log, "a"),
            new LogCommand(log, "b"),
            new LogCommand(log, "c", throwOnExecute: true)
        });

        Assert.Throws<InvalidOperationException>(() => service.Execute(composite));

        Assert.Equal(new[] { "+a", "+b", "-b", "-a" }, log);
        Assert.False(service.CanUndo);
    }

    [Fact]
    public void Composite_RequiresAtLeastOneCommand()
    {
        Assert.Throws<ArgumentException>(() => new CompositeCommand("empty", Array.Empty<IUndoableCommand>()));
    }
}
