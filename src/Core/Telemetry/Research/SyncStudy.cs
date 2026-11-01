using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Telemetry.Research;

// Step 35.3b1 (D050 amendment B5–B8): the research model of the COROS synchronisation study. It holds the source
// observations (snapshots of the FIT and the videos) and the human decisions (marks, match decisions, exclusions); it
// never holds a hypothesis or a computed result, and it is never part of the project (B5). Nothing is ever deleted:
// withdrawals, staleness, cleared decisions and retired videos are flags and journal entries.

/// <summary>The full identity of a FIT climb (D050 B8): the split index alone is stable only within one FIT, so a
/// decision or an exclusion stays bound to the climb's index, number and start, all three.</summary>
public readonly record struct ClimbIdentity(int ClimbKey, int ClimbNumber, DateTimeOffset ClimbStartUtc);

/// <summary>A climb split of the FIT as the study saw it; <see cref="Identity"/> is what decisions refer to.</summary>
public sealed record StudyClimb(int SegmentIndex, int ClimbNumber, DateTimeOffset StartUtc, long ElapsedTicks, DateTimeOffset EndUtc)
{
    public ClimbIdentity Identity => new(SegmentIndex, ClimbNumber, StartUtc);
}

/// <summary>The snapshot of the training file: its identity (SHA-256 and size), its activity and its climbs.
/// <see cref="FitKey"/> names the FIT as a subject of a location; it survives an accepted change of the file.</summary>
public sealed record StudyFit(Guid FitKey, string Sha256, long SizeBytes, TrainingActivity Activity, IReadOnlyList<StudyClimb> Climbs)
{
    /// <summary>The climb with this full identity, or null — an index alone never matches (I10).</summary>
    public StudyClimb? Find(ClimbIdentity identity) =>
        Climbs.FirstOrDefault(c => c.Identity == identity);
}

/// <summary>When and why a video left the checked set (it stays in the study with its marks and decisions).</summary>
public sealed record VideoRetirement(int Revision, long Seq, string Reason);

/// <summary>The snapshot of one video: identity by SHA-256 and size (the file name is an attribute, never proof of
/// identity), its duration and its raw recording-time tag as probed. <see cref="VideoKey"/> is stable for the study's life.</summary>
public sealed record StudyVideo(
    Guid VideoKey,
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    long DurationTicks,
    bool TagProbed,
    string? TagRaw,
    RecordingTimeTagOrigin? TagOrigin,
    int AddedInRevision,
    VideoRetirement? Retired)
{
    /// <summary>Whether the snapshot has what the evaluation needs: a duration and a completed tag probe.</summary>
    public bool MetadataComplete => DurationTicks > 0 && TagProbed;
}

/// <summary>A withdrawal — final; the record stays for the history.</summary>
public sealed record Withdrawal(int Revision, long Seq);

public enum StaleCause
{
    VideoChanged,
    FitChanged,
}

/// <summary>Staleness after an accepted change of the set; lifted only by an explicit reconfirmation.</summary>
public sealed record Staleness(int SinceRevision, StaleCause Cause, long Seq);

/// <summary>The independent lifecycle facts of a mark (both may hold at once).</summary>
[Flags]
public enum MarkState
{
    Active = 0,
    Withdrawn = 1,
    Stale = 2,
}

/// <summary>A human mark of a climb on a video: its start (the frame where the feet leave the floor, B7) and, as an
/// aid only, its end — in the video's source time (ticks).</summary>
public sealed record ClimbMark(
    Guid MarkId,
    Guid VideoKey,
    long StartSourceTicks,
    long? EndSourceTicks,
    string Note,
    int AddedInRevision,
    Withdrawal? Withdrawn,
    Staleness? Stale,
    int? ReconfirmedInRevision)
{
    public MarkState State => (Withdrawn is null ? MarkState.Active : MarkState.Withdrawn) | (Stale is null ? 0 : MarkState.Stale);

    /// <summary>Neither withdrawn nor stale: the mark and its decision may count.</summary>
    public bool IsActive => Withdrawn is null && Stale is null;
}

public enum MatchState
{
    Confirmed,
    Rejected,
    NotMatched,
}

public enum MatchReason
{
    NoFitClimb,
    NotAClimb,
    Ambiguous,
    WrongClimb,
    Other,
}

public enum MatchBasis
{
    Manual,
    ProposalAccepted,
}

/// <summary>The current human decision on a mark. <see cref="Climb"/> is required for Confirmed / Rejected and absent for
/// NotMatched; a previous decision lives only in the journal.</summary>
public sealed record MatchDecision(
    Guid MarkId,
    MatchState State,
    ClimbIdentity? Climb,
    MatchReason? Reason,
    string Note,
    MatchBasis Basis,
    string? ProposalRef,
    int MadeInRevision);

public enum ExclusionCategory
{
    MatchingError,
    MarkingError,
    FileTimeAnomaly,
    FitClimbAnomaly,
    Other,
}

