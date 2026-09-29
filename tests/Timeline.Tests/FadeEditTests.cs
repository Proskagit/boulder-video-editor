using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 10 Step 10.4 (D025 §2): editing fades through the edit service — whole frames, limits, undo merging — and
/// what split, trim, move and speed do with them (stored values kept; a split takes the fades to the outer edges).
/// </summary>
public class FadeEditTests
{
    private static TimelineFixture WithVideo(out VideoClip clip, double seconds = 10)
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(seconds, FrameRate.Ntsc30).Id).Success);
        clip = (VideoClip)f.V1.Clips[0];
        return f;
    }

    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static long Frames(TimelineFixture f, Clip clip) => TransitionRules.ClipFrames(clip, f.Rate);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;

    private static TimelineEditResult SetFades(TimelineFixture f, Clip clip, MediaTime fadeIn, MediaTime fadeOut) =>
        f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Fade = new FadeProperties(fadeIn, fadeOut) });

    [Fact]
    public void Fades_are_stored_as_whole_frames_of_the_project_rate()
    {
        var f = WithVideo(out var clip);

        Assert.True(SetFades(f, clip, new MediaTime(F(f, 10).Ticks + 1234), F(f, 15)).Success);

        Assert.Equal(F(f, 10), clip.FadeIn);      // nearest whole frame, stored exactly
        Assert.Equal(F(f, 15), clip.FadeOut);
        Assert.Equal("Change Fades", Top(f));
        Assert.True(SetFades(f, clip, F(f, 11), F(f, 15)).Success);
        Assert.Equal("Change Fade In", Top(f));
        f.AssertValid();
    }

    [Fact]
    public void A_fade_up_to_the_clips_length_is_accepted_and_longer_or_negative_is_rejected()
    {
        var f = WithVideo(out var clip);
        var frames = Frames(f, clip);
        var before = f.Snapshot();

        var tooLong = SetFades(f, clip, F(f, frames + 1), MediaTime.Zero);
        Assert.False(tooLong.Success);
        Assert.Equal("The fade in can't be longer than the clip.", tooLong.Message);
        Assert.False(SetFades(f, clip, MediaTime.Zero, F(f, frames + 1)).Success);
        Assert.False(SetFades(f, clip, new MediaTime(-1), MediaTime.Zero).Success);
        Assert.Equal(before, f.Snapshot());
        Assert.DoesNotContain("Fade", Top(f) ?? "");

        Assert.True(SetFades(f, clip, F(f, frames), F(f, frames)).Success);   // overlapping ramps are allowed
    }

    [Fact]
    public void Consecutive_edits_of_one_fade_merge_into_one_undo_step_and_undo_redo_restore_exactly()
    {
        var f = WithVideo(out var clip);
        var start = f.Snapshot();

        SetFades(f, clip, F(f, 5), MediaTime.Zero);
        SetFades(f, clip, F(f, 12), MediaTime.Zero);
        var afterIn = f.Snapshot();
        SetFades(f, clip, F(f, 12), F(f, 30));
        var afterOut = f.Snapshot();

        f.UndoRedo.Undo();                        // the fade out
        Assert.Equal(afterIn, f.Snapshot());
        f.UndoRedo.Undo();                        // both fade-in edits at once
        Assert.Equal(start, f.Snapshot());
        Assert.DoesNotContain("Fade", Top(f) ?? "");

        f.UndoRedo.Redo();
        f.UndoRedo.Redo();
        Assert.Equal(afterOut, f.Snapshot());
    }

    [Fact]
    public void Audio_image_and_text_clips_have_fades_too()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Audio(10).Id).Success);
        Assert.True(f.Service.AddClip(f.Image().Id).Success);
        Assert.True(f.Service.AddTextClip(F(f, 600)).Success);

        foreach (var clip in f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks).SelectMany(t => t.Clips))
        {
            Assert.True(SetFades(f, clip, F(f, 3), F(f, 4)).Success, clip.GetType().Name);
            Assert.Equal((F(f, 3), F(f, 4)), (clip.FadeIn, clip.FadeOut));
        }
    }

    [Fact]
    public void A_locked_track_rejects_a_fade_edit()
    {
        var f = WithVideo(out var clip);
        f.V1.IsLocked = true;
        Assert.False(SetFades(f, clip, F(f, 5), MediaTime.Zero).Success);
        Assert.Equal(MediaTime.Zero, clip.FadeIn);
    }

    [Fact]
    public void Split_keeps_the_fade_in_on_the_left_part_and_the_fade_out_on_the_right_part()
    {
        var f = WithVideo(out var clip);
        SetFades(f, clip, F(f, 20), F(f, 40));
        var before = f.Snapshot();

        Assert.True(f.Service.Split(F(f, 10)).Success);    // inside the fade in

        var (left, right) = (f.V1.Clips[0], f.V1.Clips[1]);
        Assert.Same(clip, left);
        Assert.Equal((F(f, 20), MediaTime.Zero), (left.FadeIn, left.FadeOut));    // rendered as 10 frames (clamped)
        Assert.Equal((MediaTime.Zero, F(f, 40)), (right.FadeIn, right.FadeOut));
        Assert.Equal("Split Clip", Top(f));
        var after = f.Snapshot();
        f.AssertValid();

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.Equal((F(f, 20), F(f, 40)), (clip.FadeIn, clip.FadeOut));
        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
    }

    [Fact]
    public void Split_of_a_clip_without_fades_changes_no_fade()
    {
        var f = WithVideo(out _);
        Assert.True(f.Service.Split(F(f, 100)).Success);
        Assert.All(f.V1.Clips, c => Assert.Equal((MediaTime.Zero, MediaTime.Zero), (c.FadeIn, c.FadeOut)));
    }

    [Fact]
    public void Trim_move_and_speed_keep_the_stored_fades()
    {
        var f = WithVideo(out var clip);
        SetFades(f, clip, F(f, 50), F(f, 60));

        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, F(f, 30)).Success);   // shorter than both fades
        Assert.Equal(30, Frames(f, clip));
        Assert.Equal((F(f, 50), F(f, 60)), (clip.FadeIn, clip.FadeOut));
        Assert.Equal((30L, 30L), Core.Playback.FadeRule.EffectiveFrames(clip, f.Rate));

        Assert.True(f.Service.MoveClips(new[] { clip.Id }, 7).Success);
        Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(40)).Success);   // 2×
        Assert.Equal((F(f, 50), F(f, 60)), (clip.FadeIn, clip.FadeOut));

        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, F(f, 7 + 200)).Success);  // longer again: the fades apply fully
        Assert.Equal((50L, 60L), Core.Playback.FadeRule.EffectiveFrames(clip, f.Rate));
    }

    [Fact]
    public void A_frame_rate_regrid_keeps_the_stored_fades_and_re_derives_their_frames()
    {
        var f = new TimelineFixture();                                   // provisional 30 fps
        Assert.True(f.Service.AddClip(f.Image().Id).Success);
        var image = f.V1.Clips.Single();
        Assert.True(SetFades(f, image, F(f, 30), F(f, 15)).Success);     // 1 s and 0.5 s
        var stored = (image.FadeIn, image.FadeOut);

        Assert.True(f.Service.AddClip(f.Video(4, FrameRate.Fps25).Id).Success);   // fixes 25 fps: re-grid

        Assert.Equal(FrameRate.Fps25, f.Rate);
        Assert.Equal(stored, (image.FadeIn, image.FadeOut));
        Assert.Equal((25L, 13L), Core.Playback.FadeRule.EffectiveFrames(image, f.Rate));   // 12.5 frames: ties up
        f.AssertValid();
    }

    [Fact]
    public void Changing_one_fade_keeps_the_other_even_when_it_exceeds_a_shorter_clip()
    {
        var f = WithVideo(out var clip);
        SetFades(f, clip, F(f, 100), MediaTime.Zero);
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, F(f, 40)).Success);

        Assert.True(SetFades(f, clip, clip.FadeIn, F(f, 10)).Success);

        Assert.Equal((F(f, 100), F(f, 10)), (clip.FadeIn, clip.FadeOut));
    }
}
