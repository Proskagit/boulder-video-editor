using System.Text.Json;
using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.Media;
using AiVideoEditor.Project.Research;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Telemetry.LocalTests;

/// <summary>A test that runs only when its environment variable names a study — an explicit request; otherwise skipped.</summary>
public sealed class StudyVariableFactAttribute : FactAttribute
{
    public StudyVariableFactAttribute(string variable) => Skip = SkipReason(variable, Environment.GetEnvironmentVariable(variable));

    public static string? SkipReason(string variable, string? value) => string.IsNullOrWhiteSpace(value)
        ? $"Runs only on request (Step 35.3d): set {variable}."
        : null;
}

/// <summary>
/// The tools of Step 35.3d (D050 B6, amendment of 2026-11-02): evaluate the frozen reference study, then record the owner's
/// explanations of one evaluation. Run explicitly, one at a time:
/// <code>
/// $env:AIVE_COROS_EVALUATE_STUDY = "&lt;set folder&gt;\Болдеринг20260927100239.syncstudy.json"
/// dotnet test tests/Telemetry.LocalTests/Telemetry.LocalTests.csproj --filter "FullyQualifiedName~EvaluateReferenceStudy"
///
/// $env:AIVE_COROS_EXPLAIN_STUDY = "&lt;the same study&gt;"
/// $env:AIVE_COROS_EXPLANATIONS = "&lt;explanations.json&gt;"   # {"evaluationSeq": N, "explanations": [{"climb": 7, "category": "insufficientevidence", "evidence": "…"}]}
/// dotnet test tests/Telemetry.LocalTests/Telemetry.LocalTests.csproj --filter "FullyQualifiedName~ExplainReferenceStudy"
/// </code>
/// The evaluation hashes the whole set first and records nothing on any problem; it never overwrites an export file and
/// never changes an earlier record. The study and its exports are personal data and never committed.
/// </summary>
public class EvaluateReferenceStudyTests
{
    public const string EvaluateVariable = "AIVE_COROS_EVALUATE_STUDY";
    public const string ExplainVariable = "AIVE_COROS_EXPLAIN_STUDY";
    public const string ExplanationsVariable = "AIVE_COROS_EXPLANATIONS";

    private readonly ITestOutputHelper _output;

    public EvaluateReferenceStudyTests(ITestOutputHelper output) => _output = output;

    private static SyncStudyService Service() => new(new FileFingerprintService(), () => DateTimeOffset.UtcNow);

    [StudyVariableFact(EvaluateVariable)]
    public async Task EvaluateReferenceStudy()
    {
        var service = Service();
        var opened = await service.OpenStudyAsync(Environment.GetEnvironmentVariable(EvaluateVariable)!);
        Assert.True(opened.Ok, opened.Problem);

        var run = await service.EvaluateAsync(EvaluationProtocol.B6);
        foreach (var problem in run.Problems) _output.WriteLine($"  ✗ {problem}");
        Assert.True(run.Recorded, "The evaluation was refused; nothing was written.");

        var r = run.Report!;
        _output.WriteLine($"Evaluation {run.Record!.Seq} of revision {r.Revision} ({r.RevisionContentHash}), {r.Protocol.AlgorithmVersion}");
        _output.WriteLine($"Report hash {run.Record.ReportHash}; inputs hash {r.InputsHash}");
        _output.WriteLine($"Verdict: {r.Verdict} — (1) {r.AtLeastRequired}: {r.WithinTau} of {r.Denominator} within τ; (2) {r.AllExplained}; (3) {r.NoDrift}");
        _output.WriteLine($"K̂ {r.Estimate.KHatTicks} ticks, MAD {r.Estimate.MadTicks}, n {r.Estimate.Count}; drift {r.Drift.State} {r.Drift.DriftTicks}");
        foreach (var c in r.Climbs)
            _output.WriteLine($"  C{c.ClimbNumber}: {c.Status}{(c.Reason is { } reason ? $" ({reason})" : "")}" +
                              $"{(c.ResidualTicks is { } res ? $" r = {StudyEvaluator.Milliseconds(res)} ms" : "")}" +
                              $"{(c.Needs != ExplanationFamily.None ? $" — needs {c.Needs}" : "")}");
        foreach (var t in r.TagCheck)
            _output.WriteLine($"  tag check {t.VideoName}: {(t.DifferenceTicks is { } d ? $"{StudyEvaluator.Milliseconds(d)} ms, within: {t.WithinThreshold}" : t.Problem)}");
        _output.WriteLine($"Exports: {run.JsonPath}; {run.CsvPath}");
        if (run.ExportProblem is { } exportProblem) _output.WriteLine($"  ! {exportProblem}");
    }

    [StudyVariableFact(ExplainVariable)]
    public async Task ExplainReferenceStudy()
    {
        var path = Environment.GetEnvironmentVariable(ExplanationsVariable);
        Assert.False(string.IsNullOrWhiteSpace(path), $"Set {ExplanationsVariable} to the explanations file.");
        var (seq, explanations) = ReadExplanations(await File.ReadAllTextAsync(path!));

        var service = Service();
        var opened = await service.OpenStudyAsync(Environment.GetEnvironmentVariable(ExplainVariable)!);
        Assert.True(opened.Ok, opened.Problem);

        var run = await service.ExplainAsync(seq, explanations);
        foreach (var problem in run.Problems) _output.WriteLine($"  ✗ {problem}");
        Assert.True(run.Recorded, "The explanations were refused; nothing was recorded.");

        _output.WriteLine($"Evaluation {seq}: verdict {run.Verdict!.Verdict}; (2) {run.Verdict.AllExplained}");
        foreach (var c in run.Verdict.Climbs.Where(c => c.Needs != ExplanationFamily.None))
            _output.WriteLine($"  C{c.ClimbNumber}: {c.Status}, needs {c.Needs}: {(c.Explained ? "explained" : c.Problem)}");
    }

    /// <summary>Reads <c>{"evaluationSeq": N, "explanations": [{"climb": n, "category": "…", "evidence": "…"}]}</c>.</summary>
    public static (long Seq, IReadOnlyList<ExplanationInput> Explanations) ReadExplanations(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = root.GetProperty("explanations").EnumerateArray().Select(e => new ExplanationInput(
            e.GetProperty("climb").GetInt32(),
            Enum.Parse<ExplanationCategory>(e.GetProperty("category").GetString()!, ignoreCase: true),
            e.GetProperty("evidence").GetString() ?? "")).ToList();
        return (root.GetProperty("evaluationSeq").GetInt64(), list);
    }

    [Fact]
    public void Without_the_variables_the_tools_are_skipped()
    {
        Assert.NotNull(StudyVariableFactAttribute.SkipReason(EvaluateVariable, null));
        Assert.NotNull(StudyVariableFactAttribute.SkipReason(ExplainVariable, "  "));
        Assert.Null(StudyVariableFactAttribute.SkipReason(EvaluateVariable, @"E:\x.syncstudy.json"));
    }

    [Fact]
    public void The_explanations_file_is_read_with_its_categories()
    {
        var (seq, list) = ReadExplanations(
            """{"evaluationSeq": 12, "explanations": [{"climb": 7, "category": "InsufficientEvidence", "evidence": "two attempts"}]}""");
        Assert.Equal(12, seq);
        Assert.Equal(new ExplanationInput(7, ExplanationCategory.InsufficientEvidence, "two attempts"), Assert.Single(list));
    }
}
