using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Telemetry.Research;

// Step 35.3b2c (D050 B6, amendment of 2026-11-02 «35.3d protocol»): the formal evaluation of a frozen study revision —
// pure, deterministic, in ticks. It never changes the study; Step 35.3b2d stores its report as an evaluation record.

/// <summary>
/// What is fixed before the evaluation (D050 amendment of 2026-11-02): B6 with <c>τ = 2 s</c> unchanged, the hypothesis
/// «the tag marks the end of the recording» (<c>Z = tag − duration</c>), the file-name check with the offset hypothesis
/// <c>h</c>, the 13 evaluated climbs C2–C14 (the denominator), the 7 videos of the tag check by name and its threshold.
/// The tag-check threshold was fixed after the descriptive analysis had shown those files, before this evaluation — it is
/// not a criterion registered in advance; <see cref="TagThresholdNote"/> says so in every report.
/// </summary>
public sealed record EvaluationProtocol(
    string AlgorithmVersion,
    long TauTicks,
    long DriftLimitTicks,
    int RequiredWithinTau,
    IReadOnlyList<int> EvaluatedClimbs,
    TimeSpan FileNameOffset,
    IReadOnlyList<string> TagCheckVideos,
    long TagThresholdTicks,
    string TagThresholdNote)
{
    /// <summary>The protocol of Step 35.3d on the fixed set of 2026-09-27.</summary>
    public static EvaluationProtocol B6 { get; } = new(
        "coros-b6-1",
        TauTicks: 20_000_000,
        DriftLimitTicks: 10_000_000,
        RequiredWithinTau: 11,
        EvaluatedClimbs: Enumerable.Range(2, 13).ToList(),
        FileNameOffset: TimeSpan.FromHours(3),
        TagCheckVideos: new[]
        {
            "video_20260927_100405.mp4", "video_20260927_101229.mp4", "video_20260927_102533.mp4", "video_20260927_104110.mp4",
            "video_20260927_104651.mp4", "video_20260927_105713.mp4", "video_20260927_112854.mp4",
        },
        TagThresholdTicks: 20_000_000,
        TagThresholdNote: "fixed 2026-11-02 after the descriptive analysis of these files, before the formal evaluation; not registered in advance");
}

/// <summary>The status of one evaluated climb (B6). Every evaluated climb has exactly one.</summary>
public enum ClimbStatus
{
    WithinTau,
    Outlier,
    NotComputed,
    Excluded,
}

/// <summary>Why a climb has no residual.</summary>
public enum NotComputedReason
{
    /// <summary>The climb is not in the revision's FIT.</summary>
    NotInFit,

    /// <summary>No confirmed match in force (ambiguous, not matched, or no mark at all).</summary>
    NoConfirmedMatch,

    /// <summary>More than one confirmation in force (the one-camera rule broken; never chosen between). Unreachable through
    /// the study service: the editor refuses a second confirmation, the validator calls such a study damaged and
    /// <c>EvaluationProblems</c> refuses the evaluation by the same filter as <see cref="StudyContent.ConfirmationsInForce"/>
    /// (confirmed, a climb, an active mark, a video not retired). Kept as Core's guard against a malformed input: the climb
    /// stays in the denominator, not computed, never excluded and never resolved by choosing a mark.</summary>
    ConflictingConfirmations,

    /// <summary>The video's tag is absent or not recognised: no recording zero under the hypothesis.</summary>
    RecordingStartUnknown,

    /// <summary>Fewer than two computed matches: no leave-one-out residual.</summary>
    TooFewMatches,
}

/// <summary>Met / not met / not established / waiting for explanations. «Not established» is never «met».</summary>
public enum ConditionState
{
    Met,
    NotMet,
    NotEstablished,
    PendingExplanations,
}

public enum B6Verdict
{
    Met,
    NotMet,
    PendingExplanations,
}

