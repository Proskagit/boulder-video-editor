namespace AiVideoEditor.Core.Telemetry.Research;

/// <summary>Why an edit of the study was refused; the study is unchanged.</summary>
public enum StudyRefusal
{
    UnknownVideo,
    VideoRetired,
    VideoNotRetired,
    VideoMetadataIncomplete,
    DuplicateVideo,
    UnknownSubject,
    MarkOutsideVideo,
    EndNotAfterStart,
    UnknownMark,
    MarkWithdrawn,
    MarkNotStale,
    DecisionClimbMismatch,
    UnknownClimb,
    ClimbRequired,
    ClimbNotAllowed,
    ReasonRequired,
    NoteRequired,
    NoDecision,
    UnknownExclusion,
    ExclusionWithdrawn,
    ExclusionNotStale,
    ExclusionClimbMismatch,
    CoverageConflict,
    ProposalRequired,
    ProposalNotAllowed,
    UnknownEvaluation,
    EvidenceRequired,
    UnknownExplanation,
    ExplanationWithdrawn,
    NoMarkingRule,
    MarkingRuleIncomplete,
    ClimbAlreadyConfirmed,
}

/// <summary>The outcome of an edit: done (with the id of a created entity, if any) or refused with the reason.</summary>
public readonly record struct StudyEdit(StudyRefusal? Refusal, Guid? CreatedId = null, Guid? RelatedId = null)
{
    /// <summary>A refusal naming the entity it conflicts with (e.g. the mark that already confirms a climb).</summary>
    public static StudyEdit Conflict(StudyRefusal refusal, Guid relatedId) => new(refusal, null, relatedId);

    public bool Ok => Refusal is null;

    public static StudyEdit Done(Guid? id = null) => new(null, id);

    public static implicit operator StudyEdit(StudyRefusal refusal) => new(refusal);
}

/// <summary>
/// Every change of a study's draft (Step 35.3b1): each operation checks the invariants, changes only the draft (frozen
/// revisions are immutable records), and appends a journal entry with the old and new canonical value. Nothing is deleted:
/// a withdrawal, a staleness, a cleared decision or a retirement is a flag plus an entry. A reconfirmation never changes a
/// climb identity and never moves a decision or an exclusion onto another climb: the stored identity must match a climb of
/// the current FIT in index, number and start (I10, I11). The clock is given (UTC); no system time zone is read.
/// </summary>
public sealed class SyncStudyEditor
{
    private readonly SyncStudy _study;
    private readonly Func<DateTimeOffset> _clock;

    public SyncStudyEditor(SyncStudy study, Func<DateTimeOffset> clock)
    {
        _study = study;
        _clock = clock;
    }

    public SyncStudy Study => _study;

    private StudyContent Draft => _study.Draft;
    private int Revision => _study.DraftRevision;

    /// <summary>A new study from the snapshots of the FIT and the videos with their locations (one per subject).</summary>
    public static SyncStudy Create(Guid studyId, StudyFit fit, IReadOnlyList<(StudyVideo Video, StudyLocation Location)> videos,
        StudyLocation fitLocation, Func<DateTimeOffset> clock, string note = "")
    {
        if (videos.Select(v => v.Video.Sha256).Distinct(StringComparer.Ordinal).Count() != videos.Count)
            throw new ArgumentException("Two videos of the study have the same SHA-256.", nameof(videos));
        var content = new StudyContent(fit, videos.Select(v => v.Video with { AddedInRevision = 1, Retired = null, Coverage = null }).ToList(),
            Array.Empty<ClimbMark>(), Array.Empty<MatchDecision>(), Array.Empty<Exclusion>());
        var study = new SyncStudy(studyId, content, videos.Select(v => v.Location).Prepend(fitLocation),
            Array.Empty<StudyRevision>(), Array.Empty<HistoryEntry>(), Array.Empty<EvaluationRecord>());
        var editor = new SyncStudyEditor(study, clock);
        editor.Log(HistoryKind.StudyCreated, studyId, null, StudyCanonicalJson.Value(fit), note);
        foreach (var video in content.Videos)
            editor.Log(HistoryKind.VideoAdded, video.VideoKey, null, StudyCanonicalJson.Value(video), "");
        return study;
    }

    // ---- videos and the FIT --------------------------------------------------------------------------------------

