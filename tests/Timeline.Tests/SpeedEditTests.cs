using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 7 Step 9 (D022): SetClipSpeed and the timeline edits of clips at other speeds, all with the
/// one rounding rule SourceLength(N) = ⌊N·s·10⁷/R⌋ / FramesFor. A speed change keeps the start and the
/// source range; Undo restores every tick; 1× keeps its existing exact behaviour.
/// </summary>
public class SpeedEditTests
{
    private static readonly FrameRate Fps25 = FrameRate.Fps25;
    private static MediaTime F(long frame, FrameRate? rate = null) => MediaTime.FromFrame(frame, rate ?? Fps25);
    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);
    private static ClipSpeed X(decimal value) => ClipSpeed.TryFromDecimal(value, out var s) ? s : throw new ArgumentException($"{value}");

    /// <summary>The worked example: 25 fps, a 1× video clip at frames [100, 250) showing source 1–7 s
    /// of a 10 s file (made with the real 1× operations).</summary>
    private static (TimelineFixture F, VideoClip Clip, MediaAsset Asset) Example()
    {
        var f = new TimelineFixture();
        var asset = f.Video(10, Fps25);
        Assert.True(f.Service.AddClip(asset.Id).Success);
        var clip = (VideoClip)f.V1.Clips[0];
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, F(25)).Success);
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, F(175)).Success);
        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(clip), 75).Success);
        Assert.Equal((F(100), F(250), S(1), S(7)), (clip.TimelineStart, clip.TimelineEnd, clip.SourceIn, clip.SourceOut));
        return (f, clip, asset);
    }

    private static void SetSpeed(TimelineFixture f, Clip clip, decimal speed)
    {
        var result = f.Service.SetClipSpeed(clip.Id, X(speed));
        Assert.True(result.Success, result.Message);
        f.AssertValid();
    }

    private static void AssertInvariant(MediaBackedClip clip, FrameRate rate)
    {
        var frames = clip.TimelineEnd.ToFrameFloor(rate) - clip.TimelineStart.ToFrameFloor(rate);
        Assert.True(SpeedTiming.Fits(frames, clip.SourceOut - clip.SourceIn, clip.Speed, rate),
            $"{frames} frames vs {clip.SourceOut - clip.SourceIn} at {clip.Speed}");
    }

    // --- SetSpeed ----------------------------------------------------------------------------------------

    [Fact]
    public void SetSpeed_keeps_start_and_source_range_and_takes_the_frames_the_range_allows()
    {
        var (f, clip, _) = Example();

        SetSpeed(f, clip, 1.35m);

        Assert.Equal(X(1.35m), clip.Speed);
        Assert.Equal((F(100), F(211)), (clip.TimelineStart, clip.TimelineEnd));   // ⌊60 000 000 / 540 000⌋ = 111
        Assert.Equal((S(1), S(7)), (clip.SourceIn, clip.SourceOut));               // never changed by a speed change
        Assert.Equal("Change Speed", ((IUndoableCommand)f.UndoRedo.CurrentPosition).Description);
    }

    [Theory]
    [InlineData(0.25, 600)]
    [InlineData(0.50, 300)]
    [InlineData(0.95, 157)]   // 6 s / 0.95 = 6.3158 s = 157.89 frames
    [InlineData(1.00, 150)]
    [InlineData(1.05, 142)]   // 6 s / 1.05 = 5.714 s = 142.86 frames
    [InlineData(2.00, 75)]
    [InlineData(4.00, 37)]    // 37.5 frames
    public void Boundary_speeds(double speed, long frames)
    {
        var (f, clip, _) = Example();
        var before = f.Snapshot();
        if (speed == 1.0) SetSpeed(f, clip, 2m);                 // come back to 1× from elsewhere

        SetSpeed(f, clip, (decimal)speed);

        Assert.Equal(F(100 + frames), clip.TimelineEnd);
        Assert.Equal(frames, SpeedTiming.FramesFor(S(6), X((decimal)speed), Fps25));
        Assert.Equal((F(100), S(1), S(7)), (clip.TimelineStart, clip.SourceIn, clip.SourceOut));
        AssertInvariant(clip, Fps25);
        if (speed == 1.0) Assert.Equal(before, f.Snapshot());   // exactly the original 1× clip
    }

    [Fact]
    public void Round_trips_1_to_1_35_to_1_and_1_to_1_35_to_0_75_to_1_are_exact()
    {
        var (f, clip, _) = Example();
        var original = f.Snapshot();
        var position = f.UndoRedo.CurrentPosition;

        SetSpeed(f, clip, 1.35m);
        SetSpeed(f, clip, 1m);
        Assert.Equal(original, f.Snapshot());
        Assert.Same(position, f.UndoRedo.CurrentPosition);   // the merged step cancelled out

        SetSpeed(f, clip, 1.35m);
        SetSpeed(f, clip, 0.75m);
        Assert.Equal(F(100 + 200), clip.TimelineEnd);          // 6 s / 0.75 = 8 s
        SetSpeed(f, clip, 1m);
        Assert.Equal(original, f.Snapshot());
    }

    [Fact]
    public void SetSpeed_undo_redo_is_exact_when_the_duration_is_not_an_exact_multiple()
    {
        var (f, clip, _) = Example();
        var before = f.Snapshot();

        SetSpeed(f, clip, 1.35m);                               // 111 frames = 4.44 s ≠ 6 s / 1.35
        var after = f.Snapshot();
        Assert.NotEqual(clip.Duration.Ticks * 135, (clip.SourceOut - clip.SourceIn).Ticks * 100);

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(ClipSpeed.Normal, clip.Speed);
        f.AssertValid();

        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
        f.AssertValid();
    }

    [Fact]
    public void Consecutive_speed_changes_merge_into_one_undo_step()
    {
        var (f, clip, _) = Example();
        var before = f.Snapshot();
        var position = f.UndoRedo.CurrentPosition;

        SetSpeed(f, clip, 1.35m);
        SetSpeed(f, clip, 1.5m);
        SetSpeed(f, clip, 2m);
        f.UndoRedo.Undo();

        Assert.Equal(before, f.Snapshot());
        Assert.Same(position, f.UndoRedo.CurrentPosition);

        // Another edit in between keeps them apart.
        f.UndoRedo.Redo();
        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(clip), 5).Success);
        SetSpeed(f, clip, 3m);
        f.UndoRedo.Undo();
        Assert.Equal(X(2m), clip.Speed);
    }

    [Fact]
    public void Rejected_speed_changes_change_nothing()
    {
        var (f, clip, asset) = Example();
        Assert.True(f.Service.AddClip(asset.Id, f.V1.Id, F(300)).Success);      // neighbour at frame 300
        var before = f.Snapshot();
        var position = f.UndoRedo.CurrentPosition;

        var overlap = f.Service.SetClipSpeed(clip.Id, X(0.5m));                  // would end at frame 400
        Assert.False(overlap.Success);
        Assert.Contains("overlap", overlap.Message);

        f.V1.IsLocked = true;
        Assert.Contains("locked", f.Service.SetClipSpeed(clip.Id, X(2m)).Message);
        f.V1.IsLocked = false;

        Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.Normal).NoChange);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(position, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void Images_and_text_have_no_speed_and_a_too_short_result_is_rejected()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Image().Id).Success);
        Assert.True(f.Service.AddTextClip(F(1000, f.Rate)).Success);
        foreach (var clip in f.V1.Clips)
            Assert.Equal("Only video and audio clips have a speed.", f.Service.SetClipSpeed(clip.Id, X(2m)).Message);

        // A one-frame 1× clip has less source than one frame at 4×.
        var video = f.Video(10, Fps25);
        Assert.True(f.Service.AddClip(video.Id, f.V1.Id, F(2000, f.Rate)).Success);
        var tiny = f.V1.Clips.OfType<VideoClip>().Single();
        Assert.True(f.Service.TrimClip(tiny.Id, ClipEdge.End, tiny.TimelineStart).Success);   // clamps to one frame
        Assert.Contains("shorter than one frame", f.Service.SetClipSpeed(tiny.Id, X(4m)).Message);
        f.AssertValid();
    }

    [Fact]
    public void Audio_clips_have_a_speed_too()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Audio(6).Id).Success);
        var audio = (AudioClip)f.A1.Clips[0];
        var frames = audio.TimelineEnd.ToFrameFloor(f.Rate);

        SetSpeed(f, audio, 0.5m);

        Assert.Equal(frames * 2, audio.TimelineEnd.ToFrameFloor(f.Rate));
        AssertInvariant(audio, f.Rate);
    }

    // --- Trim / Move / Split at speed -----------------------------------------------------------------------

    [Fact]
    public void Trim_end_at_speed_sets_SourceOut_from_the_frame_count()
    {
        var (f, clip, _) = Example();
        SetSpeed(f, clip, 1.35m);
        var before = f.Snapshot();

        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, F(180)).Success);   // 80 frames

        Assert.Equal(F(180), clip.TimelineEnd);
        Assert.Equal(S(1), clip.SourceIn);
        Assert.Equal(S(1) + new MediaTime(43_200_000), clip.SourceOut);            // ⌊80 · 540 000⌋ → 5.32 s
        AssertInvariant(clip, Fps25);
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());

        // Growing stops where the source ends: ⌊(10 s − 1 s) / 540 000⌋ = 166 frames.
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, F(5000)).Success);
        Assert.Equal(F(266), clip.TimelineEnd);
        Assert.True(clip.SourceOut <= S(10));
        AssertInvariant(clip, Fps25);
    }

    [Fact]
    public void Trim_start_at_speed_keeps_the_content_under_the_playhead_and_SourceOut()
    {
        var (f, clip, _) = Example();
        SetSpeed(f, clip, 1.35m);

        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, F(130)).Success);  // Δ = 30 frames

        Assert.Equal((F(130), F(211)), (clip.TimelineStart, clip.TimelineEnd));
        Assert.Equal(S(1) + new MediaTime(16_200_000), clip.SourceIn);             // 2.62 s
        Assert.Equal(S(7), clip.SourceOut);
        // Frame 150 shows source 3.70 s before and after: 1.0 + 50·0.054 = 2.62 + 20·0.054.
        Assert.Equal(S(3.7), clip.SourceIn + new MediaTime(20 * 540_000));
        AssertInvariant(clip, Fps25);

        // Extending left stops at the start of the source: ⌊26 200 000 / 540 000⌋ = 48 frames back.
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, F(0)).Success);
        Assert.Equal(F(82), clip.TimelineStart);
        Assert.True(clip.SourceIn >= MediaTime.Zero && clip.SourceIn < new MediaTime(540_000));
        Assert.Equal(S(7), clip.SourceOut);
        AssertInvariant(clip, Fps25);
    }

    [Fact]
    public void Move_at_speed_keeps_the_source_range_and_frame_count()
    {
        var (f, clip, _) = Example();
        SetSpeed(f, clip, 1.35m);
        f.Service.AddTrack(TrackType.Video);
        var v2 = f.Project.Timeline.VideoTracks[1];

        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(clip), 20).Success);
        Assert.Equal((F(120), F(231), S(1), S(7), X(1.35m)), (clip.TimelineStart, clip.TimelineEnd, clip.SourceIn, clip.SourceOut, clip.Speed));

        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(clip), -3, v2.Id).Success);
        Assert.Same(clip, Assert.Single(v2.Clips));
        Assert.Equal((F(117), F(228), S(1), S(7)), (clip.TimelineStart, clip.TimelineEnd, clip.SourceIn, clip.SourceOut));
        f.AssertValid();
    }

    [Fact]
    public void Split_at_speed_cuts_the_source_at_SourceLength_of_the_left_frames()
    {
        var (f, clip, _) = Example();
        SetSpeed(f, clip, 1.35m);
        var before = f.Snapshot();

        Assert.True(f.Service.Split(F(150), TimelineFixture.Ids(clip)).Success);

        var right = (VideoClip)f.V1.Clips[1];
        Assert.Equal((F(100), F(150), S(1), S(3.7)), (clip.TimelineStart, clip.TimelineEnd, clip.SourceIn, clip.SourceOut));
        Assert.Equal((F(150), F(211), S(3.7), S(7)), (right.TimelineStart, right.TimelineEnd, right.SourceIn, right.SourceOut));
        Assert.Equal(X(1.35m), right.Speed);
        AssertInvariant(clip, Fps25);
        AssertInvariant(right, Fps25);

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Split_and_trims_normalize_only_when_rounding_misses_the_invariant()
    {
        // Exhaustive over split points and trim targets at 29.97 and three speeds: every result
        // satisfies the invariant and no source point moves by more than one tick from the plain formula.
        foreach (var speed in new[] { 0.35m, 1.35m, 3.95m })
        {
            var f = new TimelineFixture();
            var asset = f.Video(60, FrameRate.Ntsc30);
            Assert.True(f.Service.AddClip(asset.Id).Success);
            var clip = (VideoClip)f.V1.Clips[0];
            Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, F(7, f.Rate)).Success);
            Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, F(400, f.Rate)).Success);
            SetSpeed(f, clip, speed);
            var snapshot = f.Snapshot();
            var end = clip.TimelineEnd.ToFrameFloor(f.Rate);

            for (var at = 8L; at < end; at += 3)
            {
                Assert.True(f.Service.Split(F(at, f.Rate), TimelineFixture.Ids(clip)).Success);
                var right = (VideoClip)f.V1.Clips.Single(c => c != clip);
                AssertInvariant(clip, f.Rate);
                AssertInvariant(right, f.Rate);
                Assert.True((right.SourceIn - clip.SourceOut).Ticks is >= -1 and <= 0);
                f.UndoRedo.Undo();

                Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, F(at, f.Rate)).Success);
                AssertInvariant(clip, f.Rate);
                f.UndoRedo.Undo();
                Assert.Equal(snapshot, f.Snapshot());
            }
        }
    }

    // --- Frame-rate re-grid ------------------------------------------------------------------------------

    [Fact]
    public void Regrid_keeps_the_source_range_and_takes_the_frames_it_allows_on_the_new_grid()
    {
        // An audio clip at 0.5× on the provisional 30 fps grid, then the first video fixes 25 fps.
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Audio(6).Id).Success);                      // 180 frames at 30 fps, 0–6 s
        var audio = (AudioClip)f.A1.Clips[0];
        SetSpeed(f, audio, 0.5m);                                                   // 360 frames
        var before = f.Snapshot();

        Assert.True(f.Service.AddClip(f.Video(10, Fps25).Id).Success);

        Assert.Equal(Fps25, f.Rate);
        Assert.Equal((MediaTime.Zero, F(300), MediaTime.Zero, S(6)), (audio.TimelineStart, audio.TimelineEnd, audio.SourceIn, audio.SourceOut));
        Assert.Equal(X(0.5m), audio.Speed);
        AssertInvariant(audio, Fps25);

        f.UndoRedo.Undo();                                                           // add + re-grid in one step
        Assert.Equal(before, f.Snapshot());
    }

    // --- 1× after another speed -----------------------------------------------------------------------------

    [Fact]
    public void Back_at_1x_a_source_tail_shorter_than_a_frame_is_valid_and_1x_edits_work_as_before()
    {
        var (f, clip, _) = Example();
        SetSpeed(f, clip, 1.35m);
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, F(181)).Success);    // 81 frames → 43 740 000 ticks

        SetSpeed(f, clip, 1m);

        Assert.Equal(F(209), clip.TimelineEnd);                                     // ⌊43 740 000 / 400 000⌋ = 109
        Assert.Equal(S(1) + new MediaTime(43_740_000), clip.SourceOut);            // tail of 140 000 ticks kept
        AssertInvariant(clip, Fps25);

        // 1× edits keep their exact rule (SourceOut = SourceIn + duration).
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, F(200)).Success);
        Assert.Equal(clip.SourceIn + clip.Duration, clip.SourceOut);
        f.AssertValid();
    }

    // --- Normalization -----------------------------------------------------------------------------------

    /// <summary>A clip at 0.25× on 29.97 frames [100, 102) with SourceIn 1 s and the given source length
    /// (valid: SourceLength(2) = 166 833 ≤ length &lt; SourceLength(3) = 250 250).</summary>
    private static (TimelineFixture F, VideoClip Clip) QuarterSpeedAt2997(long sourceLength)
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = FrameRate.Ntsc30;
        f.Settings.IsFrameRateLocked = true;
        var asset = f.Video(10, FrameRate.Ntsc30);
        var clip = f.Place<VideoClip>(f.V1, asset, F(100, f.Rate).Ticks, F(102, f.Rate).Ticks, S(1).Ticks);
        clip.Speed = ClipSpeed.Min;
        clip.SourceOut = clip.SourceIn + new MediaTime(sourceLength);
        f.AssertValid();
        return (f, clip);
    }

    [Fact]
    public void Extending_the_start_normalizes_a_one_tick_shortfall_by_moving_SourceIn()
    {
        // 166 833 + ⌊83 416.6⌋ = 250 249 < SourceLength(3) = 250 250: the plain sum misses by one tick.
        var (f, clip) = QuarterSpeedAt2997(166_833);
        var sourceOut = clip.SourceOut;

        var result = f.Service.TrimClip(clip.Id, ClipEdge.Start, F(99, f.Rate));

        Assert.True(result.Success, result.Message);
        Assert.Equal(S(1) - new MediaTime(83_416 + 1), clip.SourceIn);
        Assert.Equal(sourceOut, clip.SourceOut);
        AssertInvariant(clip, f.Rate);
    }

    [Fact]
    public void Shrinking_the_start_or_splitting_normalizes_a_one_tick_excess_at_SourceOut()
    {
        // The longest valid range (250 249) minus ⌊83 416.6⌋ = 166 833 = SourceLength(2): one tick too long for 1 frame.
        var (f, clip) = QuarterSpeedAt2997(250_249);
        var sourceOut = clip.SourceOut;

        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, F(101, f.Rate)).Success);
        Assert.Equal(S(1) + new MediaTime(83_416), clip.SourceIn);
        Assert.Equal(sourceOut - new MediaTime(1), clip.SourceOut);
        AssertInvariant(clip, f.Rate);
        f.UndoRedo.Undo();

        Assert.True(f.Service.Split(F(101, f.Rate), TimelineFixture.Ids(clip)).Success);
        var right = (VideoClip)f.V1.Clips.Single(c => c != clip);
        Assert.Equal(clip.SourceOut, right.SourceIn);
        Assert.Equal(sourceOut - new MediaTime(1), right.SourceOut);
        AssertInvariant(clip, f.Rate);
        AssertInvariant(right, f.Rate);
    }

    [Fact]
    public void Normalize_is_the_smallest_change_back_into_the_invariant()
    {
        var s = X(1.35m);
        var min = SpeedTiming.SourceLength(10, s, Fps25);            // 5 400 000
        var limit = SpeedTiming.SourceLength(11, s, Fps25);          // 5 940 000

        Assert.Equal((S(1), S(1) + min), ClipState.Normalize(10, S(1), S(1) + min, s, Fps25));                        // fits
        Assert.Equal((S(1) + new MediaTime(-1), S(1) + min - new MediaTime(1)),
            ClipState.Normalize(10, S(1), S(1) + min - new MediaTime(1), s, Fps25));                                // too short by 1: SourceIn back
        Assert.Equal((MediaTime.Zero, min), ClipState.Normalize(10, MediaTime.Zero, min - new MediaTime(1), s, Fps25)); // at 0: SourceOut on
        Assert.Equal((S(1), S(1) + limit - new MediaTime(1)), ClipState.Normalize(10, S(1), S(1) + limit, s, Fps25)); // too long: minimal cut
    }

    // --- Randomized -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(30000, 1001, 11)]
    [InlineData(24000, 1001, 12)]
    [InlineData(25, 1, 13)]
    public void Random_edits_with_speeds_keep_every_invariant_and_full_undo_restores_the_start(int num, int den, int seed)
    {
        var f = new TimelineFixture();
        var video = f.Video(40, new FrameRate(num, den));
        var audio = f.Audio(30);
        var start = f.Snapshot();
        f.Service.AddTrack(TrackType.Video);
        var random = new Random(seed);
        var speeds = new[] { 0.25m, 0.5m, 0.95m, 1m, 1.05m, 1.35m, 2m, 4m };

        for (var step = 0; step < 500; step++)
        {
            var clips = f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks).SelectMany(t => t.Clips).ToList();
            var pick = clips.Count > 0 ? clips[random.Next(clips.Count)] : null;
            var at = MediaTime.FromFrame(random.Next(0, 3000), f.Rate);

            switch (random.Next(8))
            {
                case 0: f.Service.AddClip(random.Next(2) == 0 ? video.Id : audio.Id); break;
                case 1: if (pick is not null) f.Service.MoveClips(TimelineFixture.Ids(pick), random.Next(-200, 200)); break;
                case 2: if (pick is not null) f.Service.TrimClip(pick.Id, random.Next(2) == 0 ? ClipEdge.Start : ClipEdge.End, at); break;
                case 3: f.Service.Split(at, pick is not null && random.Next(2) == 0 ? TimelineFixture.Ids(pick) : null); break;
                case 4: if (pick is not null) f.Service.SetClipSpeed(pick.Id, X(speeds[random.Next(speeds.Length)])); break;
                case 5: if (pick is not null && random.Next(4) == 0) f.Service.DeleteClips(TimelineFixture.Ids(pick)); break;
                case 6: if (f.UndoRedo.CanUndo) f.UndoRedo.Undo(); break;
                case 7: if (f.UndoRedo.CanRedo) f.UndoRedo.Redo(); break;
            }

            f.AssertValid();
            foreach (var media in f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks).SelectMany(t => t.Clips).OfType<MediaBackedClip>())
                Assert.True(media.SourceOut <= (media is AudioClip ? S(30) : S(40)));
        }

        while (f.UndoRedo.CanUndo) f.UndoRedo.Undo();
        Assert.Equal(start, f.Snapshot());
    }
}
