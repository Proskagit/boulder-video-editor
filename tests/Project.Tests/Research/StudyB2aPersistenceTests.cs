using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.Project.Research;
using Xunit;
using static AiVideoEditor.Project.Tests.Research.StudyTestFiles;

namespace AiVideoEditor.Project.Tests.Research;

/// <summary>
/// Step 35.3b2a: a study file written by Step 35.3b1 (schema 1, captured from that code) still loads with its frozen
/// revision valid and its content hashes unchanged; the new members round-trip; the snapshot takes the watch's offset and
/// the frame rates as observations; the service admits only the latest frozen revision of an unchanged, verified,
/// conflict-free set to an evaluation.
/// </summary>
public sealed class StudyB2aPersistenceTests
{
    private static string Fixture()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AiVideoEditor.sln")))
                return File.ReadAllText(Path.Combine(dir.FullName, "tests", "Project.Tests", "Research", "Fixtures", "schema1-35.3b1.syncstudy.json"));
        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void A_file_of_step_35_3b1_loads_with_its_revision_valid_and_its_hashes_unchanged()
    {
        var text = Fixture();
        var study = SyncStudySerializer.Deserialize(text);                  // the validator checks the revision hash

        var stored = JsonNode.Parse(text)!["revisions"]![0]!["contentHash"]!.GetValue<string>();
        Assert.Equal(stored, study.Revisions.Single().ContentHash);
        Assert.Equal(stored, StudyCanonicalJson.ContentHash(study.Revisions.Single().Content));
        Assert.Empty(study.Explanations);
        Assert.Empty(study.Withdrawals);
        Assert.All(study.Draft.Videos, v => Assert.Equal(CoverageState.Unknown, v.CoverageStatus));
        Assert.All(study.Draft.Videos, v => Assert.Equal(FrameRateState.Unknown, v.FrameRateStatus));
        Assert.Null(study.Draft.Fit.UtcOffsetMinutes);

        var again = SyncStudySerializer.Serialize(study);                   // saved in the new form…
        var reread = SyncStudySerializer.Deserialize(again);
        Assert.Equal(StudyCanonicalJson.ContentHash(study.Draft), StudyCanonicalJson.ContentHash(reread.Draft));
        Assert.Equal(stored, reread.Revisions.Single().ContentHash);
        Assert.Equal(again, SyncStudySerializer.Serialize(reread));         // …and stable from then on
        Assert.Contains("\"explanations\": []", again);
    }

    [Fact]
    public void A_proposal_reference_in_the_old_string_form_is_not_trusted()
    {
        var node = JsonNode.Parse(Fixture())!;
        node["draft"]!["decisions"]![0]!["proposalRef"] = "p-1";
        var error = Assert.Throws<SyncStudyFileException>(() => SyncStudySerializer.Deserialize(node.ToJsonString()));
        Assert.Equal(SyncStudyFileError.Damaged, error.Error);
    }

    private static async Task<(SyncStudyService Service, StudyTestFiles Files)> ServiceAsync(TimeSpan? offset = null, bool keepOffset = true)
    {
        var files = new StudyTestFiles();
        files.Put(@"D:\set\a.fit", "fit");
        files.Put(@"D:\set\a.mp4", "a");
        files.Put(@"D:\set\b.mp4", "b");
        var service = new SyncStudyService(files, Clock());
        var workout = Workout();
        if (!keepOffset) workout = new Core.Telemetry.ClimbingWorkout
        {
            DeviceName = workout.DeviceName, Manufacturer = workout.Manufacturer, Product = workout.Product, StartUtc = workout.StartUtc,
            EndUtc = workout.EndUtc, UtcOffset = null, ElapsedTime = workout.ElapsedTime, TimerTime = workout.TimerTime, Sport = workout.Sport,
            SubSport = workout.SubSport, AverageHeartRate = workout.AverageHeartRate, MaxHeartRate = workout.MaxHeartRate,
            HeartRate = workout.HeartRate, Pauses = workout.Pauses, Segments = workout.Segments, Warnings = workout.Warnings,
        };
        var a = Video(@"D:\set\a.mp4");
        a.Metadata!.FrameRate = new FrameRate(60, 1);
        a.Metadata.AvgFrameRate = new FrameRate(5994, 100);
        await service.CreateAsync(@"D:\set\a.fit", workout, new[] { a, Video(@"D:\set\b.mp4") });
        return (service, files);
    }

    [Fact]
    public async Task The_snapshot_takes_the_offset_and_the_frame_rates_as_observations_with_the_rate_state_unknown()
    {
        var (service, _) = await ServiceAsync();
        var study = service.Study!;
        Assert.Equal(180, study.Draft.Fit.UtcOffsetMinutes);
        var a = study.Draft.Videos.Single(v => v.OriginalFileName == "a.mp4");
        Assert.Equal((new StudyRate(60, 1), new StudyRate(2997, 50)), (a.FrameRate!.Value, a.AvgFrameRate!.Value));   // as reduced by FrameRate
        Assert.Equal(FrameRateState.Unknown, a.FrameRateStatus);
        Assert.Null(a.RateState);
        Assert.Null(study.Draft.Videos.Single(v => v.OriginalFileName == "b.mp4").FrameRate);

        var (noOffset, _) = await ServiceAsync(keepOffset: false);
        Assert.Null(noOffset.Study!.Draft.Fit.UtcOffsetMinutes);                // never filled from anywhere else
    }

