using System.Diagnostics;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Project.Research;
using AiVideoEditor.UI.Research;

namespace AiVideoEditor.Telemetry.LocalTests;

/// <summary>Why the reference study was not created; nothing was written.</summary>
public sealed class ReferenceStudyRefusedException : Exception
{
    public ReferenceStudyRefusedException(string message) : base(message) { }
}

/// <summary>The created study and the checks it passed.</summary>
internal sealed record ReferenceStudyCreated(string Path, SyncStudy Study, string ContentHash, TimeSpan HashTime, IReadOnlyList<string> Checks);

/// <summary>
/// Creates the study file of Step 35.3R2 (the reference marking of the set of 2026-09-27) with the existing study service
/// — never JSON by hand, never over an existing file. Refuses before anything is read in full or written: a path that is not
/// a new <c>*.syncstudy.json</c> in an existing folder outside the repository and the project folder, a FIT that is not a
/// COROS bouldering activity with the expected climbs, not the expected number of fully probed videos, a video the project
/// does not hold at the same path and size. Then hashes the set, sets the reference marking rule, saves (atomically, the
/// file's absence re-checked right before) and proves the result: it reads back through the serializer and its validator,
/// reopens with every file quick-checked, keeps its content hash and rule, holds no mark, decision or revision, and every
/// video maps to the project's asset as the marking window will map it. No freeze: that follows the marking.
/// </summary>
internal static class ReferenceStudyCreator
{
    public const string OutputVariable = "AIVE_COROS_CREATE_STUDY";
    public const string ProjectVariable = "AIVE_COROS_PROJECT";

