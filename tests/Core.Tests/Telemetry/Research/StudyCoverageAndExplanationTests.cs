using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Research;
using Xunit;
using static AiVideoEditor.Core.Tests.Telemetry.Research.StudyFixture;

namespace AiVideoEditor.Core.Tests.Telemetry.Research;

/// <summary>
/// Step 35.3b2a: the video coverage decision (only a video reviewed as holding no climb, with no unresolved mark, joins the
/// tag check; reset atomically with an accepted change), the append-only explanations and their withdrawals on one shared
/// sequence, proposal references to stored evaluations, and the new snapshot members — all without changing the content
/// hash of a study written by Step 35.3b1.
/// </summary>
public class StudyCoverageAndExplanationTests
{
    private static Guid Mark(SyncStudyEditor editor, Guid video, long seconds = 30) =>
        editor.AddMark(video, seconds * Second, null).CreatedId!.Value;

    /// <summary>The study with revision 1 frozen and an evaluation record of it (as Step 35.3b2d will write one).</summary>
    private static (SyncStudy Study, SyncStudyEditor Editor, long EvaluationSeq) Evaluated(Func<SyncStudyEditor, SyncStudy, Guid>? before = null)
    {
        var (study, editor) = Study();
        before?.Invoke(editor, study);
        var revision = editor.Freeze("rev 1");
        const string report = "{\"climbs\":[]}";
        var record = new EvaluationRecord(study.NextSeq, revision.Number, "sync-eval/1", Hash('9'), T0,
            revision.ContentHash, "{}", StudyCanonicalJson.TextHash(report), report);
        var evaluated = new SyncStudy(study.StudyId, study.Draft, study.Locations, study.Revisions, study.History,
            study.Evaluations.Append(record), study.Explanations, study.Withdrawals);
        return (evaluated, new SyncStudyEditor(evaluated, Clock()), record.Seq);
    }

    // ---- coverage ------------------------------------------------------------------------------------------------

    [Fact]
    public void Reviewed_no_climbs_is_refused_while_a_mark_is_unresolved()
    {
        var (study, editor) = Study();
        var video = study.FirstVideo();
        var mark = Mark(editor, video);
        Assert.Equal(StudyRefusal.CoverageConflict, editor.SetCoverage(video, CoverageState.ReviewedNoClimbs, "").Refusal);   // undecided

        editor.DecideMatch(mark, MatchState.NotMatched, null, MatchReason.NoFitClimb, "");
        Assert.Equal(StudyRefusal.CoverageConflict, editor.SetCoverage(video, CoverageState.ReviewedNoClimbs, "").Refusal);   // not «not a climb»

        editor.DecideMatch(mark, MatchState.NotMatched, null, MatchReason.NotAClimb, "brushing holds");
        Assert.True(editor.SetCoverage(video, CoverageState.ReviewedNoClimbs, "watched to the end").Ok);
        Assert.Equal(CoverageState.ReviewedNoClimbs, study.Draft.Video(video)!.CoverageStatus);
        Assert.Equal(HistoryKind.VideoCoverageSet, study.History[^1].Kind);
        Assert.Contains("\"reviewednoclimbs\"", study.History[^1].New);
        Assert.Empty(StudyValidator.Problems(study));
    }

    [Fact]
    public void A_stale_resolved_mark_still_blocks_and_a_withdrawn_one_does_not()
    {
        var (study, editor) = Study();
        var video = study.FirstVideo();
        var mark = Mark(editor, video);
        editor.DecideMatch(mark, MatchState.NotMatched, null, MatchReason.NotAClimb, "");
        editor.AcceptChangedFit(Fit(study.Draft.Fit.FitKey, 'e'), "b.fit", 1, "");
        Assert.Equal(StudyRefusal.CoverageConflict, editor.SetCoverage(video, CoverageState.ReviewedNoClimbs, "").Refusal);

        editor.WithdrawMark(mark, "");
        Assert.True(editor.SetCoverage(video, CoverageState.ReviewedNoClimbs, "").Ok);
    }