/// <summary>One evaluated climb: its status and, for a confirmed match, the zero, <c>k</c> and the residual.</summary>
public sealed record ClimbResult(
    int ClimbNumber,
    ClimbIdentity? Climb,
    ClimbStatus Status,
    NotComputedReason? Reason,
    ExclusionCategory? ExclusionCategory,
    string? ExclusionReason,
    Guid? MarkId,
    Guid? VideoKey,
    string? VideoName,
    long? MarkStartTicks,
    DateTimeOffset? Zero,
    long? KTicks,
    long? ResidualTicks,
    StudyRate? ConfirmedFrameRate)
{
    /// <summary>The family of explanation the climb needs for condition 2: a deviation for an outlier, an absence for a
    /// climb not computed; none when it is within τ or excluded before the evaluation.</summary>
    public ExplanationFamily Needs => Status switch
    {
        ClimbStatus.Outlier => ExplanationFamily.Deviation,
        ClimbStatus.NotComputed => ExplanationFamily.Absence,
        _ => ExplanationFamily.None,
    };
}

/// <summary>The clock-correction estimate <c>K̂ = median(k_i)</c>, its MAD and the number of values.</summary>
public sealed record ClockEstimate(long? KHatTicks, long? MadTicks, int Count);

public enum DriftState
{
    Within,
    Beyond,
    NotEstablished,
}

/// <summary>The Theil–Sen drift: the median pairwise slope of <c>k</c> against the split start times the span, rounded
/// to ticks (half away from zero) for the report; the comparison with the limit is exact.</summary>
public sealed record DriftResult(DriftState State, long? DriftTicks, long? SpanTicks, int Pairs);

/// <summary>One video of the tag check: <c>Z_tag = tag − duration</c> against <c>Z_name = L − h</c>.</summary>
public sealed record TagCheckResult(
    string VideoName,
    Guid? VideoKey,
    string? TagRaw,
    long? DurationTicks,
    DateTimeOffset? ZeroFromTag,
    DateTimeOffset? ZeroFromFileName,
    long? DifferenceTicks,
    bool? WithinThreshold,
    string? Problem);

/// <summary>Descriptive, every video: the computed start <c>tag − duration</c> and the raw tag, each minus the file-name
/// start — kept apart from the tag check.</summary>
public sealed record TagNameDifference(string VideoName, long? TagEndZeroMinusFileNameTicks, long? RawTagMinusFileNameTicks);

/// <summary>Descriptive: the evaluation without the matches of one video (the denominator stays the same).</summary>
public sealed record VideoSensitivity(string VideoName, int RemovedMatches, int WithinTau, long? KHatTicks);

/// <summary>A climb of the FIT outside the evaluated ones (C1): reported, never counted.</summary>
public sealed record OutsideClimb(int ClimbNumber, string Decision);

/// <summary>The full result of one evaluation.</summary>
public sealed record StudyEvaluationReport(
    EvaluationProtocol Protocol,
    int Revision,
    string RevisionContentHash,
    string InputsHash,
    MarkingRule? MarkingRule,
    IReadOnlyList<ClimbResult> Climbs,
    ClockEstimate Estimate,
    ClockEstimate ReferenceTagMarksStart,
    DriftResult Drift,
    IReadOnlyList<TagCheckResult> TagCheck,
    IReadOnlyList<TagNameDifference> TagNameDifferences,
    IReadOnlyList<VideoSensitivity> Sensitivity,
    IReadOnlyList<OutsideClimb> Outside,
    ConditionState AtLeastRequired,
    ConditionState AllExplained,
    ConditionState NoDrift,
    B6Verdict Verdict)
{
    public int Denominator => Climbs.Count;
    public int WithinTau => Climbs.Count(c => c.Status == ClimbStatus.WithinTau);
    public int Outliers => Climbs.Count(c => c.Status == ClimbStatus.Outlier);
    public int NotComputed => Climbs.Count(c => c.Status == ClimbStatus.NotComputed);
    public int Excluded => Climbs.Count(c => c.Status == ClimbStatus.Excluded);
}

/// <summary>The verdict of a recorded evaluation with the explanations added to it.</summary>
public sealed record ExplainedClimb(int ClimbNumber, ClimbStatus Status, ExplanationFamily Needs, bool Explained, string? Problem);

