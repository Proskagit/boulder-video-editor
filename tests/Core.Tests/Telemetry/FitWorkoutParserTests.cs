using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using Xunit;
using W = AiVideoEditor.Core.Tests.Telemetry.FitTestWriter;

namespace AiVideoEditor.Core.Tests.Telemetry;

public class FitWorkoutParserTests
{
    private static ClimbingWorkout Parse(FitTestWriter writer) => FitWorkoutParser.Parse(writer.Build());

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
        Assert.Equal(FitVendor.Coros, workout.Vendor);
        Assert.Equal(WorkoutTimeline.DefaultMaxSampleAge, workout.HeartRateHold);   // 1 s records
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

    [Theory]
    [InlineData(1L, FitVendor.Garmin)]
    [InlineData(294L, FitVendor.Coros)]
    [InlineData(255L, FitVendor.Other)]
    public void Any_makers_activity_is_read_by_the_same_rules_and_its_maker_is_recognised(long manufacturer, FitVendor vendor)
    {
        var workout = Parse(CorosFixture.Writer(manufacturer: manufacturer));

        Assert.Equal(vendor, workout.Vendor);
        Assert.Equal((int)manufacturer, workout.Manufacturer);
        Assert.Equal(52, workout.HeartRate.Count);
        Assert.Equal(2, workout.Climbs.Count());
    }

    [Fact]
    public void A_file_that_is_not_an_activity_is_refused()
    {
        var e = Assert.Throws<TrainingFileException>(() => Parse(CorosFixture.Writer(fileType: 6)));   // a course
        Assert.Equal(TrainingFileError.NotAnActivity, e.Error);
    }

    [Fact]
    public void An_activity_without_a_session_is_read_from_its_records_and_timer_events()
    {
        var workout = Parse(CorosFixture.Writer(session: false));

        Assert.Equal(CorosFixture.Start, workout.StartUtc);
        Assert.Equal(CorosFixture.Start.AddSeconds(60), workout.EndUtc);
        Assert.Equal(MediaTime.FromSeconds(60), workout.ElapsedTime);
        Assert.Equal(MediaTime.FromSeconds(50), workout.TimerTime);                 // the 10 s pause left out
        Assert.Null(workout.Sport);
        Assert.Equal(52, workout.HeartRate.Count);
        Assert.Equal((int)Math.Round(workout.HeartRate.Average(h => h.Bpm)), workout.AverageHeartRate);
        Assert.Equal(160, workout.MaxHeartRate);
    }

    [Fact]
    public void An_activity_without_a_session_or_any_timed_message_is_refused()
    {
        var file = new FitTestWriter()
            .Define(0, 0, (0, 1, W.Enum), (1, 2, W.UInt16))
            .Data(0, 4L, 1L)
            .Build();

        Assert.Equal(TrainingFileError.NoSession, Assert.Throws<TrainingFileException>(() => FitWorkoutParser.Parse(file)).Error);
    }

    [Fact]
    public void An_activity_without_heart_rate_but_with_climbs_is_accepted_and_shows_no_heart_rate()
    {
        var workout = Parse(CorosFixture.Writer(heartRate: false));

        Assert.Empty(workout.HeartRate);
        Assert.Equal(2, workout.Climbs.Count());
        Assert.Null(WorkoutTimeline.HeartRateAt(workout, CorosFixture.Start.AddSeconds(5)));
    }

    [Fact]
    public void An_activity_with_neither_heart_rate_nor_climbs_is_refused()
    {
        var e = Assert.Throws<TrainingFileException>(() => Parse(CorosFixture.Writer(heartRate: false, splits: false)));
        Assert.Equal(TrainingFileError.NoTrainingData, e.Error);
    }

    [Fact]
    public void Heart_rate_without_splits_is_accepted_with_no_climbs()
    {
        var workout = Parse(CorosFixture.Writer(splits: false));

        Assert.Equal(52, workout.HeartRate.Count);
        Assert.Empty(workout.Segments);
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

        Assert.Equal(TrainingFileError.ClockNotSet, Assert.Throws<TrainingFileException>(() => FitWorkoutParser.Parse(file)).Error);
    }

