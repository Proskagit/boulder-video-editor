using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>Phase 10 Step 10.3 (D025 §1, §3): frames of a duration, the zone split around the cut and the
/// structural validation of a track's transitions.</summary>
public class TransitionRulesTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;   // 400 000 ticks per frame
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(199_999, 0)]
    [InlineData(200_000, 1)]      // a tie goes up (ToNearestFrame)
    [InlineData(400_000, 1)]
    [InlineData(10_000_000, 25)]
    public void Frames_are_the_nearest_whole_number_of_frames(long ticks, long frames) =>
        Assert.Equal(frames, TransitionRules.Frames(new MediaTime(ticks), Rate));

    [Fact]
    public void A_stored_frame_count_reads_back_exactly_on_an_ntsc_grid()
    {
        for (long n = 0; n < 200; n++)
            Assert.Equal(n, TransitionRules.Frames(MediaTime.FromFrame(n, FrameRate.Ntsc30), FrameRate.Ntsc30));
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(7, 3, 4)]
    [InlineData(10, 5, 5)]
    [InlineData(25, 12, 13)]
    public void The_zone_puts_the_floor_half_before_the_cut_and_the_ceiling_half_after(long frames, long before, long after) =>
        Assert.Equal((before, after), TransitionRules.Zone(frames));

    private static (Track Track, Clip A, Clip B) Cut(long aFrames = 20, long bFrames = 20)
    {
        var a = new TextClip { TimelineStart = F(0), Duration = F(aFrames) };
        var b = new TextClip { TimelineStart = F(aFrames), Duration = F(bFrames) };
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        track.Clips.AddRange(new Clip[] { a, b });
        return (track, a, b);
    }

    private static Transition Dissolve(Clip a, Clip b, long frames) =>
        new() { TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(frames), LeftClipId = a.Id, RightClipId = b.Id };

    [Fact]
    public void A_track_without_transitions_is_valid() => Assert.Null(TransitionRules.ValidateTrack(Cut().Track, Rate));

    [Fact]
    public void A_dissolve_on_a_cut_is_valid()
    {
        var (track, a, b) = Cut();
        track.Transitions.Add(Dissolve(a, b, 2));
        Assert.Null(TransitionRules.ValidateTrack(track, Rate));
    }

    [Theory]
    [InlineData(40, 20, 20, true)]    // 20 | 20: exactly fills both clips
    [InlineData(41, 20, 20, false)]   // 20 in A, 21 in B (20): B is too short
    [InlineData(41, 19, 21, false)]   // 20 in A (19): A is too short
    [InlineData(41, 20, 21, true)]
    public void The_zone_must_fit_both_clips(long frames, long aFrames, long bFrames, bool valid)
    {
        var (track, a, b) = Cut(aFrames, bFrames);
        track.Transitions.Add(Dissolve(a, b, frames));
        Assert.Equal(valid, TransitionRules.ValidateTrack(track, Rate) is null);
    }

    [Fact]
    public void A_one_frame_dissolve_is_invalid()
    {
        var (track, a, b) = Cut();
        track.Transitions.Add(Dissolve(a, b, 1));
        Assert.NotNull(TransitionRules.ValidateTrack(track, Rate));
    }

    [Fact]
    public void Clips_with_a_gap_are_invalid()
    {
        var (track, a, b) = Cut();
        b.TimelineStart = F(21);
        track.Transitions.Add(Dissolve(a, b, 4));
        Assert.Equal("A transition's clips don't touch.", TransitionRules.ValidateTrack(track, Rate));
    }

    [Fact]
    public void An_audio_track_has_no_transitions()
    {
        var (track, a, b) = Cut();
        track.Type = TrackType.Audio;
        track.Transitions.Add(Dissolve(a, b, 4));
        Assert.Equal("Only video tracks have transitions.", TransitionRules.ValidateTrack(track, Rate));
    }

    [Theory]
    [InlineData(60, 10, 40, 60, 20)]     // after the cut: 10 frames of B → F = 2·10
    [InlineData(20, 30, 30, 20, 41)]     // before the cut limits: ⌊F/2⌋ = 20, ⌈F/2⌉ = 21 → 41
    [InlineData(100, 100, 0, 100, 0)]    // no handle after A: nothing fits
    [InlineData(100, 100, 100, 0, 1)]    // no handle before B: 1 frame (< 2: none)
    [InlineData(5, 5, 5, 5, 10)]
    public void The_longest_dissolve_fits_both_halves(long roomInLeft, long roomInRight, long handleAfterLeft, long handleBeforeRight, long expected)
    {
        var max = TransitionRules.MaxFrames(roomInLeft, roomInRight, handleAfterLeft, handleBeforeRight);
        Assert.Equal(expected, max);
        var (before, after) = TransitionRules.Zone(max);
        Assert.True(before <= Math.Min(roomInLeft, handleBeforeRight) && after <= Math.Min(roomInRight, handleAfterLeft));
        var (nextBefore, nextAfter) = TransitionRules.Zone(max + 1);
        Assert.False(nextBefore <= Math.Min(roomInLeft, handleBeforeRight) && nextAfter <= Math.Min(roomInRight, handleAfterLeft));
    }

    [Fact]
    public void Clip_frames_count_the_grid_frames_between_its_edges() =>
        Assert.Equal(20, TransitionRules.ClipFrames(Cut().A, Rate));
}
