using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry;

/// <summary>D054 §2–§3: the time a video is bound to the FIT by — its Z, else the tag read as the end, else the file-name
/// time under the watch's offset, else none — and the climbs a clip shows (at least half of the estimated climb).</summary>
public sealed class FitRouteBindingTests
{
    private static readonly DateTimeOffset W0 = new(2026, 9, 27, 7, 0, 0, TimeSpan.Zero);
    private static readonly ClimbingWorkout Workout = new()
    {
        DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = W0, EndUtc = W0.AddHours(1), UtcOffset = TimeSpan.FromHours(3),
        ElapsedTime = MediaTime.FromSeconds(3600), TimerTime = MediaTime.FromSeconds(3600), Sport = 31, SubSport = 69,
        AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Warnings = [],
        // Estimated (start − 12 s, end − 4.6 s): climb 1 [88, 115.4] s, climb 2 [288, 305.4] s after W0.
        Segments = new[]
        {
            new WorkoutSegment(0, WorkoutSegmentKind.Climb, 1, W0.AddSeconds(100), W0.AddSeconds(120), MediaTime.FromSeconds(20), null, null),
            new WorkoutSegment(1, WorkoutSegmentKind.Rest, null, W0.AddSeconds(120), W0.AddSeconds(300), MediaTime.FromSeconds(180), null, null),
            new WorkoutSegment(2, WorkoutSegmentKind.Climb, 2, W0.AddSeconds(300), W0.AddSeconds(310), MediaTime.FromSeconds(10), null, null),
        }
    };

    private static MediaAsset Video(string name, string? tag = null, DateTimeOffset? zero = null) => new()
    {
        FilePath = @"C:\v\" + name, Kind = MediaKind.Video, RecordingZeroOverrideUtc = zero,
        Metadata = new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(60), FrameRate = FrameRate.Fps25,
            RecordingTimeProbe = tag is null ? RecordingTimeProbe.Absent : RecordingTimeProbe.Found(tag, RecordingTimeTagOrigin.Format)
        }
    };

    [Fact]
    public void The_zero_wins_then_the_tag_as_the_end_then_the_file_name_then_nothing()
    {
        var all = Video("video_20260927_100100.mp4", "2026-09-27T07:03:00.000000Z", W0.AddSeconds(5));
        Assert.Equal(new FitBindingTime(W0.AddSeconds(5), FitBindingTimeSource.RecordingZero), FitRouteBinding.TimeOf(all, Workout));
        Assert.False(FitRouteBinding.TimeOf(all, Workout)!.Value.IsApproximate);

        var tagged = Video("clip.mp4", "2026-09-27T07:03:00.000000Z");
        Assert.Equal(new FitBindingTime(W0.AddSeconds(120), FitBindingTimeSource.RecordingTag), FitRouteBinding.TimeOf(tagged, Workout));
        var agreeing = Video("video_20260927_100204.mp4", "2026-09-27T07:03:00.000000Z");   // the name 4 s later: one time
        Assert.Equal(FitBindingTimeSource.RecordingTag, FitRouteBinding.TimeOf(agreeing, Workout)!.Value.Source);

        // The tag gives 07:02:00, the name 07:01:00: a file edited after the recording — no time, said, never a guess.
        var edited = Video("video_20260927_100100.mp4", "2026-09-27T07:03:00.000000Z");
        Assert.Null(FitRouteBinding.TimeOf(edited, Workout));
        Assert.True(FitRouteBinding.FileTimesConflict(edited, Workout));
        Assert.False(FitRouteBinding.FileTimesConflict(agreeing, Workout));
        edited.RecordingZeroOverrideUtc = W0;                                  // a Z set by the user settles it
        Assert.Equal(FitBindingTimeSource.RecordingZero, FitRouteBinding.TimeOf(edited, Workout)!.Value.Source);

        var named = Video("video_20260927_100100.mp4");                       // 10:01 local at UTC+3
        Assert.Equal(new FitBindingTime(W0.AddSeconds(60), FitBindingTimeSource.FileName), FitRouteBinding.TimeOf(named, Workout));
        Assert.True(FitRouteBinding.TimeOf(named, Workout)!.Value.IsApproximate);

        Assert.Null(FitRouteBinding.TimeOf(Video("clip.mp4"), Workout));
        Assert.Null(FitRouteBinding.TimeOf(new MediaAsset { FilePath = @"C:\v\video_20260927_100100.mp3", Kind = MediaKind.Audio }, Workout));
    }

    [Theory]
    [InlineData(60, 120, new[] { 1 })]         // climb 1 [88, 115.4] wholly inside
    [InlineData(100, 160, new[] { 1 })]        // 15.4 s of 27.4 — more than half
    [InlineData(104, 160, new int[0])]         // 11.4 s of 27.4 — less than half: the other clip's
    [InlineData(60, 320, new[] { 1, 2 })]      // both, in file order
    [InlineData(200, 260, new int[0])]         // the rest only
    public void A_clip_shows_a_climb_when_at_least_half_of_it_lies_in_its_source_range(int from, int to, int[] numbers)
    {
        var clip = new VideoClip { MediaAssetId = Guid.NewGuid(), SourceIn = MediaTime.FromSeconds(from), SourceOut = MediaTime.FromSeconds(to), Duration = MediaTime.FromSeconds(to - from) };
        var shown = FitRouteBinding.ClimbsShown(Workout, new FitBindingTime(W0, FitBindingTimeSource.RecordingTag), MediaTime.Zero, 120_000_000, clip);
        Assert.Equal(numbers, shown.Select(s => s.Climb.ClimbNumber!.Value));
    }
}