/// <summary>A climb excluded from the evaluation with a reason recorded before it (B6). It applies to a revision only
/// while neither withdrawn nor stale and only to the climb of its full identity.</summary>
public sealed record Exclusion(
    Guid ExclusionId,
    ClimbIdentity Climb,
    ExclusionCategory Category,
    string Reason,
    int RecordedInRevision,
    Withdrawal? Withdrawn,
    Staleness? Stale,
    int? ReconfirmedInRevision)
{
    public bool IsActive => Withdrawn is null && Stale is null;
}

/// <summary>What a revision freezes and <see cref="StudyCanonicalJson.ContentHash"/> hashes: the observations and the
/// decisions — never the file locations, the journal or the evaluations.</summary>
public sealed record StudyContent(
    StudyFit Fit,
    IReadOnlyList<StudyVideo> Videos,
    IReadOnlyList<ClimbMark> Marks,
    IReadOnlyList<MatchDecision> Decisions,
    IReadOnlyList<Exclusion> Exclusions)
{
    public StudyVideo? Video(Guid key) => Videos.FirstOrDefault(v => v.VideoKey == key);

    public ClimbMark? Mark(Guid id) => Marks.FirstOrDefault(m => m.MarkId == id);

    public MatchDecision? Decision(Guid markId) => Decisions.FirstOrDefault(d => d.MarkId == markId);
}

/// <summary>Where a subject (the FIT or a video) is on disk now — outside the content and its hash.</summary>
public sealed record StudyLocation(Guid SubjectKey, string CurrentPath, long? LastWriteUtcTicks);

/// <summary>A frozen, immutable snapshot of the content.</summary>
public sealed record StudyRevision(int Number, DateTimeOffset FrozenAtUtc, string ContentHash, string Note, StudyContent Content);

public enum HistoryKind
{
    StudyCreated,
    VideoAdded,
    VideoRelocated,
    VideoChangeAccepted,
    VideoRetired,
    VideoRestored,
    FitRelocated,
    FitChangeAccepted,
    MarkAdded,
    MarkUpdated,
    MarkWithdrawn,
    MarkStaled,
    MarkReconfirmed,
    DecisionSet,
    DecisionCleared,
    ExclusionAdded,
    ExclusionWithdrawn,
    ExclusionStaled,
    ExclusionReconfirmed,
    RevisionFrozen,
    EvaluationRecorded,
}

/// <summary>One append-only journal entry; <see cref="Old"/> / <see cref="New"/> hold the canonical JSON of the value.</summary>
public sealed record HistoryEntry(long Seq, int Revision, DateTimeOffset AtUtc, HistoryKind Kind, Guid? SubjectId, string? Old, string? New, string Note);

/// <summary>A record of an evaluation run (written by Step 35.3b2; empty until then).</summary>
public sealed record EvaluationRecord(long Seq, int Revision, string AlgorithmVersion, string InputsHash, DateTimeOffset AtUtc);

/// <summary>The study: the editable draft, the file locations, the frozen revisions, the journal and the evaluation
/// records. Changed only through <see cref="SyncStudyEditor"/> (and the loader).</summary>
public sealed class SyncStudy
{
    public const int CurrentSchemaVersion = 1;

    public SyncStudy(Guid studyId, StudyContent draft, IEnumerable<StudyLocation> locations, IEnumerable<StudyRevision> revisions,
        IEnumerable<HistoryEntry> history, IEnumerable<EvaluationRecord> evaluations)
    {
        StudyId = studyId;
        Draft = draft;
        LocationList = locations.ToList();
        RevisionList = revisions.OrderBy(r => r.Number).ToList();
        HistoryList = history.OrderBy(h => h.Seq).ToList();
        EvaluationList = evaluations.OrderBy(e => e.Seq).ToList();
    }

    public Guid StudyId { get; }

    public StudyContent Draft { get; internal set; }

    internal List<StudyLocation> LocationList { get; }
    internal List<StudyRevision> RevisionList { get; }
    internal List<HistoryEntry> HistoryList { get; }
    internal List<EvaluationRecord> EvaluationList { get; }

    public IReadOnlyList<StudyLocation> Locations => LocationList;
    public IReadOnlyList<StudyRevision> Revisions => RevisionList;
    public IReadOnlyList<HistoryEntry> History => HistoryList;
    public IReadOnlyList<EvaluationRecord> Evaluations => EvaluationList;

    /// <summary>The number the draft will have when frozen.</summary>
    public int DraftRevision => RevisionList.Count == 0 ? 1 : RevisionList[^1].Number + 1;

    public long NextSeq => Math.Max(HistoryList.Count == 0 ? 0 : HistoryList[^1].Seq, EvaluationList.Count == 0 ? 0 : EvaluationList[^1].Seq) + 1;

    public StudyLocation? Location(Guid subjectKey) => LocationList.FirstOrDefault(l => l.SubjectKey == subjectKey);
}