    [Fact]
    public async Task Every_new_member_round_trips_to_the_same_bytes()
    {
        var (service, _) = await ServiceAsync();
        var study = service.Study!;
        var editor = service.Editor;
        var a = study.Draft.Videos[0].VideoKey;
        var b = study.Draft.Videos[1].VideoKey;
        var mark = editor.AddMark(a, 30 * TimeSpan.TicksPerSecond, null).CreatedId!.Value;
        editor.SetCoverage(a, CoverageState.HasClimbs, "climbs at 0:30");
        editor.SetCoverage(b, CoverageState.ReviewedNoClimbs, "warm-up only");
        var revision = service.Freeze("rev 1").Revision!;
        const string report = "{\"proposals\":[]}";
        var record = new EvaluationRecord(study.NextSeq, revision.Number, "sync-eval/1", new string('9', 64), T0,
            revision.ContentHash, "{\"tau\":20000000}", StudyCanonicalJson.TextHash(report), report);
        var evaluated = new SyncStudy(study.StudyId, study.Draft, study.Locations, study.Revisions, study.History, new[] { record });
        var e2 = new SyncStudyEditor(evaluated, Clock());
        e2.DecideMatch(mark, MatchState.Confirmed, study.Draft.Fit.Climbs[1].Identity, null, "", MatchBasis.ProposalAccepted,
            new ProposalRef(record.Seq, new string('7', 64)));
        var explanation = e2.AddExplanation(record.Seq, study.Draft.Fit.Climbs[1].Identity, ExplanationCategory.InsufficientEvidence, "blurred").CreatedId!.Value;
        e2.WithdrawExplanation(explanation, "re-checked");

        var text = SyncStudySerializer.Serialize(evaluated);
        var read = SyncStudySerializer.Deserialize(text);
        Assert.Equal(text, SyncStudySerializer.Serialize(read));
        Assert.Equal(CoverageState.ReviewedNoClimbs, read.Draft.Video(b)!.CoverageStatus);
        Assert.Equal(new ProposalRef(record.Seq, new string('7', 64)), read.Draft.Decision(mark)!.Proposal);
        Assert.Equal(record, read.Evaluations.Single());
        Assert.Equal(evaluated.Explanations.Single(), read.Explanations.Single());
        Assert.Equal(evaluated.Withdrawals.Single(), read.Withdrawals.Single());
        Assert.Empty(read.ActiveExplanations(read.AllSeqs().Max()));

        var tampered = JsonNode.Parse(text)!;
        tampered["evaluations"]![0]!["report"] = "{\"proposals\":[1]}";
        Assert.Equal(SyncStudyFileError.Damaged, Assert.Throws<SyncStudyFileException>(() => SyncStudySerializer.Deserialize(tampered.ToJsonString())).Error);
    }

    [Fact]
    public async Task Only_the_latest_revision_of_an_unchanged_verified_conflict_free_set_is_admitted_to_an_evaluation()
    {
        var (service, files) = await ServiceAsync();
        Assert.Contains("no frozen revision", service.EvaluationProblems());

        var study = service.Study!;
        var a = study.Draft.Videos[0].VideoKey;
        var b = study.Draft.Videos[1].VideoKey;
        service.Freeze("rev 1");
        Assert.Empty(service.EvaluationProblems());

        files.Touch(@"D:\set\a.mp4", 9_000);                               // changed since the check
        Assert.Contains(service.EvaluationProblems(), p => p.Contains("changed since it was verified"));
        files.Touch(@"D:\set\a.mp4", 1_000);
        await service.VerifyAsync();
        Assert.Empty(service.EvaluationProblems());

        files.Put(@"D:\set\a.mp4", "a re-exported");
        await service.AcceptChangedVideoAsync(a, Video(@"D:\set\a.mp4"), "new export");
        Assert.Contains(service.EvaluationProblems(), p => p.Contains("set differs from revision 1"));

        var m1 = service.Editor.AddMark(a, 30 * TimeSpan.TicksPerSecond, null).CreatedId!.Value;
        var m2 = service.Editor.AddMark(b, 30 * TimeSpan.TicksPerSecond, null).CreatedId!.Value;
        var climb = study.Draft.Fit.Climbs[1].Identity;
        service.Editor.DecideMatch(m1, MatchState.Confirmed, climb, null, "");
        service.Editor.DecideMatch(m2, MatchState.Confirmed, climb, null, "");
        service.Freeze("rev 2");
        Assert.Contains(service.EvaluationProblems(), p => p.Contains("confirmed by more than one active mark"));
        Assert.Equal(new[] { climb }, SyncStudyService.Conflicts(study.Revisions[^1].Content));

        service.Editor.ClearDecision(m2, "the other camera");
        service.Freeze("rev 3");
        Assert.Empty(service.EvaluationProblems());
    }
}