    [Fact]
    public void A_damaged_file_is_refused_with_its_reason()
    {
        var file = CorosFixture.Writer().Build();
        file[40] ^= 0x10;
        Assert.Equal(TrainingFileError.Corrupted, Assert.Throws<TrainingFileException>(() => FitWorkoutParser.Parse(file)).Error);
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

    // ---- Garmin (D062) ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_garmin_export_is_read_with_its_end_from_the_elapsed_time_its_maker_and_its_offset()
    {
        var workout = Parse(GarminFixture.Writer());

        Assert.Equal(FitVendor.Garmin, workout.Vendor);
        Assert.Equal("Garmin", workout.DeviceName);                                 // no name in the file: the maker
        Assert.Equal(4603, workout.Product);
        Assert.Equal(GarminFixture.Start, workout.StartUtc);
        // The session is stamped with its start: the end is the start plus the exact elapsed milliseconds.
        Assert.Equal(GarminFixture.Start.AddTicks(GarminFixture.ElapsedMs * TimeSpan.TicksPerMillisecond), workout.EndUtc);
        Assert.Equal(TimeSpan.FromHours(3), workout.UtcOffset);
        Assert.True(workout.IsClimbing);
        Assert.False(workout.IsBouldering);                                         // 68: indoor climbing
        Assert.Empty(workout.Pauses);
        Assert.Equal(3, workout.Climbs.Count());
        Assert.Equal(new[] { WorkoutSegmentKind.Climb, WorkoutSegmentKind.Rest, WorkoutSegmentKind.Climb, WorkoutSegmentKind.Rest, WorkoutSegmentKind.Climb },
            workout.Segments.Select(x => x.Kind));
        Assert.Empty(workout.Warnings);
    }

    [Fact]
    public void An_activity_with_only_splits_for_times_is_read_from_them()
    {
        var workout = Parse(CorosFixture.Writer(session: false, heartRate: false));

        Assert.Equal(CorosFixture.Start, workout.StartUtc);
        Assert.Equal(2, workout.Climbs.Count());
    }

    [Fact]
    public void A_session_stamped_with_its_end_keeps_that_end()
    {
        Assert.Equal(GarminFixture.Start.AddSeconds(120), Parse(GarminFixture.Writer(sessionStampedWithStart: false)).EndUtc);
    }

    [Fact]
    public void Smart_recording_holds_a_sample_longer_and_a_one_second_recording_keeps_two_seconds()
    {
        var garmin = Parse(GarminFixture.Writer());
        Assert.Equal(FitWorkoutParser.SmartRecordingHold, garmin.HeartRateHold);

        // Samples at 0, 1, 4, 5, 7 and 13 s: 12 s is 5 s after the one at 7 s (107 bpm).
        Assert.Equal(107, WorkoutTimeline.HeartRateAt(garmin, GarminFixture.Start.AddSeconds(12)));
        Assert.Null(WorkoutTimeline.HeartRateAt(garmin, GarminFixture.Start.AddSeconds(12), TimeSpan.FromSeconds(2)));

        Assert.Equal(WorkoutTimeline.DefaultMaxSampleAge, Parse(CorosFixture.Writer()).HeartRateHold);
    }

    [Fact]
    public void The_route_of_each_climb_is_read_from_the_split_fields_69_to_73()
    {
        var workout = Parse(GarminFixture.Writer());
        var climbs = workout.Climbs.ToList();

        Assert.Equal(new ClimbRoute(ClimbGradingScale.French, 11, ClimbResult.Completed, 0, true), climbs[0].Route);
        Assert.Equal(("6b", BoulderStatus.Top), (climbs[0].Route!.Grade, climbs[0].Route!.Status));
        Assert.Equal(new ClimbRoute(ClimbGradingScale.French, 13, ClimbResult.Attempted, 2, null), climbs[1].Route);
        Assert.Equal(("6c", BoulderStatus.Attempt), (climbs[1].Route!.Grade, climbs[1].Route!.Status));
        Assert.Equal(("6b", BoulderStatus.Top), (climbs[2].Route!.Grade, climbs[2].Route!.Status));   // completed with a fall
        Assert.Equal(1, climbs[2].Route!.Falls);
        Assert.All(workout.Segments.Where(x => x.Kind == WorkoutSegmentKind.Rest), r => Assert.Null(r.Route));
    }

    [Fact]
    public void A_climb_without_route_fields_has_no_route_and_values_outside_their_enums_are_left_out()
    {
        Assert.All(Parse(CorosFixture.Writer()).Climbs, c => Assert.Null(c.Route));

        var file = CorosFixture.Writer()
            .Define(7, 312, (0, 1, W.Enum), (1, 4, W.UInt32), (9, 4, W.UInt32), (69, 1, W.Enum), (70, 4, W.UInt32), (71, 1, W.Enum), (73, 1, W.Enum))
            .Data(7, 9L, 1_000L, CorosFixture.T0 + 70, 40L, 11L, 9L, 7L)
            .Build();
        var route = FitWorkoutParser.Parse(file).Climbs.Last().Route!;

        Assert.Equal(new ClimbRoute(null, 11, null, null, null), route);
        Assert.Null(route.Grade);
        Assert.Equal(BoulderStatus.None, route.Status);
    }
}