    [Fact]
    public void While_reviewed_no_mark_can_be_added_or_decided_otherwise_and_has_climbs_needs_no_match()
    {
        var (study, editor) = Study();
        var video = study.FirstVideo();
        var mark = Mark(editor, video);
        editor.DecideMatch(mark, MatchState.NotMatched, null, MatchReason.NotAClimb, "");
        editor.SetCoverage(video, CoverageState.ReviewedNoClimbs, "");

        Assert.Equal(StudyRefusal.CoverageConflict, editor.AddMark(video, 90 * Second, null).Refusal);
        Assert.Equal(StudyRefusal.CoverageConflict, editor.DecideMatch(mark, MatchState.Confirmed, study.Identity(2), null, "").Refusal);
        Assert.Equal(StudyRefusal.CoverageConflict, editor.ClearDecision(mark, "").Refusal);

        Assert.True(editor.SetCoverage(video, CoverageState.HasClimbs, "a climb at 4:10 after all").Ok);
        Assert.True(editor.AddMark(video, 250 * Second, null).Ok);

        Assert.True(editor.SetCoverage(video, CoverageState.Unknown, "").Ok);
        Assert.Null(study.Draft.Video(video)!.Coverage);                      // one form of «unknown»
        Assert.Equal(StudyRefusal.VideoRetired, Retired(editor, study).Refusal);
    }

    private static StudyEdit Retired(SyncStudyEditor editor, SyncStudy study)
    {
        editor.RetireVideo(study.FirstVideo(), "gone");
        return editor.SetCoverage(study.FirstVideo(), CoverageState.HasClimbs, "");
    }

    [Fact]
    public void An_accepted_change_of_a_video_resets_its_coverage_in_the_same_step()
    {
        var (study, editor) = Study('a', 'b');
        var a = study.Draft.Videos[0].VideoKey;
        editor.SetCoverage(a, CoverageState.HasClimbs, "");
        var mark = Mark(editor, a);
        Assert.Equal(StudyRefusal.DuplicateVideo, editor.AcceptChangedVideo(a, Video('b'), "x", null, "").Refusal);
        Assert.Equal(CoverageState.HasClimbs, study.Draft.Video(a)!.CoverageStatus);   // a refusal changes nothing

        Assert.True(editor.AcceptChangedVideo(a, Video('z') with { Coverage = new VideoCoverage(CoverageState.HasClimbs, "x", 1) }, "a2", 2, "re-exported").Ok);
        Assert.Null(study.Draft.Video(a)!.Coverage);                           // never carried over
        Assert.Equal(new[] { HistoryKind.VideoChangeAccepted, HistoryKind.VideoCoverageReset, HistoryKind.MarkStaled },
            study.History.TakeLast(3).Select(h => h.Kind));
        Assert.NotNull(study.Draft.Mark(mark)!.Stale);
    }

    [Fact]
    public void A_new_fit_resets_a_reviewed_video_whose_resolved_marks_become_stale_but_not_an_empty_one()
    {
        var (study, editor) = Study('a', 'b');
        var withMark = study.Draft.Videos[0].VideoKey;
        var empty = study.Draft.Videos[1].VideoKey;
        var mark = Mark(editor, withMark);
        editor.DecideMatch(mark, MatchState.NotMatched, null, MatchReason.NotAClimb, "");
        editor.SetCoverage(withMark, CoverageState.ReviewedNoClimbs, "");
        editor.SetCoverage(empty, CoverageState.ReviewedNoClimbs, "");

        editor.AcceptChangedFit(Fit(study.Draft.Fit.FitKey, 'e'), "b.fit", 1, "");
        Assert.Equal(CoverageState.Unknown, study.Draft.Video(withMark)!.CoverageStatus);
        Assert.Equal(CoverageState.ReviewedNoClimbs, study.Draft.Video(empty)!.CoverageStatus);
        Assert.Contains(study.History, h => h.Kind == HistoryKind.VideoCoverageReset && h.SubjectId == withMark);
        Assert.Empty(StudyValidator.Problems(study));
    }