    /// <summary>Adds a video snapshot under a new key; a SHA-256 already in the study is refused.</summary>
    public StudyEdit AddVideo(StudyVideo snapshot, string path, long? lastWriteUtcTicks, string note = "")
    {
        if (Draft.Videos.Any(v => string.Equals(v.Sha256, snapshot.Sha256, StringComparison.Ordinal))) return StudyRefusal.DuplicateVideo;
        var video = snapshot with { VideoKey = Guid.NewGuid(), AddedInRevision = Revision, Retired = null, Coverage = null };
        SetDraft(Draft with { Videos = Draft.Videos.Append(video).ToList() });
        _study.LocationList.Add(new StudyLocation(video.VideoKey, path, lastWriteUtcTicks));
        Log(HistoryKind.VideoAdded, video.VideoKey, null, StudyCanonicalJson.Value(video), note);
        return StudyEdit.Done(video.VideoKey);
    }

    /// <summary>The subject (the FIT or a video) is at another path with the same SHA-256 and size (checked by the caller):
    /// only its location changes; the key and the content stay.</summary>
    public StudyEdit Relocate(Guid subjectKey, string newPath, long? lastWriteUtcTicks, string note = "")
    {
        var index = _study.LocationList.FindIndex(l => l.SubjectKey == subjectKey);
        if (index < 0) return StudyRefusal.UnknownSubject;
        var old = _study.LocationList[index];
        var moved = old with { CurrentPath = newPath, LastWriteUtcTicks = lastWriteUtcTicks };
        _study.LocationList[index] = moved;
        Log(subjectKey == Draft.Fit.FitKey ? HistoryKind.FitRelocated : HistoryKind.VideoRelocated, subjectKey,
            StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(moved), note);
        return StudyEdit.Done();
    }

    /// <summary>Records the location's file time after a check (no journal entry: a location is not content).</summary>
    public void Touch(Guid subjectKey, long? lastWriteUtcTicks)
    {
        var index = _study.LocationList.FindIndex(l => l.SubjectKey == subjectKey);
        if (index >= 0) _study.LocationList[index] = _study.LocationList[index] with { LastWriteUtcTicks = lastWriteUtcTicks };
    }

    /// <summary>Accepts other content of a video (explicitly): a new snapshot under the same key, its coverage decision
    /// reset to unknown in the same replacement (atomic), its marks stale.</summary>
    public StudyEdit AcceptChangedVideo(Guid videoKey, StudyVideo snapshot, string path, long? lastWriteUtcTicks, string note)
    {
        if (Draft.Video(videoKey) is not { } old) return StudyRefusal.UnknownVideo;
        if (Draft.Videos.Any(v => v.VideoKey != videoKey && string.Equals(v.Sha256, snapshot.Sha256, StringComparison.Ordinal)))
            return StudyRefusal.DuplicateVideo;
        var changed = snapshot with { VideoKey = videoKey, AddedInRevision = old.AddedInRevision, Retired = old.Retired, Coverage = null };
        SetDraft(Draft with { Videos = Replace(Draft.Videos, v => v.VideoKey == videoKey, changed) });
        SetLocation(videoKey, path, lastWriteUtcTicks);
        var seq = Log(HistoryKind.VideoChangeAccepted, videoKey, StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(changed), note);
        if (old.Coverage is { } coverage)
            Log(HistoryKind.VideoCoverageReset, videoKey, CoverageValue(coverage), null, "the video changed");
        StaleMarks(m => m.VideoKey == videoKey, StaleCause.VideoChanged, seq);
        return StudyEdit.Done();
    }

