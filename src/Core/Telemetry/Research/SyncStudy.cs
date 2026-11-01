using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Telemetry.Research;

// Step 35.3b1 (D050 amendment B5–B8): the research model of the COROS synchronisation study. It holds the source
// observations (snapshots of the FIT and the videos) and the human decisions (marks, match decisions, exclusions); it
// never holds a hypothesis or a computed result, and it is never part of the project (B5). Nothing is ever deleted:
// withdrawals, staleness, cleared decisions and retired videos are flags and journal entries.
// Step 35.3b2a adds only members that are left out when empty (the video coverage decision, the FIT's offset, the frame
// rates, a failed probe, a proposal reference), so the canonical JSON — and the content hash — of a study written by
// Step 35.3b1 stays the same and its frozen revisions stay valid.

/// <summary>The full identity of a FIT climb (D050 B8): the split index alone is stable only within one FIT, so a
/// decision or an exclusion stays bound to the climb's index, number and start, all three.</summary>
public readonly record struct ClimbIdentity(int ClimbKey, int ClimbNumber, DateTimeOffset ClimbStartUtc);

/// <summary>A climb split of the FIT as the study saw it; <see cref="Identity"/> is what decisions refer to.</summary>
public sealed record StudyClimb(int SegmentIndex, int ClimbNumber, DateTimeOffset StartUtc, long ElapsedTicks, DateTimeOffset EndUtc)
{
    public ClimbIdentity Identity => new(SegmentIndex, ClimbNumber, StartUtc);
}

/// <summary>The snapshot of the training file: its identity (SHA-256 and size), its activity and its climbs.
/// <see cref="FitKey"/> names the FIT as a subject of a location; it survives an accepted change of the file.
/// <see cref="UtcOffsetMinutes"/> is the watch's local offset as the file states it, only when the parser found it
/// usable — an observation, never put in place of the file-name hypothesis <c>h</c>.</summary>
public sealed record StudyFit(Guid FitKey, string Sha256, long SizeBytes, TrainingActivity Activity, IReadOnlyList<StudyClimb> Climbs,
    int? UtcOffsetMinutes = null)
{
    /// <summary>The climb with this full identity, or null — an index alone never matches (I10).</summary>
    public StudyClimb? Find(ClimbIdentity identity) =>
        Climbs.FirstOrDefault(c => c.Identity == identity);
}

/// <summary>When and why a video left the checked set (it stays in the study with its marks and decisions).</summary>
public sealed record VideoRetirement(int Revision, long Seq, string Reason);

/// <summary>An exact frame rate as ffprobe stated it (<c>Numerator / Denominator</c> frames per second).</summary>
public readonly record struct StudyRate(int Numerator, int Denominator);

/// <summary>Whether a video's frame rate is known to be constant. CFR only with a reliable confirmation — none exists in
/// Step 35.3b2, so every video is <see cref="Unknown"/> and residuals are shown in milliseconds only.</summary>
public enum FrameRateState
{
    Unknown,
    Cfr,
    Vfr,
}

/// <summary>What the recording-time probe of a video found (D049 §4).</summary>
public enum TagProbeState
{
    NotProbed,
    ProbeFailed,
    Found,
    Absent,
}

/// <summary>What a human found about the climbs in a video: only a video reviewed as holding no climb belongs to the
/// independent check of the recording-time tag — no confirmed match proves nothing.</summary>
public enum CoverageState
{
    Unknown,
    ReviewedNoClimbs,
    HasClimbs,
}

/// <summary>The human coverage decision on a video (absent = <see cref="CoverageState.Unknown"/>).</summary>
public sealed record VideoCoverage(CoverageState State, string Note, int SetInRevision);

