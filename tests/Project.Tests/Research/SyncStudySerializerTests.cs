using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.Project.Research;
using Xunit;
using static AiVideoEditor.Project.Tests.Research.StudyTestFiles;

namespace AiVideoEditor.Project.Tests.Research;

/// <summary>
/// Step 35.3b1: the study file round-trips without loss and to the same bytes, whatever the order in memory; a newer
/// schema is refused, an unknown member ignored; every broken rule makes the file damaged — and a stale or withdrawn
/// record with an old climb identity is history, not damage.
/// </summary>
public sealed class SyncStudySerializerTests
{
    private static async Task<SyncStudy> StudyAsync()
    {
        var files = new StudyTestFiles();
        files.Put(@"D:\set\a.fit", "fit");
        files.Put(@"D:\set\a.mp4", "a");
        files.Put(@"D:\set\b.mp4", "b");
        var service = new SyncStudyService(files, Clock());
        var study = WithRule(await service.CreateAsync(@"D:\set\a.fit", Workout(), new[] { Video(@"D:\set\a.mp4"), Video(@"D:\set\b.mp4") }), service);
        var editor = service.Editor;
        var video = study.Draft.Videos[0].VideoKey;
        var climb = study.Draft.Fit.Climbs[1].Identity;
        var m1 = editor.AddMark(video, 30 * TimeSpan.TicksPerSecond, 57 * TimeSpan.TicksPerSecond, "feet off the floor").CreatedId!.Value;
        var m2 = editor.AddMark(video, 90 * TimeSpan.TicksPerSecond, null).CreatedId!.Value;
        editor.DecideMatch(m1, MatchState.Confirmed, climb, null, "");
        editor.DecideMatch(m2, MatchState.NotMatched, null, MatchReason.Other, "rest split with 1 m ascent");
        editor.AddExclusion(study.Draft.Fit.Climbs[0].Identity, ExclusionCategory.Other, "C1 not filmed");
        service.Freeze("rev 1");
        editor.WithdrawMark(m2, "duplicate");
        editor.RetireVideo(study.Draft.Videos[1].VideoKey, "out of the session");
        return study;
    }

