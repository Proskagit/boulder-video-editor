using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 28 Step 28.2 (D044 §1, §3–§5): the library's types on their tracks, the source-handle requirement, the push's
/// direction, the defaults and input ranges in whole frames on the eight selectable rates (D044 §5's table), the 0.1 s
/// grid, the clamp of a type change and the 1.0 s rule between fill peaks.
/// </summary>
public class TransitionLibraryRulesTests
{
    private static FrameRate Rate(string fps) => fps switch
    {
        "23.976" => FrameRate.Ntsc24, "24" => FrameRate.Fps24, "25" => FrameRate.Fps25, "29.97" => FrameRate.Ntsc30,
        "30" => FrameRate.Fps30, "50" => new FrameRate(50, 1), "59.94" => FrameRate.Ntsc60, "60" => FrameRate.Fps60,
        _ => throw new ArgumentOutOfRangeException(nameof(fps))
    };

    public static TheoryData<string> Rates => new() { "23.976", "24", "25", "29.97", "30", "50", "59.94", "60" };

    [Fact]
    public void The_library_has_five_video_types_and_the_audio_track_keeps_the_crossfade()
    {
        Assert.Equal(new[] { "crossDissolve", "dipToBlack", "flash", "zoom", "push" }, TransitionRules.VideoTypes);
        foreach (var type in TransitionRules.VideoTypes)
        {
            Assert.True(TransitionRules.Allows(TrackType.Video, type), type);
            Assert.False(TransitionRules.Allows(TrackType.Audio, type), type);
        }
        Assert.True(TransitionRules.Allows(TrackType.Audio, TransitionRules.Crossfade));
        Assert.False(TransitionRules.Allows(TrackType.Video, TransitionRules.Crossfade));
        Assert.False(TransitionRules.Allows(TrackType.Video, "wipe"));
        Assert.False(TransitionRules.Allows(TrackType.Video, null));
        Assert.Equal(TransitionRules.CrossDissolve, TransitionRules.TypeFor(TrackType.Video));
        Assert.Equal(TransitionRules.Crossfade, TransitionRules.TypeFor(TrackType.Audio));
    }

    [Theory]
    [InlineData("crossDissolve", true, false, false)]
    [InlineData("dipToBlack", false, true, false)]
    [InlineData("flash", false, true, false)]
    [InlineData("zoom", false, false, false)]
    [InlineData("push", true, false, true)]
    [InlineData("crossfade", true, false, false)]
    public void Handles_fill_and_direction_by_type(string type, bool handles, bool fill, bool direction)
    {
        Assert.Equal(handles, TransitionRules.RequiresHandles(type));
        Assert.Equal(fill, TransitionRules.IsFill(type));
        Assert.Equal(direction, TransitionRules.HasDirection(type));
        Assert.Equal(direction ? TransitionDirection.FromRight : null, TransitionRules.DefaultDirection(type));
    }

