using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Calibration;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Step 37.7, D051's acceptance A4–A8 and A14 through the app's real chain: the project's Save / Save As on disk →
/// <see cref="FitCalibrationSaveSync"/> → <see cref="FitCalibrationStore"/> writing its own file in a temporary folder (never
/// the user's) → «Настройки FIT…» (<see cref="FitSettingsViewModel"/>) showing Beta, the value and the count; the marks made
/// by the import and by a video's <c>Z</c> through the edit service; a restart as a new store over the same file.
/// </summary>
public sealed class FitCalibrationAcceptanceTests : IDisposable
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-37-7", Guid.NewGuid().ToString("N"));
    private readonly StatusService _status = new();
    private FitCalibrationStore _store;
    private UndoRedoService _undo = null!;
    private ProjectService _projects = null!;
    private TimelineEditService _edit = null!;
    private TrainingSessionService _session = null!;
    private FitCalibrationSaveSync _sync = null!;

    public FitCalibrationAcceptanceTests()
    {
        Directory.CreateDirectory(_folder);
        _store = new FitCalibrationStore(CalibrationFile, NullLogger<FitCalibrationStore>.Instance);
        NewApp();
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string CalibrationFile => Path.Combine(_folder, "config", "fit-calibration.json");

    /// <summary>The app's services over the current store (a new project).</summary>
    private void NewApp()
    {
        _undo = new UndoRedoService();
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _projects.Current.Settings.IsFrameRateLocked = true;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance, _store);
        _session = new TrainingSessionService(_projects, startCorrection: _store);
        _sync = new FitCalibrationSaveSync(_projects, _store, NullLogger<FitCalibrationSaveSync>.Instance);
    }

    /// <summary>A restart: a new store instance over the same file, the app's services new.</summary>
    private async Task RestartAsync()
    {
        _store = new FitCalibrationStore(CalibrationFile, NullLogger<FitCalibrationStore>.Instance);
        await _store.LoadAsync();
        NewApp();
    }

    /// <summary><paramref name="climbs"/> climbs of 10 s, every 60 s from W0 + 30 s.</summary>
    private static ClimbingWorkout Workout(int climbs, DateTimeOffset? start = null) => new()
    {
        DeviceName = "COROS APEX 4 42mm", Manufacturer = 294, Product = 814, StartUtc = start ?? W0, EndUtc = W0.AddHours(3),
        UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(10_800), TimerTime = MediaTime.FromSeconds(10_800),
        Sport = 31, SubSport = 69, AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Warnings = [],
        Segments = Enumerable.Range(0, climbs).Select(i => new WorkoutSegment(i, WorkoutSegmentKind.Climb, i + 1, W0.AddSeconds(30 + 60 * i),
            W0.AddSeconds(40 + 60 * i), MediaTime.FromSeconds(10), null, null)).ToList()
    };

    /// <summary>A video of <paramref name="seconds"/> with its recording zero <paramref name="zero"/>, on V1.</summary>
    private MediaAsset Video(DateTimeOffset zero, double seconds)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(_folder, $"{Guid.NewGuid():N}.mp4"), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(seconds), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080 }
        };
        File.WriteAllBytes(asset.FilePath, new byte[] { 0 });
        _projects.AddMediaAssets(new[] { asset });
        Assert.True(_edit.AddClip(asset.Id).Success);
        Assert.True(_edit.SetRecordingZeroOverride(asset.Id, zero).Success);
        return asset;
    }

    private void Import(int climbs, string fit = @"C:\fit\a.fit") =>
        _session.Attach(new TrainingFile(Workout(climbs), 10, new string('a', 64)), fit);

    private TrainingReference Training => _projects.Current.Training!;

    private async Task SaveAsync(string? folder = null)
    {
        if (folder is not null) await _projects.SaveAsAsync(folder);
        else await _projects.SaveAsync();
        await _sync.Last;
        await _store.LoadAsync();
    }

    private FitSettingsViewModel Window() => new(_store, _status);

    private int Used => _store.Current.UsedCount;

    // --- A4 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A4_a_fresh_user_has_Beta_on_a_save_fills_the_calibration_an_unchanged_save_adds_nothing_a_moved_mark_changes_one_record()
    {
        await _store.LoadAsync();
        var window = Window();
        Assert.True(window.BetaEnabled);
        Assert.Equal(string.Format(Strings.FitSettings_UsedCount, 0), window.UsedCountText);
        Assert.False(File.Exists(CalibrationFile));

        Video(W0, 300);                                                     // films climbs 1–5 (30 s … 270 s)
        Import(5);
        Assert.Equal(5, Training.Marks.Count);
        await SaveAsync(Path.Combine(_folder, "p1"));
        Assert.True(File.Exists(CalibrationFile));
        Assert.Equal(5, Used);
        Assert.Equal(string.Format(Strings.FitSettings_UsedCount, 5), Window().UsedCountText);

        var written = File.GetLastWriteTimeUtc(CalibrationFile);
        var bytes = File.ReadAllBytes(CalibrationFile);
        await Task.Delay(50);
        await SaveAsync();                                                   // nothing changed
        Assert.Equal(5, Used);
        Assert.Equal(written, File.GetLastWriteTimeUtc(CalibrationFile));
        Assert.Equal(bytes, File.ReadAllBytes(CalibrationFile));

        var climb = Training.Marks[0].Climb;
        Assert.True(_edit.MoveFitMark(climb, MediaTime.FromSeconds(25)).Success);   // the real start 5 s later than proposed
        await SaveAsync();
        Assert.Equal(5, Used);
        Assert.NotEqual(bytes, File.ReadAllBytes(CalibrationFile));          // one record changed
        Assert.Equal(5, RecordCount());
        // A5's first half: below 20 records the value stays 12 s, whatever the observations.
        Assert.Equal(StartCalibration.InitialTicks, _store.Current.EstimateTicks);
    }

    /// <summary>The records in the calibration file (used, excluded and removed).</summary>
    private int RecordCount()
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(CalibrationFile));
        return json.RootElement.EnumerateObject().First(p => p.Name.Equals("records", StringComparison.OrdinalIgnoreCase)).Value.GetArrayLength();
    }

    // --- A5, A6, A7 ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A5_A6_A7_below_twenty_the_value_stays_twelve_above_it_follows_the_estimate_Beta_off_and_a_new_import()
    {
        await _store.LoadAsync();
        var asset = Video(W0, 1500);                                         // films the first 24 climbs
        Import(24);
        await SaveAsync(Path.Combine(_folder, "p1"));
        Assert.Equal(24, Used);
        Assert.Equal(StartCalibration.InitialTicks, _store.Current.EstimateTicks);   // the proposals' own value: 12 s

        // Move 19 of the 24 marks to a start 6 s earlier than proposed: with 24 records (≥ 20) the estimate follows §6.5.
        foreach (var mark in Training.Marks.Take(19).ToList())
        {
            var start = Training.Workout!.Climbs.Single(c => c.ClimbNumber == mark.Climb.ClimbNumber).StartUtc;
            Assert.True(_edit.MoveFitMark(mark.Climb, MediaTime.FromSeconds((start - W0).TotalSeconds - 6)).Success);
        }
        await SaveAsync();
        var estimate = _store.Current.EstimateTicks;
        Assert.NotEqual(StartCalibration.InitialTicks, estimate);            // A5: above 20 it follows §6.5
        Assert.InRange(estimate, MediaTime.FromSeconds(6).Ticks, StartCalibration.InitialTicks - 1);
        Assert.Equal(string.Format(Strings.FitSettings_Estimate, new MediaTime(estimate).TotalSeconds), Window().EstimateText);

        // A6: Beta off — no change, nothing removed, the last value used, kept after a restart.
        var window = Window();
        window.BetaEnabled = false;
        await Task.Delay(100);
        await _store.LoadAsync();
        Assert.False(_store.Current.BetaEnabled);
        var before = File.ReadAllBytes(CalibrationFile);
        Assert.True(_edit.DeclineClimb(Training.Marks[20].Climb).Success);
        await SaveAsync();
        Assert.Equal(24, Used);
        Assert.Equal(before, File.ReadAllBytes(CalibrationFile));
        await RestartAsync();
        Assert.False(_store.Current.BetaEnabled);
        Assert.Equal(estimate, _store.Current.EstimateTicks);

        // A7: a new import after the change proposes with the new value; the earlier project's marks did not move.
        Video(W0, 300);
        Import(3);
        Assert.All(Training.Marks, m => Assert.Equal(estimate, m.StartCorrectionTicks));
        await _projects.OpenAsync(Path.Combine(_folder, "p1"));
        Assert.Equal(StartCalibration.InitialTicks, _projects.Current.Training!.Marks
            .Where(m => m.Position.Kind == FitMarkPositionKind.Proposed).Select(m => m.StartCorrectionTicks).Distinct().Single());

        // A5: reset (the window asks first — FitMarkUiTests.Reset_asks_first_and_does_nothing_on_Cancel), then 0 records, 12 s.
        Assert.True(await _store.ResetAsync());
        await _store.LoadAsync();
        Assert.Equal(0, Used);
        Assert.Equal(StartCalibration.InitialTicks, _store.Current.EstimateTicks);
    }

    // --- A8 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A8_hide_save_undo_save_returns_the_count_without_a_duplicate()
    {
        await _store.LoadAsync();
        Video(W0, 300);
        Import(5);
        await SaveAsync(Path.Combine(_folder, "p1"));
        Assert.Equal(5, Used);

        Assert.True(_edit.DeclineClimb(Training.Marks[2].Climb).Success);   // Delete on the mark
        await SaveAsync();
        Assert.Equal(4, Used);
        _undo.Undo();
        await SaveAsync();
        Assert.Equal(5, Used);
        Assert.Equal(5, RecordCount());                                      // the same record back: no duplicate
    }

    // --- A14 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A14_a_clamped_mark_gives_no_observation_until_moved_and_undo_takes_it_back()
    {
        await _store.LoadAsync();
        Video(W0.AddSeconds(25), 60);            // climb 1 starts at W0 + 30 s, W_auto = W0 + 18 s is before the recording
        Import(1);
        var mark = Training.Marks.Single();
        Assert.Equal(FitMarkPositionKind.Clamped, mark.Position.Kind);
        Assert.Equal(MediaTime.Zero, mark.Position.SourceTime);              // on the first frame

        await SaveAsync(Path.Combine(_folder, "p1"));
        Assert.Equal(0, Used);                                               // saving does not raise the count

        Assert.True(_edit.MoveFitMark(mark.Climb, MediaTime.FromSeconds(4)).Success);
        await SaveAsync();
        Assert.Equal(1, Used);                                               // moving it raises it by one
        _undo.Undo();
        await SaveAsync();
        Assert.Equal(0, Used);                                               // Undo lowers it again
    }
}
