using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry;

/// <summary>
/// D049 §5, D050 amendments B1 / B8: the one estimator of the recording zero — the override or «unknown» with its reason,
/// never «automatic» before the rule is approved, whatever the tag says; the tag and file-name hypotheses are described,
/// never used as a zero; the file name's local time is converted only with an explicit offset hypothesis.
/// </summary>
public class RecordingZeroEstimatorTests
{
    private static readonly DateTimeOffset Zero = new(2026, 9, 27, 7, 30, 26, TimeSpan.Zero);

    private static MediaAsset Video(string name = "video_20260927_103026.mp4", DateTimeOffset? overrideUtc = null,
        RecordingTimeProbe? probe = null, double? seconds = 282.0) => new()
    {
        FilePath = Path.Combine("D:", name), Kind = MediaKind.Video, RecordingZeroOverrideUtc = overrideUtc,
        Metadata = new MediaMetadata
        {
            Duration = seconds is { } s ? MediaTime.FromSeconds(s) : MediaTime.Zero, RecordingTimeProbe = probe
        }
    };

    [Fact]
    public void The_override_is_the_zero_in_utc()
    {
        var zero = RecordingZeroEstimator.Resolve(Video(overrideUtc: Zero.ToOffset(TimeSpan.FromHours(3))));
        Assert.Equal(new RecordingZero(Zero, RecordingZeroState.Override, null), zero);
        Assert.Equal(TimeSpan.Zero, zero.Utc!.Value.Offset);
    }

    [Fact]
    public void Without_an_override_the_zero_is_unknown_even_with_a_recognised_tag()
    {
        var tagged = Video(probe: RecordingTimeProbe.Found("2026-09-27T07:35:07.000000Z", RecordingTimeTagOrigin.Format));
        Assert.Equal(RecordingZero.Unknown(RecordingZeroUnknownReason.NoApprovedRule), RecordingZeroEstimator.Resolve(tagged));
        Assert.Equal(RecordingZero.Unknown(RecordingZeroUnknownReason.NoApprovedRule), RecordingZeroEstimator.Resolve(Video()));
    }

    [Fact]
    public void Only_a_video_has_a_zero()
    {
        var audio = new MediaAsset { FilePath = "a.wav", Kind = MediaKind.Audio, RecordingZeroOverrideUtc = Zero };
        Assert.Equal(RecordingZero.Unknown(RecordingZeroUnknownReason.NotAVideo), RecordingZeroEstimator.Resolve(audio));
    }

    [Fact]
    public void The_tag_hypotheses_are_the_end_and_the_start_of_the_recording()
    {
        var tag = new DateTimeOffset(2026, 9, 27, 7, 35, 7, TimeSpan.Zero);
        var c = RecordingZeroEstimator.Candidates(
            Video(probe: RecordingTimeProbe.Found("2026-09-27T10:35:07+03:00", RecordingTimeTagOrigin.VideoStream), seconds: 282.5));

        Assert.Equal(RecordingTimeTagStatus.Recognised, c.TagStatus);
        Assert.Equal(tag, c.TagUtc);
        Assert.Equal(tag.AddTicks(-MediaTime.FromSeconds(282.5).Ticks), c.TagMarksEnd);
        Assert.Equal(tag, c.TagMarksStart);
    }

    [Fact]
    public void The_tag_status_says_why_there_is_no_tag_hypothesis()
    {
        Assert.Equal(RecordingTimeTagStatus.NotProbed, RecordingZeroEstimator.Candidates(Video()).TagStatus);
        Assert.Equal(RecordingTimeTagStatus.Absent, RecordingZeroEstimator.Candidates(Video(probe: RecordingTimeProbe.Absent)).TagStatus);

        var unrecognised = RecordingZeroEstimator.Candidates(
            Video(probe: RecordingTimeProbe.Found("2026-09-27 07:35:07", RecordingTimeTagOrigin.Format)));
        Assert.Equal(RecordingTimeTagStatus.Unrecognised, unrecognised.TagStatus);
        Assert.Null(unrecognised.TagMarksEnd);
        Assert.Null(unrecognised.TagMarksStart);

        var noDuration = RecordingZeroEstimator.Candidates(
            Video(probe: RecordingTimeProbe.Found("2026-09-27T07:35:07Z", RecordingTimeTagOrigin.Format), seconds: null));
        Assert.Null(noDuration.TagMarksEnd);
        Assert.NotNull(noDuration.TagMarksStart);
    }

    [Fact]
    public void The_file_name_time_is_converted_only_with_an_explicit_offset_once()
    {
        var local = new DateTime(2026, 9, 27, 10, 30, 26, DateTimeKind.Unspecified);
        var c = RecordingZeroEstimator.Candidates(Video(), TimeSpan.FromHours(3));
        Assert.Equal(local, c.FileNameLocalTime);
        Assert.Equal(DateTimeKind.Unspecified, c.FileNameLocalTime!.Value.Kind);
        Assert.Equal(Zero, c.FromFileName);                               // 10:30:26 at +3 h = 07:30:26Z, applied once
        Assert.Equal(TimeSpan.Zero, c.FromFileName!.Value.Offset);

        Assert.Null(RecordingZeroEstimator.Candidates(Video()).FromFileName);                  // no hypothesis: no check
        Assert.Equal(Zero.AddHours(-2), RecordingZeroEstimator.Candidates(Video(), TimeSpan.FromHours(5)).FromFileName);
        Assert.Null(RecordingZeroEstimator.Candidates(Video("clip.mp4"), TimeSpan.FromHours(3)).FileNameLocalTime);
    }

    [Theory]
    [InlineData("video_20260927_103026.mp4", "2026-09-27T10:30:26")]
    [InlineData("VID_20260927_103026_001.mp4", "2026-09-27T10:30:26")]
    [InlineData("video_20260230_103026.mp4", null)]       // no such day
    [InlineData("video_20260927_253026.mp4", null)]
    [InlineData("video_120260927_103026.mp4", null)]      // more digits than a date
    [InlineData("holiday.mp4", null)]
    public void The_file_name_stamp_is_read_strictly(string name, string? expected) =>
        Assert.Equal(expected is null ? null : DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            RecordingZeroEstimator.FileNameLocalTime(name));
}