/// <summary>The snapshot of one video: identity by SHA-256 and size (the file name is an attribute, never proof of
/// identity), its duration, its raw recording-time tag as probed and its frame rates as ffprobe stated them.
/// <see cref="VideoKey"/> is stable for the study's life. <see cref="Coverage"/> is the human coverage decision; a new
/// snapshot of the video (an accepted change) comes without it, so the decision is reset in the same step.</summary>
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
    VideoRetirement? Retired,
    bool? TagProbeFailed = null,
    StudyRate? FrameRate = null,
    StudyRate? AvgFrameRate = null,
    FrameRateState? RateState = null,
    VideoCoverage? Coverage = null)
{
    /// <summary>Whether the snapshot has what the evaluation needs: a duration and a completed tag probe (I8 — a missing
    /// or failed probe blocks a freeze).</summary>
    public bool MetadataComplete => DurationTicks > 0 && TagProbed;

    public TagProbeState ProbeState => TagProbed
        ? (TagRaw is null ? TagProbeState.Absent : TagProbeState.Found)
        : (TagProbeFailed == true ? TagProbeState.ProbeFailed : TagProbeState.NotProbed);

    public FrameRateState FrameRateStatus => RateState ?? FrameRateState.Unknown;

    public CoverageState CoverageStatus => Coverage?.State ?? CoverageState.Unknown;
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

/// <summary>Which stored proposal a decision accepted: the evaluation record holding it and the proposal's id. It stays
/// resolvable after the marks or the video change — a historical proposal is never recomputed.</summary>
public readonly record struct ProposalRef(long EvaluationSeq, string ProposalId);

/// <summary>The current human decision on a mark. <see cref="Climb"/> is required for Confirmed / Rejected and absent for
/// NotMatched; a previous decision lives only in the journal. <see cref="Proposal"/> is set exactly when the basis is an
/// accepted proposal.</summary>
public sealed record MatchDecision(
    Guid MarkId,
    MatchState State,
    ClimbIdentity? Climb,
    MatchReason? Reason,
    string Note,
    MatchBasis Basis,
    ProposalRef? Proposal,
    int MadeInRevision)
{
    /// <summary>A mark resolved as no climb — what a video reviewed as holding no climb may keep.</summary>
    public bool IsNotAClimb => State == MatchState.NotMatched && Reason == MatchReason.NotAClimb;
}

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
    VideoCoverageSet,
    VideoCoverageReset,
    ExplanationAdded,
    ExplanationWithdrawn,
}

/// <summary>One append-only journal entry; <see cref="Old"/> / <see cref="New"/> hold the canonical JSON of the value.</summary>
public sealed record HistoryEntry(long Seq, int Revision, DateTimeOffset AtUtc, HistoryKind Kind, Guid? SubjectId, string? Old, string? New, string Note);

/// <summary>
/// An immutable record of an evaluation (Step 35.3b2): the revision it evaluated with that revision's content hash, the
/// algorithm version, its parameters (canonical JSON) and inputs hash, and the full report (canonical JSON) with its
/// hash — a historical result is never recomputed. Append-only, outside the content. Written by Step 35.3b2d.
/// </summary>
public sealed record EvaluationRecord(
    long Seq,
    int Revision,
    string AlgorithmVersion,
    string InputsHash,
    DateTimeOffset AtUtc,
    string RevisionContentHash = "",
    string Parameters = "",
    string ReportHash = "",
    string Report = "");

/// <summary>Why one of the climbs C2–C14 did not count as within τ (B6). The deviation family explains a measured
/// outlier, the absence family a missing result; <see cref="Unexplained"/> records that no explanation is known and is
/// never an explanation.</summary>
public enum ExplanationCategory
{
    MatchingError,
    MarkingError,
    FileTimeAnomaly,
    FitClimbAnomaly,
    NoUsableFootage,
    InsufficientEvidence,
    MissingRequiredData,
    Unexplained,
}

public enum ExplanationFamily
{
    Deviation,
    Absence,
    None,
}

/// <summary>An explanation of one climb's result in one recorded evaluation — append-only, never changed; a withdrawal
/// is its own record (<see cref="ExplanationWithdrawal"/>).</summary>
public sealed record CaseExplanation(long Seq, Guid ExplanationId, long EvaluationSeq, string ReportHash, ClimbIdentity Climb,
    ExplanationCategory Category, string Evidence, DateTimeOffset AtUtc)
{
    public static ExplanationFamily FamilyOf(ExplanationCategory category) => category switch
    {
        ExplanationCategory.MatchingError or ExplanationCategory.MarkingError
            or ExplanationCategory.FileTimeAnomaly or ExplanationCategory.FitClimbAnomaly => ExplanationFamily.Deviation,
        ExplanationCategory.NoUsableFootage or ExplanationCategory.InsufficientEvidence
            or ExplanationCategory.MissingRequiredData => ExplanationFamily.Absence,
        _ => ExplanationFamily.None,
    };
}

