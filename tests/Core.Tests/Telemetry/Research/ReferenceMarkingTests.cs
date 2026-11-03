using AiVideoEditor.Core.Telemetry.Research;
using Xunit;
using static AiVideoEditor.Core.Tests.Telemetry.Research.StudyFixture;

namespace AiVideoEditor.Core.Tests.Telemetry.Research;

/// <summary>
/// Step 35.3R1 in Core: the marking rule (required before marking, frozen with the revision, one id per mark, never
/// rewriting old marks), the frame PTS kept for audit only, the one-camera rule (a climb has at most one confirmed mark in
/// force — refused by the editor, damage in the validator) and a mark's history read back from the journal by sequence.
/// </summary>
public class ReferenceMarkingTests
{
    private static readonly StudyFramePts Pts = new(5_400, 1, 90_000);

    private static Guid Mark(SyncStudyEditor editor, Guid video, long seconds = 30, long? endSeconds = null) =>
        editor.AddMark(video, seconds * Second, endSeconds * Second).CreatedId!.Value;

    private static (SyncStudy Study, SyncStudyEditor Editor) WithoutRule()
    {
        var fit = Fit();
        var video = Video('a');
        var clock = Clock();
        var study = SyncStudyEditor.Create(Guid.NewGuid(), fit, new[] { (video, new StudyLocation(video.VideoKey, "a.mp4", 1L)) },
            new StudyLocation(fit.FitKey, "a.fit", 1L), clock);
        return (study, new SyncStudyEditor(study, clock));
    }

    // ---- the marking rule -----------------------------------------------------------------------------------------

    [Fact]
    public void Marking_is_refused_until_a_rule_is_set_and_setting_it_is_journalled()
    {
        var (study, editor) = WithoutRule();
        var video = study.FirstVideo();
        Assert.Equal(StudyRefusal.NoMarkingRule, editor.AddMark(video, Second, null).Refusal);
        Assert.Equal(StudyRefusal.MarkingRuleIncomplete, editor.SetMarkingRule("", "text").Refusal);
        Assert.Equal(StudyRefusal.MarkingRuleIncomplete, editor.SetMarkingRule("r1", " ").Refusal);

        Assert.True(editor.SetMarkingRule("r1", "start: feet off the floor").Ok);
        Assert.Equal(new MarkingRule("r1", "start: feet off the floor"), study.Draft.MarkingRule);
        Assert.Equal((HistoryKind.MarkingRuleSet, (string?)null), (study.History[^1].Kind, study.History[^1].Old));
        var entries = study.History.Count;
        Assert.True(editor.SetMarkingRule("r1", "start: feet off the floor").Ok);
        Assert.Equal(entries, study.History.Count);                            // the same rule: nothing to record

        Assert.True(editor.AddMark(video, Second, null).Ok);
        Assert.Equal("r1", study.Draft.Marks.Single().MarkingRuleId);
    }

    [Fact]
    public void A_new_rule_rewrites_no_mark_and_a_moved_boundary_takes_the_rule_in_force()
    {
        var (study, editor) = Study();
        var mark = Mark(editor, study.FirstVideo(), 30, 50);
        editor.Freeze("rev 1");
        Assert.Equal(Rule, study.Revisions[0].Content.MarkingRule);           // frozen with the revision

        editor.SetMarkingRule("rule-2", "a new wording");
        Assert.Contains(Rule.Text, study.History[^1].Old);                     // the old rule stays in the journal
        Assert.Equal(Rule.Id, study.Draft.Mark(mark)!.MarkingRuleId);          // not rewritten

        editor.UpdateMark(mark, 30 * Second, 50 * Second, "a note only");
        Assert.Equal(Rule.Id, study.Draft.Mark(mark)!.MarkingRuleId);          // no boundary moved

        editor.UpdateMark(mark, 30 * Second, 52 * Second, "end moved");
        Assert.Equal("rule-2", study.Draft.Mark(mark)!.MarkingRuleId);         // one id for the whole mark
        Assert.Equal(Rule.Id, study.Revisions[0].Content.Mark(mark)!.MarkingRuleId);
        Assert.Empty(StudyValidator.Problems(study));
    }

