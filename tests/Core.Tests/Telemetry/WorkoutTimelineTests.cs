using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry;

public class WorkoutTimelineTests
{
    private static readonly ClimbingWorkout Workout = CorosWorkoutParser.Parse(CorosFixture.Writer().Build());
    private static DateTimeOffset At(double seconds) => CorosFixture.Start.AddTicks((long)(seconds * TimeSpan.TicksPerSecond));

    [Theory]
    [InlineData(0.0, 100)]
    [InlineData(5.0, 105)]
    [InlineData(5.999, 105)]   // the latest sample at or before the instant — never interpolated
    [InlineData(19.9, 119)]    // just before the pause starts
    [InlineData(30.0, 130)]
    [InlineData(60.0, 160)]
    public void The_heart_rate_is_the_latest_sample_at_or_before_the_instant(double seconds, int bpm) =>
        Assert.Equal(bpm, WorkoutTimeline.HeartRateAt(Workout, At(seconds)));

    [Theory]
    [InlineData(-0.001)]   // before the recording
    [InlineData(60.001)]   // after the recording
    [InlineData(20.0)]     // the pause starts at the stop event: the sample taken then is not held into it
    [InlineData(25.0)]     // inside the pause
    public void No_heart_rate_outside_the_recording_or_inside_a_pause(double seconds) =>
        Assert.Null(WorkoutTimeline.HeartRateAt(Workout, At(seconds)));

    [Fact]
    public void A_sample_older_than_the_limit_is_not_shown()
    {
        Assert.Equal(105, WorkoutTimeline.HeartRateAt(Workout, At(5.5), TimeSpan.FromSeconds(1)));
        Assert.Null(WorkoutTimeline.HeartRateAt(Workout, At(5.5), TimeSpan.FromSeconds(0.25)));
    }

    [Fact]
    public void A_split_ends_at_its_start_plus_its_elapsed_time_not_at_the_stated_whole_second()
    {
        var first = Workout.Segments[0];                                    // stated end T0 + 12, elapsed 12.34 s
        Assert.Equal(At(12), first.EndUtc);                                 // the file's value, kept as stated
        Assert.Equal(At(0).AddTicks(12_340 * TimeSpan.TicksPerMillisecond), first.End);
        Assert.Equal(new WorkoutInterval(At(0), first.End), first.Interval);

        var last = Workout.Segments[2];                                     // no stated end: start + elapsed either way
        Assert.Equal(last.EndUtc, last.End);
    }

    [Fact]
    public void A_split_without_an_elapsed_time_ends_at_its_stated_end()
    {
        var split = new WorkoutSegment(0, WorkoutSegmentKind.Climb, 1, At(0), At(5), MediaTime.Zero, null, null);
        Assert.Equal(At(5), split.End);
        Assert.Equal(new WorkoutInterval(At(0), At(5)), split.Interval);
    }

    [Fact]
    public void The_splits_overlapping_a_range_are_found()
    {
        var hits = WorkoutTimeline.SegmentsOverlapping(Workout, At(10), At(41)).Select(s => s.Index);
        Assert.Equal(new[] { 0, 1, 2 }, hits);

        var climbOnly = WorkoutTimeline.SegmentsOverlapping(Workout, At(0), At(12)).Select(s => s.Index);
        Assert.Equal(new[] { 0 }, climbOnly);   // the rest starts exactly at the range's end: [start, end)
    }
}
