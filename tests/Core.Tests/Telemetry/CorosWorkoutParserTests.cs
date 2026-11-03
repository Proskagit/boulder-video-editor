using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Telemetry;
using Xunit;
using W = AiVideoEditor.Core.Tests.Telemetry.FitTestWriter;

namespace AiVideoEditor.Core.Tests.Telemetry;

public class CorosWorkoutParserTests
{
    private static ClimbingWorkout Parse(FitTestWriter writer) => CorosWorkoutParser.Parse(writer.Build());

    [Fact]
    public void A_coros_climbing_export_is_read_completely()
    {
        var workout = Parse(CorosFixture.Writer());

        Assert.Equal("COROS APEX 4 42mm", workout.DeviceName);
        Assert.Equal(294, workout.Manufacturer);
        Assert.Equal(814, workout.Product);
        Assert.Equal(CorosFixture.Start, workout.StartUtc);
        Assert.Equal(CorosFixture.Start.AddSeconds(60), workout.EndUtc);
        Assert.Equal(TimeSpan.FromHours(3), workout.UtcOffset);
        Assert.Equal(new MediaTime(60_000 * TimeSpan.TicksPerMillisecond), workout.ElapsedTime);
        Assert.Equal(new MediaTime(50_250 * TimeSpan.TicksPerMillisecond), workout.TimerTime);
        Assert.True(workout.IsBouldering);
        Assert.Equal(130, workout.AverageHeartRate);
        Assert.Equal(160, workout.MaxHeartRate);
        Assert.Empty(workout.Warnings);
    }

    [Fact]
    public void Heart_rate_samples_keep_their_absolute_time_and_the_pause_gap()
    {
        var workout = Parse(CorosFixture.Writer());

        Assert.Equal(52, workout.HeartRate.Count);
        Assert.Equal(new HeartRateSample(CorosFixture.Start, 100), workout.HeartRate[0]);
        Assert.Equal(new HeartRateSample(CorosFixture.Start.AddSeconds(20), 120), workout.HeartRate[20]);
        Assert.Equal(new HeartRateSample(CorosFixture.Start.AddSeconds(30), 130), workout.HeartRate[21]);
        Assert.Equal(new HeartRateSample(CorosFixture.Start.AddSeconds(60), 160), workout.HeartRate[^1]);
        Assert.All(workout.HeartRate.Zip(workout.HeartRate.Skip(1)), p => Assert.True(p.Second.Utc > p.First.Utc));
    }

    [Fact]
    public void The_timer_pause_is_read_from_the_stop_and_start_events()
    {
        var pause = Assert.Single(Parse(CorosFixture.Writer()).Pauses);
        Assert.Equal(new WorkoutInterval(CorosFixture.Start.AddSeconds(20), CorosFixture.Start.AddSeconds(30)), pause);
    }

    [Fact]
    public void Splits_become_numbered_climbs_and_rests()
    {
        var segments = Parse(CorosFixture.Writer()).Segments;

        Assert.Equal(new[] { WorkoutSegmentKind.Climb, WorkoutSegmentKind.Rest, WorkoutSegmentKind.Climb }, segments.Select(s => s.Kind));
        Assert.Equal(new int?[] { 1, null, 2 }, segments.Select(s => s.ClimbNumber));

        var first = segments[0];
        Assert.Equal(CorosFixture.Start, first.StartUtc);
        Assert.Equal(CorosFixture.Start.AddSeconds(12), first.EndUtc);
        Assert.Equal(new MediaTime(12_340 * TimeSpan.TicksPerMillisecond), first.Elapsed);
        Assert.Equal(105, first.AverageHeartRate);
        Assert.Equal(112, first.MaxHeartRate);

        // No end time in the file: the end is the start plus the exact elapsed milliseconds.
        Assert.Equal(CorosFixture.Start.AddSeconds(40).AddTicks(15_500 * TimeSpan.TicksPerMillisecond), segments[2].EndUtc);
    }

    [Fact]
    public void Another_manufacturer_is_refused()
    {
        var e = Assert.Throws<TrainingFileException>(() => Parse(CorosFixture.Writer(manufacturer: 1)));   // Garmin
        Assert.Equal(TrainingFileError.NotCoros, e.Error);
    }