    // ---- frame PTS (audit only) -----------------------------------------------------------------------------------

    [Fact]
    public void Frame_pts_follow_their_boundary_and_never_change_the_time()
    {
        var (study, editor) = Study();
        var video = study.FirstVideo();
        var id = editor.AddMark(video, 30 * Second, null, "", Pts).CreatedId!.Value;
        Assert.Equal((30 * Second, Pts), (study.Draft.Mark(id)!.StartSourceTicks, study.Draft.Mark(id)!.StartFramePts!.Value));

        var endPts = new StudyFramePts(9_000, 1, 90_000);
        editor.UpdateMark(id, 30 * Second, 50 * Second, "", null, endPts);     // the end set, the start untouched
        Assert.Equal((Pts, endPts), (study.Draft.Mark(id)!.StartFramePts!.Value, study.Draft.Mark(id)!.EndFramePts!.Value));

        editor.UpdateMark(id, 31 * Second, 50 * Second, "");                   // the start moved without a reliable frame
        Assert.Null(study.Draft.Mark(id)!.StartFramePts);
        Assert.Equal(endPts, study.Draft.Mark(id)!.EndFramePts);

        editor.UpdateMark(id, 31 * Second, null, "");                          // the end removed
        Assert.Null(study.Draft.Mark(id)!.EndFramePts);
        Assert.Equal(31 * Second, study.Draft.Mark(id)!.StartSourceTicks);
        Assert.Empty(StudyValidator.Problems(study));
    }

    [Fact]
    public void A_mark_without_new_members_keeps_its_canonical_form()
    {
        var mark = new ClimbMark(Guid.NewGuid(), Guid.NewGuid(), 1, null, "", 1, null, null, null);
        var json = StudyCanonicalJson.Value(mark);
        foreach (var member in new[] { "markingRuleId", "startFramePts", "endFramePts" }) Assert.DoesNotContain(member, json);
        Assert.Contains("\"startFramePts\":{\"pts\":5400,\"timeBaseDenominator\":90000,\"timeBaseNumerator\":1}",
            StudyCanonicalJson.Value(mark with { StartFramePts = Pts }));
    }

    // ---- one camera -----------------------------------------------------------------------------------------------

    [Fact]
    public void Reconfirming_or_restoring_never_gives_a_climb_a_second_confirmation()
    {
        var (study, editor) = Study('a', 'b');
        var a = study.Draft.Videos[0].VideoKey;
        var b = study.Draft.Videos[1].VideoKey;
        var onA = Mark(editor, a);
        editor.DecideMatch(onA, MatchState.Confirmed, study.Identity(2), null, "");

        // A stale mark of b cannot be decided for a climb that a's mark already confirms.
        var onB = Mark(editor, b);
        editor.AcceptChangedVideo(b, Video('c'), "b2", 1, "");                 // onB stale
        Assert.Equal(StudyRefusal.ClimbAlreadyConfirmed, editor.DecideMatch(onB, MatchState.Confirmed, study.Identity(2), null, "").Refusal);

        // A retired video's confirmation does not count — but bringing it back may not double it.
        var c = Mark(editor, a, 90);
        editor.DecideMatch(c, MatchState.Confirmed, study.Identity(3), null, "");
        editor.RetireVideo(a, "out");
        var onB2 = Mark(editor, b, 60);
        Assert.True(editor.DecideMatch(onB2, MatchState.Confirmed, study.Identity(3), null, "").Ok);   // free while a is retired
        var restore = editor.RestoreVideo(a, "back");
        Assert.Equal(StudyRefusal.ClimbAlreadyConfirmed, restore.Refusal);
        Assert.NotNull(restore.RelatedId);
        Assert.NotNull(study.Draft.Video(a)!.Retired);                         // nothing changed
        Assert.Empty(StudyValidator.Problems(study));
    }