    [Fact]
    public async Task A_study_round_trips_without_loss_and_to_the_same_bytes()
    {
        var study = await StudyAsync();
        var text = SyncStudySerializer.Serialize(study);
        var read = SyncStudySerializer.Deserialize(text);

        Assert.Equal(text, SyncStudySerializer.Serialize(read));
        Assert.Equal(StudyCanonicalJson.ContentHash(study.Draft), StudyCanonicalJson.ContentHash(read.Draft));
        Assert.Equal(study.Revisions.Single().ContentHash, read.Revisions.Single().ContentHash);
        Assert.Equal(study.History.Select(h => (h.Seq, h.Kind, h.Old, h.New)), read.History.Select(h => (h.Seq, h.Kind, h.Old, h.New)));
        Assert.Equal(study.Locations.OrderBy(l => l.SubjectKey), read.Locations.OrderBy(l => l.SubjectKey));
        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public async Task The_order_in_memory_does_not_change_the_bytes()
    {
        var study = await StudyAsync();
        var d = study.Draft;
        var shuffled = new SyncStudy(study.StudyId,
            d with { Videos = d.Videos.Reverse().ToList(), Marks = d.Marks.Reverse().ToList(), Decisions = d.Decisions.Reverse().ToList() },
            study.Locations.Reverse(), study.Revisions.Reverse(), study.History.Reverse(), study.Evaluations);
        Assert.Equal(SyncStudySerializer.Serialize(study), SyncStudySerializer.Serialize(shuffled));
    }

    [Fact]
    public async Task A_newer_schema_is_refused_and_an_unknown_member_ignored()
    {
        var text = SyncStudySerializer.Serialize(await StudyAsync());
        var newer = Assert.Throws<SyncStudyFileException>(() => SyncStudySerializer.Deserialize(text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2")));
        Assert.Equal(SyncStudyFileError.NewerSchema, newer.Error);

        var extra = text.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"futureMember\": [1, 2],");
        Assert.Equal(text, SyncStudySerializer.Serialize(SyncStudySerializer.Deserialize(extra)));
    }

    public static TheoryData<string, Func<string, SyncStudy, string>> Breaks() => new()
    {
        { "not JSON", (t, _) => t[..^5] },
        { "schema 0", (t, _) => t.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 0") },
        { "a missing member", (t, _) => t.Replace("\"studyId\"", "\"studyIdentifier\"") },
        { "an upper-case key", (t, s) => t.Replace(StudyCanonicalJson.Key(s.StudyId), StudyCanonicalJson.Key(s.StudyId).ToUpperInvariant()) },
        { "a malformed hash", (t, s) => t.Replace(s.Draft.Videos[0].Sha256, "xyz") },
        { "an instant with an offset", (t, _) => t.Replace("2026-09-27T07:02:39.0000000Z", "2026-09-27T10:02:39.0000000+03:00") },
        { "an unknown enum value", (t, _) => t.Replace("\"confirmed\"", "\"maybe\"") },
        { "a mark on an unknown video", (t, _) => Mutate(t, n => n["draft"]!["marks"]![0]!["videoKey"] = StudyCanonicalJson.Key(Guid.NewGuid())) },
        { "an active decision on another climb", (t, _) => Mutate(t, n =>
            n["draft"]!["decisions"]!.AsArray().Single(d => (string)d!["state"]! == "confirmed")!["climb"]!["climbNumber"] = 7) },
        { "a tampered revision", (t, s) => t.Replace("\"note\": \"feet off the floor\"", "\"note\": \"feet off the wall\"") },
        { "a second location of one subject", (t, s) => t.Replace("\"locations\": [", "\"locations\": [\n    { \"currentPath\": \"x\", \"subjectKey\": \"" + StudyCanonicalJson.Key(s.Draft.Fit.FitKey) + "\" },") },
        { "a location of nothing", (t, s) => t.Replace("\"locations\": [", "\"locations\": [\n    { \"currentPath\": \"x\", \"subjectKey\": \"" + StudyCanonicalJson.Key(Guid.NewGuid()) + "\" },") },
    };

    private static string Mutate(string text, Action<System.Text.Json.Nodes.JsonNode> change)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        change(node);
        return node.ToJsonString();
    }

    [Theory]
    [MemberData(nameof(Breaks))]
    public async Task A_broken_file_is_damaged(string _, Func<string, SyncStudy, string> breakIt)
    {
        var study = await StudyAsync();
        var text = SyncStudySerializer.Serialize(study);
        var broken = breakIt(text, study);
        Assert.NotEqual(text, broken);
        var error = Assert.Throws<SyncStudyFileException>(() => SyncStudySerializer.Deserialize(broken));
        Assert.Equal(SyncStudyFileError.Damaged, error.Error);
    }

    [Fact]
    public async Task A_stale_decision_with_an_old_identity_is_read_as_history()
    {
        var files = new StudyTestFiles();
        files.Put(@"D:\set\a.fit", "fit");
        files.Put(@"D:\set\a.mp4", "a");
        files.Put(@"D:\set\b.fit", "fit 2");
        var service = new SyncStudyService(files, Clock());
        var study = WithRule(await service.CreateAsync(@"D:\set\a.fit", Workout(), new[] { Video(@"D:\set\a.mp4") }), service);
        var mark = service.Editor.AddMark(study.Draft.Videos[0].VideoKey, TimeSpan.TicksPerSecond, null).CreatedId!.Value;
        service.Editor.DecideMatch(mark, MatchState.Confirmed, study.Draft.Fit.Climbs[1].Identity, null, "");
        await service.AcceptChangedFitAsync(@"D:\set\b.fit", Workout(secondClimbNumber: 5), "new export");

        var read = SyncStudySerializer.Deserialize(SyncStudySerializer.Serialize(study));
        Assert.Equal(2, read.Draft.Decision(mark)!.Climb!.Value.ClimbNumber);
        Assert.NotNull(read.Draft.Mark(mark)!.Stale);
    }
}
