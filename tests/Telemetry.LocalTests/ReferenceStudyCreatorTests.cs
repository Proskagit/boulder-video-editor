using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.Media;
using AiVideoEditor.Project.Research;
using Xunit;

namespace AiVideoEditor.Telemetry.LocalTests;

/// <summary>
/// The reference-study tool on a synthetic set in a temporary folder (no personal data, the real set untouched): it creates a
/// valid, quick-checked, rule-carrying, empty study that maps to the project; it refuses a bad path, an existing file (before
/// any hashing, and again right before saving), wrong sources and a project that does not hold the videos — writing nothing.
/// </summary>
public sealed class ReferenceStudyCreatorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aive-ref-creator", Guid.NewGuid().ToString("N"));
    private readonly string _set;
    private readonly string _project;
    private readonly string _fit;
    private readonly List<string> _videoPaths;
    private readonly CountingFingerprints _fingerprints = new();

    public ReferenceStudyCreatorTests()
    {
        _set = Directory.CreateDirectory(Path.Combine(_root, "Боулдер 27.09.2026")).FullName;
        _project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        _fit = Path.Combine(_set, "Болдеринг20260927100239.fit");
        File.WriteAllText(_fit, "fit bytes");
        _videoPaths = Enumerable.Range(0, 16).Select(i =>
        {
            var path = Path.Combine(_set, $"video_20260927_10{i:D2}00.mp4");
            File.WriteAllText(path, $"video {i}");
            return path;
        }).ToList();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The real fingerprint service, counting the full reads.</summary>
    private sealed class CountingFingerprints : IFileFingerprintService
    {
        private readonly FileFingerprintService _inner = new();
        public int Hashes { get; private set; }
        public FileStamp? Stamp(string path) => _inner.Stamp(path);
        public Task<FileFingerprint?> HashAsync(string path, IProgress<long>? progress = null, CancellationToken ct = default)
        {
            Hashes++;
            return _inner.HashAsync(path, progress, ct);
        }
    }

    private static ClimbingWorkout Workout(int manufacturer = 294, int sport = 31, int climbs = 14) => new()
    {
        DeviceName = "COROS APEX 4 42mm", Manufacturer = manufacturer, Product = 814, StartUtc = T0, EndUtc = T0.AddHours(2),
        UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(7200), TimerTime = MediaTime.FromSeconds(7200),
        Sport = sport, SubSport = 69, AverageHeartRate = 130, MaxHeartRate = 169, HeartRate = Array.Empty<HeartRateSample>(),
        Pauses = Array.Empty<WorkoutInterval>(), Warnings = Array.Empty<TrainingFileWarning>(),
        Segments = Enumerable.Range(1, climbs).Select(n => new WorkoutSegment(2 * (n - 1), WorkoutSegmentKind.Climb, n,
            T0.AddMinutes(5 * (n - 1)), T0.AddMinutes(5 * (n - 1)).AddSeconds(21), MediaTime.FromSeconds(20), 120, 130)).ToList(),
    };

    private static MediaAsset Probed(string path, bool probed = true) => new()
    {
        FilePath = path, Kind = MediaKind.Video, FileSizeBytes = new FileInfo(path).Length,
        Metadata = new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(300),
            RecordingTimeProbe = probed ? RecordingTimeProbe.Found("2026-09-27T07:10:00.000000Z", RecordingTimeTagOrigin.Format) : null
        }
    };

    private IReadOnlyList<MediaAsset> Videos() => _videoPaths.Select(p => Probed(p)).ToList();

    private string Output => Path.Combine(_set, "Болдеринг20260927100239.syncstudy.json");

    private Task<ReferenceStudyCreated> CreateAsync(string? output = null, ClimbingWorkout? workout = null, IReadOnlyList<MediaAsset>? videos = null,
        IReadOnlyList<MediaAsset>? project = null, Action? beforeSave = null) =>
        ReferenceStudyCreator.CreateAsync(output ?? Output, _fit, workout ?? Workout(), videos ?? Videos(), _project, project ?? Videos(),
            CreateReferenceStudyTests.RepositoryRoot(), _fingerprints, () => DateTimeOffset.UtcNow, beforeSave: beforeSave);

    [Fact]
    public async Task It_creates_a_valid_quick_checked_empty_study_with_the_rule_that_maps_to_the_project()
    {
        var created = await CreateAsync();

        Assert.True(File.Exists(Output));
        var read = SyncStudySerializer.Deserialize(await File.ReadAllTextAsync(Output));
        Assert.Equal(16, read.Draft.Videos.Count);
        Assert.Equal(14, read.Draft.Fit.Climbs.Count);
        Assert.Equal(ReferenceRule.Rule, read.Draft.MarkingRule);
        Assert.Empty(read.Draft.Marks);
        Assert.Empty(read.Draft.Decisions);
        Assert.Empty(read.Revisions);
        Assert.Equal(created.ContentHash, StudyCanonicalJson.ContentHash(read.Draft));
        Assert.Equal(17, _fingerprints.Hashes);                              // the FIT and 16 videos, once each
        Assert.Contains(created.Checks, c => c.StartsWith("project mapping: all 16 videos map"));
        Assert.Contains(created.Checks, c => c.StartsWith("reopened: 17 files quick-checked"));
        Assert.Equal(new[] { "project", "Боулдер 27.09.2026" }, Directory.GetDirectories(_root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(Directory.GetFiles(_project));                          // nothing written into the project folder
    }

    [Fact]
    public async Task An_existing_study_is_never_overwritten_and_nothing_is_hashed()
    {
        await File.WriteAllTextAsync(Output, "the owner's marks");
        var refused = await Assert.ThrowsAsync<ReferenceStudyRefusedException>(() => CreateAsync());
        Assert.Contains("already exists", refused.Message);
        Assert.Equal(0, _fingerprints.Hashes);
        Assert.Equal("the owner's marks", await File.ReadAllTextAsync(Output));
    }

    [Fact]
    public async Task A_study_appearing_before_the_save_is_not_overwritten()
    {
        var refused = await Assert.ThrowsAsync<ReferenceStudyRefusedException>(() =>
            CreateAsync(beforeSave: () => File.WriteAllText(Output, "written meanwhile")));
        Assert.Contains("appeared", refused.Message);
        Assert.Equal("written meanwhile", await File.ReadAllTextAsync(Output));
    }

    public static TheoryData<string, string> BadPaths() => new()
    {
        { "relative", "x.syncstudy.json" },
        { "wrong extension", "{set}\\study.json" },
        { "inside the project", "{project}\\x.syncstudy.json" },
        { "the project file", "{project}\\project.json" },
        { "inside the repository", "{repo}\\x.syncstudy.json" },
        { "missing folder", "{set}\\missing\\x.syncstudy.json" },
    };

    [Theory]
    [MemberData(nameof(BadPaths))]
    public async Task A_bad_path_is_refused_before_anything_is_read(string _, string template)
    {
        var path = template.Replace("{set}", _set).Replace("{project}", _project).Replace("{repo}", CreateReferenceStudyTests.RepositoryRoot());
        await Assert.ThrowsAsync<ReferenceStudyRefusedException>(() => CreateAsync(output: path));
        Assert.Equal(0, _fingerprints.Hashes);
        Assert.False(File.Exists(path));
    }

    public static TheoryData<string, Func<ReferenceStudyCreatorTests, (ClimbingWorkout, IReadOnlyList<MediaAsset>)>> BadSources() => new()
    {
        { "not COROS", t => (Workout(manufacturer: 1), t.Videos()) },
        { "not bouldering", t => (Workout(sport: 1), t.Videos()) },
        { "13 climbs", t => (Workout(climbs: 13), t.Videos()) },
        { "15 videos", t => (Workout(), t.Videos().Take(15).ToList()) },
        { "a video not probed", t => (Workout(), t.Videos().Select((v, i) => i == 3 ? Probed(v.FilePath, probed: false) : v).ToList()) },
    };

    [Theory]
    [MemberData(nameof(BadSources))]
    public async Task Wrong_sources_are_refused_and_nothing_is_written(string _, Func<ReferenceStudyCreatorTests, (ClimbingWorkout, IReadOnlyList<MediaAsset>)> sources)
    {
        var (workout, videos) = sources(this);
        await Assert.ThrowsAsync<ReferenceStudyRefusedException>(() => CreateAsync(workout: workout, videos: videos, project: videos));
        Assert.Equal(0, _fingerprints.Hashes);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task A_project_that_does_not_hold_the_videos_at_their_paths_is_refused()
    {
        var missing = Videos().Skip(1).ToList();
        var refused = await Assert.ThrowsAsync<ReferenceStudyRefusedException>(() => CreateAsync(project: missing));
        Assert.Contains("not in the project", refused.Message);

        var otherSize = Videos().ToList();
        otherSize[0].FileSizeBytes += 1;
        refused = await Assert.ThrowsAsync<ReferenceStudyRefusedException>(() => CreateAsync(project: otherSize));
        Assert.Contains("size differs", refused.Message);
        Assert.Equal(0, _fingerprints.Hashes);
        Assert.False(File.Exists(Output));
    }
}