    // ---- proposals -----------------------------------------------------------------------------------------------

    [Fact]
    public void An_accepted_proposal_names_a_stored_evaluation_and_a_manual_decision_none()
    {
        Guid mark = default;
        var (study, editor, seq) = Evaluated((e, s) => mark = Mark(e, s.FirstVideo()));
        var climb = study.Identity(2);

        Assert.Equal(StudyRefusal.ProposalRequired, editor.DecideMatch(mark, MatchState.Confirmed, climb, null, "", MatchBasis.ProposalAccepted).Refusal);
        Assert.Equal(StudyRefusal.ProposalNotAllowed, editor.DecideMatch(mark, MatchState.Confirmed, climb, null, "", MatchBasis.Manual, new ProposalRef(seq, "p")).Refusal);
        Assert.Equal(StudyRefusal.UnknownEvaluation, editor.DecideMatch(mark, MatchState.Confirmed, climb, null, "", MatchBasis.ProposalAccepted, new ProposalRef(seq + 100, "p")).Refusal);
        Assert.True(editor.DecideMatch(mark, MatchState.Confirmed, climb, null, "", MatchBasis.ProposalAccepted, new ProposalRef(seq, Hash('7'))).Ok);
        Assert.Equal(new ProposalRef(seq, Hash('7')), study.Draft.Decision(mark)!.Proposal);
        Assert.Empty(StudyValidator.Problems(study));
    }

    // ---- explanations --------------------------------------------------------------------------------------------

    [Fact]
    public void An_explanation_needs_a_stored_evaluation_a_climb_of_its_fit_and_evidence()
    {
        var (study, editor, seq) = Evaluated();
        var climb = study.Identity(2);
        Assert.Equal(StudyRefusal.UnknownEvaluation, editor.AddExplanation(seq + 1, climb, ExplanationCategory.MarkingError, "x").Refusal);
        Assert.Equal(StudyRefusal.UnknownClimb, editor.AddExplanation(seq, climb with { ClimbNumber = 9 }, ExplanationCategory.MarkingError, "x").Refusal);
        Assert.Equal(StudyRefusal.EvidenceRequired, editor.AddExplanation(seq, climb, ExplanationCategory.MarkingError, " ").Refusal);
        Assert.Equal(StudyRefusal.EvidenceRequired, editor.AddExplanation(seq, climb, ExplanationCategory.Unexplained, "").Refusal);

        var hash = StudyCanonicalJson.ContentHash(study.Draft);
        var id = editor.AddExplanation(seq, climb, ExplanationCategory.MarkingError, "the mark is 1.4 s late: feet left at 0:31.2").CreatedId!.Value;
        var explanation = study.Explanations.Single();
        Assert.Equal((id, seq, study.Evaluation(seq)!.ReportHash), (explanation.ExplanationId, explanation.EvaluationSeq, explanation.ReportHash));
        Assert.True(explanation.Seq > seq);
        Assert.Equal(HistoryKind.ExplanationAdded, study.History[^1].Kind);
        Assert.Equal(hash, StudyCanonicalJson.ContentHash(study.Draft));       // outside the content
        Assert.Single(study.Revisions);                                         // no new revision
        Assert.Empty(StudyValidator.Problems(study));
    }