public sealed record FinalVerdict(B6Verdict Verdict, ConditionState AllExplained, IReadOnlyList<ExplainedClimb> Climbs);

/// <summary>
/// The evaluation of D050 B6 under <see cref="EvaluationProtocol"/>: <c>k_i = W_split_i − (Z(M_i) + s_i)</c> with
/// <c>Z = tag − duration</c> and <c>s_i</c> the marked start; <c>K̂ = median(k_i)</c>, its MAD; the leave-one-out
/// residual <c>r_i = k_i − median(k_j, j ≠ i)</c>, within τ when <c>|r_i| ≤ τ</c>; the Theil–Sen drift against τ / 2.
/// The median of an even count is the midpoint of the two middle values rounded down (towards −∞) to a tick. All 13
/// evaluated climbs stay in the denominator: excluded, not computed and outliers are «not within τ».
/// </summary>
public static class StudyEvaluator
{
    public static StudyEvaluationReport Evaluate(StudyRevision revision, EvaluationProtocol protocol)
    {
        var content = revision.Content;
        var parameters = StudyCanonicalJson.Value(w => StudyCanonicalJson.WriteProtocol(w, protocol));
        var inputsHash = StudyCanonicalJson.TextHash(StudyCanonicalJson.Value(w => StudyCanonicalJson.WriteInputs(w, revision.ContentHash, protocol)));

        var drafts = protocol.EvaluatedClimbs.Select(n => Draft(content, n)).ToList();
        var climbs = Classify(drafts, protocol.TauTicks, exceptVideo: null);

        var computed = climbs.Where(c => c.KTicks is not null).ToList();
        var estimate = Estimate(computed.Select(c => c.KTicks!.Value).ToList());
        var reference = Estimate(drafts.Where(d => d.ReferenceK is not null && d.Status is null).Select(d => d.ReferenceK!.Value).ToList());
        var drift = Drift(computed.Where(c => c.Status is ClimbStatus.WithinTau or ClimbStatus.Outlier).ToList(), protocol.DriftLimitTicks);

        var sensitivity = computed.Where(c => c.Status is ClimbStatus.WithinTau or ClimbStatus.Outlier)
            .GroupBy(c => (c.VideoKey!.Value, c.VideoName!))
            .OrderBy(g => g.Key.Item2, StringComparer.Ordinal)
            .Select(g =>
            {
                var without = Classify(drafts, protocol.TauTicks, exceptVideo: g.Key.Item1);
                var kept = without.Where(c => c.Status is ClimbStatus.WithinTau or ClimbStatus.Outlier).Select(c => c.KTicks!.Value).ToList();
                return new VideoSensitivity(g.Key.Item2, g.Count(), without.Count(c => c.Status == ClimbStatus.WithinTau), Estimate(kept).KHatTicks);
            })
            .ToList();

        var within = climbs.Count(c => c.Status == ClimbStatus.WithinTau);
        var atLeast = within >= protocol.RequiredWithinTau ? ConditionState.Met : ConditionState.NotMet;
        var explained = climbs.All(c => c.Needs == ExplanationFamily.None) ? ConditionState.Met : ConditionState.PendingExplanations;
        var noDrift = drift.State switch
        {
            DriftState.Within => ConditionState.Met,
            DriftState.Beyond => ConditionState.NotMet,
            _ => ConditionState.NotEstablished,
        };

        return new StudyEvaluationReport(protocol, revision.Number, revision.ContentHash, inputsHash, content.MarkingRule, climbs,
            estimate, reference, drift, TagCheck(content, protocol), TagNameDifferences(content, protocol), sensitivity,
            Outside(content, protocol), atLeast, explained, noDrift, Verdict(atLeast, explained, noDrift));
    }

