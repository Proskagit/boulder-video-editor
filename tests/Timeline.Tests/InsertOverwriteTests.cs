using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 16 Step 16.3 (D031 SQ6–SQ9, SQ12, SQ13, SQ15): Insert and Overwrite of a source range at a timeline point.
/// Insert splits a clip under the point (the split rule) and moves its right part and every later clip of the target
/// track by the range (the move rule); Overwrite clears the range with the trim rule (a covered clip removed, a covering
/// one split and trimmed). Both are built from the existing rules only, so each result is compared, tick for tick, with
/// the same edit made of the existing commands. One undo step; a refused edit changes nothing and leaves no step.
/// </summary>
public class InsertOverwriteTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static long Frame(MediaTime t) => t.ToNearestFrame(Rate);
    private static (long Start, long End) Frames(Clip c) => (Frame(c.TimelineStart), Frame(c.TimelineEnd));
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;

    /// <summary>A 20 s video at 25 fps (500 frames) on V1 from 0, trimmed to the sum of <paramref name="lengths"/> and split
    /// into clips of those lengths, back to back — continuous source, so every cut has the media a dissolve needs.</summary>
    private static (TimelineFixture F, MediaAsset Video, List<Clip> Clips) Track(params long[] lengths)
    {
        var f = new TimelineFixture();
        var video = f.Video(20, Rate);
        Ok(f.Service.AddClip(video.Id));
        if (lengths.Sum() < 500) Ok(f.Service.TrimClip(f.V1.Clips[0].Id, ClipEdge.End, F(lengths.Sum())));
        var at = 0L;
        foreach (var length in lengths.SkipLast(1))
            Ok(f.Service.Split(F(at += length)));
        return (f, video, f.V1.Clips.OrderBy(c => c.TimelineStart).ToList());
    }

    private static Clip NewClip(TimelineFixture f, TimelineEditResult r) =>
        f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks).SelectMany(t => t.Clips).Single(c => c.Id == r.ClipIds.Single());

    private static void AssertRefused(TimelineFixture f, TimelineEditResult result, string message, string before, object? top)
    {
        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    // --- the new clip ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_new_clip_is_the_source_range_at_the_point_at_1x()
    {
        var (f, video, clips) = Track(100);

        var result = f.Service.InsertClip(video.Id, F(10), F(40), F(200));

        Ok(result);
        var clip = Assert.IsType<VideoClip>(NewClip(f, result));
        Assert.Equal((200, 230), Frames(clip));
        Assert.Equal(F(10), clip.SourceIn);
        Assert.True(clip.Speed.IsNormal);
        Assert.Same(f.V1, f.Project.Timeline.VideoTracks.Single(t => t.Clips.Contains(clip)));
        Assert.Equal((0, 100), Frames(clips[0]));                    // nothing after the point to move
        Assert.Equal("Insert Clip", Top(f));
        f.AssertValid();
    }

    [Fact]
    public void Without_In_or_Out_the_range_is_the_whole_source_and_In_and_Out_snap_to_the_grid()
    {
        var f = new TimelineFixture();
        var video = f.Video(20, Rate);
        Ok(f.Service.AddClip(video.Id));                             // locks 25 fps
        Ok(f.Service.DeleteClips(TimelineFixture.Ids(f.V1.Clips[0])));

        var whole = NewClip(f, f.Service.InsertClip(video.Id, null, null, F(0)));
        Assert.Equal((0, 500), Frames(whole));
        Assert.Equal(MediaTime.Zero, whole.As<MediaBackedClip>().SourceIn);

        var fromIn = NewClip(f, f.Service.OverwriteClip(video.Id, new MediaTime(F(490).Ticks + 1234), null, F(600)));
        Assert.Equal((600, 610), Frames(fromIn));                    // In snapped to frame 490, Out the last whole frame
        Assert.Equal(F(490), fromIn.As<MediaBackedClip>().SourceIn);
        f.AssertValid();
    }

    [Fact]
    public void An_audio_asset_goes_onto_an_audio_track_and_a_video_with_its_sound_onto_a_video_track()
    {
        var f = new TimelineFixture();
        var music = f.Audio(10);
        var video = f.Video(20, Rate);

        var audio = NewClip(f, f.Service.InsertClip(music.Id, F(0), F(50), F(0)));
        Assert.IsType<AudioClip>(audio);
        Assert.Contains(audio, f.A1.Clips);

        var picture = NewClip(f, f.Service.InsertClip(video.Id, F(0), F(50), F(0)));
        Assert.IsType<VideoClip>(picture);                           // SQ15: one clip, picture and sound
        Assert.Contains(picture, f.V1.Clips);

        Assert.Equal("Audio can only go on an audio track.", f.Service.InsertClip(music.Id, null, null, F(0), f.V1.Id).Message);
        Assert.Equal("Video can only go on a video track.", f.Service.InsertClip(video.Id, null, null, F(0), f.A1.Id).Message);
    }

    [Fact]
    public void The_first_video_locks_the_frame_rate_as_Add_does()
    {
        var f = new TimelineFixture();
        var video = f.Video(20, Rate);
        Assert.False(f.Settings.IsFrameRateLocked);

        var result = f.Service.InsertClip(video.Id, null, null, MediaTime.Zero);

        Ok(result);
        Assert.Equal(Rate, f.Settings.FrameRate);
        Assert.True(f.Settings.IsFrameRateLocked);
        Assert.Equal("Project frame rate set to 25 FPS from clip.mp4.", result.Message);
        f.UndoRedo.Undo();
        Assert.False(f.Settings.IsFrameRateLocked);
        Assert.Empty(f.V1.Clips);
    }

    // --- Insert (SQ6, SQ7) ----------------------------------------------------------------------------------------------

    [Fact]
    public void Insert_at_a_cut_moves_the_later_clips_of_the_target_track_only()
    {
        var (f, video, clips) = Track(100, 100, 50);
        var (expected, _, twin) = Track(100, 100, 50);
        Ok(f.Service.AddTrack(TrackType.Video));
        Ok(expected.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[1];
        Ok(f.Service.AddClip(video.Id, v2.Id, F(150)));
        Ok(f.Service.AddClip(f.Audio(10).Id, null, F(120)));
        Ok(f.Service.AddMarker(F(150)));
        var v2Before = ClipState.Capture(v2.Clips[0]);
        var a1Before = ClipState.Capture(f.A1.Clips[0]);

        var result = f.Service.InsertClip(video.Id, F(0), F(30), F(100));

        Ok(result);
        Ok(expected.Service.MoveClips(TimelineFixture.Ids(twin[1], twin[2]), 30));
        Assert.Equal(ClipState.Capture(twin[0]), ClipState.Capture(clips[0]));
        Assert.Equal(ClipState.Capture(twin[1]), ClipState.Capture(clips[1]));
        Assert.Equal(ClipState.Capture(twin[2]), ClipState.Capture(clips[2]));
        Assert.Equal((100, 130), Frames(NewClip(f, result)));
        Assert.Equal(v2Before, ClipState.Capture(v2.Clips[0]));        // other tracks stay
        Assert.Equal(a1Before, ClipState.Capture(f.A1.Clips[0]));
        Assert.Equal(F(150), f.Project.Timeline.Markers.Single().Position); // markers stay (D027 §2)
        f.AssertValid();
    }

    [Theory]
    [InlineData(20)]
    [InlineData(10)]
    [InlineData(40)]
    public void Insert_inside_a_clip_is_the_split_rule_and_the_move_rule(int speedSteps)
    {
        var (f, video, clips) = Track(200);
        var (expected, _, twin) = Track(200);
        foreach (var (fx, c) in new[] { (f, clips[0]), (expected, twin[0]) })
        {
            if (speedSteps != 20) Ok(fx.Service.SetClipSpeed(c.Id, ClipSpeed.FromSteps(speedSteps)));
            Ok(fx.Service.SetClipProperties(c.Id, new ClipPropertyChange { Fade = new FadeProperties(F(5), F(5)) }));
            Ok(fx.Service.AddClip(c.As<MediaBackedClip>().MediaAssetId, null, F(1000)));
        }
        var later = f.V1.Clips.Single(c => c != clips[0]);
        var twinLater = expected.V1.Clips.Single(c => c != twin[0]);

        var result = f.Service.InsertClip(video.Id, F(0), F(30), F(80));

        Ok(result);
        var split = expected.Service.Split(F(80), TimelineFixture.Ids(twin[0]));
        Ok(split);
        var twinRight = expected.V1.Clips.Single(c => c.Id == split.ClipIds[1]);
        Ok(expected.Service.MoveClips(TimelineFixture.Ids(twinRight, twinLater), 30));

        var right = f.V1.Clips.Single(c => c != clips[0] && c != later && c.Id != result.ClipIds[0]);
        Assert.Equal(ClipState.Capture(twin[0]), ClipState.Capture(clips[0]));      // D022 at every speed
        Assert.Equal(ClipState.Capture(twinRight), ClipState.Capture(right));
        Assert.Equal(ClipState.Capture(twinLater), ClipState.Capture(later));
        Assert.Equal((F(5), MediaTime.Zero), (clips[0].FadeIn, clips[0].FadeOut));   // D025 §2: outer edges keep the fades
        Assert.Equal((MediaTime.Zero, F(5)), (right.FadeIn, right.FadeOut));
        Assert.Equal((80, 110), Frames(NewClip(f, result)));
        f.AssertValid();
    }

    [Fact]
    public void Insert_at_a_dissolve_cut_removes_the_dissolve_and_a_dissolve_that_moves_whole_stays()
    {
        var (f, video, clips) = Track(100, 100, 100);
        Ok(f.Service.AddTransition(clips[0].Id, clips[1].Id, F(10)));
        Ok(f.Service.AddTransition(clips[1].Id, clips[2].Id, F(10)));
        var kept = f.V1.Transitions.Single(t => t.LeftClipId == clips[1].Id);

        var result = f.Service.InsertClip(video.Id, F(0), F(30), F(100));

        Ok(result);
        Assert.Equal("A dissolve was removed: its clips no longer meet.", result.Message);
        Assert.Same(kept, f.V1.Transitions.Single());
        Assert.Equal((130, 230), Frames(clips[1]));
        Assert.Equal((230, 330), Frames(clips[2]));
        f.AssertValid();
    }

    [Fact]
    public void Insert_inside_a_dissolve_zone_is_refused()
    {
        var (f, video, clips) = Track(100, 100);
        Ok(f.Service.AddTransition(clips[0].Id, clips[1].Id, F(10)));   // zone 95–105
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        AssertRefused(f, f.Service.InsertClip(video.Id, F(0), F(30), F(97)), "Can't insert inside a dissolve.", before, top);
        AssertRefused(f, f.Service.InsertClip(video.Id, F(0), F(30), F(103)), "Can't insert inside a dissolve.", before, top);
    }

    // --- Overwrite (SQ9) ------------------------------------------------------------------------------------------------

    [Fact]
    public void Overwrite_removes_a_covered_clip_and_trims_the_partly_covered_ones_by_the_trim_rule()
    {
        var (f, video, clips) = Track(100, 50, 150);
        var (expected, _, twin) = Track(100, 50, 150);

        var result = f.Service.OverwriteClip(video.Id, F(300), F(370), F(90));          // [90, 160)

        Ok(result);
        Ok(expected.Service.DeleteClips(TimelineFixture.Ids(twin[1])));
        Ok(expected.Service.TrimClip(twin[0].Id, ClipEdge.End, F(90)));
        Ok(expected.Service.TrimClip(twin[2].Id, ClipEdge.Start, F(160)));
        Assert.Equal(ClipState.Capture(twin[0]), ClipState.Capture(clips[0]));
        Assert.Equal(ClipState.Capture(twin[2]), ClipState.Capture(clips[2]));
        Assert.DoesNotContain(clips[1], f.V1.Clips);
        var clip = NewClip(f, result);
        Assert.Equal((90, 160), Frames(clip));
        Assert.Equal(F(300), clip.As<MediaBackedClip>().SourceIn);
        Assert.Equal("Overwrite Clip", Top(f));
        f.AssertValid();
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    public void Overwrite_inside_a_clip_splits_it_and_trims_its_right_part(int speedSteps)
    {
        var (f, video, clips) = Track(300);
        var (expected, _, twin) = Track(300);
        if (speedSteps != 20)
        {
            Ok(f.Service.SetClipSpeed(clips[0].Id, ClipSpeed.FromSteps(speedSteps)));
            Ok(expected.Service.SetClipSpeed(twin[0].Id, ClipSpeed.FromSteps(speedSteps)));
        }

        var result = f.Service.OverwriteClip(video.Id, F(0), F(50), F(60));            // [60, 110)

        Ok(result);
        var split = expected.Service.Split(F(60), TimelineFixture.Ids(twin[0]));
        var twinRight = expected.V1.Clips.Single(c => c.Id == split.ClipIds[1]);
        Ok(expected.Service.TrimClip(twinRight.Id, ClipEdge.Start, F(110)));
        var right = f.V1.Clips.Single(c => c != clips[0] && c.Id != result.ClipIds[0]);
        Assert.Equal(ClipState.Capture(twin[0]), ClipState.Capture(clips[0]));
        Assert.Equal(ClipState.Capture(twinRight), ClipState.Capture(right));
        Assert.Equal((60, 110), Frames(NewClip(f, result)));
        f.AssertValid();
    }

    [Fact]
    public void Overwrite_across_a_dissolve_cut_removes_the_dissolve()
    {
        var (f, video, clips) = Track(100, 100);
        Ok(f.Service.AddTransition(clips[0].Id, clips[1].Id, F(10)));

        var result = f.Service.OverwriteClip(video.Id, F(0), F(50), F(80));            // [80, 130)

        Ok(result);
        Assert.Equal("A dissolve was removed: its clips no longer meet.", result.Message);
        Assert.Empty(f.V1.Transitions);
        Assert.Equal((0, 80), Frames(clips[0]));
        Assert.Equal((130, 200), Frames(clips[1]));
        f.AssertValid();
    }

    [Fact]
    public void Overwrite_that_ends_inside_the_frames_a_dissolve_needs_is_refused()
    {
        var (f, video, clips) = Track(100, 100);
        Ok(f.Service.AddTransition(clips[0].Id, clips[1].Id, F(20)));   // zone 90–110
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        // [60, 95): the part of clip 0 left after 95 would be shorter than its 10 frames in the dissolve.
        AssertRefused(f, f.Service.OverwriteClip(video.Id, F(0), F(35), F(60)), "Can't overwrite inside a dissolve.", before, top);
        // [105, 150): the part of clip 1 left before 105 is inside the dissolve.
        AssertRefused(f, f.Service.OverwriteClip(video.Id, F(0), F(45), F(105)), "Can't overwrite inside a dissolve.", before, top);
        // [105, 205): clip 1 would end at 105, shorter than its 10 frames in the dissolve at its start.
        AssertRefused(f, f.Service.OverwriteClip(video.Id, F(0), F(100), F(105)), "Can't overwrite inside a dissolve.", before, top);
        // [0, 95): clip 0 would start at 95, shorter than its 10 frames in the dissolve at its end.
        AssertRefused(f, f.Service.OverwriteClip(video.Id, F(0), F(95), F(0)), "Can't overwrite inside a dissolve.", before, top);
    }

    // --- refusals, undo -------------------------------------------------------------------------------------------------

    [Fact]
    public void Refused_for_a_locked_track_an_image_a_missing_asset_and_a_range_under_one_frame()
    {
        var (f, video, _) = Track(100);
        var image = f.Image();
        var missing = f.Video(5, Rate, "gone.mp4");
        missing.IsMissing = true;
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        foreach (var overwrite in new[] { false, true })
        {
            TimelineEditResult Place(Guid asset, MediaTime? i, MediaTime? o) =>
                overwrite ? f.Service.OverwriteClip(asset, i, o, F(50)) : f.Service.InsertClip(asset, i, o, F(50));

            AssertRefused(f, Place(image.Id, null, null), "Images have no source range: add them with Add to Timeline.", before, top);
            AssertRefused(f, Place(missing.Id, null, null), "gone.mp4 is missing.", before, top);
            AssertRefused(f, Place(video.Id, F(40), F(40)), "The source range is shorter than one frame.", before, top);
            AssertRefused(f, Place(video.Id, F(40), F(30)), "The source range is shorter than one frame.", before, top);
            AssertRefused(f, Place(Guid.NewGuid(), null, null), "That media is not in the project.", before, top);
        }

        Ok(f.Service.SetTrackLocked(f.V1.Id, true));
        before = f.Snapshot();
        top = f.UndoRedo.CurrentPosition;
        AssertRefused(f, f.Service.InsertClip(video.Id, F(0), F(10), F(50)), "Track V1 is locked.", before, top);
        AssertRefused(f, f.Service.OverwriteClip(video.Id, F(0), F(10), F(50)), "Track V1 is locked.", before, top);
    }

    [Fact]
    public void One_undo_step_exact_on_undo_and_redo_and_dirty_by_the_save_point()
    {
        foreach (var overwrite in new[] { false, true })
        {
            var (f, video, clips) = Track(200, 100);
            Ok(f.Service.AddTransition(clips[0].Id, clips[1].Id, F(10)));
            var before = f.Snapshot();
            f.UndoRedo.MarkSavePoint(f.UndoRedo.CurrentPosition!);
            f.Project.IsDirty = false;

            var result = overwrite
                ? f.Service.OverwriteClip(video.Id, F(0), F(30), F(50))
                : f.Service.InsertClip(video.Id, F(0), F(30), F(50));
            Ok(result);
            var after = f.Snapshot();
            Assert.True(f.Project.IsDirty);

            f.UndoRedo.Undo();
            Assert.Equal(before, f.Snapshot());
            Assert.True(f.UndoRedo.IsAtSavePoint);
            f.UndoRedo.Redo();
            Assert.Equal(after, f.Snapshot());
            Assert.False(f.UndoRedo.IsAtSavePoint);
            f.AssertValid();
        }
    }
}

internal static class ClipCast
{
    public static T As<T>(this Clip clip) where T : Clip => Assert.IsAssignableFrom<T>(clip);
}
