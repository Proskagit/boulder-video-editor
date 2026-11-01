using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry;

/// <summary>
/// D049 §6 / D050 amendment B2: <c>w(n) = Z + s(n) + ClockCorrection</c> in ticks; <c>Z = W_split − ClockCorrection − s(n)</c>
/// puts the chosen frame on the split; the mapping of a picture never changes with move, trim, split or slip, a speed
/// change is in <c>s</c>; the clock correction W − C has the sign of «watch ahead → positive»; no time zone is involved.
/// </summary>
public class TrainingAlignmentTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset Zero = new(2026, 9, 27, 7, 30, 26, TimeSpan.Zero);

    private static VideoClip Clip(long startFrame = 100, long sourceInFrame = 50, long durationFrames = 250, ClipSpeed? speed = null) => new()
    {
        MediaAssetId = Guid.NewGuid(), TimelineStart = MediaTime.FromFrame(startFrame, Rate), Duration = MediaTime.FromFrame(durationFrames, Rate),
        SourceIn = MediaTime.FromFrame(sourceInFrame, Rate), SourceOut = MediaTime.FromFrame(sourceInFrame + durationFrames, Rate),
        Speed = speed ?? ClipSpeed.Normal
    };

    private static MediaAsset Asset(DateTimeOffset? zero) => new() { FilePath = "climb.mp4", Kind = MediaKind.Video, RecordingZeroOverrideUtc = zero };

    [Fact]
    public void The_watch_time_is_zero_plus_source_time_plus_the_clock_correction()
    {
        var clip = Clip();
        var s = TrainingAlignment.SourceTimeAt(clip, 120, Rate);                  // 20 frames into the clip, from source frame 50
        Assert.Equal(MediaTime.FromFrame(70, Rate), s);

        Assert.Equal(Zero.AddTicks(s.Ticks + 2 * TimeSpan.TicksPerSecond),
            TrainingAlignment.WatchTime(Zero, s, new MediaTime(2 * TimeSpan.TicksPerSecond)));
    }

    [Fact]
    public void The_sign_of_the_correction_is_watch_minus_camera()
    {
        // The watch ahead of the phone by 2 s: a frame filmed at C 07:30:26 reads the watch sample of 07:30:28.
        Assert.Equal(Zero.AddSeconds(2), TrainingAlignment.WatchTime(Zero, MediaTime.Zero, new MediaTime(2 * TimeSpan.TicksPerSecond)));
        Assert.Equal(Zero.AddSeconds(-2), TrainingAlignment.WatchTime(Zero, MediaTime.Zero, new MediaTime(-2 * TimeSpan.TicksPerSecond)));
        Assert.NotEqual(TrainingAlignment.WatchTime(Zero, MediaTime.Zero, MediaTime.Zero),
            TrainingAlignment.WatchTime(Zero, MediaTime.Zero, new MediaTime(1)));   // never skipped
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2 * TimeSpan.TicksPerSecond)]
    [InlineData(-37 * TimeSpan.TicksPerSecond)]
    public void Matching_a_frame_to_a_split_puts_that_frame_on_the_split(long correctionTicks)
    {
        var correction = new MediaTime(correctionTicks);
        var split = new DateTimeOffset(2026, 9, 27, 7, 31, 21, TimeSpan.Zero);
        var clip = Clip(speed: ClipSpeed.FromSteps(40));
        var s = TrainingAlignment.SourceTimeAt(clip, 180, Rate);

        var zero = TrainingAlignment.ZeroForSplit(split, correction, s);

        Assert.Equal(split, TrainingAlignment.WatchTime(zero, s, correction));
        Assert.Equal(TimeSpan.Zero, zero.Offset);
    }

    [Fact]
    public void A_picture_keeps_its_watch_time_through_move_trim_split_and_slip()
    {
        var clip = Clip(startFrame: 100, sourceInFrame: 50);
        var original = TrainingAlignment.SourceTimeAt(clip, 130, Rate);           // the picture at source frame 80

        var moved = Clip(startFrame: 400, sourceInFrame: 50);                     // move by 300 frames
        Assert.Equal(original, TrainingAlignment.SourceTimeAt(moved, 430, Rate));

        var trimmed = Clip(startFrame: 110, sourceInFrame: 60, durationFrames: 240); // head trim by 10 frames
        Assert.Equal(original, TrainingAlignment.SourceTimeAt(trimmed, 130, Rate));

        var rightPart = Clip(startFrame: 120, sourceInFrame: 70, durationFrames: 230); // split at timeline frame 120
        Assert.Equal(original, TrainingAlignment.SourceTimeAt(rightPart, 130, Rate));

        var slipped = Clip(startFrame: 100, sourceInFrame: 75);                   // slip: another picture at the same frame…
        Assert.Equal(MediaTime.FromFrame(105, Rate), TrainingAlignment.SourceTimeAt(slipped, 130, Rate)); // …its own source time
    }

    [Fact]
    public void A_speed_change_is_in_the_source_time()
    {
        var normal = TrainingAlignment.SourceTimeAt(Clip(), 150, Rate);
        var doubled = TrainingAlignment.SourceTimeAt(Clip(speed: ClipSpeed.FromSteps(40)), 150, Rate);
        var ntsc = TrainingAlignment.SourceTimeAt(Clip(speed: ClipSpeed.FromSteps(10)), 150, Rate);

        Assert.Equal(MediaTime.FromFrame(100, Rate), normal);                     // 50 + 50
        Assert.Equal(MediaTime.FromFrame(150, Rate), doubled);                    // 50 + 2 × 50
        Assert.Equal(MediaTime.FromFrame(75, Rate), ntsc);                        // 50 + 50 / 2
    }

    [Fact]
    public void Instants_with_an_offset_map_to_the_same_watch_time()
    {
        var local = new DateTimeOffset(2026, 9, 27, 10, 30, 26, TimeSpan.FromHours(3));
        Assert.Equal(TrainingAlignment.WatchTime(Zero, MediaTime.Zero, MediaTime.Zero),
            TrainingAlignment.WatchTime(local, MediaTime.Zero, MediaTime.Zero));
        Assert.Equal(TimeSpan.Zero, TrainingAlignment.WatchTime(local, MediaTime.Zero, MediaTime.Zero).Offset);
    }

    [Fact]
    public void The_mapping_goes_from_source_time_to_watch_time_and_back_exactly()
    {
        // Moved from the removed WorkoutVideoAlignment (Step 35.1): one implementation of D049 §6 (D050 amendment B8).
        var zero = CorosFixture.Start.AddSeconds(-90);
        var ntsc = new FrameRate(60000, 1001);
        var correction = MediaTime.FromFrame(3, ntsc);
        var source = MediaTime.FromFrame(5_400, ntsc);                            // ≈ 90.09 s into the video

        var w = TrainingAlignment.WatchTime(zero, source, correction);

        Assert.Equal(zero.UtcTicks + source.Ticks + correction.Ticks, w.UtcTicks);
        Assert.Equal(TimeSpan.Zero, w.Offset);
        Assert.Equal(source, TrainingAlignment.SourceTimeOf(zero, w, correction));
    }

    [Fact]
    public void A_negative_correction_maps_a_frame_to_an_earlier_watch_time()
    {
        var correction = MediaTime.FromSeconds(-2);
        Assert.Equal(CorosFixture.Start.AddSeconds(8), TrainingAlignment.WatchTime(CorosFixture.Start, MediaTime.FromSeconds(10), correction));
        Assert.Equal(MediaTime.FromSeconds(2), TrainingAlignment.SourceTimeOf(CorosFixture.Start, CorosFixture.Start, correction));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 0L)]                                        // one tick: never lost
    [InlineData(-1L, 0L)]                                       // before the video's first picture
    [InlineData(0L, 1L)]
    [InlineData(0L, -1L)]
    [InlineData(864_000_000_000L, -37 * TimeSpan.TicksPerSecond)]   // 24 h into a recording
    [InlineData(-864_000_000_000L, 14 * TimeSpan.TicksPerHour)]     // a correction as large as a time zone: still a clock error
    [InlineData(123_456_789_012L, -98_765_432_109L)]
    public void The_inverse_undoes_the_mapping_at_the_edges(long sourceTicks, long correctionTicks)
    {
        var zero = new DateTimeOffset(2026, 9, 27, 10, 30, 26, TimeSpan.FromHours(3));   // an offset on input: the same instant
        var source = new MediaTime(sourceTicks);
        var correction = new MediaTime(correctionTicks);

        var w = TrainingAlignment.WatchTime(zero, source, correction);
        Assert.Equal(TimeSpan.Zero, w.Offset);
        Assert.Equal(source, TrainingAlignment.SourceTimeOf(zero, w, correction));
        Assert.Equal(zero.UtcTicks, TrainingAlignment.ZeroForSplit(w, correction, source).UtcTicks);
        Assert.Equal(w, TrainingAlignment.WatchTime(TrainingAlignment.ZeroForSplit(w, correction, source), source, correction));
    }

    [Fact]
    public void A_mapping_outside_the_calendar_is_refused_not_wrapped()
    {
        var zero = DateTimeOffset.MaxValue.AddTicks(-10);
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainingAlignment.WatchTime(zero, new MediaTime(11), MediaTime.Zero));
        Assert.Throws<OverflowException>(() => TrainingAlignment.WatchTime(zero, new MediaTime(long.MaxValue), MediaTime.Zero));
        Assert.Throws<OverflowException>(() =>
            TrainingAlignment.SourceTimeOf(DateTimeOffset.MaxValue, DateTimeOffset.MinValue, new MediaTime(long.MaxValue)));
    }

    [Fact]
    public void The_climbs_in_a_source_range_come_with_their_source_interval()
    {
        var workout = CorosWorkoutParser.Parse(CorosFixture.Writer().Build());     // climbs [0, 12.34 s) and [40, 55.5 s)
        var zero = CorosFixture.Start.AddSeconds(-5);                               // the video starts 5 s before the training
        var correction = MediaTime.FromSeconds(1);                                  // the watch 1 s ahead of the phone

        var all = TrainingAlignment.ClimbsInSource(workout, zero, correction, MediaTime.Zero, MediaTime.FromSeconds(70));
        Assert.Equal(new[] { 1, 2 }, all.Select(c => c.Climb.ClimbNumber!.Value));
        var first = all[0];
        Assert.Equal(MediaTime.FromSeconds(4), first.SourceStart);                  // w = zero + 4 + 1 = the climb's start
        Assert.Equal(MediaTime.FromSeconds(4) + first.Climb.Elapsed, first.SourceEnd);   // the one boundary rule

        var gap = TrainingAlignment.ClimbsInSource(workout, zero, correction, MediaTime.FromSeconds(20), MediaTime.FromSeconds(30));
        Assert.Empty(gap);                                                          // the rest is not a climb

        // [start, end) on both sides: climb 1 ends at source 16.34 s, climb 2 starts at source 44 s.
        var edge = TrainingAlignment.ClimbsInSource(workout, zero, correction, MediaTime.FromSeconds(16), MediaTime.FromSeconds(44));
        Assert.Equal(new[] { 1 }, edge.Select(c => c.Climb.ClimbNumber!.Value));
        Assert.Empty(TrainingAlignment.ClimbsInSource(workout, zero, correction, MediaTime.FromSeconds(17), MediaTime.FromSeconds(44)));
        var second = Assert.Single(TrainingAlignment.ClimbsInSource(workout, zero, correction, MediaTime.FromSeconds(17), MediaTime.FromSeconds(45)));
        Assert.Equal(MediaTime.FromSeconds(44), second.SourceStart);
    }

    [Fact]
    public void The_readout_names_why_there_is_no_value()
    {
        var workout = CorosWorkoutParser.Parse(CorosFixture.Writer().Build());     // 07:02:39Z…07:03:39Z, HR 100 + s
        var training = new TrainingReference
        {
            FilePath = "x.fit", FileSizeBytes = 1, Sha256 = new string('0', 64), Activity = TrainingActivity.Of(workout),
            Workout = workout, Availability = TrainingAvailability.Available
        };
        var clip = Clip(startFrame: 0, sourceInFrame: 0, durationFrames: 1500);    // 60 s
        var zero = CorosFixture.Start;                                              // source 0 = the training's start

        Assert.Equal(TrainingReadoutReason.NoTraining, TrainingAlignment.ReadoutAt(null, clip, Asset(zero), 0, Rate).Reason);
        Assert.Equal(TrainingReadoutReason.RecordingStartUnknown, TrainingAlignment.ReadoutAt(training, clip, Asset(null), 0, Rate).Reason);
        var tagged = Asset(null);                                                   // a recognised tag is a hypothesis, not a zero
        tagged.Metadata = new MediaMetadata { RecordingTimeProbe = RecordingTimeProbe.Found("2026-09-27T07:03:39Z", RecordingTimeTagOrigin.Format) };
        Assert.Equal(TrainingReadoutReason.RecordingStartUnknown, TrainingAlignment.ReadoutAt(training, clip, tagged, 250, Rate).Reason);
        Assert.Equal(TrainingReadoutReason.OutsideClip, TrainingAlignment.ReadoutAt(training, clip, Asset(zero), 9999, Rate).Reason);

        var at10s = TrainingAlignment.ReadoutAt(training, clip, Asset(zero), 250, Rate);  // 10 s in
        Assert.Equal((CorosFixture.Start.AddSeconds(10), 110), (at10s.WatchTime!.Value, at10s.Bpm!.Value));

        var inPause = TrainingAlignment.ReadoutAt(training, clip, Asset(zero), 625, Rate); // 25 s in: the timer paused 20 → 30
        Assert.Equal(TrainingReadoutReason.NoSample, inPause.Reason);

        training.Availability = TrainingAvailability.Missing;
        training.Workout = null;
        Assert.Equal(TrainingReadoutReason.TrainingNotAvailable, TrainingAlignment.ReadoutAt(training, clip, Asset(zero), 250, Rate).Reason);
    }
}
