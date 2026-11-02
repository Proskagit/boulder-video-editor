using System.Text;
using System.Text.Json;

namespace AiVideoEditor.Core.Telemetry.Research;

/// <summary>
/// The canonical form of an evaluation (Step 35.3b2c): the protocol (the record's parameters), the inputs behind the
/// inputs hash and the report — by the rules of the study's canonical JSON (ordinal member order, integers only; the
/// milliseconds and frames of a residual as invariant strings) — and the report as CSV (one row per evaluated climb and
/// per climb outside them, invariant culture, LF line ends).
/// </summary>
public static partial class StudyCanonicalJson
{
    /// <summary>Why a residual has no frames (Step 35.3b2a: frames only at a confirmed constant rate).</summary>
    private const string NoFrames = "no frames: the video's constant frame rate is not confirmed";

    private static Member Ints(IEnumerable<int> items) => Array(items, (w, i) => w.WriteNumberValue(i));
    private static Member Strs(IEnumerable<string> items) => Array(items, (w, s) => w.WriteStringValue(s));

    public static void WriteProtocol(Utf8JsonWriter w, EvaluationProtocol p) => Object(w,
        ("algorithmVersion", Str(p.AlgorithmVersion)),
        ("denominator", Num(p.EvaluatedClimbs.Count)),
        ("driftLimitTicks", Num(p.DriftLimitTicks)),
        ("evaluatedClimbs", Ints(p.EvaluatedClimbs)),
        ("fileNameOffsetMinutes", Num((long)p.FileNameOffset.TotalMinutes)),
        ("framesRule", Str("only for a video whose constant frame rate is confirmed; 3 decimals, half away from zero")),
        ("hypothesis", Str("tagMarksEnd: Z = tag - duration")),
        ("medianRule", Str("even count: the midpoint of the two middle values rounded towards -infinity to a tick")),
        ("reference", Str("tagMarksStart: Z = tag (descriptive only)")),
        ("requiredWithinTau", Num(p.RequiredWithinTau)),
        ("residual", Str("leave-one-out: r_i = k_i - median(k_j, j != i); within tau when |r_i| <= tau")),
        ("startOfClimb", Str("the mark's start (the end is not part of the criterion)")),
        ("tagCheckThresholdNote", Str(p.TagThresholdNote)),
        ("tagCheckThresholdTicks", Num(p.TagThresholdTicks)),
        ("tagCheckVideos", Strs(p.TagCheckVideos)),
        ("tauTicks", Num(p.TauTicks)));

    public static void WriteInputs(Utf8JsonWriter w, string revisionContentHash, EvaluationProtocol p) => Object(w,
        ("parameters", w2 => WriteProtocol(w2, p)),
        ("revisionContentHash", Str(revisionContentHash)));

    private static Member Estimate(ClockEstimate e) => w => Object(w,
        ("count", Num(e.Count)),
        ("kHatTicks", Num(e.KHatTicks)),
        ("madTicks", Num(e.MadTicks)));

    private static void WriteClimbResult(Utf8JsonWriter w, ClimbResult c) => Object(w,
        ("climb", c.Climb is { } id ? (Member)(w2 => WriteIdentity(w2, id)) : null),
        ("climbNumber", Num(c.ClimbNumber)),
        ("exclusionCategory", En(c.ExclusionCategory)),
        ("exclusionReason", Str(c.ExclusionReason, true)),
        ("kTicks", Num(c.KTicks)),
        ("markId", Id(c.MarkId)),
        ("markStartTicks", Num(c.MarkStartTicks)),
        ("needs", En(c.Needs)),
        ("reason", En(c.Reason)),
        ("residualFrames", c.ResidualTicks is { } rf && c.ConfirmedFrameRate is { } rate ? Str(StudyEvaluator.Frames(rf, rate)) : null),
        ("residualFramesNote", c.ResidualTicks is not null && c.ConfirmedFrameRate is null ? Str(NoFrames) : null),
        ("residualMs", c.ResidualTicks is { } rm ? Str(StudyEvaluator.Milliseconds(rm)) : null),
        ("residualTicks", Num(c.ResidualTicks)),
        ("status", En(c.Status)),
        ("videoKey", Id(c.VideoKey)),
        ("videoName", Str(c.VideoName, true)),
        ("zeroUtc", c.Zero is { } z ? At(z) : null));

    private static void WriteTagCheck(Utf8JsonWriter w, TagCheckResult t) => Object(w,
        ("differenceTicks", Num(t.DifferenceTicks)),
        ("durationTicks", Num(t.DurationTicks)),
        ("problem", Str(t.Problem, true)),
        ("tagRaw", Str(t.TagRaw, true)),
        ("videoKey", Id(t.VideoKey)),
        ("videoName", Str(t.VideoName)),
        ("withinThreshold", Bool(t.WithinThreshold)),
        ("zeroFromFileNameUtc", t.ZeroFromFileName is { } n ? At(n) : null),
        ("zeroFromTagUtc", t.ZeroFromTag is { } z ? At(z) : null));

