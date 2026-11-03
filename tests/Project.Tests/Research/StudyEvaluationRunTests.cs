using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.Project.Research;
using Xunit;
using static AiVideoEditor.Project.Tests.Research.StudyTestFiles;

namespace AiVideoEditor.Project.Tests.Research;

/// <summary>
/// Step 35.3b2d: the evaluation run records a new evaluation of the latest frozen revision and writes its exports; any
/// problem of <c>EvaluationProblems</c> refuses with the study file unchanged; an export file is never overwritten; an
/// export failing after the save keeps the record; a second evaluation never changes the first; a failed save drops the
/// unsaved record. Explanations belong to one record and its report, fit the climb's status or are refused — all or none.
/// </summary>
public sealed class StudyEvaluationRunTests : IDisposable
{
    private const long S = TimeSpan.TicksPerSecond;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-eval-run", Guid.NewGuid().ToString("N"));
    private readonly StudyTestFiles _files = new();
    private readonly SyncStudyService _service;

    /// <summary>C1–C3 evaluated, two needed within τ, the tag check on <c>a.mp4</c>.</summary>
    private static readonly EvaluationProtocol P = EvaluationProtocol.B6 with
    {
        EvaluatedClimbs = new[] { 1, 2, 3 }, RequiredWithinTau = 2, TagCheckVideos = new[] { "a.mp4" },
    };