    [Fact]
    public void A_withdrawal_is_its_own_record_and_the_active_explanations_can_be_restored_at_any_moment()
    {
        var (study, editor, seq) = Evaluated();
        var first = editor.AddExplanation(seq, study.Identity(2), ExplanationCategory.Unexplained, "no idea yet").CreatedId!.Value;
        var afterFirst = study.NextSeq - 1;
        var second = editor.AddExplanation(seq, study.Identity(2), ExplanationCategory.FileTimeAnomaly, "the tag is 181.7 s off its name").CreatedId!.Value;
        var original = study.Explanations[0];

        Assert.True(editor.WithdrawExplanation(first, "superseded").Ok);
        var afterWithdrawal = study.NextSeq - 1;
        Assert.Equal(StudyRefusal.ExplanationWithdrawn, editor.WithdrawExplanation(first, "").Refusal);
        Assert.Equal(StudyRefusal.UnknownExplanation, editor.WithdrawExplanation(Guid.NewGuid(), "").Refusal);

        Assert.Same(original, study.Explanations[0]);                          // never changed
        Assert.Single(study.Withdrawals);
        Assert.Equal(new[] { first }, study.ActiveExplanations(afterFirst).Select(e => e.ExplanationId));
        Assert.Equal(new[] { first, second }, study.ActiveExplanations(afterWithdrawal - 2).Select(e => e.ExplanationId));
        Assert.Equal(new[] { second }, study.ActiveExplanations(afterWithdrawal).Select(e => e.ExplanationId));
        Assert.Equal(study.AllSeqs().Count(), study.AllSeqs().Distinct().Count());   // one shared sequence
        Assert.Empty(StudyValidator.Problems(study));
    }

    [Theory]
    [InlineData(ExplanationCategory.MatchingError, ExplanationFamily.Deviation)]
    [InlineData(ExplanationCategory.MarkingError, ExplanationFamily.Deviation)]
    [InlineData(ExplanationCategory.FileTimeAnomaly, ExplanationFamily.Deviation)]
    [InlineData(ExplanationCategory.FitClimbAnomaly, ExplanationFamily.Deviation)]
    [InlineData(ExplanationCategory.NoUsableFootage, ExplanationFamily.Absence)]
    [InlineData(ExplanationCategory.InsufficientEvidence, ExplanationFamily.Absence)]
    [InlineData(ExplanationCategory.MissingRequiredData, ExplanationFamily.Absence)]
    [InlineData(ExplanationCategory.Unexplained, ExplanationFamily.None)]
    public void Categories_fall_into_the_deviation_and_absence_families_and_unexplained_into_none(ExplanationCategory category, ExplanationFamily family) =>
        Assert.Equal(family, CaseExplanation.FamilyOf(category));

    // ---- integrity -----------------------------------------------------------------------------------------------

    [Fact]
    public void Records_sharing_a_sequence_number_or_with_a_wrong_hash_are_damage()
    {
        var (study, editor, seq) = Evaluated();
        editor.AddExplanation(seq, study.Identity(2), ExplanationCategory.MarkingError, "late mark");
        SyncStudy With(IEnumerable<EvaluationRecord>? evaluations = null, IEnumerable<CaseExplanation>? explanations = null,
            IEnumerable<ExplanationWithdrawal>? withdrawals = null) =>
            new(study.StudyId, study.Draft, study.Locations, study.Revisions, study.History, evaluations ?? study.Evaluations,
                explanations ?? study.Explanations, withdrawals ?? study.Withdrawals);

        var e = study.Evaluations.Single();
        var x = study.Explanations.Single();
        Assert.Contains(StudyValidator.Problems(With(withdrawals: new[] { new ExplanationWithdrawal(x.Seq, x.ExplanationId, "", T0) })),
            p => p.Contains("share a sequence number"));
        Assert.Contains(StudyValidator.Problems(With(evaluations: new[] { e with { Report = e.Report + " " } })), p => p.Contains("report hash"));
        Assert.Contains(StudyValidator.Problems(With(evaluations: new[] { e with { RevisionContentHash = Hash('1') } })), p => p.Contains("content hash"));
        Assert.Contains(StudyValidator.Problems(With(explanations: new[] { x with { Seq = 1_000, EvaluationSeq = 999 } })), p => p.Contains("no evaluation"));
        Assert.Contains(StudyValidator.Problems(With(withdrawals: new[] { new ExplanationWithdrawal(2_000, Guid.NewGuid(), "", T0) })), p => p.Contains("no such explanation"));
    }

