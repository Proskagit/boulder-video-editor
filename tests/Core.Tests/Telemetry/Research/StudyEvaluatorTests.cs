using System.Globalization;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Research;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry.Research;

/// <summary>
/// Step 35.3b2c (D050 B6, amendment of 2026-11-02 «35.3d protocol») on synthetic revisions: <c>k_i = W − (Z + s)</c> with
/// <c>Z = tag − duration</c>; the leave-one-out residual within <c>τ</c> inclusive; all 13 climbs C2–C14 in the
/// denominator (excluded, not computed and outliers are not within τ); ≥ 11 of 13; the explanations by family; the
/// Theil–Sen drift against τ / 2 with «not established» never met; the tag check per file, outside the verdict; a report
/// that is the same for the same input.
/// </summary>
public class StudyEvaluatorTests
{
    private const long S = TimeSpan.TicksPerSecond;
    private const long Tau = 20_000_000;
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);
    private static readonly EvaluationProtocol P = EvaluationProtocol.B6;

    /// <summary>Climb n starts at T0 + 300 n s; its video starts 60 s before it (name and tag agree, the tag marks the end).</summary>
    private static DateTimeOffset W(int n) => T0.AddSeconds(300 * n);

    private static string Name(DateTimeOffset utcStart) =>
        "video_" + utcStart.ToOffset(TimeSpan.FromHours(3)).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".mp4";

    private static string Tag(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static StudyVideo Video(DateTimeOffset start, long durationTicks = 200 * S, long tagShiftTicks = 0, bool tag = true,
        StudyRate? cfr = null) =>
        new(Guid.NewGuid(), Name(start), 1_000, new string('a', 64), durationTicks, true,
            tag ? Tag(start.AddTicks(durationTicks + tagShiftTicks)) : null, tag ? RecordingTimeTagOrigin.Format : null, 1, null,
            FrameRate: cfr, RateState: cfr is null ? null : FrameRateState.Cfr);

    private sealed class Set
    {
        public readonly List<StudyClimb> Climbs = Enumerable.Range(1, 14).Select(n => new StudyClimb(2 * (n - 1), n, W(n), 20 * S, W(n).AddSeconds(21))).ToList();
        public readonly List<StudyVideo> Videos = new();
        public readonly List<ClimbMark> Marks = new();
        public readonly List<MatchDecision> Decisions = new();
        public readonly List<Exclusion> Exclusions = new();

        public ClimbIdentity Id(int n) => Climbs[n - 1].Identity;

        /// <summary>A confirmed mark of climb n whose <c>k</c> is <paramref name="k"/> (ticks): s = 60 s − k.</summary>
        public StudyVideo Confirm(int n, long k, StudyVideo? video = null)
        {
            video ??= Video(W(n).AddSeconds(-60));
            if (!Videos.Contains(video)) Videos.Add(video);
            var videoStart = video.TagRaw is null ? W(n).AddSeconds(-60) : video.StartUtc();
            var start = W(n).UtcTicks - videoStart.UtcTicks - k;
            var mark = new ClimbMark(Guid.NewGuid(), video.VideoKey, start, start + 15 * S, "", 1, null, null, null);
            Marks.Add(mark);
            Decisions.Add(new MatchDecision(mark.MarkId, MatchState.Confirmed, Id(n), null, "", MatchBasis.Manual, null, 1));
            return video;
        }

        /// <summary>An ambiguous mark near climb n (no confirmed match).</summary>
        public void Ambiguous(int n)
        {
            var video = Video(W(n).AddSeconds(-60));
            Videos.Add(video);
            var mark = new ClimbMark(Guid.NewGuid(), video.VideoKey, 60 * S, null, "", 1, null, null, null);
            Marks.Add(mark);
            Decisions.Add(new MatchDecision(mark.MarkId, MatchState.NotMatched, null, MatchReason.Ambiguous, "", MatchBasis.Manual, null, 1));
        }

        public void Exclude(int n) =>
            Exclusions.Add(new Exclusion(Guid.NewGuid(), Id(n), ExclusionCategory.FitClimbAnomaly, "recorded before", 1, null, null, null));

        public StudyRevision Revision()
        {
            var content = new StudyContent(new StudyFit(Guid.NewGuid(), new string('f', 64), 54_003,
                new TrainingActivity(T0, T0.AddHours(2), 294, 814), Climbs, 180), Videos, Marks, Decisions, Exclusions,
                new MarkingRule("coros-ref-1", "rule"));
            return new StudyRevision(1, T0.AddDays(30), StudyCanonicalJson.ContentHash(content), "", content);
        }

        /// <summary>Climbs C2–C14 all confirmed with <c>k = 5 s</c>, except those given.</summary>
        public static Set Uniform(Dictionary<int, long>? k = null, params int[] skip)
        {
            var set = new Set();
            foreach (var n in Enumerable.Range(2, 13).Where(n => !skip.Contains(n)))
                set.Confirm(n, k is not null && k.TryGetValue(n, out var v) ? v : 5 * S);
            return set;
        }
    }

    private static StudyEvaluationReport Evaluate(Set set, EvaluationProtocol? protocol = null) =>
        StudyEvaluator.Evaluate(set.Revision(), protocol ?? P);

    [Fact]
    public void The_protocol_is_b6_with_tau_two_seconds_and_thirteen_climbs()
    {
        Assert.Equal(20_000_000, P.TauTicks);
        Assert.Equal(10_000_000, P.DriftLimitTicks);
        Assert.Equal(11, P.RequiredWithinTau);
        Assert.Equal(Enumerable.Range(2, 13), P.EvaluatedClimbs);
        Assert.Equal(TimeSpan.FromHours(3), P.FileNameOffset);
        Assert.Equal(7, P.TagCheckVideos.Count);
        Assert.Equal(20_000_000, P.TagThresholdTicks);
        Assert.Contains("after the descriptive analysis", P.TagThresholdNote);
        Assert.Contains("not registered in advance", P.TagThresholdNote);
    }

    [Fact]
    public void K_is_the_split_start_less_the_tag_end_zero_and_the_marked_start()
    {
        var set = new Set();
        var video = set.Confirm(5, 7 * S + 123);
        var r = Evaluate(set).Climbs.Single(c => c.ClimbNumber == 5);

        var zero = DateTimeOffset.Parse(video.TagRaw!, CultureInfo.InvariantCulture).AddTicks(-video.DurationTicks);
        Assert.Equal(zero, r.Zero);
        Assert.Equal(W(5).UtcTicks - (zero.UtcTicks + r.MarkStartTicks!.Value), r.KTicks);
        Assert.Equal(7 * S + 123, r.KTicks);
    }

    [Fact]
    public void All_thirteen_within_tau_meet_b6_with_nothing_to_explain()
    {
        var r = Evaluate(Set.Uniform());

        Assert.Equal(13, r.Denominator);
        Assert.Equal(13, r.WithinTau);
        Assert.Equal((5 * S, 0L, 13), (r.Estimate.KHatTicks!.Value, r.Estimate.MadTicks!.Value, r.Estimate.Count));
        Assert.Equal(DriftState.Within, r.Drift.State);
        Assert.Equal(0, r.Drift.DriftTicks);
        Assert.Equal((ConditionState.Met, ConditionState.Met, ConditionState.Met), (r.AtLeastRequired, r.AllExplained, r.NoDrift));
        Assert.Equal(B6Verdict.Met, r.Verdict);
        Assert.Equal(new[] { 1 }, r.Outside.Select(o => o.ClimbNumber));     // C1 reported, never counted
    }

    [Fact]
    public void Eleven_of_thirteen_meet_condition_one_and_wait_for_the_outliers_explanations()
    {
        var r = Evaluate(Set.Uniform(new() { [3] = 60 * S, [9] = -40 * S }));

        Assert.Equal((11, 2), (r.WithinTau, r.Outliers));
        Assert.Equal(ConditionState.Met, r.AtLeastRequired);
        Assert.Equal(ConditionState.PendingExplanations, r.AllExplained);
        Assert.Equal(B6Verdict.PendingExplanations, r.Verdict);
        Assert.All(r.Climbs.Where(c => c.Status == ClimbStatus.Outlier), c => Assert.Equal(ExplanationFamily.Deviation, c.Needs));
    }

    [Fact]
    public void Ten_of_thirteen_do_not_meet_b6()
    {
        var r = Evaluate(Set.Uniform(new() { [3] = 60 * S, [9] = -40 * S, [12] = 30 * S }));

        Assert.Equal(10, r.WithinTau);
        Assert.Equal(ConditionState.NotMet, r.AtLeastRequired);
        Assert.Equal(B6Verdict.NotMet, r.Verdict);
    }

    [Fact]
    public void Ambiguous_missing_and_excluded_climbs_stay_in_the_denominator_of_thirteen()
    {
        var set = Set.Uniform(null, 7, 10, 13);
        set.Ambiguous(7);
        set.Ambiguous(13);                                                     // and C10 has no mark at all
        set.Exclude(4);                                                        // recorded before the evaluation

        var r = Evaluate(set);

        Assert.Equal(13, r.Denominator);
        Assert.Equal((9, 3, 1, 0), (r.WithinTau, r.NotComputed, r.Excluded, r.Outliers));
        Assert.Equal(ConditionState.NotMet, r.AtLeastRequired);                // 9 of 13, never 9 of 9
        var c7 = r.Climbs.Single(c => c.ClimbNumber == 7);
        Assert.Equal((ClimbStatus.NotComputed, NotComputedReason.NoConfirmedMatch, ExplanationFamily.Absence), (c7.Status, c7.Reason, c7.Needs));
        Assert.Equal(NotComputedReason.NoConfirmedMatch, r.Climbs.Single(c => c.ClimbNumber == 10).Reason);
        var c4 = r.Climbs.Single(c => c.ClimbNumber == 4);
        Assert.Equal((ClimbStatus.Excluded, ExplanationFamily.None, ExclusionCategory.FitClimbAnomaly), (c4.Status, c4.Needs, c4.ExclusionCategory));
        Assert.Null(c4.KTicks);                                                // an excluded climb is not computed at all
        Assert.Equal(9, r.Estimate.Count);
    }

    [Fact]
    public void A_withdrawn_exclusion_does_not_exclude()
    {
        var set = Set.Uniform();
        set.Exclusions.Add(new Exclusion(Guid.NewGuid(), set.Id(4), ExclusionCategory.Other, "x", 1, new Withdrawal(1, 5), null, null));
        Assert.Equal(ClimbStatus.WithinTau, Evaluate(set).Climbs.Single(c => c.ClimbNumber == 4).Status);
    }

    [Theory]
    [InlineData(Tau, ClimbStatus.WithinTau)]
    [InlineData(-Tau, ClimbStatus.WithinTau)]
    [InlineData(Tau + 1, ClimbStatus.Outlier)]
    [InlineData(-Tau - 1, ClimbStatus.Outlier)]
    public void The_residual_is_within_tau_inclusive(long offset, ClimbStatus expected)
    {
        var r = Evaluate(Set.Uniform(new() { [8] = 5 * S + offset })).Climbs.Single(c => c.ClimbNumber == 8);
        Assert.Equal(offset, r.ResidualTicks);                                 // the others' median is 5 s exactly
        Assert.Equal(expected, r.Status);
    }

    [Theory]
    [InlineData(new long[] { 5 }, 5)]
    [InlineData(new long[] { 1, 2 }, 1)]
    [InlineData(new long[] { -3, -2 }, -3)]                                    // −2.5 → −3: towards −∞
    [InlineData(new long[] { -1, 2 }, 0)]
    [InlineData(new long[] { 9, 1, 5, 3 }, 4)]
    [InlineData(new long[] { long.MaxValue, long.MaxValue - 1 }, long.MaxValue - 1)]
    public void The_median_of_an_even_count_is_the_midpoint_rounded_down(long[] values, long expected) =>
        Assert.Equal(expected, StudyEvaluator.Median(values));

    [Fact]
    public void One_computed_match_has_no_residual_and_the_drift_is_not_established_hence_not_met()
    {
        var set = new Set();
        set.Confirm(6, 5 * S);
        var r = Evaluate(set);

        var c6 = r.Climbs.Single(c => c.ClimbNumber == 6);
        Assert.Equal((ClimbStatus.NotComputed, NotComputedReason.TooFewMatches), (c6.Status, c6.Reason));
        Assert.Equal(5 * S, c6.KTicks);
        Assert.Equal(DriftState.NotEstablished, r.Drift.State);
        Assert.Equal(ConditionState.NotEstablished, r.NoDrift);
        Assert.Equal(B6Verdict.NotMet, r.Verdict);
    }

    [Theory]
    [InlineData(10_000_000, DriftState.Within)]                              // exactly τ / 2
    [InlineData(10_000_001, DriftState.Beyond)]
    [InlineData(-10_000_001, DriftState.Beyond)]
    public void The_drift_is_compared_with_half_tau_exactly(long kDifference, DriftState expected)
    {
        var set = new Set();
        set.Confirm(2, 5 * S);
        set.Confirm(14, 5 * S + kDifference);                                  // two points: slope × span = the difference
        var r = Evaluate(set);

        Assert.Equal(expected, r.Drift.State);
        Assert.Equal(kDifference, r.Drift.DriftTicks);
        Assert.Equal((W(14) - W(2)).Ticks, r.Drift.SpanTicks);
    }

    [Fact]
    public void The_theil_sen_drift_is_the_median_slope_times_the_span()
    {
        // k grows by 1 s per climb over C2–C14 (12 steps): the drift over the span is 12 s, beyond τ / 2.
        var k = Enumerable.Range(2, 13).ToDictionary(n => n, n => (long)(n - 2) * S);
        var r = Evaluate(Set.Uniform(k));
        Assert.Equal(12 * S, r.Drift.DriftTicks);
        Assert.Equal(78, r.Drift.Pairs);
        Assert.Equal(DriftState.Beyond, r.Drift.State);
        Assert.Equal(B6Verdict.NotMet, r.Verdict);
    }

    [Fact]
    public void A_video_without_a_tag_leaves_its_climb_not_computed()
    {
        var set = Set.Uniform(null, 5);
        set.Confirm(5, 5 * S, Video(W(5).AddSeconds(-60), tag: false));
        var c5 = Evaluate(set).Climbs.Single(c => c.ClimbNumber == 5);
        Assert.Equal((ClimbStatus.NotComputed, NotComputedReason.RecordingStartUnknown, ExplanationFamily.Absence), (c5.Status, c5.Reason, c5.Needs));
    }

    [Fact]
    public void Two_confirmations_of_one_climb_are_never_chosen_between_and_stay_in_the_denominator()
    {
        // Unreachable through the study service (the editor, the validator and EvaluationProblems all refuse it by the same
        // filter); Core's guard against a malformed input: not computed, needs an absence explanation, never excluded.
        var set = Set.Uniform();
        set.Confirm(5, 6 * S);
        Assert.Single(set.Revision().Content.DoubleConfirmations());

        var r = Evaluate(set);
        var c5 = r.Climbs.Single(c => c.ClimbNumber == 5);
        Assert.Equal((ClimbStatus.NotComputed, NotComputedReason.ConflictingConfirmations, ExplanationFamily.Absence), (c5.Status, c5.Reason, c5.Needs));
        Assert.Null(c5.KTicks);
        Assert.Null(c5.MarkId);                                                // no mark is chosen
        Assert.Equal((13, 12, 0), (r.Denominator, r.WithinTau, r.Excluded));
    }

    [Fact]
    public void The_tag_check_compares_the_computed_start_with_the_file_name_and_is_reported_per_file()
    {
        var set = Set.Uniform();
        var at2s = Video(T0.AddHours(3), tagShiftTicks: 2 * S);
        var over = Video(T0.AddHours(4), tagShiftTicks: 2 * S + 10);   // the tag keeps microseconds
        var none = Video(T0.AddHours(5), tag: false);
        set.Videos.AddRange(new[] { at2s, over, none });
        var protocol = P with { TagCheckVideos = new[] { at2s.OriginalFileName, over.OriginalFileName, none.OriginalFileName, "video_missing.mp4" } };

        var r = Evaluate(set, protocol);

        var check = r.TagCheck.ToDictionary(t => t.VideoName);
        Assert.Equal((2 * S, true), (check[at2s.OriginalFileName].DifferenceTicks!.Value, check[at2s.OriginalFileName].WithinThreshold!.Value));
        Assert.Equal((2 * S + 10, false), (check[over.OriginalFileName].DifferenceTicks!.Value, check[over.OriginalFileName].WithinThreshold!.Value));
        Assert.Equal("no tag", check[none.OriginalFileName].Problem);
        Assert.Null(check[none.OriginalFileName].WithinThreshold);
        Assert.Equal("not in the revision", check["video_missing.mp4"].Problem);
        Assert.Equal(B6Verdict.Met, r.Verdict);                                // a file outside the threshold is a finding only

        // The descriptive table keeps the computed start and the raw tag apart: raw − name = duration + shift.
        var d = r.TagNameDifferences.Single(x => x.VideoName == over.OriginalFileName);
        Assert.Equal(2 * S + 10, d.TagEndZeroMinusFileNameTicks);
        Assert.Equal(over.DurationTicks + 2 * S + 10, d.RawTagMinusFileNameTicks);
    }

    [Fact]
    public void The_sensitivity_leaves_out_one_video_and_keeps_the_denominator()
    {
        var set = new Set();
        var shared = Video(W(2).AddSeconds(-60), durationTicks: 4000 * S);
        foreach (var n in new[] { 2, 3, 4 }) set.Confirm(n, 5 * S, shared);
        foreach (var n in Enumerable.Range(5, 10)) set.Confirm(n, 5 * S);

        var r = Evaluate(set);
        var without = r.Sensitivity.Single(s => s.VideoName == shared.OriginalFileName);
        Assert.Equal((3, 10), (without.RemovedMatches, without.WithinTau));
        Assert.Equal(11, r.Sensitivity.Count);
        Assert.Equal(B6Verdict.Met, r.Verdict);                                // the sensitivity never changes the verdict
    }

    [Fact]
    public void The_reference_hypothesis_is_descriptive_only()
    {
        var r = Evaluate(Set.Uniform());
        Assert.Equal(5 * S - 200 * S, r.ReferenceTagMarksStart.KHatTicks);      // the tag as the start: Z later by the duration
        Assert.Equal(B6Verdict.Met, r.Verdict);
    }

    [Fact]
    public void The_report_is_the_same_for_the_same_input_and_its_inputs_hash_names_the_protocol()
    {
        var revision = Set.Uniform(new() { [3] = 60 * S }).Revision();
        var a = StudyEvaluator.Evaluate(revision, P);
        var b = StudyEvaluator.Evaluate(revision, P);
        Assert.Equal(StudyCanonicalJson.Report(a), StudyCanonicalJson.Report(b));
        Assert.Equal(a.InputsHash, b.InputsHash);
        Assert.NotEqual(a.InputsHash, StudyEvaluator.Evaluate(revision, P with { TauTicks = Tau + 1 }).InputsHash);

        var json = StudyCanonicalJson.Report(a);
        Assert.DoesNotContain("\r", json);
        Assert.Contains("\"verdict\":\"pendingexplanations\"", json);
        Assert.Contains("\"denominator\":13", json);
        Assert.Contains("not registered in advance", json);
        Assert.Contains("\"residualMs\":\"55000.0000\"", json);                 // 55 s off the others' 5 s, exactly in ms
    }

    [Fact]
    public void Frames_are_reported_only_for_a_confirmed_constant_rate()
    {
        var set = Set.Uniform(null, 5);
        set.Confirm(5, 5 * S + 1_234_567, Video(W(5).AddSeconds(-60), cfr: new StudyRate(60, 1)));
        var r = Evaluate(set);
        var json = StudyCanonicalJson.Report(r);
        Assert.Contains("\"residualFrames\":\"7.407\"", json);                // 0.1234567 s × 60 = 7.407402
        Assert.Single(r.Climbs, c => c.ConfirmedFrameRate is not null);

        // Every other residual has milliseconds and the reason it has no frames — once per computed residual.
        Assert.Equal(12, json.Split("\"residualFramesNote\":\"no frames: the video\\u0027s constant frame rate is not confirmed\"").Length - 1);
        Assert.Equal(13, json.Split("\"residualMs\"").Length - 1);
        var csv = StudyCanonicalJson.EvaluationCsv(r).Split('\n');
        Assert.Contains("no frames: the video's constant frame rate is not confirmed", csv[1]);
        Assert.DoesNotContain("no frames", csv.Single(l => l.StartsWith("5,", StringComparison.Ordinal)));
        Assert.Equal("-0.0001", StudyEvaluator.Milliseconds(-1));
        Assert.Equal("1.000", StudyEvaluator.Frames(166_667, new StudyRate(60, 1)));   // 1.00000200 → 1.000
    }

    [Fact]
    public void The_csv_has_one_row_per_evaluated_climb_and_the_climbs_outside_in_invariant_form()
    {
        var csv = StudyCanonicalJson.EvaluationCsv(Evaluate(Set.Uniform(new() { [3] = 60 * S }, 7)));
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + 13 + 1, lines.Length);
        Assert.Equal("climb,status,reason,needs,video,markStartTicks,zeroUtc,kTicks,residualTicks,residualMs,residualFrames,residualFramesNote,exclusion", lines[0]);
        Assert.StartsWith("3,outlier,,deviation,", lines[2]);
        Assert.StartsWith("7,notcomputed,noconfirmedmatch,absence,", lines[6]);
        Assert.StartsWith("1,outside,", lines[^1]);
        Assert.Contains("55000.0000", lines[2]);
        Assert.DoesNotContain("\r", csv);
    }

    // ---- the final verdict with the explanations ---------------------------------------------------------------------

    private static CaseExplanation Explain(StudyEvaluationReport r, string hash, int n, ExplanationCategory category, long seq = 10, long evaluationSeq = 5) =>
        new(seq, Guid.NewGuid(), evaluationSeq, hash, r.Climbs.Single(c => c.ClimbNumber == n).Climb!.Value, category, "evidence", T0);

    [Fact]
    public void Each_outlier_and_climb_not_computed_needs_an_explanation_of_its_family()
    {
        var set = Set.Uniform(new() { [3] = 60 * S }, 7);
        set.Ambiguous(7);
        var r = Evaluate(set);
        var report = StudyCanonicalJson.Report(r);
        var hash = StudyCanonicalJson.TextHash(report);
        Assert.Equal((11, ConditionState.Met), (r.WithinTau, r.AtLeastRequired));

        Assert.Equal(B6Verdict.PendingExplanations, StudyEvaluator.FinalVerdict(report, hash, 5, Array.Empty<CaseExplanation>()).Verdict);

        var wrongFamily = StudyEvaluator.FinalVerdict(report, hash, 5, new[]
        {
            Explain(r, hash, 3, ExplanationCategory.NoUsableFootage),           // an absence for an outlier
            Explain(r, hash, 7, ExplanationCategory.MarkingError),              // a deviation for a climb not computed
        });
        Assert.Equal(B6Verdict.PendingExplanations, wrongFamily.Verdict);
        Assert.All(wrongFamily.Climbs.Where(c => c.Needs != ExplanationFamily.None), c => Assert.Equal("the explanation's category does not fit the status", c.Problem));

        var unexplained = StudyEvaluator.FinalVerdict(report, hash, 5, new[]
        {
            Explain(r, hash, 3, ExplanationCategory.Unexplained), Explain(r, hash, 7, ExplanationCategory.InsufficientEvidence),
        });
        Assert.Equal("recorded as unexplained", unexplained.Climbs.Single(c => c.ClimbNumber == 3).Problem);
        Assert.Equal(B6Verdict.PendingExplanations, unexplained.Verdict);

        var otherEvaluation = StudyEvaluator.FinalVerdict(report, hash, 5, new[]
        {
            Explain(r, hash, 3, ExplanationCategory.MarkingError, evaluationSeq: 6), Explain(r, "other", 7, ExplanationCategory.InsufficientEvidence),
        });
        Assert.Equal(B6Verdict.PendingExplanations, otherEvaluation.Verdict);   // another record or report never explains

        var explained = StudyEvaluator.FinalVerdict(report, hash, 5, new[]
        {
            Explain(r, hash, 3, ExplanationCategory.MarkingError), Explain(r, hash, 7, ExplanationCategory.InsufficientEvidence),
        });
        Assert.Equal((B6Verdict.Met, ConditionState.Met), (explained.Verdict, explained.AllExplained));
        Assert.Equal(13, explained.Climbs.Count);
    }

    [Fact]
    public void Explanations_never_lift_a_failed_condition_one_or_three()
    {
        var r = Evaluate(Set.Uniform(new() { [3] = 60 * S, [9] = -40 * S, [12] = 30 * S }));
        var report = StudyCanonicalJson.Report(r);
        var hash = StudyCanonicalJson.TextHash(report);
        var all = new[] { 3, 9, 12 }.Select((n, i) => Explain(r, hash, n, ExplanationCategory.MarkingError, seq: 10 + i)).ToList();

        var verdict = StudyEvaluator.FinalVerdict(report, hash, 5, all);
        Assert.Equal(ConditionState.Met, verdict.AllExplained);
        Assert.Equal(B6Verdict.NotMet, verdict.Verdict);
    }
}

internal static class StudyVideoTestExtensions
{
    /// <summary>The recording start the tests build into a video: its tag less its duration.</summary>
    public static DateTimeOffset StartUtc(this StudyVideo video) =>
        DateTimeOffset.Parse(video.TagRaw!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).AddTicks(-video.DurationTicks);
}