    // D044 §5's table: the defaults by ToNearestFrame (ties up), the minima rounded up, the maxima rounded down.
    [Theory]
    [InlineData("23.976", 24, 14, 7, 5, 3, 5, 11, 23)]
    [InlineData("24", 24, 14, 7, 5, 3, 5, 12, 24)]
    [InlineData("25", 25, 15, 8, 5, 3, 5, 12, 25)]
    [InlineData("29.97", 30, 18, 9, 6, 3, 6, 14, 29)]
    [InlineData("30", 30, 18, 9, 6, 3, 6, 15, 30)]
    [InlineData("50", 50, 30, 15, 10, 5, 10, 25, 50)]
    [InlineData("59.94", 60, 36, 18, 12, 6, 12, 29, 59)]
    [InlineData("60", 60, 36, 18, 12, 6, 12, 30, 60)]
    public void Defaults_and_limits_in_whole_frames_match_the_table(string fps, long dissolve, long dip, long zoomOrPush, long flash,
        long min01, long min02, long max05, long max10)
    {
        var rate = Rate(fps);
        Assert.Equal(dissolve, TransitionRules.DefaultFramesOf(TransitionRules.CrossDissolve, rate));
        Assert.Equal(dip, TransitionRules.DefaultFramesOf(TransitionRules.DipToBlack, rate));
        Assert.Equal(flash, TransitionRules.DefaultFramesOf(TransitionRules.Flash, rate));
        Assert.Equal(zoomOrPush, TransitionRules.DefaultFramesOf(TransitionRules.Zoom, rate));
        Assert.Equal(zoomOrPush, TransitionRules.DefaultFramesOf(TransitionRules.Push, rate));

        foreach (var type in new[] { TransitionRules.CrossDissolve, TransitionRules.DipToBlack, TransitionRules.Flash })
            Assert.Equal(min01, TransitionRules.MinFramesOf(type, rate));
        Assert.Equal(min02, TransitionRules.MinFramesOf(TransitionRules.Zoom, rate));
        Assert.Equal(min02, TransitionRules.MinFramesOf(TransitionRules.Push, rate));
        Assert.Equal(max05, TransitionRules.MaxFramesOf(TransitionRules.Flash, rate));
        Assert.Equal(max10, TransitionRules.MaxFramesOf(TransitionRules.Zoom, rate));
        foreach (var type in new[] { TransitionRules.CrossDissolve, TransitionRules.DipToBlack, TransitionRules.Push, TransitionRules.Crossfade })
            Assert.Equal(long.MaxValue, TransitionRules.MaxFramesOf(type, rate));
        Assert.Equal(TransitionRules.MinFrames, TransitionRules.MinFramesOf(TransitionRules.Crossfade, rate));   // D032 unchanged
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void The_limits_never_promise_more_than_the_frames_give(string fps)
    {
        var rate = Rate(fps);
        foreach (var type in TransitionRules.VideoTypes)
        {
            var limits = TransitionRules.LimitsOf(type);
            var min = TransitionRules.MinFramesOf(type, rate);
            Assert.True(MediaTime.FromFrame(min, rate) >= limits.Min!.Value, $"{type} {fps}: the minimum is never shorter");
            Assert.True(MediaTime.FromFrame(min - 1, rate) < limits.Min.Value, $"{type} {fps}: the minimum is the shortest such");
            if (limits.Max is { } maxTime)
            {
                var max = TransitionRules.MaxFramesOf(type, rate);
                Assert.True(MediaTime.FromFrame(max, rate) <= maxTime, $"{type} {fps}: the maximum is never longer");
                Assert.True(MediaTime.FromFrame(max + 1, rate) > maxTime, $"{type} {fps}: the maximum is the longest such");
            }
            var defaults = TransitionRules.DefaultFramesOf(type, rate);
            Assert.InRange(defaults, min, TransitionRules.MaxFramesOf(type, rate));
        }
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void The_tenth_of_a_second_grid_maps_to_strictly_increasing_frames_that_read_back_to_their_point(string fps)
    {
        var rate = Rate(fps);
        var previous = -1L;
        for (var tenths = 1; tenths <= 30; tenths++)
        {
            var frames = TransitionRules.Frames(new MediaTime(tenths * 1_000_000L), rate);
            Assert.True(frames > previous, $"{fps}: {tenths / 10.0} s");
            previous = frames;
            var seconds = MediaTime.FromFrame(frames, rate).TotalSeconds;
            Assert.Equal(tenths, (int)Math.Round(seconds * 10, MidpointRounding.AwayFromZero));
        }
    }

    [Fact]
    public void A_type_change_keeps_a_duration_in_range_and_clamps_to_the_nearest_allowed_value()
    {
        var rate = FrameRate.Fps30;
        Assert.Equal(6, TransitionRules.ClampFrames(TransitionRules.Zoom, 6, long.MaxValue, rate));        // Flash 0.2 s → Zoom: kept
        Assert.Equal(15, TransitionRules.ClampFrames(TransitionRules.Flash, 30, long.MaxValue, rate));     // Dissolve 1 s → Flash: 0.5 s
        Assert.Equal(6, TransitionRules.ClampFrames(TransitionRules.Push, 3, long.MaxValue, rate));        // below Push's 0.2 s
        Assert.Equal(10, TransitionRules.ClampFrames(TransitionRules.Push, 18, 10, rate));                // to the longest that fits
        Assert.Null(TransitionRules.ClampFrames(TransitionRules.Push, 18, 5, rate));                      // less than the minimum fits
        Assert.Equal(30, TransitionRules.ClampFrames(TransitionRules.Zoom, 45, 60, rate));                // the type's maximum wins
    }

    // --- validation ---------------------------------------------------------------------------------------------------

    private static readonly FrameRate R = FrameRate.Fps30;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, R);

    private static (Track Track, Clip A, Clip B) Cut(TrackType type = TrackType.Video)
    {
        var a = new TextClip { TimelineStart = F(0), Duration = F(30) };
        var b = new TextClip { TimelineStart = F(30), Duration = F(30) };
        var track = new Track { Name = "V1", Type = type };
        track.Clips.AddRange(new Clip[] { a, b });
        return (track, a, b);
    }

    private static string? Validate(string type, TransitionDirection? direction, TrackType trackType = TrackType.Video)
    {
        var (track, a, b) = Cut(trackType);
        track.Transitions.Add(new Transition { TransitionTypeId = type, Duration = F(10), LeftClipId = a.Id, RightClipId = b.Id, Direction = direction });
        return TransitionRules.ValidateTrack(track, R);
    }

    [Fact]
    public void Every_video_type_is_valid_on_a_video_track_and_only_a_push_has_a_direction()
    {
        foreach (var type in TransitionRules.VideoTypes.Where(t => t != TransitionRules.Push))
        {
            Assert.Null(Validate(type, null));
            Assert.Equal(Messages.TransitionRule_Direction, Validate(type, TransitionDirection.FromLeft));
        }
        foreach (var direction in Enum.GetValues<TransitionDirection>())
            Assert.Null(Validate(TransitionRules.Push, direction));
        Assert.Equal(Messages.TransitionRule_Direction, Validate(TransitionRules.Push, null));
        Assert.Equal(Messages.TransitionRule_Direction, Validate(TransitionRules.Push, (TransitionDirection)42));
    }

    [Fact]
    public void A_video_type_on_an_audio_track_or_an_unknown_type_is_invalid()
    {
        Assert.Equal(Messages.TransitionRule_VideoOnly, Validate(TransitionRules.Zoom, null, TrackType.Audio));
        Assert.Equal(Messages.TransitionRule_AudioOnly, Validate(TransitionRules.Crossfade, null));
        Assert.Equal(Messages.TransitionRule_UnknownType, Validate("wipe", null));
        Assert.Null(Validate(TransitionRules.Crossfade, null, TrackType.Audio));
    }

    [Theory]
    [InlineData("flash", "flash", 29, false)]
    [InlineData("flash", "flash", 30, true)]                 // exactly 1.0 s apart is allowed
    [InlineData("flash", "dipToBlack", 29, false)]           // a short dip is a dark flash (D044 §4)
    [InlineData("dipToBlack", "dipToBlack", 29, false)]
    [InlineData("flash", "crossDissolve", 1, true)]          // only fill transitions count
    [InlineData("flash", "zoom", 1, true)]
    [InlineData("flash", "push", 1, true)]
    public void Fill_peaks_are_at_least_one_second_apart(string first, string second, long framesApart, bool valid)
    {
        var peaks = new[] { (first, F(40)), (second, F(40 + framesApart)) };
        Assert.Equal(valid ? null : Messages.TransitionRule_FillPeaksTooClose, TransitionRules.ValidateFillPeaks(peaks));
    }

    [Fact]
    public void The_fill_peak_rule_spans_every_video_track()
    {
        Track TrackWith(string type, long cut)
        {
            var a = new TextClip { TimelineStart = F(0), Duration = F(cut) };
            var b = new TextClip { TimelineStart = F(cut), Duration = F(30) };
            var track = new Track { Name = "V", Type = TrackType.Video };
            track.Clips.AddRange(new Clip[] { a, b });
            track.Transitions.Add(new Transition { TransitionTypeId = type, Duration = F(6), LeftClipId = a.Id, RightClipId = b.Id });
            return track;
        }

        Assert.Equal(Messages.TransitionRule_FillPeaksTooClose,
            TransitionRules.ValidateFillPeaks(new[] { TrackWith(TransitionRules.Flash, 30), TrackWith(TransitionRules.Flash, 50) }));
        Assert.Null(TransitionRules.ValidateFillPeaks(new[] { TrackWith(TransitionRules.Flash, 30), TrackWith(TransitionRules.Flash, 60) }));
    }
}