    [Fact]
    public void Snapshot_members_have_one_form_each()
    {
        var (study, _) = Study();
        StudyContent Video(Func<StudyVideo, StudyVideo> change) =>
            study.Draft with { Videos = study.Draft.Videos.Select(change).ToList() };
        SyncStudy Of(StudyContent c) => new(study.StudyId, c, study.Locations, study.Revisions, study.History, study.Evaluations);

        Assert.Contains(StudyValidator.Problems(Of(Video(v => v with { RateState = FrameRateState.Unknown }))), p => p.Contains("frame-rate state"));
        Assert.Contains(StudyValidator.Problems(Of(Video(v => v with { TagProbeFailed = true }))), p => p.Contains("failed probe"));
        Assert.Contains(StudyValidator.Problems(Of(Video(v => v with { TagProbeFailed = false, TagProbed = false, TagRaw = null, TagOrigin = null }))), p => p.Contains("failed probe"));
        Assert.Contains(StudyValidator.Problems(Of(Video(v => v with { Coverage = new VideoCoverage(CoverageState.Unknown, "", 1) }))), p => p.Contains("invalid coverage"));
        Assert.Contains(StudyValidator.Problems(Of(Video(v => v with { FrameRate = new StudyRate(0, 1) }))), p => p.Contains("invalid frame rate"));

        var failed = study.Draft.Videos[0] with { TagProbed = false, TagRaw = null, TagOrigin = null, TagProbeFailed = true };
        Assert.Equal(TagProbeState.ProbeFailed, failed.ProbeState);
        Assert.False(failed.MetadataComplete);                                  // I8: a failed probe blocks a freeze
        Assert.Equal(TagProbeState.NotProbed, (failed with { TagProbeFailed = null }).ProbeState);
        Assert.Equal(FrameRateState.Unknown, failed.FrameRateStatus);
    }

    /// <summary>Revision 1 frozen (a mark, video coverage), the draft is revision 2; <paramref name="draft"/> /
    /// <paramref name="revision"/> change the draft or revision 1 (whose hash is recomputed, so only the bound is tested).</summary>
    private static IReadOnlyList<string> Bounds(Func<StudyContent, StudyContent>? draft = null, Func<StudyContent, StudyContent>? revision = null)
    {
        var (study, editor) = Study();
        var video = study.FirstVideo();
        Mark(editor, video);
        editor.SetCoverage(video, CoverageState.HasClimbs, "");
        editor.Freeze("rev 1");
        Assert.Equal(2, study.DraftRevision);
        var frozen = study.Revisions.Single();
        var changed = revision?.Invoke(frozen.Content) ?? frozen.Content;
        var revisions = new[] { frozen with { Content = changed, ContentHash = StudyCanonicalJson.ContentHash(changed) } };
        return StudyValidator.Problems(new SyncStudy(study.StudyId, draft?.Invoke(study.Draft) ?? study.Draft, study.Locations,
            revisions, study.History, study.Evaluations));
    }

    private static StudyContent Coverage(StudyContent c, int setIn) =>
        c with { Videos = c.Videos.Select(v => v with { Coverage = v.Coverage! with { SetInRevision = setIn } }).ToList() };

    private static StudyContent VideoAdded(StudyContent c, int addedIn) =>
        c with { Videos = c.Videos.Select(v => v with { AddedInRevision = addedIn }).ToList() };

    private static StudyContent MarkAdded(StudyContent c, int addedIn) =>
        c with { Marks = c.Marks.Select(m => m with { AddedInRevision = addedIn }).ToList() };

