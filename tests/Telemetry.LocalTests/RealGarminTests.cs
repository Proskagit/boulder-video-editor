using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Telemetry.LocalTests;

/// <summary>
/// D062 on real files. A Garmin Venu X1 indoor-climbing export of 2026-11-14 (a friend's file, never committed; path in
/// <c>AIVE_GARMIN_FIT</c>), checked against its Garmin Connect summary: 16 routes, 2 attempted, 14 completed, 9 sends,
/// 12 falls, the grades 6b (14) and 6c (2), 1:26:00 in all, average 114 / maximum 169 bpm. And the owner's COROS export
/// of 2026-08-08, the first one with grades (Font 6B / 6C / 7A).
/// </summary>
public class RealGarminTests
{
    private static readonly string GarminPath =
        Environment.GetEnvironmentVariable("AIVE_GARMIN_FIT") is { Length: > 0 } path ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "25076394986_ACTIVITY.fit");

    private static readonly string Coros0808Path =
        Environment.GetEnvironmentVariable("AIVE_COROS_FIT_0808") is { Length: > 0 } path
            ? path
            : @"D:\Времянка для монтажа\Боулдер 08.08.2026\Болдеринг20260808093423.fit";

    private static ClimbingWorkout Load(string path)
    {
        Assert.True(File.Exists(path), $"The export was not found at {path}.");
        return FitWorkoutParser.Parse(File.ReadAllBytes(path));
    }

    [Fact]
    public void The_garmin_export_matches_its_garmin_connect_summary()
    {
        var workout = Load(GarminPath);

        Assert.Equal(FitVendor.Garmin, workout.Vendor);
        Assert.Equal(4603, workout.Product);                                         // venuX1 in the FIT SDK profile
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 12, 31, 9, TimeSpan.Zero), workout.StartUtc);
        Assert.Equal(TimeSpan.FromSeconds(5160.292), workout.EndUtc - workout.StartUtc);   // 1:26:00
        Assert.Equal(TimeSpan.FromHours(3), workout.UtcOffset);                      // 15:31 local in Garmin Connect
        Assert.Equal((114, 169), (workout.AverageHeartRate, workout.MaxHeartRate));
        Assert.Equal(3445, workout.HeartRate.Count);
        Assert.Equal(FitWorkoutParser.SmartRecordingHold, workout.HeartRateHold);
        Assert.Empty(workout.Warnings);

        var climbs = workout.Climbs.ToList();
        Assert.Equal(16, climbs.Count);
        Assert.Equal(2, climbs.Count(c => c.Route?.Result == ClimbResult.Attempted));
        Assert.Equal(14, climbs.Count(c => c.Route?.Result == ClimbResult.Completed));
        Assert.Equal(9, climbs.Count(c => c.Route?.Send == true));
        Assert.Equal(12, climbs.Sum(c => c.Route?.Falls ?? 0));
        Assert.Equal(14, climbs.Count(c => c.Route?.Grade == "6b"));
        Assert.Equal(2, climbs.Count(c => c.Route?.Grade == "6c"));
        Assert.Equal(14, climbs.Count(c => c.Route?.Status == BoulderStatus.Top));
        // Climbing time 15:32 in Garmin Connect: the climbs' elapsed times.
        Assert.Equal(932, (int)Math.Round(climbs.Sum(c => c.Elapsed.Ticks) / (double)TimeSpan.TicksPerSecond));
        // Every heart-rate sample lies inside the recording.
        Assert.All(workout.HeartRate, s => Assert.True(workout.Recording.Contains(s.Utc)));
    }

    [Fact]
    public void The_coros_export_of_8_august_states_font_grades()
    {
        var workout = Load(Coros0808Path);

        Assert.Equal(FitVendor.Coros, workout.Vendor);
        Assert.Equal(WorkoutTimeline.DefaultMaxSampleAge, workout.HeartRateHold);
        var grades = workout.Climbs.Select(c => c.Route?.Grade).ToList();
        Assert.All(grades.Where(g => g is not null), g => Assert.Contains(g, new[] { "6B", "6C", "7A" }));
        Assert.Contains("7A", grades);
    }
}