    /// <summary>Met only with all three conditions met; not met when condition 1 or 3 is not met (or 3 not established);
    /// otherwise waiting for the explanations of condition 2.</summary>
    public static B6Verdict Verdict(ConditionState atLeast, ConditionState explained, ConditionState noDrift) =>
        atLeast != ConditionState.Met || noDrift != ConditionState.Met ? B6Verdict.NotMet
        : explained == ConditionState.Met ? B6Verdict.Met
        : B6Verdict.PendingExplanations;

    /// <summary>
    /// The verdict of a stored report with the explanations in force for its evaluation: an outlier is explained by an
    /// explanation of the deviation family, a climb not computed by one of the absence family, both for the report's hash;
    /// «unexplained» or an explanation of the other family never explains. The report is read, never recomputed.
    /// </summary>
    public static FinalVerdict FinalVerdict(string report, string reportHash, long evaluationSeq, IEnumerable<CaseExplanation> active)
    {
        using var doc = JsonDocument.Parse(report);
        var root = doc.RootElement;
        var conditions = root.GetProperty("conditions");
        var atLeast = Parse<ConditionState>(conditions.GetProperty("atLeastRequired").GetString()!);
        var noDrift = Parse<ConditionState>(conditions.GetProperty("noDrift").GetString()!);
        var explanations = active.Where(e => e.EvaluationSeq == evaluationSeq && e.ReportHash == reportHash).ToList();

        var climbs = new List<ExplainedClimb>();
        foreach (var c in root.GetProperty("climbs").EnumerateArray())
        {
            var number = c.GetProperty("climbNumber").GetInt32();
            var status = Parse<ClimbStatus>(c.GetProperty("status").GetString()!);
            var needs = Parse<ExplanationFamily>(c.GetProperty("needs").GetString()!);
            if (needs == ExplanationFamily.None)
            {
                climbs.Add(new ExplainedClimb(number, status, needs, true, null));
                continue;
            }
            var mine = c.TryGetProperty("climb", out var id) ? explanations.Where(e => Same(e.Climb, id)).ToList() : new List<CaseExplanation>();
            var fits = mine.Any(e => CaseExplanation.FamilyOf(e.Category) == needs);
            var problem = fits ? null
                : mine.Count == 0 ? "no explanation"
                : mine.All(e => e.Category == ExplanationCategory.Unexplained) ? "recorded as unexplained"
                : "the explanation's category does not fit the status";
            climbs.Add(new ExplainedClimb(number, status, needs, fits, problem));
        }
        var explained = climbs.All(c => c.Explained) ? ConditionState.Met : ConditionState.PendingExplanations;
        return new FinalVerdict(Verdict(atLeast, explained, noDrift), explained, climbs);
    }

    /// <summary>A climb as a stored report states it: number, identity (absent when not in the FIT), status, needs.</summary>
    public sealed record ReportedClimb(int ClimbNumber, ClimbIdentity? Climb, ClimbStatus Status, ExplanationFamily Needs);

    /// <summary>The evaluated climbs of a stored report, read — never recomputed.</summary>
    public static IReadOnlyList<ReportedClimb> ReportedClimbs(string report)
    {
        using var doc = JsonDocument.Parse(report);
        return doc.RootElement.GetProperty("climbs").EnumerateArray().Select(c => new ReportedClimb(
            c.GetProperty("climbNumber").GetInt32(),
            c.TryGetProperty("climb", out var id)
                ? new ClimbIdentity(id.GetProperty("climbKey").GetInt32(), id.GetProperty("climbNumber").GetInt32(),
                    DateTimeOffset.ParseExact(id.GetProperty("climbStartUtc").GetString()!, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))
                : null,
            Parse<ClimbStatus>(c.GetProperty("status").GetString()!),
            Parse<ExplanationFamily>(c.GetProperty("needs").GetString()!))).ToList();
    }

