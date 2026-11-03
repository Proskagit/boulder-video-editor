using System.Security.Cryptography;
using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Research;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Research;
using AiVideoEditor.UI.ViewModels.Research;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests.Research;

/// <summary>
/// Step 35.3R1: the reference marking over the Source viewer — start (required) and end (optional) at the Source position,
/// independent moves, «go to», manual confirmation with the one-camera rule, all of C1–C14 shown without hints, the rule
/// required and shown; a video is marked only when it maps to the project's asset by path and size and is verified and
/// unchanged; the project is never touched.
/// </summary>
public sealed class SyncReferenceViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);
    private const string Fit = @"D:\set\a.fit";
    private const string A = @"D:\set\video_a.mp4";
    private const string B = @"D:\set\video_b.mp4";

    private readonly Files _files = new();
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly FakeSource _source = new();
    private readonly SyncStudyService _service;
    private SyncReferenceViewModel _vm = null!;
    private MediaAsset _a = null!;
    private MediaAsset _b = null!;

    public SyncReferenceViewModelTests()
    {
        var t = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        _service = new SyncStudyService(_files, () => t = t.AddSeconds(1));
    }

    /// <summary>In-memory files with a size, a write time and their bytes' SHA-256.</summary>
    private sealed class Files : IFileFingerprintService
    {
        private readonly Dictionary<string, (string Content, long Ticks)> _files = new(StringComparer.OrdinalIgnoreCase);
        public int Hashes { get; private set; }
        public void Put(string path, string content, long ticks = 1_000) => _files[path] = (content, ticks);
        public void Touch(string path, long ticks) => _files[path] = (_files[path].Content, ticks);
        public long Size(string path) => Encoding.UTF8.GetByteCount(_files[path].Content);

        public FileStamp? Stamp(string path) => _files.TryGetValue(path, out var f) ? new FileStamp(Encoding.UTF8.GetByteCount(f.Content), f.Ticks) : null;

        public Task<FileFingerprint?> HashAsync(string path, IProgress<long>? progress = null, CancellationToken ct = default)
        {
            if (Stamp(path) is not { } stamp) return Task.FromResult<FileFingerprint?>(null);
            Hashes++;
            var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_files[path].Content))).ToLowerInvariant();
            return Task.FromResult<FileFingerprint?>(new FileFingerprint(sha, stamp, stamp));
        }
    }

    /// <summary>The Source viewer as the tool sees it: what is shown, where, and the calls made to it.</summary>
    private sealed class FakeSource : IStudySourceView
    {
        public MediaAsset? Asset { get; set; }
        public MediaTime Position { get; set; }
        public StudyFramePts? ShownFramePts { get; set; }
        public List<MediaTime> GoTos { get; } = new();
        public string? Open(MediaAsset asset) { Asset = asset; Changed?.Invoke(this, EventArgs.Empty); return null; }
        public void GoTo(MediaTime position) { GoTos.Add(position); Position = position; }
        public event EventHandler? Changed;
    }

    private static ClimbingWorkout Workout()
    {
        var segments = Enumerable.Range(1, 14).Select(n => new WorkoutSegment(2 * (n - 1), WorkoutSegmentKind.Climb, n,
            T0.AddMinutes(5 * (n - 1)), T0.AddMinutes(5 * (n - 1)).AddSeconds(21), MediaTime.FromSeconds(20), 120, 130)).ToList();
        return new ClimbingWorkout
        {
            DeviceName = "COROS APEX 4 42mm", Manufacturer = 294, Product = 814, StartUtc = T0, EndUtc = T0.AddHours(2),
            UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(7200), TimerTime = MediaTime.FromSeconds(7200),
            Sport = 31, SubSport = 69, AverageHeartRate = 130, MaxHeartRate = 169, HeartRate = Array.Empty<HeartRateSample>(),
            Pauses = Array.Empty<WorkoutInterval>(), Warnings = Array.Empty<TrainingFileWarning>(), Segments = segments,
        };
    }

    private MediaAsset Asset(string path) => new()
    {
        FilePath = path, Kind = MediaKind.Video, FileSizeBytes = _files.Size(path),
        Metadata = new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(600),
            RecordingTimeProbe = RecordingTimeProbe.Found("2026-09-27T07:10:00.000000Z", RecordingTimeTagOrigin.Format)
        }
    };

    /// <summary>A project holding the two videos and a study of them saved and reopened (nothing verified yet).</summary>
    private async Task SetUpAsync(bool rule = true)
    {
        _files.Put(Fit, "fit");
        _files.Put(A, "video a");
        _files.Put(B, "video b");
        _a = Asset(A);
        _b = Asset(B);
        _projects.Current.MediaAssets.Add(_a);
        _projects.Current.MediaAssets.Add(_b);
        await _service.CreateAsync(Fit, Workout(), new[] { _a, _b });
        if (rule) _service.Editor.SetMarkingRule(ReferenceRule.Id, ReferenceRule.Text);
        var folder = Path.Combine(Path.GetTempPath(), "aive-ref-vm", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "set.syncstudy.json");
        await _service.SaveAsync(path, projectFolder: null);
        _vm = new SyncReferenceViewModel(_service, _source, _projects);
        Assert.True(await _vm.OpenStudyAsync(path));
    }

    private Guid Key(string name) => _service.Study!.Draft.Videos.Single(v => v.OriginalFileName == name).VideoKey;

    private async Task OpenAsync(string name)
    {
        _vm.SelectedVideo = _vm.Videos.Single(r => r.VideoKey == Key(name));
        await _vm.OpenVideoCommand.ExecuteAsync(null);
    }

    private ClimbMark OnlyMark() => _service.Study!.Draft.Marks.Single(m => m.Withdrawn is null);

    [Fact]
    public async Task All_fit_climbs_are_listed_without_hints_and_the_rule_is_shown()
    {
        await SetUpAsync();
        Assert.Equal(Enumerable.Range(1, 14), _vm.Climbs.Select(c => c.Identity.ClimbNumber));
        Assert.Contains(Strings.Reference_ClimbNotFilmed, _vm.Climbs[0].Display);
        Assert.DoesNotContain(_vm.Climbs.Skip(1), c => c.Display.Contains(Strings.Reference_ClimbNotFilmed));
        Assert.All(_vm.Climbs, c => Assert.False(c.IsConfirmed));
        Assert.True(_vm.HasRule);
        Assert.Equal(ReferenceRule.Text, _vm.RuleText);
        Assert.Equal(2, _vm.Videos.Count);
    }

    [Fact]
    public async Task Marking_waits_for_the_rule()
    {
        await SetUpAsync(rule: false);
        await OpenAsync("video_a.mp4");
        _vm.MarkStartCommand.Execute(null);
        Assert.Contains(Strings.Reference_NoRule, _vm.Message);
        Assert.Empty(_service.Study!.Draft.Marks);

        _vm.SetRuleCommand.Execute(null);
        Assert.True(_vm.HasRule);
        Assert.Equal(new MarkingRule(ReferenceRule.Id, ReferenceRule.Text), _service.Study.Draft.MarkingRule);
        _vm.MarkStartCommand.Execute(null);
        Assert.Single(_service.Study.Draft.Marks);
    }

    [Fact]
    public async Task A_video_opens_only_when_it_maps_to_the_project_and_passes_its_check()
    {
        await SetUpAsync();
        _a.FileSizeBytes += 1;                                               // the project's file is not the study's
        await OpenAsync("video_a.mp4");
        Assert.Contains(Strings.Reference_SizeDiffers, _vm.Message);
        Assert.Null(_source.Asset);
        _a.FileSizeBytes -= 1;

        _projects.Current.MediaAssets.Remove(_b);
        await OpenAsync("video_b.mp4");
        Assert.Contains(Strings.Reference_NotInProject, _vm.Message);

        var hashes = _files.Hashes;
        await OpenAsync("video_a.mp4");
        Assert.Same(_a, _source.Asset);
        Assert.Equal(hashes + 1, _files.Hashes);                             // checked once by SHA-256
        await OpenAsync("video_a.mp4");
        Assert.Equal(hashes + 1, _files.Hashes);                             // fresh: not read again

        _files.Put(A, "video A");                                            // other bytes behind the same stamp — caught by the next check
        _files.Touch(A, 2_000);
        await OpenAsync("video_a.mp4");
        Assert.Contains(string.Format(Strings.Reference_NotVerified, StudyFileState.Changed), _vm.Message);
    }

    [Fact]
    public async Task Start_and_end_are_taken_at_the_source_position_and_move_independently()
    {
        await SetUpAsync();
        await OpenAsync("video_a.mp4");
        _source.Position = MediaTime.FromSeconds(30);
        _source.ShownFramePts = new StudyFramePts(2_700_000, 1, 90_000);
        _vm.MarkStartCommand.Execute(null);
        var mark = OnlyMark();
        Assert.Equal((MediaTime.FromSeconds(30).Ticks, (long?)null), (mark.StartSourceTicks, mark.EndSourceTicks));
        Assert.Equal(_source.ShownFramePts, mark.StartFramePts);
        Assert.Equal(ReferenceRule.Id, mark.MarkingRuleId);
        Assert.NotNull(_vm.SelectedMark);

        _source.Position = MediaTime.FromSeconds(20);
        _vm.MarkEndCommand.Execute(null);                                    // an end before the start
        Assert.Null(OnlyMark().EndSourceTicks);

        _source.Position = MediaTime.FromSeconds(55);
        _source.ShownFramePts = null;                                        // no reliable frame: no PTS, still a mark
        _vm.MarkEndCommand.Execute(null);
        Assert.Equal((MediaTime.FromSeconds(30).Ticks, (long?)MediaTime.FromSeconds(55).Ticks), (OnlyMark().StartSourceTicks, OnlyMark().EndSourceTicks));
        Assert.Null(OnlyMark().EndFramePts);

        _source.Position = MediaTime.FromSeconds(31);
        _vm.MoveStartCommand.Execute(null);
        Assert.Equal((MediaTime.FromSeconds(31).Ticks, (long?)MediaTime.FromSeconds(55).Ticks), (OnlyMark().StartSourceTicks, OnlyMark().EndSourceTicks));

        _vm.ClearEndCommand.Execute(null);
        Assert.Equal((MediaTime.FromSeconds(31).Ticks, (long?)null), (OnlyMark().StartSourceTicks, OnlyMark().EndSourceTicks));
    }

    [Fact]
    public async Task An_end_needs_a_mark()
    {
        await SetUpAsync();
        await OpenAsync("video_a.mp4");
        _vm.MarkEndCommand.Execute(null);
        Assert.Equal(Strings.Reference_NoMark, _vm.Message);
        Assert.Empty(_service.Study!.Draft.Marks);
    }

    [Fact]
    public async Task Going_to_a_mark_uses_the_source_viewers_own_seek()
    {
        await SetUpAsync();
        await OpenAsync("video_a.mp4");
        _source.Position = MediaTime.FromSeconds(30);
        _vm.MarkStartCommand.Execute(null);
        _source.Position = MediaTime.FromSeconds(55);
        _vm.MarkEndCommand.Execute(null);

        _vm.GoToStartCommand.Execute(null);
        _vm.GoToEndCommand.Execute(null);
        Assert.Equal(new[] { MediaTime.FromSeconds(30), MediaTime.FromSeconds(55) }, _source.GoTos);
    }

    [Fact]
    public async Task A_confirmation_survives_a_moved_start_and_a_taken_climb_is_refused()
    {
        await SetUpAsync();
        await OpenAsync("video_a.mp4");
        _source.Position = MediaTime.FromSeconds(30);
        _vm.MarkStartCommand.Execute(null);
        _vm.SelectedClimb = _vm.Climbs.Single(c => c.Identity.ClimbNumber == 2);
        _vm.ConfirmCommand.Execute(null);
        var first = OnlyMark().MarkId;
        Assert.True(_vm.Climbs.Single(c => c.Identity.ClimbNumber == 2).IsConfirmed);

        _source.Position = MediaTime.FromSeconds(32);
        _vm.MoveStartCommand.Execute(null);
        Assert.Equal(MatchState.Confirmed, _service.Study!.Draft.Decision(first)!.State);
        Assert.Equal(HistoryKind.MarkUpdated, _service.Study.History.Last(h => h.SubjectId == first).Kind);

        await OpenAsync("video_b.mp4");
        _source.Position = MediaTime.FromSeconds(40);
        _vm.MarkStartCommand.Execute(null);
        _vm.SelectedClimb = _vm.Climbs.Single(c => c.Identity.ClimbNumber == 2);
        _vm.ConfirmCommand.Execute(null);
        Assert.StartsWith(string.Format(Strings.Reference_ClimbTaken, "video_a.mp4", "0:32.000")[..20], _vm.Message);
        var second = _service.Study.Draft.Marks.Single(m => m.MarkId != first).MarkId;
        Assert.Null(_service.Study.Draft.Decision(second));
    }

    [Fact]
    public async Task Marking_is_blocked_when_the_file_changed_or_another_video_is_shown()
    {
        await SetUpAsync();
        await OpenAsync("video_a.mp4");
        _files.Touch(A, 9_000);
        _vm.MarkStartCommand.Execute(null);
        Assert.Contains(Strings.Reference_NotFresh, _vm.Message);
        Assert.Empty(_service.Study!.Draft.Marks);

        _files.Touch(A, 1_000);
        await OpenAsync("video_a.mp4");                                      // checked again
        _source.Asset = _b;                                                  // the user opened another video in Source
        _vm.MarkStartCommand.Execute(null);
        Assert.Contains(Strings.Reference_NotShown, _vm.Message);
        Assert.Empty(_service.Study.Draft.Marks);
    }

    [Fact]
    public async Task The_project_is_never_touched()
    {
        await SetUpAsync();
        var assets = _projects.Current.MediaAssets.ToList();
        await OpenAsync("video_a.mp4");
        _source.Position = MediaTime.FromSeconds(30);
        _vm.MarkStartCommand.Execute(null);
        _vm.SelectedClimb = _vm.Climbs[1];
        _vm.ConfirmCommand.Execute(null);
        _vm.SetCoverageCommand.Execute(CoverageState.HasClimbs);
        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.False(_projects.Current.IsDirty);
        Assert.Equal(assets, _projects.Current.MediaAssets);
        Assert.Equal(Strings.Reference_Saved, _vm.Message);
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AiVideoEditor.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void The_tool_uses_no_proposal_no_zero_hypothesis_no_training_mapping_and_no_editing_command()
    {
        var root = Root();
        var sources = new[]
        {
            "src/UI/ViewModels/Research/SyncReferenceViewModel.cs", "src/UI/Research/StudySourceView.cs", "src/UI/Research/StudyAssetMap.cs",
            "src/UI/Views/Research/SyncReferenceWindow.axaml", "src/UI/Views/Research/SyncReferenceWindow.axaml.cs",
        }.Select(f => File.ReadAllText(Path.Combine(root, f))).ToList();
        foreach (var forbidden in new[] { "Candidates", "TrainingAlignment", "Resolve(", "Proposal", "InsertClip", "OverwriteClip", "SetIn(", "SetOut(", "ClearRange" })
            Assert.All(sources, s => Assert.DoesNotContain(forbidden, s));
    }

    [Fact]
    public void The_launch_argument_needs_a_path_and_exists_in_debug_only()
    {
        var type = typeof(App.App).Assembly.GetType("AiVideoEditor.App.DevStartup");
#if DEBUG
        Assert.NotNull(type);
        var parse = type!.GetMethod("CorosReferencePath")!;
        string? Parse(params string[] args) => (string?)parse.Invoke(null, new object[] { args });
        Assert.Equal(@"D:\s.syncstudy.json", Parse("--open-project", @"D:\set", "--coros-reference", @"D:\s.syncstudy.json"));
        Assert.Null(Parse("--coros-reference"));
        Assert.Null(Parse("--coros-reference", "--open-project", @"D:\set"));
        Assert.Null(Parse("--open-project", @"D:\set"));
#else
        Assert.Null(type);
#endif
    }
}
