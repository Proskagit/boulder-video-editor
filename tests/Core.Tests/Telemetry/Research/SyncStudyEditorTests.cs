using AiVideoEditor.Core.Telemetry.Research;
using Xunit;
using static AiVideoEditor.Core.Tests.Telemetry.Research.StudyFixture;

namespace AiVideoEditor.Core.Tests.Telemetry.Research;

/// <summary>
/// Step 35.3b1: the study's draft changes only through the editor — every change journalled, nothing deleted; a mark's
/// withdrawal and staleness are independent; a reconfirmation never moves a decision or an exclusion onto another climb
/// (the full identity index + number + start must match the current FIT); retirement is content; frozen revisions never
/// change.
/// </summary>
public class SyncStudyEditorTests
{
    private static Guid AddMark(SyncStudyEditor editor, Guid video, long startSeconds = 30, long? endSeconds = null) =>
        editor.AddMark(video, startSeconds * Second, endSeconds * Second).CreatedId!.Value;

    private static StudyFit Refit(SyncStudy study, params StudyClimb[] climbs) => Fit(study.Draft.Fit.FitKey, 'e', climbs);

    [Fact]
    public void A_new_study_is_journalled_and_valid()
    {
        var (study, _) = Study('a', 'b');
        Assert.Equal(new[] { HistoryKind.StudyCreated, HistoryKind.VideoAdded, HistoryKind.VideoAdded, HistoryKind.MarkingRuleSet },
            study.History.Select(h => h.Kind));                                // the fixture sets the marking rule (Step 35.3R1)
        Assert.Equal(new long[] { 1, 2, 3, 4 }, study.History.Select(h => h.Seq));
        Assert.All(study.History, h => Assert.Equal(TimeSpan.Zero, h.AtUtc.Offset));
        Assert.Equal(1, study.DraftRevision);
        Assert.Empty(StudyValidator.Problems(study));
    }

    [Fact]
    public void Two_videos_with_one_hash_cannot_form_a_study()
    {
        var fit = Fit();
        var v1 = Video('a');
        var v2 = Video('a', name: "other.mp4");
        Assert.Throws<ArgumentException>(() => SyncStudyEditor.Create(Guid.NewGuid(), fit,
            new[] { (v1, new StudyLocation(v1.VideoKey, "1", null)), (v2, new StudyLocation(v2.VideoKey, "2", null)) },
            new StudyLocation(fit.FitKey, "f", null), Clock()));
    }

    // ---- marks ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_mark_must_lie_inside_its_video()
    {
        var (study, editor) = Study();
        var video = study.FirstVideo();
        Assert.Equal(StudyRefusal.MarkOutsideVideo, editor.AddMark(video, -1, null).Refusal);
        Assert.Equal(StudyRefusal.MarkOutsideVideo, editor.AddMark(video, 600 * Second, null).Refusal);
        Assert.Equal(StudyRefusal.EndNotAfterStart, editor.AddMark(video, 30 * Second, 30 * Second).Refusal);
        Assert.Equal(StudyRefusal.MarkOutsideVideo, editor.AddMark(video, 30 * Second, 601 * Second).Refusal);
        Assert.Equal(StudyRefusal.UnknownVideo, editor.AddMark(Guid.NewGuid(), 0, null).Refusal);
        Assert.True(editor.AddMark(video, 0, 600 * Second).Ok);
    }

    [Fact]
    public void Withdrawal_is_final_and_keeps_the_record_and_its_decision()
    {
        var (study, editor) = Study();
        var mark = AddMark(editor, study.FirstVideo());
        Assert.True(editor.DecideMatch(mark, MatchState.Confirmed, study.Identity(2), null, "").Ok);

        Assert.True(editor.WithdrawMark(mark, "wrong frame").Ok);
        Assert.Equal(MarkState.Withdrawn, study.Draft.Mark(mark)!.State);
        Assert.Equal(StudyRefusal.MarkWithdrawn, editor.WithdrawMark(mark, "").Refusal);
        Assert.Equal(StudyRefusal.MarkWithdrawn, editor.UpdateMark(mark, 40 * Second, null, "").Refusal);
        Assert.NotNull(study.Draft.Decision(mark));                       // kept for the history
        Assert.Single(study.Draft.Marks);
        Assert.Equal(HistoryKind.MarkWithdrawn, study.History[^1].Kind);
    }