    /// <summary>Accepts another FIT (explicitly): a new snapshot under the same FIT key; every mark and every exclusion that
    /// is not withdrawn becomes stale; decisions keep their stored climb identity.</summary>
    public StudyEdit AcceptChangedFit(StudyFit snapshot, string path, long? lastWriteUtcTicks, string note)
    {
        var old = Draft.Fit;
        var changed = snapshot with { FitKey = old.FitKey };
        // A video reviewed as holding no climb keeps only active «not a climb» marks; those become stale now, so its
        // coverage decision is reset in the same replacement as the FIT.
        var reset = Draft.Videos.Where(v => v.CoverageStatus == CoverageState.ReviewedNoClimbs
                                            && Draft.Marks.Any(m => m.VideoKey == v.VideoKey && m.Withdrawn is null)).ToList();
        SetDraft(Draft with
        {
            Fit = changed,
            Videos = Draft.Videos.Select(v => reset.Contains(v) ? v with { Coverage = null } : v).ToList(),
        });
        SetLocation(old.FitKey, path, lastWriteUtcTicks);
        var seq = Log(HistoryKind.FitChangeAccepted, old.FitKey, StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(changed), note);
        foreach (var video in reset)
            Log(HistoryKind.VideoCoverageReset, video.VideoKey, CoverageValue(video.Coverage!), null, "the FIT changed: its marks are stale");
        StaleMarks(_ => true, StaleCause.FitChanged, seq);
        foreach (var exclusion in Draft.Exclusions.Where(e => e.Withdrawn is null && e.Stale is null).ToList())
        {
            var stale = exclusion with { Stale = new Staleness(Revision, StaleCause.FitChanged, seq) };
            SetDraft(Draft with { Exclusions = Replace(Draft.Exclusions, e => e.ExclusionId == exclusion.ExclusionId, stale) });
            Log(HistoryKind.ExclusionStaled, exclusion.ExclusionId, StudyCanonicalJson.Value(exclusion), StudyCanonicalJson.Value(stale), "");
        }
        return StudyEdit.Done();
    }

    /// <summary>Takes a video out of the checked set and the evaluation; it stays with its marks and decisions.</summary>
    public StudyEdit RetireVideo(Guid videoKey, string reason)
    {
        if (Draft.Video(videoKey) is not { } old) return StudyRefusal.UnknownVideo;
        if (old.Retired is not null) return StudyRefusal.VideoRetired;
        if (string.IsNullOrWhiteSpace(reason)) return StudyRefusal.ReasonRequired;
        var seq = _study.NextSeq;
        var retired = old with { Retired = new VideoRetirement(Revision, seq, reason) };
        SetDraft(Draft with { Videos = Replace(Draft.Videos, v => v.VideoKey == videoKey, retired) });
        Log(HistoryKind.VideoRetired, videoKey, StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(retired), reason);
        return StudyEdit.Done();
    }

    /// <summary>Brings a retired video back into the set (explicitly); it must be verified again before a freeze.</summary>
    public StudyEdit RestoreVideo(Guid videoKey, string note)
    {
        if (Draft.Video(videoKey) is not { } old) return StudyRefusal.UnknownVideo;
        if (old.Retired is null) return StudyRefusal.VideoNotRetired;
        var restored = old with { Retired = null };
        var after = Draft with { Videos = Replace(Draft.Videos, v => v.VideoKey == videoKey, restored) };
        // One camera: bringing the video back must not give a climb a second confirmation in force.
        if (after.DoubleConfirmations() is [var doubled, ..])
            return StudyEdit.Conflict(StudyRefusal.ClimbAlreadyConfirmed, (Draft.ConfirmedHolder(doubled) ?? after.ConfirmedHolder(doubled)!).MarkId);
        SetDraft(after);
        Log(HistoryKind.VideoRestored, videoKey, StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(restored), note);
        return StudyEdit.Done();
    }

    /// <summary>
    /// Records what a human found about the climbs in a video. «Reviewed, no climbs» is refused while the video has a mark
    /// that is neither withdrawn nor resolved — resolved means not stale and decided «not matched: not a climb»; while it
    /// holds, no mark can be added and no such mark decided otherwise. «Has climbs» may be set before any match is confirmed.
    /// </summary>
    public StudyEdit SetCoverage(Guid videoKey, CoverageState state, string note)
    {
        if (Draft.Video(videoKey) is not { } video) return StudyRefusal.UnknownVideo;
        if (video.Retired is not null) return StudyRefusal.VideoRetired;
        if (state == CoverageState.ReviewedNoClimbs && Draft.Marks.Any(m => m.VideoKey == videoKey && m.Withdrawn is null && !IsResolved(m)))
            return StudyRefusal.CoverageConflict;
        var coverage = state == CoverageState.Unknown ? null : new VideoCoverage(state, note, Revision);
        SetDraft(Draft with { Videos = Replace(Draft.Videos, v => v.VideoKey == videoKey, video with { Coverage = coverage }) });
        Log(HistoryKind.VideoCoverageSet, videoKey, video.Coverage is { } old ? CoverageValue(old) : null,
            coverage is null ? null : CoverageValue(coverage), note);
        return StudyEdit.Done();
    }

    private bool IsResolved(ClimbMark mark) => mark.Stale is null && Draft.Decision(mark.MarkId) is { IsNotAClimb: true };

