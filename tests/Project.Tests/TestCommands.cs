using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Project.Tests;

/// <summary>An undoable edit that goes through NotifyTimelineChanged like the real timeline commands.</summary>
internal sealed class RenameTimelineCommand(IProjectService projects, string name) : IUndoableCommand
{
    private string? _old;
    public string Description => "Rename";

    public void Execute()
    {
        _old = projects.Current.Timeline.Name;
        projects.Current.Timeline.Name = name;
        projects.NotifyTimelineChanged();
    }

    public void Undo()
    {
        projects.Current.Timeline.Name = _old!;
        projects.NotifyTimelineChanged();
    }
}

internal static class Wait
{
    /// <summary>Polls <paramref name="condition"/> until true or the timeout elapses.</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) return false;
            await Task.Delay(10);
        }
        return true;
    }
}
