using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Telemetry.LocalTests;

/// <summary>
/// The product owner's COROS export of the bouldering session of 2026-09-27, read by the app's parser. The expected
/// values come from an independent decoder (a stdlib Python FIT reader with its own CRC check) and the COROS app's
/// summary screenshot of the same session (1:36:08, average 130 / maximum 169 bpm, 14 routes).
/// </summary>
public class RealSessionTests
{
    private static readonly string FitPath =
        Environment.GetEnvironmentVariable("AIVE_COROS_FIT") is { Length: > 0 } path
            ? path
            : @"D:\Времянка для монтажа\Боулдер 27.09.2026\Болдеринг20260927100239.fit";

    private static ClimbingWorkout Load()
    {
        Assert.True(File.Exists(FitPath), $"The COROS export was not found at {FitPath} (set AIVE_COROS_FIT).");
        return CorosWorkoutParser.Parse(File.ReadAllBytes(FitPath));
    }

    private static DateTimeOffset Utc(int hour, int minute, int second) => new(2026, 9, 27, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void The_file_is_valid_fit_from_a_coros_apex_4()
    {
        var (info, messages) = FitFile.Read(File.ReadAllBytes(FitPath));
        Assert.Equal(14, info.HeaderSize);
        Assert.Equal(53_987, info.DataSize);
        Assert.Equal(5769, messages.Count(m => m.GlobalNumber == 20));

        var workout = Load();
        Assert.Equal("COROS APEX 4 42mm", workout.DeviceName);
        Assert.Equal(CorosWorkoutParser.CorosManufacturer, workout.Manufacturer);
        Assert.True(workout.IsBouldering);
        Assert.Empty(workout.Warnings);
    }

    [Fact]
    public void The_session_times_and_the_local_offset()
    {
        var workout = Load();
        Assert.Equal(Utc(7, 2, 39), workout.StartUtc);
        Assert.Equal(Utc(8, 39, 14), workout.EndUtc);
        Assert.Equal(TimeSpan.FromHours(3), workout.UtcOffset);
        Assert.Equal(new MediaTime(5_794_340 * TimeSpan.TicksPerMillisecond), workout.ElapsedTime);
        Assert.Equal(new MediaTime(5_768_200 * TimeSpan.TicksPerMillisecond), workout.TimerTime);   // 1:36:08 in the app
        Assert.Equal(130, workout.AverageHeartRate);
        Assert.Equal(169, workout.MaxHeartRate);
    }

    [Fact]
    public void The_heart_rate_series_matches_the_reference_decoder()
    {
        var hr = Load().HeartRate;
        Assert.Equal(5766, hr.Count);
        Assert.Equal(749_194, hr.Sum(s => (long)s.Bpm));
        Assert.Equal(new HeartRateSample(Utc(7, 2, 42), 121), hr[0]);
        Assert.Equal(new HeartRateSample(Utc(8, 39, 14), 120), hr[^1]);
        Assert.Equal(85, hr.Min(s => s.Bpm));
        Assert.Equal(169, hr.Max(s => s.Bpm));
    }

    [Fact]
    public void The_one_timer_pause()
    {
        var pause = Assert.Single(Load().Pauses);
        Assert.Equal(new WorkoutInterval(Utc(8, 18, 17), Utc(8, 18, 45)), pause);
    }

    [Fact]
    public void Fourteen_climbs_with_the_durations_of_the_app()
    {
        var workout = Load();
        Assert.Equal(33, workout.Segments.Count);

        var climbs = workout.Climbs.ToList();
        Assert.Equal(Enumerable.Range(1, 14).Select(n => (int?)n), climbs.Select(c => c.ClimbNumber));
        Assert.Equal(
            new long[] { 20170, 27300, 22400, 17500, 32500, 36200, 51600, 3600, 10300, 17800, 7000, 14800, 11130, 22510 },
            climbs.Select(c => c.Elapsed.Ticks / TimeSpan.TicksPerMillisecond));
        Assert.Equal(Utc(7, 2, 39), climbs[0].StartUtc);
        Assert.Equal(Utc(7, 3, 0), climbs[0].EndUtc);
        Assert.Equal(Utc(7, 6, 57), climbs[1].StartUtc);
    }

    [Fact]
    public void The_watch_split_heart_rates_agree_with_the_samples_except_at_the_edges()
    {
        // Fields 15 / 16 of the split are its average / maximum heart rate: equal to the samples' (end inclusive, average
        // rounded half up) in 30 of 33 splits. They differ for the first climb (the recording's start), the 3.6 s climb and
        // the climb that ends at the timer pause — the parser keeps the watch's own values.
        var workout = Load();
        var agreeing = workout.Segments.Count(s =>
        {
            var inside = workout.HeartRate.Where(h => h.Utc >= s.StartUtc && h.Utc <= s.EndUtc).Select(h => h.Bpm).ToList();
            return inside.Count > 0 && s.MaxHeartRate == inside.Max() && s.AverageHeartRate == (int)Math.Round(inside.Average(), MidpointRounding.AwayFromZero);
        });
        Assert.Equal(30, agreeing);
    }
}