    private bool ReviewedNoClimbs(Guid videoKey) => Draft.Video(videoKey)?.CoverageStatus == CoverageState.ReviewedNoClimbs;

    private static string CoverageValue(VideoCoverage coverage) => StudyCanonicalJson.Value(coverage);

    // ---- marks ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets the marking rule of the draft (Step 35.3R1) — frozen with the revision, journalled with the previous one. Marks
    /// keep the rule id they were made under: a new rule rewrites none of them.
    /// </summary>
    public StudyEdit SetMarkingRule(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text)) return StudyRefusal.MarkingRuleIncomplete;
        var old = Draft.MarkingRule;
        var rule = new MarkingRule(id, text);
        if (old == rule) return StudyEdit.Done();
        SetDraft(Draft with { MarkingRule = rule });
        Log(HistoryKind.MarkingRuleSet, _study.StudyId, old is null ? null : StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(rule), "");
        return StudyEdit.Done();
    }

    /// <summary>A new mark with its start (and, optionally, its end) — refused without a marking rule; it records the rule's
    /// id. The frame PTS are audit data only.</summary>
    public StudyEdit AddMark(Guid videoKey, long startSourceTicks, long? endSourceTicks, string note = "",
        StudyFramePts? startFramePts = null, StudyFramePts? endFramePts = null)
    {
        if (Draft.MarkingRule is not { } rule) return StudyRefusal.NoMarkingRule;
        if (CheckMarkPlace(videoKey, startSourceTicks, endSourceTicks) is { } refusal) return refusal;
        if (ReviewedNoClimbs(videoKey)) return StudyRefusal.CoverageConflict;
        var mark = new ClimbMark(Guid.NewGuid(), videoKey, startSourceTicks, endSourceTicks, note, Revision, null, null, null,
            rule.Id, startFramePts, endSourceTicks is null ? null : endFramePts);
        SetDraft(Draft with { Marks = Draft.Marks.Append(mark).ToList() });
        Log(HistoryKind.MarkAdded, mark.MarkId, null, StudyCanonicalJson.Value(mark), note);
        return StudyEdit.Done(mark.MarkId);
    }

    /// <summary>
    /// Moves a mark's start and / or end, or changes its note — refused without a marking rule. Moving either boundary
    /// records the current rule's id (one id for the whole mark); a boundary that does not move keeps its frame PTS. The
    /// decision stays (a confirmation is about the climb, not the time); a stale mark stays stale.
    /// </summary>
    public StudyEdit UpdateMark(Guid markId, long startSourceTicks, long? endSourceTicks, string note,
        StudyFramePts? startFramePts = null, StudyFramePts? endFramePts = null)
    {
        if (Draft.Mark(markId) is not { } old) return StudyRefusal.UnknownMark;
        if (old.Withdrawn is not null) return StudyRefusal.MarkWithdrawn;
        if (Draft.MarkingRule is not { } rule) return StudyRefusal.NoMarkingRule;
        if (CheckMarkPlace(old.VideoKey, startSourceTicks, endSourceTicks) is { } refusal) return refusal;
        var startMoved = startSourceTicks != old.StartSourceTicks;
        var endMoved = endSourceTicks != old.EndSourceTicks;
        var updated = old with
        {
            StartSourceTicks = startSourceTicks,
            EndSourceTicks = endSourceTicks,
            Note = note,
            MarkingRuleId = startMoved || endMoved ? rule.Id : old.MarkingRuleId,
            StartFramePts = startMoved ? startFramePts : startFramePts ?? old.StartFramePts,
            EndFramePts = endSourceTicks is null ? null : endMoved ? endFramePts : endFramePts ?? old.EndFramePts,
        };
        ReplaceMark(updated);
        Log(HistoryKind.MarkUpdated, markId, StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(updated), note);
        return StudyEdit.Done();
    }

    /// <summary>Withdraws a mark for good; its decision stays in the record. A correction is a new mark.</summary>
    public StudyEdit WithdrawMark(Guid markId, string note)
    {
        if (Draft.Mark(markId) is not { } old) return StudyRefusal.UnknownMark;
        if (old.Withdrawn is not null) return StudyRefusal.MarkWithdrawn;
        var withdrawn = old with { Withdrawn = new Withdrawal(Revision, _study.NextSeq) };
        ReplaceMark(withdrawn);
        Log(HistoryKind.MarkWithdrawn, markId, StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(withdrawn), note);
        return StudyEdit.Done();
    }

    /// <summary>
    /// Lifts a mark's staleness together with its current decision — an explicit event. Refused while the decision names a
    /// climb whose full identity (index, number, start) is not a climb of the current FIT: the human makes a new decision
    /// first (<see cref="DecideMatch"/> or <see cref="ClearDecision"/>); nothing is redirected.
    /// </summary>
    public StudyEdit ReconfirmMark(Guid markId, string note)
    {
        if (Draft.Mark(markId) is not { } old) return StudyRefusal.UnknownMark;
        if (old.Withdrawn is not null) return StudyRefusal.MarkWithdrawn;
        if (old.Stale is null) return StudyRefusal.MarkNotStale;
        if (CheckMarkPlace(old.VideoKey, old.StartSourceTicks, old.EndSourceTicks) is { } refusal) return refusal;
        if (Draft.Decision(markId) is { Climb: { } climb } decision)
        {
            if (Draft.Fit.Find(climb) is null) return StudyRefusal.DecisionClimbMismatch;
            if (decision.State == MatchState.Confirmed && Draft.ConfirmedHolder(climb, markId) is { } holder)
                return StudyEdit.Conflict(StudyRefusal.ClimbAlreadyConfirmed, holder.MarkId);
        }
        var reconfirmed = old with { Stale = null, ReconfirmedInRevision = Revision };
        ReplaceMark(reconfirmed);
        Log(HistoryKind.MarkReconfirmed, markId, StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(reconfirmed), note);
        return StudyEdit.Done();
    }

    private StudyRefusal? CheckMarkPlace(Guid videoKey, long start, long? end)
    {
        if (Draft.Video(videoKey) is not { } video) return StudyRefusal.UnknownVideo;
        if (video.Retired is not null) return StudyRefusal.VideoRetired;
        if (video.DurationTicks <= 0) return StudyRefusal.VideoMetadataIncomplete;
        if (start < 0 || start >= video.DurationTicks) return StudyRefusal.MarkOutsideVideo;
        if (end is { } e && e <= start) return StudyRefusal.EndNotAfterStart;
        if (end is { } e2 && e2 > video.DurationTicks) return StudyRefusal.MarkOutsideVideo;
        return null;
    }

    // ---- decisions -----------------------------------------------------------------------------------------------

    /// <summary>Sets the current decision on a mark (also on a stale one — that does not reconfirm it). Confirmed / Rejected
    /// name a climb of the current FIT by its full identity; NotMatched names none. Rejected / NotMatched need a reason,
    /// «other» needs a note. Never set automatically: a proposal becomes a decision only through this call.</summary>
    public StudyEdit DecideMatch(Guid markId, MatchState state, ClimbIdentity? climb, MatchReason? reason, string note,
        MatchBasis basis = MatchBasis.Manual, ProposalRef? proposal = null)
    {
        if (Draft.Mark(markId) is not { } mark) return StudyRefusal.UnknownMark;
        if (mark.Withdrawn is not null) return StudyRefusal.MarkWithdrawn;
        if (basis == MatchBasis.ProposalAccepted && proposal is null) return StudyRefusal.ProposalRequired;
        if (basis != MatchBasis.ProposalAccepted && proposal is not null) return StudyRefusal.ProposalNotAllowed;
        if (proposal is { } p && _study.Evaluation(p.EvaluationSeq) is null) return StudyRefusal.UnknownEvaluation;
        if (ReviewedNoClimbs(mark.VideoKey) && !(state == MatchState.NotMatched && reason == MatchReason.NotAClimb))
            return StudyRefusal.CoverageConflict;
        if (state == MatchState.NotMatched && climb is not null) return StudyRefusal.ClimbNotAllowed;
        if (state != MatchState.NotMatched && climb is null) return StudyRefusal.ClimbRequired;
        if (climb is { } c && Draft.Fit.Find(c) is null) return StudyRefusal.UnknownClimb;
        // One camera: a climb has at most one confirmed mark in force (D050 amendment of 2026-11-02).
        if (state == MatchState.Confirmed && climb is { } taken && Draft.ConfirmedHolder(taken, markId) is { } holder)
            return StudyEdit.Conflict(StudyRefusal.ClimbAlreadyConfirmed, holder.MarkId);
        if (state != MatchState.Confirmed && reason is null) return StudyRefusal.ReasonRequired;
        if (reason == MatchReason.Other && string.IsNullOrWhiteSpace(note)) return StudyRefusal.NoteRequired;

        var old = Draft.Decision(markId);
        var decision = new MatchDecision(markId, state, climb, reason, note, basis, proposal, Revision);
        SetDraft(Draft with { Decisions = Draft.Decisions.Where(d => d.MarkId != markId).Append(decision).ToList() });
        Log(HistoryKind.DecisionSet, markId, old is null ? null : StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(decision), note);
        return StudyEdit.Done();
    }

    /// <summary>Clears the current decision; the previous one stays in the journal.</summary>
    public StudyEdit ClearDecision(Guid markId, string note)
    {
        if (Draft.Decision(markId) is not { } old) return StudyRefusal.NoDecision;
        if (Draft.Mark(markId) is { Withdrawn: null } mark && ReviewedNoClimbs(mark.VideoKey)) return StudyRefusal.CoverageConflict;
        SetDraft(Draft with { Decisions = Draft.Decisions.Where(d => d.MarkId != markId).ToList() });
        Log(HistoryKind.DecisionCleared, markId, StudyCanonicalJson.Value(old), null, note);
        return StudyEdit.Done();
    }

    // ---- exclusions ----------------------------------------------------------------------------------------------

    /// <summary>Excludes a climb of the current FIT (full identity) with its reason — only in the draft, so before the
    /// evaluation of the revision it is frozen into (I6).</summary>
    public StudyEdit AddExclusion(ClimbIdentity climb, ExclusionCategory category, string reason)
    {
        if (Draft.Fit.Find(climb) is null) return StudyRefusal.UnknownClimb;
        if (string.IsNullOrWhiteSpace(reason)) return StudyRefusal.ReasonRequired;
        var exclusion = new Exclusion(Guid.NewGuid(), climb, category, reason, Revision, null, null, null);
        SetDraft(Draft with { Exclusions = Draft.Exclusions.Append(exclusion).ToList() });
        Log(HistoryKind.ExclusionAdded, exclusion.ExclusionId, null, StudyCanonicalJson.Value(exclusion), reason);
        return StudyEdit.Done(exclusion.ExclusionId);
    }

    /// <summary>Withdraws an exclusion: the record stays with its withdrawal.</summary>
    public StudyEdit WithdrawExclusion(Guid exclusionId, string note)
    {
        if (Draft.Exclusions.FirstOrDefault(e => e.ExclusionId == exclusionId) is not { } old) return StudyRefusal.UnknownExclusion;
        if (old.Withdrawn is not null) return StudyRefusal.ExclusionWithdrawn;
        var withdrawn = old with { Withdrawn = new Withdrawal(Revision, _study.NextSeq) };
        SetDraft(Draft with { Exclusions = Replace(Draft.Exclusions, e => e.ExclusionId == exclusionId, withdrawn) });
        Log(HistoryKind.ExclusionWithdrawn, exclusionId, StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(withdrawn), note);
        return StudyEdit.Done();
    }

    /// <summary>Lifts an exclusion's staleness — only when its full climb identity is a climb of the current FIT; otherwise
    /// a new exclusion is needed, so an old one never excludes another climb.</summary>
    public StudyEdit ReconfirmExclusion(Guid exclusionId, string note)
    {
        if (Draft.Exclusions.FirstOrDefault(e => e.ExclusionId == exclusionId) is not { } old) return StudyRefusal.UnknownExclusion;
        if (old.Withdrawn is not null) return StudyRefusal.ExclusionWithdrawn;
        if (old.Stale is null) return StudyRefusal.ExclusionNotStale;
        if (Draft.Fit.Find(old.Climb) is null) return StudyRefusal.ExclusionClimbMismatch;
        var reconfirmed = old with { Stale = null, ReconfirmedInRevision = Revision };
        SetDraft(Draft with { Exclusions = Replace(Draft.Exclusions, e => e.ExclusionId == exclusionId, reconfirmed) });
        Log(HistoryKind.ExclusionReconfirmed, exclusionId, StudyCanonicalJson.Value(old), StudyCanonicalJson.Value(reconfirmed), note);
        return StudyEdit.Done();
    }

    // ---- explanations (append-only, outside the content) ----------------------------------------------------------

    /// <summary>
    /// Explains one climb's result in a recorded evaluation (B6) — an append-only record with its own sequence number and
    /// a journal entry; it never changes the content, a revision or the stored report. The climb must be a climb of the
    /// evaluated revision's FIT by its full identity and the evidence is required (also for «unexplained», which is never
    /// an explanation). Whether the category fits the climb's status in the report is checked with the report (Step 35.3b2c).
    /// </summary>
    public StudyEdit AddExplanation(long evaluationSeq, ClimbIdentity climb, ExplanationCategory category, string evidence)
    {
        if (_study.Evaluation(evaluationSeq) is not { } evaluation) return StudyRefusal.UnknownEvaluation;
        var revision = _study.Revisions.FirstOrDefault(r => r.Number == evaluation.Revision);
        if (revision?.Content.Fit.Find(climb) is null) return StudyRefusal.UnknownClimb;
        if (string.IsNullOrWhiteSpace(evidence)) return StudyRefusal.EvidenceRequired;
        var explanation = new CaseExplanation(_study.NextSeq, Guid.NewGuid(), evaluationSeq, evaluation.ReportHash, climb, category,
            evidence, _clock().ToUniversalTime());
        _study.ExplanationList.Add(explanation);
        Log(HistoryKind.ExplanationAdded, explanation.ExplanationId, null, StudyCanonicalJson.Value(explanation), evidence);
        return StudyEdit.Done(explanation.ExplanationId);
    }

    /// <summary>Withdraws an explanation by a record of its own; the explanation itself is never changed.</summary>
    public StudyEdit WithdrawExplanation(Guid explanationId, string note)
    {
        if (_study.Explanations.All(e => e.ExplanationId != explanationId)) return StudyRefusal.UnknownExplanation;
        if (_study.Withdrawals.Any(w => w.ExplanationId == explanationId)) return StudyRefusal.ExplanationWithdrawn;
        var withdrawal = new ExplanationWithdrawal(_study.NextSeq, explanationId, note, _clock().ToUniversalTime());
        _study.WithdrawalList.Add(withdrawal);
        Log(HistoryKind.ExplanationWithdrawn, explanationId, null, StudyCanonicalJson.Value(withdrawal), note);
        return StudyEdit.Done();
    }

    // ---- revisions -----------------------------------------------------------------------------------------------

    /// <summary>Freezes the draft into the next revision with its content hash. The caller checks the set first (I8).</summary>
    public StudyRevision Freeze(string note)
    {
        var revision = new StudyRevision(Revision, _clock().ToUniversalTime(), StudyCanonicalJson.ContentHash(Draft), note, Draft);
        Log(HistoryKind.RevisionFrozen, _study.StudyId, null, revision.ContentHash, note);
        _study.RevisionList.Add(revision);
        return revision;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private void StaleMarks(Func<ClimbMark, bool> which, StaleCause cause, long causeSeq)
    {
        foreach (var mark in Draft.Marks.Where(m => m.Withdrawn is null && m.Stale is null && which(m)).ToList())
        {
            var stale = mark with { Stale = new Staleness(Revision, cause, causeSeq) };
            ReplaceMark(stale);
            Log(HistoryKind.MarkStaled, mark.MarkId, StudyCanonicalJson.Value(mark), StudyCanonicalJson.Value(stale), "");
        }
    }

    private void ReplaceMark(ClimbMark mark) =>
        SetDraft(Draft with { Marks = Replace(Draft.Marks, m => m.MarkId == mark.MarkId, mark) });

    private void SetLocation(Guid subjectKey, string path, long? lastWriteUtcTicks)
    {
        var index = _study.LocationList.FindIndex(l => l.SubjectKey == subjectKey);
        var location = new StudyLocation(subjectKey, path, lastWriteUtcTicks);
        if (index < 0) _study.LocationList.Add(location);
        else _study.LocationList[index] = location;
    }

    private void SetDraft(StudyContent content) => _study.Draft = content;

    private static IReadOnlyList<T> Replace<T>(IReadOnlyList<T> items, Func<T, bool> match, T replacement) =>
        items.Select(i => match(i) ? replacement : i).ToList();

    private long Log(HistoryKind kind, Guid? subject, string? old, string? @new, string note)
    {
        var seq = _study.NextSeq;
        _study.HistoryList.Add(new HistoryEntry(seq, Revision, _clock().ToUniversalTime(), kind, subject, old, @new, note));
        return seq;
    }
}
