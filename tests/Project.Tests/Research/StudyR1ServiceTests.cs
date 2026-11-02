using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.Project.Research;
using Xunit;
using static AiVideoEditor.Project.Tests.Research.StudyTestFiles;

namespace AiVideoEditor.Project.Tests.Research;

/// <summary>
/// Step 35.3R1 in the study service: one video is checked by SHA-256 when it is first opened (the set is not re-read),
/// a fresh check is re-confirmed by size and time before every marking action, a change drops it; the study file opens and
/// saves through the tool's interface without ever being created; the new members round-trip.
/// </summary>
public sealed class StudyR1ServiceTests : IDisposable
{
    private const string Fit = @"D:\set\a.fit";
    private const string A = @"D:\set\video_a.mp4";
    private const string B = @"D:\set\video_b.mp4";
    private readonly StudyTestFiles _files = new();
    private readonly SyncStudyService _service;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-study-r1", Guid.NewGuid().ToString("N"));

    public StudyR1ServiceTests()
    {
        _service = new SyncStudyService(_files, Clock());
        _files.Put(Fit, "fit");
        _files.Put(A, "video a");
        _files.Put(B, "video b");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<(SyncStudy Study, Guid A, Guid B)> ReopenedAsync()
    {
        var study = WithRule(await _service.CreateAsync(Fit, Workout(), new[] { Video(A), Video(B) }), _service);
        var path = Path.Combine(_folder, "set.syncstudy.json");
        await _service.SaveAsync(path, projectFolder: null);
        Assert.True((await _service.OpenStudyAsync(path)).Ok);              // a fresh session: nothing verified yet
        var reopened = _service.Study!;
        return (reopened, reopened.Draft.Videos.Single(v => v.OriginalFileName == "video_a.mp4").VideoKey,
            reopened.Draft.Videos.Single(v => v.OriginalFileName == "video_b.mp4").VideoKey);
    }

    [Fact]
    public async Task One_video_is_checked_alone_and_a_fresh_check_is_not_repeated()
    {
        var (_, a, b) = await ReopenedAsync();
        Assert.Equal(StudyFileState.QuickOk, _service.StateOf(a));
        Assert.False(_service.IsFresh(a));                                   // a quick check is not a verification
        var hashes = _files.Hashes;

        Assert.Equal(StudyFileState.Verified, await _service.VerifyAsync(a));
        Assert.Equal(hashes + 1, _files.Hashes);                             // only this file was read
        Assert.Equal(StudyFileState.QuickOk, _service.StateOf(b));
        Assert.True(_service.IsFresh(a));
        Assert.True(_service.IsFresh(a));
        Assert.Equal(hashes + 1, _files.Hashes);                             // freshness re-reads size and time only
    }

    [Fact]
    public async Task A_change_after_the_check_drops_it_until_the_file_is_checked_again()
    {
        var (_, a, _) = await ReopenedAsync();
        await _service.VerifyAsync(a);
        _files.Touch(A, 7_000);
        Assert.False(_service.IsFresh(a));
        Assert.Equal(StudyFileState.Changed, _service.StateOf(a));
        Assert.False(_service.IsFresh(a));                                   // stays dropped

        _files.Touch(A, 1_000);
        Assert.Equal(StudyFileState.Verified, await _service.VerifyAsync(a));
        Assert.True(_service.IsFresh(a));

        _files.Put(A, "video A");                                            // other bytes, same size and time
        Assert.Equal(StudyFileState.Changed, await _service.VerifyAsync(a));
    }

    [Fact]
    public async Task A_missing_unreadable_or_moving_file_is_not_verified()
    {
        var (_, a, b) = await ReopenedAsync();
        _files.Locked.Add(B);
        Assert.Equal(StudyFileState.Unreadable, await _service.VerifyAsync(b));
        _files.DuringRead = p => { if (p == A) _files.Touch(A, 3_000); };
        Assert.Equal(StudyFileState.ChangedDuringRead, await _service.VerifyAsync(a));
        _files.DuringRead = null;
        _files.Remove(A);
        Assert.Equal(StudyFileState.Missing, await _service.VerifyAsync(a));
        Assert.False(_service.IsFresh(a));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.VerifyAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_tool_never_creates_a_study_and_cannot_save_into_the_project()
    {
        var missing = Path.Combine(_folder, "none.syncstudy.json");
        var refused = await _service.OpenStudyAsync(missing);
        Assert.False(refused.Ok);
        Assert.Null(_service.Study);
        Assert.False(File.Exists(missing));

        Directory.CreateDirectory(_folder);
        var damaged = Path.Combine(_folder, "bad.syncstudy.json");
        await File.WriteAllTextAsync(damaged, "{ not json");
        Assert.False((await _service.OpenStudyAsync(damaged)).Ok);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(damaged));    // the file is left as it was

        var (_, _, _) = await ReopenedAsync();
        var project = Path.Combine(_folder, "set.syncstudy.json");          // the study file itself, as a "project folder"
        Assert.False((await _service.SaveStudyAsync(projectFolder: _folder)).Ok);
        Assert.True((await _service.SaveStudyAsync(projectFolder: Path.Combine(_folder, "project"))).Ok);
        Assert.True(File.Exists(project));
    }

    [Fact]
    public async Task The_marking_members_round_trip_to_the_same_bytes()
    {
        var (study, a, _) = await ReopenedAsync();
        var editor = _service.Editor;
        var mark = editor.AddMark(a, 30 * TimeSpan.TicksPerSecond, 50 * TimeSpan.TicksPerSecond, "", new StudyFramePts(5_400, 1, 90_000),
            new StudyFramePts(9_000, 1, 90_000)).CreatedId!.Value;
        editor.DecideMatch(mark, MatchState.Confirmed, study.Draft.Fit.Climbs[1].Identity, null, "", MatchBasis.Manual);

        var text = SyncStudySerializer.Serialize(study);
        var read = SyncStudySerializer.Deserialize(text);
        Assert.Equal(text, SyncStudySerializer.Serialize(read));
        Assert.Equal(study.Draft.MarkingRule, read.Draft.MarkingRule);
        Assert.Equal(study.Draft.Mark(mark), read.Draft.Mark(mark));
        Assert.Equal(MarkHistory.For(study, mark).Select(s => (s.Seq, s.Kind)), MarkHistory.For(read, mark).Select(s => (s.Seq, s.Kind)));
    }
}