/// <summary>The withdrawal of an explanation — its own append-only record with its own sequence number.</summary>
public sealed record ExplanationWithdrawal(long Seq, Guid ExplanationId, string Note, DateTimeOffset AtUtc);

/// <summary>The study: the editable draft, the file locations, the frozen revisions, the journal and the evaluation
/// records. Changed only through <see cref="SyncStudyEditor"/> (and the loader).</summary>
public sealed class SyncStudy
{
    public const int CurrentSchemaVersion = 1;

    public SyncStudy(Guid studyId, StudyContent draft, IEnumerable<StudyLocation> locations, IEnumerable<StudyRevision> revisions,
        IEnumerable<HistoryEntry> history, IEnumerable<EvaluationRecord> evaluations,
        IEnumerable<CaseExplanation>? explanations = null, IEnumerable<ExplanationWithdrawal>? withdrawals = null)
    {
        StudyId = studyId;
        Draft = draft;
        LocationList = locations.ToList();
        RevisionList = revisions.OrderBy(r => r.Number).ToList();
        HistoryList = history.OrderBy(h => h.Seq).ToList();
        EvaluationList = evaluations.OrderBy(e => e.Seq).ToList();
        ExplanationList = (explanations ?? Enumerable.Empty<CaseExplanation>()).OrderBy(e => e.Seq).ToList();
        WithdrawalList = (withdrawals ?? Enumerable.Empty<ExplanationWithdrawal>()).OrderBy(w => w.Seq).ToList();
    }

    public Guid StudyId { get; }

    public StudyContent Draft { get; internal set; }

    internal List<StudyLocation> LocationList { get; }
    internal List<StudyRevision> RevisionList { get; }
    internal List<HistoryEntry> HistoryList { get; }
    internal List<EvaluationRecord> EvaluationList { get; }
    internal List<CaseExplanation> ExplanationList { get; }
    internal List<ExplanationWithdrawal> WithdrawalList { get; }

    public IReadOnlyList<StudyLocation> Locations => LocationList;
    public IReadOnlyList<StudyRevision> Revisions => RevisionList;
    public IReadOnlyList<HistoryEntry> History => HistoryList;
    public IReadOnlyList<EvaluationRecord> Evaluations => EvaluationList;
    public IReadOnlyList<CaseExplanation> Explanations => ExplanationList;
    public IReadOnlyList<ExplanationWithdrawal> Withdrawals => WithdrawalList;

    /// <summary>The number the draft will have when frozen.</summary>
    public int DraftRevision => RevisionList.Count == 0 ? 1 : RevisionList[^1].Number + 1;

    /// <summary>The next number of the one sequence shared by the journal, the evaluation records, the explanations and
    /// their withdrawals.</summary>
    public long NextSeq => AllSeqs().DefaultIfEmpty(0).Max() + 1;

    /// <summary>Every sequence number in use, across all append-only lists.</summary>
    public IEnumerable<long> AllSeqs() =>
        HistoryList.Select(h => h.Seq).Concat(EvaluationList.Select(e => e.Seq))
            .Concat(ExplanationList.Select(e => e.Seq)).Concat(WithdrawalList.Select(w => w.Seq));

    /// <summary>The explanations in force at <paramref name="asOfSeq"/>: recorded at or before it and not withdrawn at or
    /// before it — the state at any moment of the history can be restored.</summary>
    public IReadOnlyList<CaseExplanation> ActiveExplanations(long asOfSeq) =>
        ExplanationList.Where(e => e.Seq <= asOfSeq
                                   && !WithdrawalList.Any(w => w.ExplanationId == e.ExplanationId && w.Seq <= asOfSeq))
            .ToList();

    public EvaluationRecord? Evaluation(long seq) => EvaluationList.FirstOrDefault(e => e.Seq == seq);

    public StudyLocation? Location(Guid subjectKey) => LocationList.FirstOrDefault(l => l.SubjectKey == subjectKey);
}