    [Fact]
    public void Withdrawal_and_staleness_are_independent_and_editing_a_stale_mark_does_not_reconfirm_it()
    {
        var (study, editor) = Study();
        var video = study.FirstVideo();
        var mark = AddMark(editor, video);
        editor.AcceptChangedVideo(video, Video('z') with { VideoKey = video }, @"D:\set\a.mp4", 200, "re-exported");
        Assert.Equal(MarkState.Stale, study.Draft.Mark(mark)!.State);

        Assert.True(editor.UpdateMark(mark, 31 * Second, null, "moved").Ok);
        Assert.Equal(MarkState.Stale, study.Draft.Mark(mark)!.State);    // an edit is not a reconfirmation

        Assert.True(editor.WithdrawMark(mark, "").Ok);
        Assert.Equal(MarkState.Withdrawn | MarkState.Stale, study.Draft.Mark(mark)!.State);
        Assert.Equal(StudyRefusal.MarkWithdrawn, editor.ReconfirmMark(mark, "").Refusal);
    }

    [Fact]
    public void A_changed_video_makes_only_its_own_marks_stale_each_with_an_entry()
    {
        var (study, editor) = Study('a', 'b');
        var a = study.Draft.Videos[0].VideoKey;
        var b = study.Draft.Videos[1].VideoKey;
        var onA = AddMark(editor, a);
        var onB = AddMark(editor, b);

        Assert.True(editor.AcceptChangedVideo(a, Video('z'), @"D:\set\a2.mp4", 300, "replaced").Ok);
        Assert.Equal(StaleCause.VideoChanged, study.Draft.Mark(onA)!.Stale!.Cause);
        Assert.True(study.Draft.Mark(onB)!.IsActive);
        Assert.Equal(a, study.Draft.Videos[0].VideoKey);                  // the key survives the new snapshot
        Assert.Equal(Hash('z'), study.Draft.Videos[0].Sha256);
        Assert.Equal(new[] { HistoryKind.VideoChangeAccepted, HistoryKind.MarkStaled }, study.History.TakeLast(2).Select(h => h.Kind));
        Assert.Equal(StudyRefusal.DuplicateVideo, editor.AcceptChangedVideo(a, Video('b'), "x", null, "").Refusal);
    }

    // ---- decisions -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_decision_names_a_climb_of_the_current_fit_by_its_full_identity()
    {
        var (study, editor) = Study();
        var mark = AddMark(editor, study.FirstVideo());
        var climb = study.Identity(2);

        Assert.Equal(StudyRefusal.ClimbRequired, editor.DecideMatch(mark, MatchState.Confirmed, null, null, "").Refusal);
        Assert.Equal(StudyRefusal.ClimbNotAllowed, editor.DecideMatch(mark, MatchState.NotMatched, climb, MatchReason.NoFitClimb, "").Refusal);
        Assert.Equal(StudyRefusal.UnknownClimb, editor.DecideMatch(mark, MatchState.Confirmed, climb with { ClimbNumber = 9 }, null, "").Refusal);
        Assert.Equal(StudyRefusal.UnknownClimb, editor.DecideMatch(mark, MatchState.Confirmed, climb with { ClimbStartUtc = climb.ClimbStartUtc.AddTicks(1) }, null, "").Refusal);
        Assert.Equal(StudyRefusal.ReasonRequired, editor.DecideMatch(mark, MatchState.Rejected, climb, null, "").Refusal);
        Assert.Equal(StudyRefusal.ReasonRequired, editor.DecideMatch(mark, MatchState.NotMatched, null, null, "").Refusal);
        Assert.Equal(StudyRefusal.NoteRequired, editor.DecideMatch(mark, MatchState.NotMatched, null, MatchReason.Other, " ").Refusal);
        Assert.True(editor.DecideMatch(mark, MatchState.Confirmed, climb, null, "").Ok);
    }