    public static void WriteReport(Utf8JsonWriter w, StudyEvaluationReport r) => Object(w,
        ("algorithmVersion", Str(r.Protocol.AlgorithmVersion)),
        ("climbs", Array(r.Climbs, WriteClimbResult)),
        ("conditions", w2 => Object(w2,
            ("allExplained", En(r.AllExplained)),
            ("atLeastRequired", En(r.AtLeastRequired)),
            ("noDrift", En(r.NoDrift)))),
        ("counts", w2 => Object(w2,
            ("denominator", Num(r.Denominator)),
            ("excluded", Num(r.Excluded)),
            ("notComputed", Num(r.NotComputed)),
            ("outliers", Num(r.Outliers)),
            ("withinTau", Num(r.WithinTau)))),
        ("drift", w2 => Object(w2,
            ("driftTicks", Num(r.Drift.DriftTicks)),
            ("pairs", Num(r.Drift.Pairs)),
            ("spanTicks", Num(r.Drift.SpanTicks)),
            ("state", En(r.Drift.State)))),
        ("estimate", Estimate(r.Estimate)),
        ("inputsHash", Str(r.InputsHash)),
        ("markingRule", r.MarkingRule is { } rule ? (Member)(w2 => WriteMarkingRule(w2, rule)) : null),
        ("notes", Strs(new[]
        {
            "descriptive tables (tagNameDifferences, sensitivity, reference) never change the verdict",
            "the marks carry the error of a manual visual choice by the marking rule",
            "the tag check is reported per file and is not part of the B6 verdict",
        })),
        ("outside", Array(r.Outside, (w2, o) => Object(w2, ("climbNumber", Num(o.ClimbNumber)), ("decision", Str(o.Decision))))),
        ("parameters", w2 => WriteProtocol(w2, r.Protocol)),
        ("reference", w2 => Object(w2, ("tagMarksStart", Estimate(r.ReferenceTagMarksStart)))),
        ("revision", Num(r.Revision)),
        ("revisionContentHash", Str(r.RevisionContentHash)),
        ("sensitivity", Array(r.Sensitivity, (w2, s) => Object(w2,
            ("kHatTicks", Num(s.KHatTicks)),
            ("omittedVideo", Str(s.VideoName)),
            ("removedMatches", Num(s.RemovedMatches)),
            ("withinTau", Num(s.WithinTau))))),
        ("tagCheck", Array(r.TagCheck, WriteTagCheck)),
        ("tagNameDifferences", Array(r.TagNameDifferences, (w2, d) => Object(w2,
            ("rawTagMinusFileNameTicks", Num(d.RawTagMinusFileNameTicks)),
            ("tagEndZeroMinusFileNameTicks", Num(d.TagEndZeroMinusFileNameTicks)),
            ("videoName", Str(d.VideoName))))),
        ("verdict", En(r.Verdict)));

    /// <summary>The canonical JSON of the report — what the evaluation record stores and its report hash covers.</summary>
    public static string Report(StudyEvaluationReport report) => Value(w => WriteReport(w, report));

    /// <summary>The report as CSV: a header, one row per evaluated climb, then the climbs outside them.</summary>
    public static string EvaluationCsv(StudyEvaluationReport r)
    {
        var csv = new StringBuilder();
        void Row(params string?[] cells) => csv.Append(string.Join(",", cells.Select(Cell))).Append('\n');
        Row("climb", "status", "reason", "needs", "video", "markStartTicks", "zeroUtc", "kTicks", "residualTicks", "residualMs",
            "residualFrames", "residualFramesNote", "exclusion");
        foreach (var c in r.Climbs)
            Row(c.ClimbNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), Name(c.Status), c.Reason is { } re ? Name(re) : null,
                Name(c.Needs), c.VideoName, Inv(c.MarkStartTicks), c.Zero is { } z ? Instant(z) : null, Inv(c.KTicks), Inv(c.ResidualTicks),
                c.ResidualTicks is { } rm ? StudyEvaluator.Milliseconds(rm) : null,
                c.ResidualTicks is { } rf && c.ConfirmedFrameRate is { } rate ? StudyEvaluator.Frames(rf, rate) : null,
                c.ResidualTicks is not null && c.ConfirmedFrameRate is null ? NoFrames : null,
                c.ExclusionCategory is { } ec ? $"{Name(ec)}: {c.ExclusionReason}" : null);
        foreach (var o in r.Outside)
            Row(o.ClimbNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), "outside", null, null, null, null, null, null,
                null, null, null, null, o.Decision);
        return csv.ToString();
    }

    private static string? Inv(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Cell(string? value) =>
        value is null ? "" : value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
}
