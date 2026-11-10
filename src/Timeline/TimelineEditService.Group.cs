using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Timeline;

// --- Several edits as one step (D058 §3: the Inspector's group of clips) -------------------------------------------------
public sealed partial class TimelineEditService
{
    /// <summary>The commands of the running <see cref="EditAsOneStep"/>, already executed; null outside it.</summary>
    private List<IUndoableCommand>? _gathered;

    public TimelineEditResult EditAsOneStep(string description, string? mergeKey, Func<IReadOnlyList<TimelineEditResult>> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (_gathered is not null) throw new InvalidOperationException("EditAsOneStep is not nested.");

        var gathered = _gathered = new List<IUndoableCommand>();
        IReadOnlyList<TimelineEditResult> results;
        try
        {
            results = edits();
        }
        catch
        {
            Rollback(gathered);
            throw;
        }
        finally
        {
            _gathered = null;
        }

        if (results.FirstOrDefault(r => !r.Success) is { } refused)
        {
            Rollback(gathered);
            return refused;
        }
        if (gathered.Count == 0) return TimelineEditResult.Unchanged();
        _undoRedo.Record(new GroupEditCommand(description, mergeKey, gathered));
        return TimelineEditResult.Ok(results.SelectMany(r => r.ClipIds).Distinct().ToList());
    }

    private static void Rollback(List<IUndoableCommand> executed)
    {
        for (var i = executed.Count - 1; i >= 0; i--)
            executed[i].Undo();
    }
}
