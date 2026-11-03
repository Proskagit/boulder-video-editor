using AiVideoEditor.Core.Telemetry.Research;

namespace AiVideoEditor.Project.Research;

/// <summary>
/// The outcome of an evaluation run (Step 35.3b2d): refused with its problems (nothing written), or recorded — the record
/// saved in the study, the report, and the export files; <see cref="ExportProblem"/> says why an export was not written
/// after the record had been saved (the record stays the source; its JSON export is the stored report text).
/// </summary>
public sealed record StudyEvaluationRun(
    IReadOnlyList<string> Problems,
    EvaluationRecord? Record,
    StudyEvaluationReport? Report,
    string? JsonPath,
    string? CsvPath,
    string? ExportProblem)
{
    public bool Recorded => Record is not null;

    public static StudyEvaluationRun Refused(params string[] problems) => new(problems, null, null, null, null, null);

    public static StudyEvaluationRun Refused(IReadOnlyList<string> problems) => new(problems, null, null, null, null, null);
}

/// <summary>One explanation to record: the climb by its number in the evaluated report, the category and the evidence.</summary>
public sealed record ExplanationInput(int ClimbNumber, ExplanationCategory Category, string Evidence);

/// <summary>The outcome of recording explanations: refused with its problems (nothing recorded), or recorded with the
/// verdict of the evaluation and the explanations now in force.</summary>
public sealed record StudyExplanationRun(IReadOnlyList<string> Problems, FinalVerdict? Verdict)
{
    public bool Recorded => Verdict is not null;
}