    /// <summary>
    /// Why an explanation does not fit the climb's status in a B6 report (Step 35.3b2d), or null when it fits: the climb
    /// must be one of the evaluated climbs; a climb within τ or excluded takes none; an outlier takes the deviation family,
    /// a climb not computed the absence family — either may take «unexplained», recorded honestly but never counted.
    /// </summary>
    public static StudyRefusal? ExplanationProblem(string report, ClimbIdentity climb, ExplanationCategory category)
    {
        if (ReportedClimbs(report).FirstOrDefault(c => c.Climb == climb) is not { } reported) return StudyRefusal.ClimbNotEvaluated;
        if (reported.Needs == ExplanationFamily.None) return StudyRefusal.ExplanationNotAccepted;
        return category == ExplanationCategory.Unexplained || CaseExplanation.FamilyOf(category) == reported.Needs
            ? null
            : StudyRefusal.ExplanationDoesNotFit;
    }

    private static bool Same(ClimbIdentity climb, JsonElement id) =>
        id.GetProperty("climbKey").GetInt32() == climb.ClimbKey
        && id.GetProperty("climbNumber").GetInt32() == climb.ClimbNumber
        && id.GetProperty("climbStartUtc").GetString() == StudyCanonicalJson.Instant(climb.ClimbStartUtc);

    private static T Parse<T>(string name) where T : struct, Enum => Enum.Parse<T>(name, ignoreCase: true);

    // ---- one climb ---------------------------------------------------------------------------------------------------

    /// <summary>A climb before the residuals: decided (excluded / not computed) or with its <c>k</c>.</summary>
    private sealed record ClimbDraft(int Number, ClimbIdentity? Climb, ClimbStatus? Status, NotComputedReason? Reason,
        Exclusion? Exclusion, ClimbMark? Mark, StudyVideo? Video, DateTimeOffset? Zero, long? K, long? ReferenceK);

    private static ClimbDraft Draft(StudyContent content, int number)
    {
        var climb = content.Fit.Climbs.Where(c => c.ClimbNumber == number).OrderBy(c => c.SegmentIndex).FirstOrDefault();
        if (climb is null) return new(number, null, ClimbStatus.NotComputed, NotComputedReason.NotInFit, null, null, null, null, null, null);
        var identity = climb.Identity;

        var exclusion = content.Exclusions.FirstOrDefault(e => e.IsActive && e.Climb == identity);
        if (exclusion is not null) return new(number, identity, ClimbStatus.Excluded, null, exclusion, null, null, null, null, null);

        var marks = content.ConfirmationsInForce().Where(x => x.Climb == identity).Select(x => x.Mark).ToList();
        if (marks.Count == 0) return new(number, identity, ClimbStatus.NotComputed, NotComputedReason.NoConfirmedMatch, null, null, null, null, null, null);
        if (marks.Count > 1) return new(number, identity, ClimbStatus.NotComputed, NotComputedReason.ConflictingConfirmations, null, null, null, null, null, null);

        var mark = marks[0];
        var video = content.Video(mark.VideoKey)!;
        var candidates = Candidates(video, null);
        var k = candidates.TagMarksEnd is { } zero ? Watch(identity) - (zero.UtcTicks + mark.StartSourceTicks) : (long?)null;
        var reference = candidates.TagMarksStart is { } start ? Watch(identity) - (start.UtcTicks + mark.StartSourceTicks) : (long?)null;
        return k is null
            ? new(number, identity, ClimbStatus.NotComputed, NotComputedReason.RecordingStartUnknown, null, mark, video, null, null, reference)
            : new(number, identity, null, null, null, mark, video, candidates.TagMarksEnd, k, reference);
    }

    private static long Watch(ClimbIdentity climb) => climb.ClimbStartUtc.UtcTicks;

    private static RecordingZeroCandidates Candidates(StudyVideo video, TimeSpan? offset) =>
        RecordingZeroEstimator.Candidates(video.OriginalFileName, new MediaTime(video.DurationTicks),
            !video.TagProbed ? null
            : video.TagRaw is { } tag && video.TagOrigin is { } origin ? RecordingTimeProbe.Found(tag, origin)
            : video.TagRaw is { } bare ? new RecordingTimeProbe(bare, null)
            : RecordingTimeProbe.Absent,
            offset);

