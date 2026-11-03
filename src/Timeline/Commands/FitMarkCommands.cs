using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;

namespace AiVideoEditor.Timeline.Commands;

/// <summary>The training's FIT marks and declined climbs replaced as a whole (D051 §5): absolute before / after, so Redo
/// gives exactly the same marks — the same <c>positionChangedAt</c>, estimate and version — and Undo the former ones.</summary>
public sealed class SetFitMarksCommand(
    TrainingReference training,
    IReadOnlyList<FitMark> marksBefore, IReadOnlyList<DeclinedClimb> declinedBefore,
    IReadOnlyList<FitMark> marksAfter, IReadOnlyList<DeclinedClimb> declinedAfter,
    string description) : IUndoableCommand
{
    public string Description => description;

    public void Execute() => Apply(marksAfter, declinedAfter);

    public void Undo() => Apply(marksBefore, declinedBefore);

    private void Apply(IReadOnlyList<FitMark> marks, IReadOnlyList<DeclinedClimb> declined)
    {
        training.Marks.Clear();
        training.Marks.AddRange(marks);
        training.DeclinedClimbs.Clear();
        training.DeclinedClimbs.AddRange(declined);
    }
}

/// <summary>
/// An edit with the FIT marks following it in the same step (D051 §5.4, §5.6): after <paramref name="inner"/> runs the
/// first time, a link whose clip lost its Duration markup follows it to the clip the edit created or is removed, and
/// <c>positionChangedAt</c> is set where the effective start changed. The marks before and after are kept, so Undo and
/// Redo restore them exactly. Merges like its inner command (a value being spun), keeping the first marks before and the
/// last marks after.
/// </summary>
public sealed class FitMarksFollowingCommand : IMergeableCommand
{
    private readonly Project _project;
    private readonly TrainingReference _training;
    private readonly Func<DateTimeOffset> _now;
    private readonly IReadOnlySet<ClimbIdentity>? _keep;
    private IReadOnlyList<FitMark>? _before;
    private IReadOnlyList<FitMark>? _after;
    private IReadOnlyList<DeclinedClimb>? _declinedBefore;
    private IReadOnlyList<DeclinedClimb>? _declinedAfter;

    /// <param name="keep">Marks whose <c>positionChangedAt</c> this step keeps (D051 §5.6, «Разметить трассу от метки»).</param>
    public FitMarksFollowingCommand(IUndoableCommand inner, Project project, TrainingReference training, Func<DateTimeOffset> now,
        IReadOnlySet<ClimbIdentity>? keep = null)
    {
        Inner = inner;
        _project = project;
        _training = training;
        _now = now;
        _keep = keep;
    }

    private FitMarksFollowingCommand(IUndoableCommand inner, Project project, TrainingReference training, Func<DateTimeOffset> now,
        IReadOnlyList<FitMark> before, IReadOnlyList<FitMark> after, IReadOnlyList<DeclinedClimb> declinedBefore,
        IReadOnlyList<DeclinedClimb> declinedAfter)
        : this(inner, project, training, now)
    {
        _before = before;
        _after = after;
        _declinedBefore = declinedBefore;
        _declinedAfter = declinedAfter;
    }

    public IUndoableCommand Inner { get; }

    public string Description => Inner.Description;

    public void Execute()
    {
        if (_before is null)
        {
            _before = _training.Marks.ToList();
            _declinedBefore = _training.DeclinedClimbs.ToList();
            var captured = FitMarkRules.Capture(_project);
            Inner.Execute();
            if (ReferenceEquals(_project.Training, _training) && FitMarkRules.Follow(_project, captured, _now(), _keep) is { } followed)
                Replace(followed.Marks, followed.Declined);
            _after = _training.Marks.ToList();
            _declinedAfter = _training.DeclinedClimbs.ToList();
            return;
        }
        Inner.Execute();
        Replace(_after!, _declinedAfter!);
    }

    public void Undo()
    {
        Inner.Undo();
        Replace(_before!, _declinedBefore!);
    }

    public bool TryMerge(IUndoableCommand next, out IUndoableCommand? merged)
    {
        merged = null;
        if (Inner is not IMergeableCommand mergeable || next is not FitMarksFollowingCommand n || !ReferenceEquals(n._training, _training)
            || !mergeable.TryMerge(n.Inner, out var inner))
            return false;
        if (inner is null && _before!.SequenceEqual(n._after!) && _declinedBefore!.SequenceEqual(n._declinedAfter!))
            return true;                                              // the two cancel out
        merged = new FitMarksFollowingCommand(inner ?? NoChange.Instance, _project, _training, _now, _before!, n._after!,
            _declinedBefore!, n._declinedAfter!);
        return true;
    }

    private void Replace(IReadOnlyList<FitMark> marks, IReadOnlyList<DeclinedClimb> declined)
    {
        _training.Marks.Clear();
        _training.Marks.AddRange(marks);
        _training.DeclinedClimbs.Clear();
        _training.DeclinedClimbs.AddRange(declined);
    }

    private sealed class NoChange : IUndoableCommand
    {
        public static NoChange Instance { get; } = new();
        public string Description => "No Change";
        public void Execute() { }
        public void Undo() { }
    }
}
