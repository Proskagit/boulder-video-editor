using System.Diagnostics;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.Media;
using AiVideoEditor.Project.Research;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Telemetry.LocalTests;

/// <summary>
/// Step 35.3b1 on the fixed set (D050 B5): the session folder of 2026-09-27 — the FIT and its 16 videos — becomes a
/// study: every video probed with ffprobe, every file hashed in full, the set frozen, written to a temporary folder outside
/// the repository and reopened (a quick check only). No mark, no proposal, no statistic: those are Steps 35.3b2 / 35.3d.
/// </summary>
public class RealStudyTests
{
    private readonly ITestOutputHelper _output;

    public RealStudyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task The_whole_set_becomes_a_frozen_study_that_reopens_unchanged()
    {
        var fitPath = RealSet.FitPath;
        var workout = await RealSet.WorkoutAsync(fitPath);
        var videoPaths = RealSet.VideoPaths(fitPath);
        Assert.Equal(RealSet.VideoCount, videoPaths.Count);
        var videos = await RealSet.ProbeAsync(videoPaths);
        var service = new SyncStudyService(new FileFingerprintService(), () => DateTimeOffset.UtcNow);
        var clock = Stopwatch.StartNew();
        var study = await service.CreateAsync(fitPath, workout, videos);
        clock.Stop();
        var bytes = videoPaths.Sum(p => new FileInfo(p).Length) + new FileInfo(fitPath).Length;
        _output.WriteLine($"Hashed {bytes / 1e9:F2} GB in {clock.Elapsed.TotalSeconds:F1} s ({bytes / 1e6 / clock.Elapsed.TotalSeconds:F0} MB/s).");

        Assert.Equal(16, study.Draft.Videos.Count);
        Assert.Equal(14, study.Draft.Fit.Climbs.Count);
        Assert.Equal(Enumerable.Range(1, 14), study.Draft.Fit.Climbs.Select(c => c.ClimbNumber));
        Assert.Equal(16, study.Draft.Videos.Select(v => v.VideoKey).Distinct().Count());
        Assert.Equal(16, study.Draft.Videos.Select(v => v.Sha256).Distinct().Count());
        Assert.All(study.Draft.Videos, v => Assert.True(v.MetadataComplete, v.OriginalFileName));
        Assert.All(study.Draft.Videos, v => Assert.NotNull(v.TagRaw));
        Assert.All(service.SetSubjects(), k => Assert.Equal(StudyFileState.Verified, service.StateOf(k)));
        Assert.Empty(study.Draft.Marks);

        var frozen = service.Freeze("the fixed set of 2026-09-27");
        Assert.True(frozen.Ok, frozen.Problem);
        _output.WriteLine($"Revision 1: {frozen.Revision!.ContentHash}");

        var temp = Path.Combine(Path.GetTempPath(), "aive-real-study", Guid.NewGuid().ToString("N"));
        try
        {
            var studyPath = Path.Combine(temp, "Болдеринг20260927100239.syncstudy.json");
            await service.SaveAsync(studyPath, projectFolder: null);

            var reopened = new SyncStudyService(new FileFingerprintService(), () => DateTimeOffset.UtcNow);
            var read = await reopened.OpenAsync(studyPath);
            Assert.All(reopened.SetSubjects(), k => Assert.Equal(StudyFileState.QuickOk, reopened.StateOf(k)));
            Assert.Equal(frozen.Revision.ContentHash, read.Revisions.Single().ContentHash);
            Assert.Equal(StudyCanonicalJson.ContentHash(study.Draft), StudyCanonicalJson.ContentHash(read.Draft));
            Assert.False(reopened.Freeze("x").Ok);                          // a quick check never verifies
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }
}