    [Fact]
    public void A_file_that_is_not_an_activity_is_refused()
    {
        var e = Assert.Throws<TrainingFileException>(() => Parse(CorosFixture.Writer(fileType: 6)));   // a course
        Assert.Equal(TrainingFileError.NotAnActivity, e.Error);
    }

    [Fact]
    public void An_activity_without_a_session_is_refused()
    {
        var e = Assert.Throws<TrainingFileException>(() => Parse(CorosFixture.Writer(session: false)));
        Assert.Equal(TrainingFileError.NoSession, e.Error);
    }

    [Fact]
    public void An_activity_without_heart_rate_is_refused()
    {
        var e = Assert.Throws<TrainingFileException>(() => Parse(CorosFixture.Writer(heartRate: false)));
        Assert.Equal(TrainingFileError.NoHeartRate, e.Error);
    }

    [Fact]
    public void Relative_device_times_mean_the_clock_was_not_set()
    {
        var file = new FitTestWriter()
            .Define(0, 0, (0, 1, W.Enum), (1, 2, W.UInt16))
            .Data(0, 4L, 294L)
            .Define(1, 18, (253, 4, W.UInt32), (2, 4, W.UInt32))
            .Data(1, 5_000L, 1_000L)
            .Build();

        Assert.Equal(TrainingFileError.ClockNotSet, Assert.Throws<TrainingFileException>(() => CorosWorkoutParser.Parse(file)).Error);
    }

    [Fact]
    public void A_damaged_file_is_refused_with_its_reason()
    {
        var file = CorosFixture.Writer().Build();
        file[40] ^= 0x10;
        Assert.Equal(TrainingFileError.Corrupted, Assert.Throws<TrainingFileException>(() => CorosWorkoutParser.Parse(file)).Error);
    }

    [Fact]
    public void Repeated_or_backward_samples_and_implausible_heart_rates_are_left_out_with_warnings()
    {
        var workout = Parse(CorosFixture.Writer(extraRecords: w => w
            .Data(2, CorosFixture.T0 + 60, 150L, 2600L)     // the same second again
            .Data(2, CorosFixture.T0 + 10, 150L, 2600L)     // back in time
            .Data(2, CorosFixture.T0 + 61, 0xFEL, 2600L)    // 254 bpm
            .Data(2, CorosFixture.T0 + 62, 20L, 2600L)));   // 20 bpm

        Assert.Equal(52, workout.HeartRate.Count);
        Assert.Contains(new TrainingFileWarning(TrainingFileWarningKind.NonMonotonicSamples, 2), workout.Warnings);
        Assert.Contains(new TrainingFileWarning(TrainingFileWarningKind.ImplausibleHeartRate, 2), workout.Warnings);
    }

    [Fact]
    public void A_missing_local_time_offset_is_unknown_and_an_odd_one_is_ignored_with_a_warning()
    {
        Assert.Null(Parse(CorosFixture.Writer(localOffsetSeconds: null)).UtcOffset);

        var odd = Parse(CorosFixture.Writer(localOffsetSeconds: 10_807));
        Assert.Null(odd.UtcOffset);
        Assert.Contains(new TrainingFileWarning(TrainingFileWarningKind.UnusableUtcOffset, 1), odd.Warnings);

        Assert.Equal(TimeSpan.FromHours(-5.5), Parse(CorosFixture.Writer(localOffsetSeconds: -19_800)).UtcOffset);
    }

    [Fact]
    public void A_split_ending_before_its_start_is_left_out_with_a_warning()
    {
        var workout = Parse(CorosFixture.Writer().Define(7, 312, (0, 1, W.Enum), (1, 4, W.UInt32), (9, 4, W.UInt32), (27, 4, W.UInt32))
            .Data(7, 9L, 1_000L, CorosFixture.T0 + 50, CorosFixture.T0 + 45));

        Assert.Equal(3, workout.Segments.Count);
        Assert.Contains(new TrainingFileWarning(TrainingFileWarningKind.InvalidSegments, 1), workout.Warnings);
    }
}
