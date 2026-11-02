namespace AiVideoEditor.Core.Telemetry.Research;

/// <summary>One step of a mark's history: the journal entry, the mark after it and the decision in force after it (with its
/// climb identity), or null where there was none.</summary>
public sealed record MarkHistoryStep(long Seq, DateTimeOffset AtUtc, int Revision, HistoryKind Kind, ClimbMark? Mark, MatchDecision? Decision,
    string Note)
{
    /// <summary>The climb the mark was bound to after this step (a confirmed or rejected decision names one).</summary>
    public ClimbIdentity? Climb => Decision?.Climb;
}

/// <summary>
/// A mark's chronology read back from the study's journal by sequence number (Step 35.3R1) — no journal format of its own:
/// the mark events carry the whole mark after the change (its start, end, marking rule, flags) and the decision events
/// the whole decision with its climb identity, so the climb a mark was bound to when it moved is the last decision event
/// before that move. Read-only.
/// </summary>
public static class MarkHistory
{
    private static readonly HashSet<HistoryKind> MarkKinds = new()
    {
        HistoryKind.MarkAdded, HistoryKind.MarkUpdated, HistoryKind.MarkWithdrawn, HistoryKind.MarkStaled, HistoryKind.MarkReconfirmed,
    };

    /// <summary>The mark's steps in journal order.</summary>
    public static IReadOnlyList<MarkHistoryStep> For(SyncStudy study, Guid markId)
    {
        var steps = new List<MarkHistoryStep>();
        ClimbMark? mark = null;
        MatchDecision? decision = null;
        foreach (var entry in study.History.Where(h => h.SubjectId == markId).OrderBy(h => h.Seq))
        {
            if (MarkKinds.Contains(entry.Kind))
                mark = StudyJsonReader.Value(entry.New ?? throw new StudyJsonException($"journal entry {entry.Seq}: no mark"), StudyJsonReader.ReadMark);
            else if (entry.Kind == HistoryKind.DecisionSet)
                decision = StudyJsonReader.Value(entry.New ?? throw new StudyJsonException($"journal entry {entry.Seq}: no decision"), StudyJsonReader.ReadDecision);
            else if (entry.Kind == HistoryKind.DecisionCleared)
                decision = null;
            else
                continue;
            steps.Add(new MarkHistoryStep(entry.Seq, entry.AtUtc, entry.Revision, entry.Kind, mark, decision, entry.Note));
        }
        return steps;
    }

    /// <summary>The mark and its decision as they were at <paramref name="asOfSeq"/> — the last step at or before it; null
    /// before the mark existed.</summary>
    public static MarkHistoryStep? StateAt(IReadOnlyList<MarkHistoryStep> steps, long asOfSeq) =>
        steps.LastOrDefault(s => s.Seq <= asOfSeq);
}