    /// <summary>The residuals of the drafts' computed matches (all but <paramref name="exceptVideo"/>'s), and the statuses.</summary>
    private static List<ClimbResult> Classify(IReadOnlyList<ClimbDraft> drafts, long tau, Guid? exceptVideo)
    {
        bool Counts(ClimbDraft d) => d.Status is null && d.Video!.VideoKey != exceptVideo;
        var ks = drafts.Where(Counts).Select(d => d.K!.Value).ToList();

        return drafts.Select(d =>
        {
            if (d.Status is not null || !Counts(d))
            {
                var reason = d.Status is null ? NotComputedReason.NoConfirmedMatch : d.Reason;   // left out in a sensitivity run
                return Result(d, d.Status ?? ClimbStatus.NotComputed, reason, null);
            }
            if (ks.Count < 2) return Result(d, ClimbStatus.NotComputed, NotComputedReason.TooFewMatches, null);
            var others = new List<long>(ks);
            others.Remove(d.K!.Value);
            var r = d.K!.Value - Median(others);
            return Result(d, Math.Abs((decimal)r) <= tau ? ClimbStatus.WithinTau : ClimbStatus.Outlier, null, r);
        }).ToList();
    }

    private static ClimbResult Result(ClimbDraft d, ClimbStatus status, NotComputedReason? reason, long? residual) => new(
        d.Number, d.Climb, status, status == ClimbStatus.NotComputed ? reason : null,
        d.Exclusion?.Category, d.Exclusion?.Reason, d.Mark?.MarkId, d.Video?.VideoKey, d.Video?.OriginalFileName,
        d.Mark?.StartSourceTicks, d.Zero, d.K, residual,
        d.Video is { FrameRateStatus: FrameRateState.Cfr, FrameRate: { } rate } ? rate : null);

    // ---- statistics --------------------------------------------------------------------------------------------------

    /// <summary>The median in ticks; an even count gives the midpoint of the two middle values rounded towards −∞.</summary>
    public static long Median(IReadOnlyCollection<long> values)
    {
        if (values.Count == 0) throw new ArgumentException("no values", nameof(values));
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        if (sorted.Count % 2 == 1) return sorted[mid];
        var sum = (Int128)sorted[mid - 1] + sorted[mid];
        return (long)(sum >= 0 ? sum / 2 : -((-sum + 1) / 2));
    }

    private static ClockEstimate Estimate(IReadOnlyList<long> ks)
    {
        if (ks.Count == 0) return new(null, null, 0);
        var kHat = Median(ks);
        return new(kHat, Median(ks.Select(k => (long)Math.Abs((decimal)k - kHat)).ToList()), ks.Count);
    }

    /// <summary>The Theil–Sen drift over the computed matches, exactly: the median of
    /// <c>(k_j − k_i) · span / (W_j − W_i)</c> over the pairs with different split starts.</summary>
    private static DriftResult Drift(IReadOnlyList<ClimbResult> computed, long limit)
    {
        var points = computed.Select(c => (W: c.Climb!.Value.ClimbStartUtc.UtcTicks, K: c.KTicks!.Value)).ToList();
        if (points.Count < 2) return new(DriftState.NotEstablished, null, null, 0);
        var span = points.Max(p => p.W) - points.Min(p => p.W);
        var slopes = new List<(BigInteger Num, BigInteger Den)>();
        for (var i = 0; i < points.Count; i++)
            for (var j = i + 1; j < points.Count; j++)
            {
                var dw = (BigInteger)points[j].W - points[i].W;
                if (dw.IsZero) continue;
                var num = ((BigInteger)points[j].K - points[i].K) * span;
                slopes.Add(dw.Sign < 0 ? (-num, -dw) : (num, dw));
            }
        if (slopes.Count == 0) return new(DriftState.NotEstablished, null, span, 0);

        slopes.Sort((a, b) => (a.Num * b.Den).CompareTo(b.Num * a.Den));
        var m = slopes.Count / 2;
        (BigInteger Num, BigInteger Den) median = slopes.Count % 2 == 1
            ? slopes[m]
            : (slopes[m - 1].Num * slopes[m].Den + slopes[m].Num * slopes[m - 1].Den, 2 * slopes[m - 1].Den * slopes[m].Den);
        var beyond = BigInteger.Abs(median.Num) > limit * median.Den;
        return new(beyond ? DriftState.Beyond : DriftState.Within, RoundHalfAwayFromZero(median.Num, median.Den), span, slopes.Count);
    }