    public StudyEvaluationRunTests()
    {
        Directory.CreateDirectory(_folder);
        _files.Put(@"D:\set\a.fit", "fit");
        _files.Put(@"D:\set\a.mp4", "a");
        _service = new SyncStudyService(_files, Clock());
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string StudyPath => Path.Combine(_folder, "set.syncstudy.json");

    /// <summary>One video of 1500 s whose tag (07:07:00Z) marks its end: Z = 06:42:00Z. C2 starts 897 s after Z, C3 1181 s.
    /// A mark of C2 at 890 s gives k = 7 s; of C3 at 1174 s − extra gives k = 7 s + extra. Frozen and saved.</summary>
    private async Task<SyncStudy> FrozenAsync(long extraOnC3 = 0, bool freeze = true)
    {
        var study = WithRule(await _service.CreateAsync(@"D:\set\a.fit", Workout(), new[] { Video(@"D:\set\a.mp4", 1500) }), _service);
        var video = study.Draft.Videos[0].VideoKey;
        var m2 = _service.Editor.AddMark(video, 890 * S, 900 * S).CreatedId!.Value;
        var m3 = _service.Editor.AddMark(video, 1174 * S - extraOnC3, 1190 * S).CreatedId!.Value;
        Assert.True(_service.Editor.DecideMatch(m2, MatchState.Confirmed, study.Draft.Fit.Climbs[1].Identity, null, "").Ok);
        Assert.True(_service.Editor.DecideMatch(m3, MatchState.Confirmed, study.Draft.Fit.Climbs[2].Identity, null, "").Ok);
        if (freeze) Assert.True(_service.Freeze("rev 1").Ok);
        await _service.SaveAsync(StudyPath, null);
        return study;
    }

    private string Bytes() => File.ReadAllText(StudyPath);

    private SyncStudy Reread() => SyncStudySerializer.Deserialize(Bytes());   // the validator checks every record

    private string[] Exports() => Directory.GetFiles(_folder, "*.eval-*").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    [Fact]
    public async Task A_problem_of_the_admission_refuses_and_writes_nothing()
    {
        await FrozenAsync(freeze: false);
        var before = Bytes();
        var run = await _service.EvaluateAsync(P);
        Assert.False(run.Recorded);
        Assert.Contains("no frozen revision", run.Problems);
        Assert.Equal(before, Bytes());
        Assert.Empty(Exports());

        Assert.True(_service.Freeze("rev 1").Ok);
        await _service.SaveAsync(StudyPath, null);
        before = Bytes();
        _files.Put(@"D:\set\a.mp4", "another export");                     // the set changed since the freeze
        run = await _service.EvaluateAsync(P);
        Assert.False(run.Recorded);
        Assert.NotEmpty(run.Problems);
        Assert.Equal(before, Bytes());
        Assert.Empty(Exports());
        Assert.Empty(_service.Study!.Evaluations);
    }

    [Fact]
    public async Task An_evaluation_is_recorded_saved_and_exported()
    {
        await FrozenAsync();
        var run = await _service.EvaluateAsync(P);

        Assert.True(run.Recorded);
        Assert.Null(run.ExportProblem);
        var record = run.Record!;
        Assert.Equal(StudyCanonicalJson.Report(run.Report!), record.Report);
        Assert.Equal(StudyCanonicalJson.TextHash(record.Report), record.ReportHash);
        Assert.Equal(("coros-b6-1", 1, run.Report!.InputsHash), (record.AlgorithmVersion, record.Revision, record.InputsHash));
        Assert.Equal(_service.Study!.Revisions[0].ContentHash, record.RevisionContentHash);
        Assert.Equal((2, ClimbStatus.NotComputed), (run.Report.WithinTau, run.Report.Climbs[0].Status));   // C1: no match

        var saved = Reread();
        Assert.Equal(record, Assert.Single(saved.Evaluations));
        Assert.Equal(HistoryKind.EvaluationRecorded, saved.History[^1].Kind);
        Assert.Equal(record.Report, File.ReadAllText(run.JsonPath!));
        Assert.Equal(StudyCanonicalJson.EvaluationCsv(run.Report), File.ReadAllText(run.CsvPath!));
        Assert.Equal(new[] { $"set.eval-{record.Seq}.csv", $"set.eval-{record.Seq}.json" }, Exports());
    }

    [Fact]
    public async Task An_existing_export_file_is_never_overwritten_and_refuses_before_anything_is_recorded()
    {
        await FrozenAsync();
        var before = Bytes();
        var (json, _) = SyncStudyService.ExportPaths(StudyPath, _service.Study!.NextSeq);
        File.WriteAllText(json, "the owner's file");

        var run = await _service.EvaluateAsync(P);

        Assert.False(run.Recorded);
        Assert.Contains(run.Problems, p => p.Contains("already exists"));
        Assert.Equal("the owner's file", File.ReadAllText(json));
        Assert.Equal(before, Bytes());
        Assert.Empty(_service.Study.Evaluations);
    }

    [Fact]
    public async Task An_export_failing_after_the_save_keeps_the_record_and_overwrites_nothing()
    {
        await FrozenAsync();
        var (_, csv) = SyncStudyService.ExportPaths(StudyPath, _service.Study!.NextSeq);

        var run = await _service.EvaluateAsync(P, beforeExport: () => File.WriteAllText(csv, "appeared meanwhile"));

        Assert.True(run.Recorded);
        Assert.Contains(Path.GetFileName(csv), run.ExportProblem);
        Assert.Contains($"evaluation {run.Record!.Seq} is saved in the study", run.ExportProblem);
        Assert.Equal("appeared meanwhile", File.ReadAllText(csv));
        Assert.Equal(run.Record, Assert.Single(Reread().Evaluations));
        Assert.Equal(run.Record.Report, File.ReadAllText(run.JsonPath!));      // the other export is written
    }

    [Fact]
    public async Task A_failed_save_records_nothing()
    {
        await FrozenAsync();
        var before = Bytes();
        using (new FileStream(StudyPath, FileMode.Open, FileAccess.Read, FileShare.Read))   // the replace fails, reading works
        {
            var run = await _service.EvaluateAsync(P);
            Assert.False(run.Recorded);
            Assert.Contains(run.Problems, p => p.Contains("not saved"));
        }
        Assert.Equal(before, Bytes());
        Assert.Empty(_service.Study!.Evaluations);                           // reloaded from the file
        Assert.Empty(Exports());
    }

    [Fact]
    public async Task A_second_evaluation_is_a_new_record_and_never_changes_the_first()
    {
        await FrozenAsync();
        var first = (await _service.EvaluateAsync(P)).Record!;
        var firstJson = File.ReadAllText(SyncStudyService.ExportPaths(StudyPath, first.Seq).Json);
        var firstBytes = StudyCanonicalJson.Value(w => StudyCanonicalJson.WriteEvaluation(w, first));

        var second = (await _service.EvaluateAsync(P)).Record!;

        Assert.True(second.Seq > first.Seq);
        var saved = Reread();
        Assert.Equal(2, saved.Evaluations.Count);
        Assert.Equal(firstBytes, StudyCanonicalJson.Value(w => StudyCanonicalJson.WriteEvaluation(w, saved.Evaluation(first.Seq)!)));
        Assert.Equal(firstJson, File.ReadAllText(SyncStudyService.ExportPaths(StudyPath, first.Seq).Json));
        Assert.Equal(4, Exports().Length);
    }

    [Fact]
    public async Task Only_a_report_of_the_latest_frozen_revision_is_recorded()
    {
        await FrozenAsync();
        var report = StudyEvaluator.Evaluate(_service.Study!.Revisions[0], P);
        Assert.True(_service.Freeze("rev 2").Ok);
        Assert.Equal(StudyRefusal.NotLatestRevision, _service.Editor.RecordEvaluation(report).Refusal);
        Assert.Empty(_service.Study.Evaluations);
    }

    // ---- explanations ------------------------------------------------------------------------------------------------

    /// <summary>C1 not computed, C2 and C3 outliers (C3 30 s off), condition 1 not met.</summary>
    private async Task<EvaluationRecord> OutliersAsync()
    {
        await FrozenAsync(extraOnC3: 30 * S);
        var run = await _service.EvaluateAsync(P);
        Assert.Equal((0, 2, 1), (run.Report!.WithinTau, run.Report.Outliers, run.Report.NotComputed));
        return run.Record!;
    }

    [Fact]
    public async Task A_misfit_explanation_is_refused_and_a_batch_is_all_or_none()
    {
        var record = await OutliersAsync();
        var before = Bytes();

        foreach (var bad in new[]
                 {
                     new ExplanationInput(2, ExplanationCategory.InsufficientEvidence, "absence for an outlier"),
                     new ExplanationInput(1, ExplanationCategory.MarkingError, "deviation for a climb not computed"),
                     new ExplanationInput(9, ExplanationCategory.MarkingError, "no such climb"),
                     new ExplanationInput(2, ExplanationCategory.MarkingError, "  "),
                 })
        {
            var run = await _service.ExplainAsync(record.Seq, new[] { new ExplanationInput(1, ExplanationCategory.NoUsableFootage, "valid"), bad });
            Assert.False(run.Recorded);
            Assert.Single(run.Problems);
            Assert.Equal(before, Bytes());
            Assert.Empty(_service.Study!.Explanations);
        }
        Assert.Contains("no evaluation 999", (await _service.ExplainAsync(999, new[] { new ExplanationInput(1, ExplanationCategory.NoUsableFootage, "x") })).Problems);

        // The editor refuses a misfit as well — never stored and ignored later.
        var climb2 = _service.Study!.Draft.Fit.Climbs[1].Identity;
        Assert.Equal(StudyRefusal.ExplanationDoesNotFit,
            _service.Editor.AddExplanation(record.Seq, climb2, ExplanationCategory.InsufficientEvidence, "x").Refusal);
    }

    [Fact]
    public async Task Unexplained_is_recorded_honestly_and_never_counts()
    {
        var record = await OutliersAsync();
        var run = await _service.ExplainAsync(record.Seq, new[]
        {
            new ExplanationInput(1, ExplanationCategory.Unexplained, "nothing known yet"),
            new ExplanationInput(2, ExplanationCategory.Unexplained, "nothing known yet"),
        });
        Assert.True(run.Recorded);
        Assert.Equal(2, Reread().Explanations.Count);
        Assert.Equal(ConditionState.PendingExplanations, run.Verdict!.AllExplained);
        Assert.Equal("recorded as unexplained", run.Verdict.Climbs.Single(c => c.ClimbNumber == 2).Problem);
    }

    [Fact]
    public async Task Fitting_explanations_complete_condition_two_and_never_lift_condition_one()
    {
        var record = await OutliersAsync();
        var run = await _service.ExplainAsync(record.Seq, new[]
        {
            new ExplanationInput(1, ExplanationCategory.NoUsableFootage, "not filmed"),
            new ExplanationInput(2, ExplanationCategory.MarkingError, "late mark"),
            new ExplanationInput(3, ExplanationCategory.FileTimeAnomaly, "tag off"),
        });

        Assert.True(run.Recorded);
        Assert.Equal(ConditionState.Met, run.Verdict!.AllExplained);
        Assert.Equal(B6Verdict.NotMet, run.Verdict.Verdict);                  // 0 of the 2 required within τ
        var saved = Reread();
        Assert.All(saved.Explanations, e => Assert.Equal((record.Seq, record.ReportHash), (e.EvaluationSeq, e.ReportHash)));
    }

    [Fact]
    public async Task A_climb_within_tau_takes_no_explanation_and_explanations_belong_to_their_record()
    {
        await FrozenAsync();
        var first = (await _service.EvaluateAsync(P)).Record!;
        var within = await _service.ExplainAsync(first.Seq, new[] { new ExplanationInput(2, ExplanationCategory.MarkingError, "x") });
        Assert.Contains(within.Problems, p => p.Contains(nameof(StudyRefusal.ExplanationNotAccepted)));

        var forFirst = await _service.ExplainAsync(first.Seq, new[] { new ExplanationInput(1, ExplanationCategory.NoUsableFootage, "not filmed") });
        Assert.Equal(B6Verdict.Met, forFirst.Verdict!.Verdict);

        var second = (await _service.EvaluateAsync(P)).Record!;
        var verdictOfSecond = StudyEvaluator.FinalVerdict(second.Report, second.ReportHash, second.Seq, _service.Study!.ActiveExplanations(long.MaxValue));
        Assert.Equal(B6Verdict.PendingExplanations, verdictOfSecond.Verdict);   // the first record's explanation is not the second's
    }
}