    [Fact]
    public void Replacing_or_clearing_a_decision_keeps_the_previous_one_in_the_journal()
    {
        var (study, editor) = Study();
        var mark = AddMark(editor, study.FirstVideo());
        editor.DecideMatch(mark, MatchState.Confirmed, study.Identity(2), null, "first");
        editor.DecideMatch(mark, MatchState.Rejected, study.Identity(2), MatchReason.WrongClimb, "second");
        var replaced = study.History[^1];
        Assert.Equal(HistoryKind.DecisionSet, replaced.Kind);
        Assert.Contains("\"confirmed\"", replaced.Old);
        Assert.Contains("\"rejected\"", replaced.New);

        Assert.True(editor.ClearDecision(mark, "undo").Ok);
        Assert.Null(study.Draft.Decision(mark));
        Assert.Equal(HistoryKind.DecisionCleared, study.History[^1].Kind);
        Assert.Contains("\"rejected\"", study.History[^1].Old);
        Assert.Equal(StudyRefusal.NoDecision, editor.ClearDecision(mark, "").Refusal);
    }

    [Fact]
    public void A_climb_takes_one_confirmed_mark_only()
    {
        // One camera (D050 amendment of 2026-11-02): replaces the allowance of Step 35.3b1 that several marks may confirm a climb.
        var (study, editor) = Study('a', 'b');
        var m1 = AddMark(editor, study.Draft.Videos[0].VideoKey);
        var m2 = AddMark(editor, study.Draft.Videos[1].VideoKey);
        Assert.True(editor.DecideMatch(m1, MatchState.Confirmed, study.Identity(2), null, "").Ok);

        var refused = editor.DecideMatch(m2, MatchState.Confirmed, study.Identity(2), null, "");
        Assert.Equal((StudyRefusal.ClimbAlreadyConfirmed, m1), (refused.Refusal, refused.RelatedId!.Value));
        Assert.Null(study.Draft.Decision(m2));
        Assert.True(editor.DecideMatch(m1, MatchState.Confirmed, study.Identity(2), null, "again").Ok);   // the same mark: no conflict
        Assert.True(editor.DecideMatch(m2, MatchState.Rejected, study.Identity(2), MatchReason.WrongClimb, "").Ok);   // not a confirmation

        editor.WithdrawMark(m1, "wrong video");
        Assert.True(editor.DecideMatch(m2, MatchState.Confirmed, study.Identity(2), null, "").Ok);   // free again
        Assert.Empty(StudyValidator.Problems(study));
    }

    // ---- a changed FIT: decisions (full identity) -----------------------------------------------------------------

    public static TheoryData<string, StudyClimb[], bool> NewFits() => new()
    {
        { "the same climbs", new[] { Climb(0, 1, 0), Climb(2, 2, 258), Climb(4, 3, 542) }, true },
        { "index 2 is another climb number", new[] { Climb(0, 1, 0), Climb(2, 3, 258), Climb(4, 4, 542) }, false },
        { "index 2 starts one tick later", new[] { Climb(0, 1, 0), Climb(2, 2, 258, 1), Climb(4, 3, 542) }, false },
        { "index 2 is gone", new[] { Climb(0, 1, 0), Climb(4, 3, 542) }, false },
    };

    [Theory]
    [MemberData(nameof(NewFits))]
    public void After_a_new_fit_a_decision_is_reconfirmed_only_when_its_full_climb_identity_matches(string _, StudyClimb[] climbs, bool reconfirmable)
    {
        var (study, editor) = Study();
        var mark = AddMark(editor, study.FirstVideo());
        var old = study.Identity(2);
        editor.DecideMatch(mark, MatchState.Confirmed, old, null, "");

        Assert.True(editor.AcceptChangedFit(Refit(study, climbs), @"D:\set\b.fit", 1, "re-exported").Ok);
        Assert.Equal(StaleCause.FitChanged, study.Draft.Mark(mark)!.Stale!.Cause);
        Assert.Empty(StudyValidator.Problems(study));                        // a stale record keeps its old identity: no damage

        var result = editor.ReconfirmMark(mark, "checked");
        if (reconfirmable)
        {
            Assert.True(result.Ok);
            Assert.True(study.Draft.Mark(mark)!.IsActive);
            Assert.Equal(HistoryKind.MarkReconfirmed, study.History[^1].Kind);
        }
        else
        {
            Assert.Equal(StudyRefusal.DecisionClimbMismatch, result.Refusal);
            Assert.NotNull(study.Draft.Mark(mark)!.Stale);
            Assert.Equal(old, study.Draft.Decision(mark)!.Climb);           // never redirected
        }
    }

