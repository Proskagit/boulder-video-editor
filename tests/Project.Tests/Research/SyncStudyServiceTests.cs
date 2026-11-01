using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.Project.Research;
using Xunit;
using static AiVideoEditor.Project.Tests.Research.StudyTestFiles;

namespace AiVideoEditor.Project.Tests.Research;

/// <summary>
/// Step 35.3b1: the study service identifies the set by SHA-256 and size, verifies it in full before a freeze (a quick
/// stamp is never enough, a change since the verification blocks it), accepts a changed file only explicitly, keeps a
/// relocated file's key, lets a retired video leave the checked set — and never moves or deletes a mark.
/// </summary>
public sealed class SyncStudyServiceTests : IDisposable
{
    private const string Fit = @"D:\set\Болдеринг.fit";
    private const string A = @"D:\set\video_a.mp4";
    private const string B = @"D:\set\video_b.mp4";
    private readonly StudyTestFiles _files = new();
    private readonly SyncStudyService _service;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-study-service", Guid.NewGuid().ToString("N"));

    public SyncStudyServiceTests()
    {
        _service = new SyncStudyService(_files, Clock());
        _files.Put(Fit, "fit bytes");
        _files.Put(A, "video a");
        _files.Put(B, "video b");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<SyncStudy> CreateAsync() => await _service.CreateAsync(Fit, Workout(), new[] { Video(A), Video(B) });

    private Guid KeyOf(string fileName) => _service.Study!.Draft.Videos.Single(v => v.OriginalFileName == fileName).VideoKey;

    [Fact]
    public async Task A_new_study_is_a_snapshot_of_the_set_verified_by_its_hashes()
    {
        var study = await CreateAsync();
        Assert.Equal(new[] { 0, 2, 4 }, study.Draft.Fit.Climbs.Select(c => c.SegmentIndex));
        Assert.Equal(201_700_000, study.Draft.Fit.Climbs[0].ElapsedTicks);
        Assert.Equal(2, study.Draft.Videos.Count);
        Assert.All(_service.SetSubjects(), k => Assert.Equal(StudyFileState.Verified, _service.StateOf(k)));
        Assert.Empty(_service.ReadinessProblems());
        Assert.Equal(3, study.Locations.Count);

        var frozen = _service.Freeze("rev 1");
        Assert.True(frozen.Ok);
        Assert.Equal(1, frozen.Revision!.Number);
    }

    [Fact]
    public async Task Two_videos_with_the_same_bytes_cannot_form_a_study()
    {
        _files.Put(B, "video a");
        await Assert.ThrowsAsync<ArgumentException>(CreateAsync);
    }

    [Fact]
    public async Task After_opening_a_quick_check_is_not_enough_to_freeze()
    {
        await CreateAsync();
        var path = Path.Combine(_folder, "study.syncstudy.json");
        await _service.SaveAsync(path, projectFolder: null);
        var hashes = _files.Hashes;

        await _service.OpenAsync(path);
        Assert.All(_service.SetSubjects(), k => Assert.Equal(StudyFileState.QuickOk, _service.StateOf(k)));
        Assert.Equal(hashes, _files.Hashes);                              // opening hashes nothing
        Assert.False(_service.Freeze("x").Ok);

        await _service.VerifyAsync();
        Assert.True(_service.Freeze("x").Ok);
    }

    [Fact]
    public async Task The_full_check_finds_other_bytes_behind_an_unchanged_stamp()
    {
        await CreateAsync();
        var path = Path.Combine(_folder, "study.syncstudy.json");
        await _service.SaveAsync(path, projectFolder: null);
        _files.Put(A, "video A", ticks: 1_000);                          // same size, same time, other bytes
        await _service.OpenAsync(path);
        Assert.Equal(StudyFileState.QuickOk, _service.StateOf(KeyOf("video_a.mp4")));

        var states = await _service.VerifyAsync();
        Assert.Equal(StudyFileState.Changed, states[KeyOf("video_a.mp4")]);
        Assert.Equal(StudyFileState.Verified, states[KeyOf("video_b.mp4")]);
        Assert.False(_service.Freeze("x").Ok);
    }

    [Fact]
    public async Task A_file_changed_during_the_read_or_unreadable_is_not_verified()
    {
        await CreateAsync();
        _files.DuringRead = p => { if (p == A) _files.Touch(A, 2_000); };
        var states = await _service.VerifyAsync();
        Assert.Equal(StudyFileState.ChangedDuringRead, states[KeyOf("video_a.mp4")]);

        _files.DuringRead = null;
        _files.Locked.Add(B);
        states = await _service.VerifyAsync();
        Assert.Equal(StudyFileState.Unreadable, states[KeyOf("video_b.mp4")]);
        Assert.False(_service.Freeze("x").Ok);
    }

    [Fact]
    public async Task A_change_after_the_verification_drops_it_and_blocks_the_freeze()
    {
        await CreateAsync();
        _files.Touch(A, 5_000);                                           // written again after the check
        var result = _service.Freeze("x");
        Assert.False(result.Ok);
        Assert.Contains("video_a.mp4: changed since it was verified", result.Problem);
        Assert.Equal(StudyFileState.Changed, _service.StateOf(KeyOf("video_a.mp4")));
        Assert.Empty(_service.Study!.Revisions);

        _files.Touch(A, 1_000);
        Assert.False(_service.Freeze("x").Ok);                            // only a new full check restores it
        await _service.VerifyAsync();
        Assert.True(_service.Freeze("x").Ok);
    }

    [Fact]
    public async Task A_relocated_file_keeps_its_key_when_its_bytes_are_equal_whatever_its_name()
    {
        var study = await CreateAsync();
        var key = KeyOf("video_a.mp4");
        var hash = StudyCanonicalJson.ContentHash(study.Draft);
        _files.Remove(A);
        _files.Put(@"E:\moved\renamed.mp4", "video a");

        Assert.True((await _service.RelocateAsync(key, @"E:\moved\renamed.mp4", "moved")).Ok);
        Assert.Equal(@"E:\moved\renamed.mp4", study.Location(key)!.CurrentPath);
        Assert.Equal(key, KeyOf("video_a.mp4"));
        Assert.Equal(hash, StudyCanonicalJson.ContentHash(study.Draft));
        Assert.Equal(HistoryKind.VideoRelocated, study.History[^1].Kind);
        Assert.Equal(StudyFileState.Verified, _service.StateOf(key));

        _files.Put(@"E:\moved\video_b.mp4", "other");                     // the same name proves nothing
        var refused = await _service.RelocateAsync(KeyOf("video_b.mp4"), @"E:\moved\video_b.mp4");
        Assert.False(refused.Ok);
        Assert.Equal(B, study.Location(KeyOf("video_b.mp4"))!.CurrentPath);
    }

    [Fact]
    public async Task An_accepted_change_of_a_video_keeps_and_stales_its_marks()
    {
        var study = await CreateAsync();
        var key = KeyOf("video_a.mp4");
        var mark = _service.Editor.AddMark(key, 30 * TimeSpan.TicksPerSecond, null).CreatedId!.Value;
        _files.Put(A, "video a re-exported");

        Assert.True((await _service.AcceptChangedVideoAsync(key, Video(A), "re-exported")).Ok);
        Assert.Equal(key, KeyOf("video_a.mp4"));
        Assert.Equal(StaleCause.VideoChanged, study.Draft.Mark(mark)!.Stale!.Cause);
        Assert.Single(study.Draft.Marks);
        Assert.True(_service.Freeze("rev").Ok);
    }

    [Fact]
    public async Task An_accepted_new_fit_stales_marks_and_exclusions_under_the_same_fit_key()
    {
        var study = await CreateAsync();
        var fitKey = study.Draft.Fit.FitKey;
        var mark = _service.Editor.AddMark(KeyOf("video_a.mp4"), 30 * TimeSpan.TicksPerSecond, null).CreatedId!.Value;
        var climb = study.Draft.Fit.Climbs[1].Identity;
        _service.Editor.DecideMatch(mark, MatchState.Confirmed, climb, null, "");
        _service.Editor.AddExclusion(climb, ExclusionCategory.FitClimbAnomaly, "doubt");
        _files.Put(@"D:\set\new.fit", "fit bytes v2");

        Assert.True((await _service.AcceptChangedFitAsync(@"D:\set\new.fit", Workout(secondClimbNumber: 5), "new export")).Ok);
        Assert.Equal(fitKey, study.Draft.Fit.FitKey);
        Assert.NotNull(study.Draft.Mark(mark)!.Stale);
        Assert.NotNull(study.Draft.Exclusions.Single().Stale);
        Assert.Equal(StudyRefusal.DecisionClimbMismatch, _service.Editor.ReconfirmMark(mark, "").Refusal);   // index 2 is climb 5 now
        Assert.Empty(StudyValidator.Problems(study));
    }

    [Fact]
    public async Task A_missing_video_blocks_the_freeze_until_found_or_retired_and_a_returned_one_needs_a_check()
    {
        var study = await CreateAsync();
        var key = KeyOf("video_b.mp4");
        var mark = _service.Editor.AddMark(key, 30 * TimeSpan.TicksPerSecond, null).CreatedId!.Value;
        _files.Remove(B);
        _service.QuickCheck();
        Assert.Equal(StudyFileState.Missing, _service.StateOf(key));
        Assert.False(_service.Freeze("x").Ok);

        Assert.True(_service.RetireVideo(key, "lost").Ok);
        Assert.DoesNotContain(key, _service.SetSubjects());
        Assert.NotNull(study.Draft.Mark(mark));
        Assert.True(_service.Freeze("without b").Ok);

        _files.Put(B, "video b");
        Assert.True((await _service.RestoreVideoAsync(key, "found")).Ok);
        Assert.Equal(StudyFileState.QuickOk, _service.StateOf(key));
        Assert.False(_service.Freeze("with b").Ok);
        await _service.VerifyAsync();
        Assert.True(_service.Freeze("with b").Ok);
        Assert.NotEqual(study.Revisions[0].ContentHash, study.Revisions[1].ContentHash);
    }

    [Fact]
    public async Task A_video_without_complete_metadata_blocks_the_freeze()
    {
        await _service.CreateAsync(Fit, Workout(), new[] { Video(A, probed: false) });
        var result = _service.Freeze("x");
        Assert.False(result.Ok);
        Assert.Contains("probe missing", result.Problem);
    }

    [Fact]
    public async Task The_study_is_saved_next_to_the_project_never_into_it_and_the_project_is_untouched()
    {
        var project = Path.Combine(_folder, "Боулдер");
        Directory.CreateDirectory(project);
        var projectFile = Path.Combine(project, "project.json");
        await File.WriteAllTextAsync(projectFile, "{ \"formatVersion\": 11 }");
        var before = await File.ReadAllBytesAsync(projectFile);
        await CreateAsync();

        var path = SyncStudyFileStore.DefaultPath(project, Fit);
        Assert.Equal(Path.Combine(_folder, "Болдеринг.syncstudy.json"), path);
        await _service.SaveAsync(path, project);

        await Assert.ThrowsAsync<SyncStudyFileException>(() => _service.SaveAsync(projectFile, project));
        await Assert.ThrowsAsync<SyncStudyFileException>(() => _service.SaveAsync(Path.Combine(project, "x.syncstudy.json"), project));
        Assert.Equal(before, await File.ReadAllBytesAsync(projectFile));
        Assert.Equal(new[] { "project.json" }, Directory.GetFiles(project).Select(Path.GetFileName));
    }
}