    [Fact]
    public void A_second_confirmation_in_force_is_damage_in_the_draft_and_in_a_revision()
    {
        var (study, editor) = Study('a', 'b');
        var m1 = Mark(editor, study.Draft.Videos[0].VideoKey);
        var m2 = Mark(editor, study.Draft.Videos[1].VideoKey);
        editor.DecideMatch(m1, MatchState.Confirmed, study.Identity(2), null, "");
        editor.DecideMatch(m2, MatchState.Rejected, study.Identity(2), MatchReason.WrongClimb, "");
        editor.Freeze("rev 1");
        Assert.Empty(StudyValidator.Problems(study));                          // a rejection is not a confirmation

        var doubled = study.Draft with
        {
            Decisions = study.Draft.Decisions.Select(d => d.MarkId == m2 ? d with { State = MatchState.Confirmed, Reason = null } : d).ToList()
        };
        var draftBroken = new SyncStudy(study.StudyId, doubled, study.Locations, study.Revisions, study.History, study.Evaluations);
        Assert.Contains(StudyValidator.Problems(draftBroken), p => p.StartsWith("draft:") && p.Contains("more than one mark"));

        var revisionBroken = new SyncStudy(study.StudyId, study.Draft, study.Locations,
            new[] { study.Revisions[0] with { Content = doubled, ContentHash = StudyCanonicalJson.ContentHash(doubled) } },
            study.History, study.Evaluations);
        Assert.Contains(StudyValidator.Problems(revisionBroken), p => p.StartsWith("revision 1:") && p.Contains("more than one mark"));

        var withdrawn = doubled with { Marks = doubled.Marks.Select(m => m.MarkId == m2 ? m with { Withdrawn = new Withdrawal(1, 99) } : m).ToList() };
        Assert.Empty(StudyValidator.Problems(new SyncStudy(study.StudyId, withdrawn, study.Locations, study.Revisions, study.History, study.Evaluations)));
    }

    // ---- the mark's history ---------------------------------------------------------------------------------------

    [Fact]
    public void A_marks_history_restores_every_step_with_the_climb_it_was_bound_to()
    {
        var (study, editor) = Study();
        var video = study.FirstVideo();
        var id = Mark(editor, video, 30);
        editor.UpdateMark(id, 31 * Second, null, "");                          // move start (no climb yet)
        editor.UpdateMark(id, 31 * Second, 55 * Second, "");                   // set the end
        editor.DecideMatch(id, MatchState.Confirmed, study.Identity(2), null, "");
        editor.UpdateMark(id, 32 * Second, 55 * Second, "");                   // move start while confirmed for C2
        editor.DecideMatch(id, MatchState.Confirmed, study.Identity(3), null, "");   // the binding changes
        editor.UpdateMark(id, 32 * Second, 56 * Second, "");                   // move end while bound to C3
        editor.ClearDecision(id, "");
        editor.WithdrawMark(id, "redo");

        var steps = MarkHistory.For(study, id);
        Assert.Equal(new[]
        {
            HistoryKind.MarkAdded, HistoryKind.MarkUpdated, HistoryKind.MarkUpdated, HistoryKind.DecisionSet, HistoryKind.MarkUpdated,
            HistoryKind.DecisionSet, HistoryKind.MarkUpdated, HistoryKind.DecisionCleared, HistoryKind.MarkWithdrawn,
        }, steps.Select(s => s.Kind));
        Assert.Equal(steps.Select(s => s.Seq).OrderBy(s => s), steps.Select(s => s.Seq));
        Assert.Equal(new long?[] { 30, 31, 31, 31, 32, 32, 32, 32, 32 }, steps.Select(s => s.Mark!.StartSourceTicks / Second as long?));
        Assert.Equal(new int?[] { null, null, null, 2, 2, 3, 3, null, null }, steps.Select(s => s.Climb?.ClimbNumber));
        Assert.Equal(56 * Second, steps[6].Mark!.EndSourceTicks);
        Assert.NotNull(steps[^1].Mark!.Withdrawn);

        // The state at any moment: right before the binding changed, the mark was at 0:32 for C2.
        var beforeRebind = MarkHistory.StateAt(steps, steps[5].Seq - 1)!;
        Assert.Equal((32 * Second, 2), (beforeRebind.Mark!.StartSourceTicks, beforeRebind.Climb!.Value.ClimbNumber));
        Assert.Null(MarkHistory.StateAt(steps, steps[0].Seq - 1));
        Assert.All(steps, s => Assert.Equal(TimeSpan.Zero, s.AtUtc.Offset));
    }
}