    [Fact]
    public void A_mismatched_decision_needs_a_new_human_decision_before_the_mark_is_reconfirmed()
    {
        var (study, editor) = Study();
        var mark = AddMark(editor, study.FirstVideo());
        editor.DecideMatch(mark, MatchState.Confirmed, study.Identity(2), null, "");
        editor.AcceptChangedFit(Refit(study, Climb(0, 1, 0), Climb(2, 3, 258)), "b.fit", 1, "");
        Assert.Equal(StudyRefusal.DecisionClimbMismatch, editor.ReconfirmMark(mark, "").Refusal);

        Assert.True(editor.DecideMatch(mark, MatchState.Confirmed, study.Identity(3), null, "the climb is now number 3").Ok);
        Assert.True(editor.ReconfirmMark(mark, "").Ok);
        Assert.Contains(study.History, h => h.Kind == HistoryKind.DecisionSet && h.Old is not null && h.Old.Contains("\"climbNumber\":2"));
    }

    [Fact]
    public void A_not_matched_decision_or_none_is_reconfirmed_without_a_climb()
    {
        var (study, editor) = Study();
        var unmatched = AddMark(editor, study.FirstVideo(), 30);
        var undecided = AddMark(editor, study.FirstVideo(), 90);
        editor.DecideMatch(unmatched, MatchState.NotMatched, null, MatchReason.NoFitClimb, "");
        editor.AcceptChangedFit(Refit(study, Climb(7, 1, 0)), "b.fit", 1, "");

        Assert.True(editor.ReconfirmMark(unmatched, "").Ok);
        Assert.True(editor.ReconfirmMark(undecided, "").Ok);
        Assert.Equal(StudyRefusal.MarkNotStale, editor.ReconfirmMark(undecided, "").Refusal);
    }

    // ---- a changed FIT: exclusions --------------------------------------------------------------------------------

    [Fact]
    public void After_a_new_fit_exclusions_are_stale_and_an_old_one_never_excludes_another_climb()
    {
        var (study, editor) = Study();
        var excluded = editor.AddExclusion(study.Identity(2), ExclusionCategory.FitClimbAnomaly, "3.6 s split").CreatedId!.Value;
        var withdrawn = editor.AddExclusion(study.Identity(3), ExclusionCategory.MarkingError, "blurred").CreatedId!.Value;
        editor.WithdrawExclusion(withdrawn, "not needed");

        // In the new FIT index 2 is another climb (number 3, another start).
        editor.AcceptChangedFit(Refit(study, Climb(0, 1, 0), Climb(2, 3, 300)), "b.fit", 1, "");
        var stale = study.Draft.Exclusions.Single(e => e.ExclusionId == excluded);
        Assert.Equal(StaleCause.FitChanged, stale.Stale!.Cause);
        Assert.Null(study.Draft.Exclusions.Single(e => e.ExclusionId == withdrawn).Stale);   // a withdrawn one stays as it was
        Assert.Contains(study.History, h => h.Kind == HistoryKind.ExclusionStaled && h.SubjectId == excluded);
        Assert.Empty(StudyValidator.Problems(study));

        Assert.Equal(StudyRefusal.ExclusionClimbMismatch, editor.ReconfirmExclusion(excluded, "").Refusal);
        Assert.False(study.Draft.Exclusions.Single(e => e.ExclusionId == excluded).IsActive);
        Assert.Equal(StudyRefusal.ExclusionWithdrawn, editor.ReconfirmExclusion(withdrawn, "").Refusal);
    }