    [Fact]
    public void No_record_of_the_draft_is_from_a_later_revision_than_the_draft_and_the_bound_itself_is_accepted()
    {
        Assert.Empty(Bounds(draft: c => Coverage(c, 2)));                      // the draft's own number
        Assert.Empty(Bounds(draft: c => VideoAdded(c, 2)));
        Assert.Empty(Bounds(draft: c => MarkAdded(c, 2)));

        Assert.Contains(Bounds(draft: c => Coverage(c, 3)), p => p.StartsWith("draft:") && p.Contains("coverage set in a later revision"));
        Assert.Contains(Bounds(draft: c => VideoAdded(c, 3)), p => p.StartsWith("draft:") && p.Contains("added in a later revision"));
        Assert.Contains(Bounds(draft: c => MarkAdded(c, 3)), p => p.StartsWith("draft:") && p.Contains("added in a later revision"));
    }

    [Fact]
    public void No_record_of_a_frozen_revision_is_from_a_later_revision_and_the_bound_itself_is_accepted()
    {
        Assert.Empty(Bounds(revision: c => Coverage(c, 1)));                   // revision 1's own number
        Assert.Empty(Bounds(revision: c => VideoAdded(c, 1)));
        Assert.Empty(Bounds(revision: c => MarkAdded(c, 1)));

        Assert.Contains(Bounds(revision: c => Coverage(c, 2)), p => p.StartsWith("revision 1:") && p.Contains("coverage set in a later revision"));
        Assert.Contains(Bounds(revision: c => VideoAdded(c, 2)), p => p.StartsWith("revision 1:") && p.Contains("added in a later revision"));
        Assert.Contains(Bounds(revision: c => MarkAdded(c, 2)), p => p.StartsWith("revision 1:") && p.Contains("added in a later revision"));
    }

    // ---- compatibility and the estimator --------------------------------------------------------------------------

    [Fact]
    public void New_members_left_empty_keep_the_hash_and_set_ones_change_it()
    {
        var (study, _) = Study();
        var hash = StudyCanonicalJson.ContentHash(study.Draft);
        var json = System.Text.Encoding.UTF8.GetString(StudyCanonicalJson.ContentBytes(study.Draft));
        foreach (var member in new[] { "utcOffsetMinutes", "frameRate", "avgFrameRate", "rateState", "coverage", "tagProbeFailed", "proposal" })
            Assert.DoesNotContain($"\"{member}\"", json);

        var richer = study.Draft with
        {
            Fit = study.Draft.Fit with { UtcOffsetMinutes = 180 },
            Videos = study.Draft.Videos.Select(v => v with { FrameRate = new StudyRate(60, 1), AvgFrameRate = new StudyRate(5994, 100) }).ToList(),
        };
        Assert.NotEqual(hash, StudyCanonicalJson.ContentHash(richer));
        var richJson = System.Text.Encoding.UTF8.GetString(StudyCanonicalJson.ContentBytes(richer));
        Assert.Contains("\"utcOffsetMinutes\":180", richJson);
        Assert.Contains("\"avgFrameRate\":{\"denominator\":100,\"numerator\":5994}", richJson);
    }

    [Fact]
    public void The_snapshot_form_of_the_candidates_equals_the_asset_form()
    {
        var probe = RecordingTimeProbe.Found("2026-09-27T07:35:07.000000Z", RecordingTimeTagOrigin.Format);
        var asset = new MediaAsset
        {
            FilePath = @"D:\set\video_20260927_103026.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(282), RecordingTimeProbe = probe }
        };
        var h = TimeSpan.FromHours(3);
        Assert.Equal(RecordingZeroEstimator.Candidates(asset, h),
            RecordingZeroEstimator.Candidates("video_20260927_103026.mp4", MediaTime.FromSeconds(282), probe, h));
        Assert.Null(RecordingZeroEstimator.Candidates("video_20260927_103026.mp4", MediaTime.FromSeconds(282), probe, null).FromFileName);
    }
}