    private static long RoundHalfAwayFromZero(BigInteger num, BigInteger den)
    {
        var q = BigInteger.DivRem(BigInteger.Abs(num), den, out var rem);
        if (rem * 2 >= den) q += 1;
        return (long)(num.Sign < 0 ? -q : q);
    }

    // ---- the tag check and the descriptive tables --------------------------------------------------------------------

    private static IReadOnlyList<TagCheckResult> TagCheck(StudyContent content, EvaluationProtocol protocol) =>
        protocol.TagCheckVideos.Select(name =>
        {
            var video = content.Videos.FirstOrDefault(v => v.OriginalFileName == name && v.Retired is null);
            if (video is null) return new TagCheckResult(name, null, null, null, null, null, null, null, "not in the revision");
            var c = Candidates(video, protocol.FileNameOffset);
            var problem = c.TagStatus switch
            {
                RecordingTimeTagStatus.NotProbed => "not probed",
                RecordingTimeTagStatus.Absent => "no tag",
                RecordingTimeTagStatus.Unrecognised => "tag not recognised",
                _ => c.TagMarksEnd is null ? "no duration" : c.FromFileName is null ? "no time in the file name" : null,
            };
            var diff = problem is null ? c.TagMarksEnd!.Value.UtcTicks - c.FromFileName!.Value.UtcTicks : (long?)null;
            return new TagCheckResult(name, video.VideoKey, video.TagRaw, video.DurationTicks, c.TagMarksEnd, c.FromFileName, diff,
                diff is { } d ? Math.Abs((decimal)d) <= protocol.TagThresholdTicks : null, problem);
        }).ToList();

    private static IReadOnlyList<TagNameDifference> TagNameDifferences(StudyContent content, EvaluationProtocol protocol) =>
        content.Videos.Where(v => v.Retired is null).OrderBy(v => v.OriginalFileName, StringComparer.Ordinal).Select(v =>
        {
            var c = Candidates(v, protocol.FileNameOffset);
            var name = c.FromFileName?.UtcTicks;
            return new TagNameDifference(v.OriginalFileName,
                c.TagMarksEnd is { } end && name is { } n1 ? end.UtcTicks - n1 : null,
                c.TagUtc is { } raw && name is { } n2 ? raw.UtcTicks - n2 : null);
        }).ToList();

    private static IReadOnlyList<OutsideClimb> Outside(StudyContent content, EvaluationProtocol protocol) =>
        content.Fit.Climbs.Where(c => !protocol.EvaluatedClimbs.Contains(c.ClimbNumber)).OrderBy(c => c.SegmentIndex).Select(c =>
        {
            var decisions = content.Decisions.Where(d => d.Climb == c.Identity && content.Mark(d.MarkId) is { IsActive: true }).ToList();
            var decision = decisions.Count > 0 ? string.Join(",", decisions.Select(d => StudyCanonicalJson.Name(d.State)).Distinct()) : "no confirmed or rejected mark";
            return new OutsideClimb(c.ClimbNumber, decision + "; outside B6");
        }).ToList();

    // ---- formatting for the report -----------------------------------------------------------------------------------

    /// <summary>Ticks as milliseconds, exactly (1 tick = 0.0001 ms).</summary>
    public static string Milliseconds(long ticks) =>
        ((decimal)ticks / 10_000m).ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>Ticks as frames at a confirmed constant rate, to 3 decimals, half away from zero.</summary>
    public static string Frames(long ticks, StudyRate rate) =>
        Math.Round((decimal)ticks * rate.Numerator / ((decimal)rate.Denominator * 10_000_000m), 3, MidpointRounding.AwayFromZero)
            .ToString("0.000", CultureInfo.InvariantCulture);
}