    [Fact]
    public void An_exclusion_whose_identity_matches_the_new_fit_is_reconfirmed_with_both_revisions_kept()
    {
        var (study, editor) = Study();
        var id = editor.AddExclusion(study.Identity(2), ExclusionCategory.FitClimbAnomaly, "split doubt").CreatedId!.Value;
        editor.Freeze("rev 1");
        editor.AcceptChangedFit(Refit(study, Climb(0, 1, 0), Climb(2, 2, 258)), "b.fit", 1, "");

        Assert.True(editor.ReconfirmExclusion(id, "same climb").Ok);
        var exclusion = study.Draft.Exclusions.Single();
        Assert.Equal((1, 2), (exclusion.RecordedInRevision, exclusion.ReconfirmedInRevision!.Value));
        Assert.True(exclusion.IsActive);
        Assert.Equal(StudyRefusal.ExclusionNotStale, editor.ReconfirmExclusion(id, "").Refusal);
    }

    [Fact]
    public void Withdrawing_an_exclusion_keeps_the_record()
    {
        var (study, editor) = Study();
        var id = editor.AddExclusion(study.Identity(1), ExclusionCategory.Other, "not filmed").CreatedId!.Value;
        Assert.True(editor.WithdrawExclusion(id, "filmed after all").Ok);
        Assert.NotNull(study.Draft.Exclusions.Single().Withdrawn);
        Assert.Equal(StudyRefusal.ExclusionWithdrawn, editor.WithdrawExclusion(id, "").Refusal);
        Assert.Equal(StudyRefusal.UnknownClimb, editor.AddExclusion(study.Identity(1) with { ClimbKey = 99 }, ExclusionCategory.Other, "x").Refusal);
        Assert.Equal(StudyRefusal.ReasonRequired, editor.AddExclusion(study.Identity(1), ExclusionCategory.Other, "").Refusal);
    }

    // ---- retirement and revisions ---------------------------------------------------------------------------------

    [Fact]
    public void A_retired_video_keeps_its_marks_takes_none_and_returns_only_explicitly()
    {
        var (study, editor) = Study();
        var video = study.FirstVideo();
        var mark = AddMark(editor, video);
        Assert.Equal(StudyRefusal.ReasonRequired, editor.RetireVideo(video, "").Refusal);
        Assert.True(editor.RetireVideo(video, "file lost").Ok);
        Assert.Equal(StudyRefusal.VideoRetired, editor.RetireVideo(video, "again").Refusal);
        Assert.Equal(StudyRefusal.VideoRetired, editor.AddMark(video, 10 * Second, null).Refusal);
        Assert.NotNull(study.Draft.Mark(mark));

        Assert.True(editor.RestoreVideo(video, "found").Ok);
        Assert.Equal(StudyRefusal.VideoNotRetired, editor.RestoreVideo(video, "").Refusal);
        Assert.Equal(new[] { HistoryKind.VideoRetired, HistoryKind.VideoRestored }, study.History.TakeLast(2).Select(h => h.Kind));
    }

    [Fact]
    public void Retirement_changes_the_content_hash_of_the_next_revision_and_never_a_frozen_one()
    {
        var (study, editor) = Study();
        var first = editor.Freeze("rev 1");
        Assert.Equal(1, first.Number);
        Assert.Equal(2, study.DraftRevision);

        editor.RetireVideo(study.FirstVideo(), "lost");
        var second = editor.Freeze("rev 2");
        Assert.NotEqual(first.ContentHash, second.ContentHash);
        Assert.Null(study.Revisions[0].Content.Videos[0].Retired);         // revision 1 is unchanged
        Assert.Equal(first.ContentHash, StudyCanonicalJson.ContentHash(study.Revisions[0].Content));

        editor.RestoreVideo(study.FirstVideo(), "found");
        Assert.Equal(first.ContentHash, StudyCanonicalJson.ContentHash(study.Draft));   // back to the same content
        Assert.Empty(StudyValidator.Problems(study));
    }