    /// <summary>Why <paramref name="outputPath"/> may not receive the study, or null.</summary>
    public static string? PathProblem(string outputPath, string repositoryRoot, string projectFolder)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || !Path.IsPathFullyQualified(outputPath)) return "the output path must be a full path";
        var full = Path.GetFullPath(outputPath);
        if (SyncStudyFileStore.PathProblem(full, projectFolder) is { } storeProblem) return storeProblem;   // extension, project file / folder
        if (Inside(full, repositoryRoot)) return "the study file must be outside the repository";
        if (Path.GetDirectoryName(full) is not { } folder || !Directory.Exists(folder)) return "the output folder does not exist";
        if (File.Exists(full)) return "a study file already exists at the output path — it is never overwritten";
        return null;
    }

    /// <summary>What is wrong with the sources: the FIT and the probed videos.</summary>
    public static IReadOnlyList<string> SourceProblems(ClimbingWorkout workout, IReadOnlyList<MediaAsset> videos, int videoCount, int climbCount)
    {
        var problems = new List<string>();
        if (workout.Manufacturer != CorosWorkoutParser.CorosManufacturer) problems.Add($"the FIT is not a COROS export (manufacturer {workout.Manufacturer})");
        if (!workout.IsBouldering) problems.Add($"the FIT is not a bouldering activity (sport {workout.Sport}, sub-sport {workout.SubSport})");
        var climbs = workout.Climbs.Select(c => c.ClimbNumber).ToList();
        if (!climbs.SequenceEqual(Enumerable.Range(1, climbCount).Select(n => (int?)n)))
            problems.Add($"the FIT has climbs {string.Join(", ", climbs)} instead of C1–C{climbCount}");
        if (videos.Count != videoCount) problems.Add($"{videos.Count} videos instead of {videoCount}");
        foreach (var video in videos)
        {
            var name = Path.GetFileName(video.FilePath);
            if (video.Kind != MediaKind.Video) problems.Add($"{name}: not a video");
            else if (video.Metadata is not { } m || m.Duration <= Core.Common.MediaTime.Zero) problems.Add($"{name}: no duration from ffprobe");
            else if (m.RecordingTimeProbe is null) problems.Add($"{name}: the recording-time probe did not complete");
        }
        return problems;
    }

    /// <summary>What prevents the marking window from mapping the videos: each must be a video of the project at the same full
    /// path, once, with the file's size.</summary>
    public static IReadOnlyList<string> ProjectProblems(IReadOnlyList<MediaAsset> projectAssets, IEnumerable<string> videoPaths)
    {
        var problems = new List<string>();
        foreach (var path in videoPaths)
        {
            var name = Path.GetFileName(path);
            var atPath = projectAssets.Where(a => a.Kind == MediaKind.Video && string.Equals(Path.GetFullPath(a.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).ToList();
            if (atPath.Count == 0) problems.Add($"{name}: not in the project at {path}");
            else if (atPath.Count > 1) problems.Add($"{name}: more than one project video at its path");
            else if (atPath[0].FileSizeBytes != new FileInfo(path).Length) problems.Add($"{name}: the project's size differs from the file's");
        }
        return problems;
    }

    public static async Task<ReferenceStudyCreated> CreateAsync(
        string outputPath, string fitPath, ClimbingWorkout workout, IReadOnlyList<MediaAsset> videos,
        string projectFolder, IReadOnlyList<MediaAsset> projectAssets, string repositoryRoot,
        IFileFingerprintService fingerprints, Func<DateTimeOffset> clock,
        int videoCount = RealSet.VideoCount, int climbCount = RealSet.ClimbCount, Action? beforeSave = null)
    {
        // 1. Refusals before anything is hashed or written.
        if (PathProblem(outputPath, repositoryRoot, projectFolder) is { } pathProblem) throw new ReferenceStudyRefusedException(pathProblem);
        if (SourceProblems(workout, videos, videoCount, climbCount) is { Count: > 0 } sourceProblems)
            throw new ReferenceStudyRefusedException(string.Join("; ", sourceProblems));
        if (ProjectProblems(projectAssets, videos.Select(v => v.FilePath)) is { Count: > 0 } projectProblems)
            throw new ReferenceStudyRefusedException(string.Join("; ", projectProblems));
        var path = Path.GetFullPath(outputPath);
        var checks = new List<string>
        {
            $"path: a new study file outside the repository and the project folder ({path})",
            $"FIT: COROS bouldering, climbs C1–C{climbCount}",
            $"videos: {videos.Count}, each probed (duration and recording-time probe)",
            $"project: every video at the same path and size",
        };

        // 2. The study: the existing service, every file hashed in full; the reference marking rule; no freeze.
        var service = new SyncStudyService(fingerprints, clock);
        var watch = Stopwatch.StartNew();
        var study = await service.CreateAsync(fitPath, workout, videos);
        watch.Stop();
        var rule = service.Editor.SetMarkingRule(ReferenceRule.Id, ReferenceRule.Text);
        if (!rule.Ok) throw new ReferenceStudyRefusedException($"the marking rule was refused: {rule.Refusal}");
        var hash = StudyCanonicalJson.ContentHash(study.Draft);
        checks.Add($"hashed: FIT and {study.Draft.Videos.Count} videos (SHA-256 distinct), {watch.Elapsed.TotalSeconds:F1} s");

        // 3. Saved — the file's absence checked again right before (never overwritten).
        beforeSave?.Invoke();
        if (File.Exists(path)) throw new ReferenceStudyRefusedException("a study file appeared at the output path — it is never overwritten");
        await service.SaveAsync(path, projectFolder);

        // 4. The proof.
        var read = SyncStudySerializer.Deserialize(await File.ReadAllTextAsync(path));        // the validator runs here
        checks.Add("read back: the serializer and the validator accept it");
        var reopened = new SyncStudyService(fingerprints, clock);
        await reopened.OpenAsync(path);
        var subjects = reopened.SetSubjects();
        Require(subjects.Count == videoCount + 1 && subjects.All(k => reopened.StateOf(k) == StudyFileState.QuickOk),
            $"reopened: not every one of the {videoCount + 1} files is quick-checked");
        checks.Add($"reopened: {subjects.Count} files quick-checked");
        Require(StudyCanonicalJson.ContentHash(read.Draft) == hash && StudyCanonicalJson.ContentHash(reopened.Study!.Draft) == hash,
            "the content hash changed through the file");
        checks.Add($"content hash kept: {hash}");
        Require(read.Draft.MarkingRule == ReferenceRule.Rule, "the marking rule was not kept");
        checks.Add($"marking rule: {ReferenceRule.Id}");
        Require(read.Draft.Marks.Count == 0 && read.Draft.Decisions.Count == 0 && read.Revisions.Count == 0, "the study is not empty");
        checks.Add("no mark, no decision, no frozen revision");
        foreach (var video in read.Draft.Videos)
            Require(StudyAssetMap.Find(read, video.VideoKey, projectAssets).Ok, $"{video.OriginalFileName}: does not map to the project's asset");
        checks.Add($"project mapping: all {read.Draft.Videos.Count} videos map by path and size (as the marking window maps them)");
        return new ReferenceStudyCreated(path, read, hash, watch.Elapsed, checks);
    }

    /// <summary>The project's assets as its <c>project.json</c> states them (read only, nothing written).</summary>
    public static async Task<IReadOnlyList<MediaAsset>> ProjectAssetsAsync(string projectFolder)
    {
        var file = ProjectFileStore.ProjectFilePath(projectFolder);
        if (!File.Exists(file)) throw new ReferenceStudyRefusedException($"no project.json in {projectFolder}");
        var json = await new ProjectFileStore().ReadAsync(file);
        return ProjectSerializer.Deserialize(json, projectFolder).MediaAssets.ToList();
    }

    private static void Require(bool condition, string problem)
    {
        if (!condition) throw new ReferenceStudyRefusedException("the created study failed its check: " + problem);
    }

    private static bool Inside(string path, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
