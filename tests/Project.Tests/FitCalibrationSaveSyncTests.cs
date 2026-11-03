using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Calibration;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// Step 37.5 (D051 §5.8–§5.11, §6.4; T5): a real save of a project with FIT marks writes the observations of its usable
/// marks to a real calibration file (in a temporary folder) — moved → its start, proposed → its frozen correction, a
/// clamped one without a link → no record; a hidden mark → a tombstone; Undo → the same record back; a save without a
/// change writes nothing; Beta off writes nothing; a FIT not usable now touches nothing; autosave never synchronises.
/// </summary>
public sealed class FitCalibrationSaveSyncTests : IDisposable
{
    private const long S = TimeSpan.TicksPerSecond;
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly FitCalibrationStore _store;
    private readonly FitCalibrationSaveSync _sync;
    private readonly TrainingReference _training;
    private readonly MediaAsset _video;
    private readonly ClimbIdentity _a, _b, _c;

    public FitCalibrationSaveSyncTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _store = new FitCalibrationStore(_temp.Combine("config", "fit-calibration.json"), NullLogger<FitCalibrationStore>.Instance);
        _sync = new FitCalibrationSaveSync(_projects, _store, NullLogger<FitCalibrationSaveSync>.Instance);

        _video = new MediaAsset
        {
            FilePath = _temp.Combine("media", "v.mp4"), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            RecordingZeroOverrideUtc = W0, Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(100), FrameRate = Rate }
        };
        _projects.Current.MediaAssets.Add(_video);
        var climbs = new[]
        {
            new WorkoutSegment(0, WorkoutSegmentKind.Climb, 1, W0.AddSeconds(30), W0.AddSeconds(50), MediaTime.FromSeconds(20), null, null),
            new WorkoutSegment(2, WorkoutSegmentKind.Climb, 2, W0.AddSeconds(60), W0.AddSeconds(80), MediaTime.FromSeconds(20), null, null),
            new WorkoutSegment(4, WorkoutSegmentKind.Climb, 3, W0.AddSeconds(5), W0.AddSeconds(25), MediaTime.FromSeconds(20), null, null),
        };
        _training = new TrainingReference
        {
            FilePath = _temp.Combine("fit", "a.fit"), FileSizeBytes = 1, Sha256 = new string('a', 64),
            Activity = new TrainingActivity(W0.AddMinutes(-5), W0.AddHours(1), 294, 814),
            Workout = new ClimbingWorkout
            {
                DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = W0.AddMinutes(-5), EndUtc = W0.AddHours(1), UtcOffset = null,
                ElapsedTime = MediaTime.FromSeconds(3900), TimerTime = MediaTime.FromSeconds(3900), Sport = 31, SubSport = 69,
                AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Segments = climbs, Warnings = []
            },
            Availability = TrainingAvailability.Available
        };
        (_a, _b, _c) = (FitMarkRules.IdentityOf(climbs[0]), FitMarkRules.IdentityOf(climbs[1]), FitMarkRules.IdentityOf(climbs[2]));
        _training.Marks.Add(new FitMark(_a, W0.AddSeconds(18), 12 * S, 0, _video.Id, FitMarkPosition.Proposed, W0.AddDays(1)));
        _training.Marks.Add(new FitMark(_b, W0.AddSeconds(48), 12 * S, 0, _video.Id, FitMarkPosition.Moved(MediaTime.FromSeconds(45)), W0.AddDays(1).AddMinutes(1)));
        _training.Marks.Add(new FitMark(_c, W0.AddSeconds(-7), 12 * S, 0, _video.Id, FitMarkPosition.Clamped(MediaTime.Zero), W0.AddDays(1).AddMinutes(2)));
        _projects.SetTraining(_training);
    }

    public void Dispose() => _temp.Dispose();

    private async Task SaveAsync()
    {
        await _sync.Loading;
        await _projects.SaveAsAsync(_temp.Combine("project"));
        await _sync.Last;
    }

    private CalibrationRecord Record(ClimbIdentity climb) => _store.LastRecords[new CalibrationKey(_training.Activity, climb)];

    [Fact]
    public async Task A_save_writes_one_record_per_usable_mark()
    {
        await SaveAsync();
        Assert.Equal(2, _store.LastRecords.Count);                                // the clamped mark without a link: none
        Assert.Equal((12 * S, CalibrationRecordStatus.Used), (Record(_a).Value, Record(_a).Status));
        Assert.Equal(60 * S - 45 * S, Record(_b).Value);                           // W_s − (Z + 45 s + CC)
        Assert.Equal(_training.Marks[1].PositionChangedAt, Record(_b).PositionChangedAt);
        Assert.Equal(2, _store.Current.UsedCount);
    }

    [Fact]
    public async Task A_save_without_a_change_writes_nothing()
    {
        await SaveAsync();
        var file = _store.FilePath;
        var written = File.GetLastWriteTimeUtc(file);
        var text = await File.ReadAllTextAsync(file);
        await SaveAsync();
        Assert.Equal(text, await File.ReadAllTextAsync(file));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task A_hidden_mark_leaves_a_tombstone_and_its_return_brings_the_same_record_back()
    {
        await SaveAsync();
        var first = Record(_a);
        var mark = _training.Marks[0];
        _training.Marks.RemoveAt(0);
        _training.DeclinedClimbs.Add(new DeclinedClimb(_a, mark));
        await SaveAsync();
        Assert.Equal(CalibrationRecordStatus.Removed, Record(_a).Status);

        _training.DeclinedClimbs.Clear();
        _training.Marks.Insert(0, mark);
        await SaveAsync();
        Assert.Equal(first, Record(_a));                                          // firstSeenAt and both times kept
    }

    [Fact]
    public async Task Beta_off_writes_nothing()
    {
        await _sync.Loading;
        Assert.True(await _store.SetBetaAsync(false));
        await SaveAsync();
        Assert.Empty(_store.LastRecords);
    }

    [Fact]
    public async Task A_FIT_not_usable_now_touches_no_record()
    {
        await SaveAsync();
        var before = await File.ReadAllTextAsync(_store.FilePath);
        _training.Availability = TrainingAvailability.Missing;
        _training.Marks.Clear();                                                  // even with every mark gone
        await SaveAsync();
        Assert.Equal(before, await File.ReadAllTextAsync(_store.FilePath));
    }

    [Fact]
    public async Task No_Z_on_the_video_leaves_the_records_unchanged()
    {
        await SaveAsync();
        var before = _store.LastRecords.Values.ToList();
        _video.RecordingZeroOverrideUtc = null;
        await SaveAsync();
        Assert.Equal(before.OrderBy(r => r.Key), _store.LastRecords.Values.OrderBy(r => r.Key));
    }

    [Fact]
    public async Task Removing_the_training_leaves_its_records_untouched()
    {
        await SaveAsync();
        var before = await File.ReadAllTextAsync(_store.FilePath);
        _projects.SetTraining(null);
        await SaveAsync();
        Assert.Equal(before, await File.ReadAllTextAsync(_store.FilePath));
    }
}