    [Fact]
    public void A_frozen_revision_keeps_its_content_while_the_draft_moves_on()
    {
        var (study, editor) = Study();
        var mark = AddMark(editor, study.FirstVideo());
        var revision = editor.Freeze("before the move");
        editor.UpdateMark(mark, 45 * Second, null, "moved");

        Assert.Equal(30 * Second, study.Revisions[0].Content.Mark(mark)!.StartSourceTicks);
        Assert.Equal(45 * Second, study.Draft.Mark(mark)!.StartSourceTicks);
        Assert.Equal(revision.ContentHash, StudyCanonicalJson.ContentHash(study.Revisions[0].Content));
        Assert.Contains(study.History, h => h.Kind == HistoryKind.RevisionFrozen && h.New == revision.ContentHash);
    }

    [Fact]
    public void Relocation_changes_the_location_and_the_journal_not_the_content()
    {
        var (study, editor) = Study();
        var video = study.FirstVideo();
        var before = StudyCanonicalJson.ContentHash(study.Draft);

        Assert.True(editor.Relocate(video, @"E:\moved\renamed.mp4", 500, "moved disk").Ok);
        Assert.Equal(@"E:\moved\renamed.mp4", study.Location(video)!.CurrentPath);
        Assert.Equal(before, StudyCanonicalJson.ContentHash(study.Draft));
        Assert.Equal(HistoryKind.VideoRelocated, study.History[^1].Kind);
        Assert.Contains("renamed.mp4", study.History[^1].New);

        Assert.True(editor.Relocate(study.Draft.Fit.FitKey, @"E:\moved\a.fit", 1, "").Ok);
        Assert.Equal(HistoryKind.FitRelocated, study.History[^1].Kind);
        Assert.Equal(StudyRefusal.UnknownSubject, editor.Relocate(Guid.NewGuid(), "x", null).Refusal);
    }

    // ---- integrity ------------------------------------------------------------------------------------------------

    [Fact]
    public void Locations_must_name_an_existing_subject_once_each()
    {
        var (study, _) = Study();
        var fitKey = study.Draft.Fit.FitKey;
        Assert.Empty(StudyValidator.Problems(study));

        var extra = new SyncStudy(study.StudyId, study.Draft, study.Locations.Append(new StudyLocation(Guid.NewGuid(), "x", null)),
            study.Revisions, study.History, study.Evaluations);
        Assert.Contains(StudyValidator.Problems(extra), p => p.Contains("no such FIT or video"));

        var doubled = new SyncStudy(study.StudyId, study.Draft, study.Locations.Append(new StudyLocation(fitKey, "y", null)),
            study.Revisions, study.History, study.Evaluations);
        Assert.Contains(StudyValidator.Problems(doubled), p => p.Contains("more than one current location"));

        var missing = new SyncStudy(study.StudyId, study.Draft, study.Locations.Where(l => l.SubjectKey != fitKey),
            study.Revisions, study.History, study.Evaluations);
        Assert.Contains(StudyValidator.Problems(missing), p => p.Contains("no location"));
    }

    [Fact]
    public void An_active_decision_with_a_foreign_identity_is_damage_a_stale_one_is_not()
    {
        var (study, editor) = Study();
        var mark = AddMark(editor, study.FirstVideo());
        editor.DecideMatch(mark, MatchState.Confirmed, study.Identity(2), null, "");
        var foreign = study.Draft with
        {
            Decisions = study.Draft.Decisions.Select(d => d with { Climb = d.Climb!.Value with { ClimbNumber = 7 } }).ToList()
        };
        var damaged = new SyncStudy(study.StudyId, foreign, study.Locations, study.Revisions, study.History, study.Evaluations);
        Assert.Contains(StudyValidator.Problems(damaged), p => p.Contains("not a climb of this FIT"));

        var staleForeign = foreign with { Marks = foreign.Marks.Select(m => m with { Stale = new Staleness(1, StaleCause.FitChanged, 9) }).ToList() };
        var history = new SyncStudy(study.StudyId, staleForeign, study.Locations, study.Revisions, study.History, study.Evaluations);
        Assert.Empty(StudyValidator.Problems(history));
    }
}
