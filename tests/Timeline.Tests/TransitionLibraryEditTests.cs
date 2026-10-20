using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 28 Step 28.2 (D044 §1–§8): adding each library type with its default (or the longest that fits, never another
/// type), the source handles only for Dissolve and Push, the input ranges, the type change that keeps or clamps the
/// duration, the push's direction, locked and audio tracks, and the 1.0 s rule between fill peaks on every path that can
/// bring two peaks closer — add, type change, move, ripple delete, ripple trim, frame-rate change — while a speed change
/// can only separate a cut. Undo / Redo restore everything (<see cref="TimelineFixture.Snapshot"/> includes the type and
/// the direction).
/// </summary>
public class TransitionLibraryEditTests
{
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;

    /// <summary>Images on V1 at 30 fps (unlimited handles), touching, of the given lengths in frames.</summary>
    private static (TimelineFixture F, List<ImageClip> Clips) Images(params long[] lengths)
    {
        var f = new TimelineFixture();
        var image = f.Image();
        var clips = new List<ImageClip>();
        long at = 0;
        foreach (var length in lengths)
        {
            clips.Add(f.PlaceImageFrames(f.V1, image, at, at + length, f.Rate));
            at += length;
        }
        return (f, clips);
    }

    /// <summary>Two whole videos appended on V1 (25 fps): A ends at its source's end, B starts at its source's start — no
    /// source handles on either side of the cut.</summary>
    private static (TimelineFixture F, Clip A, Clip B) NoHandles()
    {
        var f = new TimelineFixture();
        var video = f.Video(4, FrameRate.Fps25);
        Ok(f.Service.AddClip(video.Id));
        Ok(f.Service.AddClip(video.Id));
        return (f, f.V1.Clips[0], f.V1.Clips[1]);
    }

    private static Transition Add(TimelineFixture f, Clip a, Clip b, string type)
    {
        var result = f.Service.AddTransition(a.Id, b.Id, type);
        Ok(result);
        return f.V1.Transitions.Single(t => t.Id == result.TransitionId);
    }

    // --- add ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("crossDissolve", 30, null)]
    [InlineData("dipToBlack", 18, null)]
    [InlineData("flash", 6, null)]
    [InlineData("zoom", 9, null)]
    [InlineData("push", 9, TransitionDirection.FromRight)]
    public void Each_type_is_added_with_its_default_in_one_undo_step(string type, long frames, TransitionDirection? direction)
    {
        var (f, clips) = Images(100, 100);
        var before = f.Snapshot();

        var transition = Add(f, clips[0], clips[1], type);

        Assert.Equal((type, F(f, frames), direction), (transition.TransitionTypeId, transition.Duration, transition.Direction));
        Assert.Equal(type == TransitionRules.CrossDissolve ? "Add Dissolve" : "Add Transition", Top(f));
        f.AssertValid();
        var after = f.Snapshot();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
    }

    [Theory]
    [InlineData("dipToBlack")]
    [InlineData("flash")]
    [InlineData("zoom")]
    public void A_type_without_handles_fits_a_cut_without_media_beyond_the_clips(string type)
    {
        var (f, a, b) = NoHandles();
        Assert.Equal(0, f.Service.MaxTransitionFrames(a.Id, b.Id));                      // no dissolve fits
        Assert.True(f.Service.MaxTransitionFrames(a.Id, b.Id, type) >= TransitionRules.DefaultFramesOf(type, f.Rate));

        var transition = Add(f, a, b, type);
        Assert.Equal(F(f, TransitionRules.DefaultFramesOf(type, f.Rate)), transition.Duration);
        f.AssertValid();
    }

    [Theory]
    [InlineData("crossDissolve")]
    [InlineData("push")]
    public void A_type_needing_handles_is_refused_without_them_and_never_replaced(string type)
    {
        var (f, a, b) = NoHandles();
        var before = f.Snapshot();
        Assert.Equal(0, f.Service.MaxTransitionFrames(a.Id, b.Id, type));

        var result = f.Service.AddTransition(a.Id, b.Id, type);

        Assert.False(result.Success);
        Assert.Equal(Messages.Transition_NotEnoughMediaType, result.Message);           // names Dip to black and Flash
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void The_default_that_does_not_fit_becomes_the_longest_that_fits_and_below_the_minimum_nothing_is_made()
    {
        var (f, clips) = Images(8, 8, 1, 1);
        var dip = Add(f, clips[0], clips[1], TransitionRules.DipToBlack);
        Assert.Equal(F(f, 16), dip.Duration);                                             // 0.6 s = 18 frames don't fit in 8 + 8

        var before = f.Snapshot();
        var result = f.Service.AddTransition(clips[2].Id, clips[3].Id, TransitionRules.Flash);   // 1 + 1 frames < 3
        Assert.False(result.Success);
        Assert.Equal(Messages.TransitionRule_ClipTooShort, result.Message);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void An_unknown_or_audio_type_is_refused_on_a_video_track_and_a_video_type_on_an_audio_track()
    {
        var (f, clips) = Images(100, 100);
        Assert.Equal(Messages.Transition_WrongType, f.Service.AddTransition(clips[0].Id, clips[1].Id, "wipe").Message);
        Assert.Equal(Messages.Transition_WrongType, f.Service.AddTransition(clips[0].Id, clips[1].Id, TransitionRules.Crossfade).Message);

        var music = f.Audio(20);
        var x = f.Place<AudioClip>(f.A1, music, 0, F(f, 100).Ticks, F(f, 50).Ticks);
        var y = f.Place<AudioClip>(f.A1, music, F(f, 100).Ticks, F(f, 200).Ticks, F(f, 300).Ticks);
        Assert.Equal(Messages.Transition_WrongType, f.Service.AddTransition(x.Id, y.Id, TransitionRules.Zoom).Message);
        var crossfade = f.Service.AddTransition(x.Id, y.Id, TransitionRules.Crossfade);
        Ok(crossfade);
        Assert.Equal(Messages.Transition_WrongType, f.Service.SetTransitionType(crossfade.TransitionId!.Value, TransitionRules.Flash).Message);
        Assert.Null(f.A1.Transitions.Single().Direction);
    }

    // --- ranges ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("flash", 15, true)]
    [InlineData("flash", 16, false)]          // above 0.5 s
    [InlineData("flash", 3, true)]
    [InlineData("zoom", 5, false)]            // below 0.2 s
    [InlineData("zoom", 30, true)]
    [InlineData("zoom", 31, false)]           // above 1.0 s
    [InlineData("push", 5, false)]
    [InlineData("push", 6, true)]
    [InlineData("dipToBlack", 2, false)]      // below 0.1 s
    [InlineData("dipToBlack", 3, true)]
    public void A_duration_outside_the_types_range_is_refused(string type, long frames, bool accepted)
    {
        var (f, clips) = Images(100, 100);
        var transition = Add(f, clips[0], clips[1], type);
        var before = f.Snapshot();

        var result = f.Service.SetTransitionDuration(transition.Id, F(f, frames));

        if (accepted)
        {
            Assert.True(result.Success || result.NoChange, result.Message);
            Assert.Equal(F(f, frames), transition.Duration);
        }
        else
        {
            Assert.False(result.Success);
            var (min, max) = (TransitionRules.MinFramesOf(type, f.Rate), TransitionRules.MaxFramesOf(type, f.Rate));
            Assert.Equal(max == long.MaxValue ? string.Format(Messages.Transition_DurationMin, min)
                : string.Format(Messages.Transition_DurationRange, min, max), result.Message);
            Assert.Equal(before, f.Snapshot());
        }
    }

    // --- type change ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_type_change_keeps_the_duration_in_range_clamps_it_otherwise_and_is_one_step_of_its_own()
    {
        var (f, clips) = Images(100, 100);
        var t = Add(f, clips[0], clips[1], TransitionRules.CrossDissolve);               // 30 frames
        var dissolve = f.Snapshot();

        Ok(f.Service.SetTransitionType(t.Id, TransitionRules.Flash));                     // 1 s → 0.5 s
        Assert.Equal((TransitionRules.Flash, F(f, 15), (TransitionDirection?)null), (t.TransitionTypeId, t.Duration, t.Direction));
        Assert.Equal("Change Transition Type", Top(f));
        var flash = f.Snapshot();

        Ok(f.Service.SetTransitionDuration(t.Id, F(f, 6)));                               // its own step: never merged
        Ok(f.Service.SetTransitionType(t.Id, TransitionRules.Zoom));                      // 6 frames lie in Zoom's 6–30: kept
        Assert.Equal(F(f, 6), t.Duration);
        Ok(f.Service.SetTransitionType(t.Id, TransitionRules.Push));                      // the default direction
        Assert.Equal((F(f, 6), (TransitionDirection?)TransitionDirection.FromRight), (t.Duration, t.Direction));
        Ok(f.Service.SetTransitionType(t.Id, TransitionRules.DipToBlack));
        Assert.Null(t.Direction);
        Assert.True(f.Service.SetTransitionType(t.Id, TransitionRules.DipToBlack).NoChange);
        f.AssertValid();

        f.UndoRedo.Undo();                                                                // Dip → Push
        f.UndoRedo.Undo();                                                                // Push → Zoom
        f.UndoRedo.Undo();                                                                // Zoom → Flash 6
        f.UndoRedo.Undo();                                                                // 6 → 15
        Assert.Equal(flash, f.Snapshot());
        f.UndoRedo.Undo();
        Assert.Equal(dissolve, f.Snapshot());
    }

    [Fact]
    public void A_type_change_to_a_type_needing_handles_is_refused_where_there_are_none()
    {
        var (f, a, b) = NoHandles();
        var t = Add(f, a, b, TransitionRules.Zoom);
        var before = f.Snapshot();

        var result = f.Service.SetTransitionType(t.Id, TransitionRules.Push);

        Assert.False(result.Success);
        Assert.Equal(Messages.Transition_NotEnoughMediaType, result.Message);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_type_needing_handles_is_clamped_to_the_longest_that_fits()
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = FrameRate.Fps25;
        var first = f.Video(4, FrameRate.Fps25, "first.mp4");                              // 100 frames each
        var second = f.Video(4, FrameRate.Fps25, "second.mp4");
        // A uses the first 97 frames (3 frames of source after its end), B starts 5 frames into its source.
        var a = f.Place<VideoClip>(f.V1, first, 0, F(f, 97).Ticks, 0);
        var b = f.Place<VideoClip>(f.V1, second, F(f, 97).Ticks, F(f, 192).Ticks, F(f, 5).Ticks);
        var t = Add(f, a, b, TransitionRules.Zoom);                                         // 0.3 s = 8 frames, no handles needed
        Assert.Equal(F(f, 8), t.Duration);

        // ⌊F/2⌋ ≤ B's 5 frames before, ⌈F/2⌉ ≤ A's 3 frames after → 6; Push's minimum is 5 frames at 25 fps.
        Assert.Equal(6, f.Service.MaxTransitionFrames(a.Id, b.Id, TransitionRules.Push));
        Ok(f.Service.SetTransitionType(t.Id, TransitionRules.Push));
        Assert.Equal(F(f, 6), t.Duration);
        f.AssertValid();
    }

    [Fact]
    public void A_library_type_that_does_not_fit_names_the_transition_not_the_dissolve()
    {
        var (f, clips) = Images(10, 10);
        var zoom = Add(f, clips[0], clips[1], TransitionRules.Zoom);                      // 9 frames; 20 fit
        Assert.Equal(string.Format(Messages.Transition_TooLongType, 21, 20), f.Service.SetTransitionDuration(zoom.Id, F(f, 21)).Message);

        var (g, a, b) = NoHandles();
        var dip = Add(g, a, b, TransitionRules.DipToBlack);
        Ok(g.Service.SetTransitionType(dip.Id, TransitionRules.Zoom));
        var before = g.Snapshot();
        // A push needs handles the cut hasn't got: the message names the types that need none (D044 §6).
        Assert.Equal(Messages.Transition_NotEnoughMediaType, g.Service.SetTransitionType(dip.Id, TransitionRules.Push).Message);
        Assert.Equal(before, g.Snapshot());
    }

    // --- direction ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Only_a_push_has_a_direction_and_its_change_is_one_step()
    {
        var (f, clips) = Images(100, 100, 100);
        var push = Add(f, clips[0], clips[1], TransitionRules.Push);
        var zoom = Add(f, clips[1], clips[2], TransitionRules.Zoom);
        var before = f.Snapshot();

        Ok(f.Service.SetTransitionDirection(push.Id, TransitionDirection.FromTop));
        Assert.Equal(TransitionDirection.FromTop, push.Direction);
        Assert.Equal("Change Push Direction", Top(f));
        Assert.True(f.Service.SetTransitionDirection(push.Id, TransitionDirection.FromTop).NoChange);
        Assert.Equal(Messages.Transition_NoDirection, f.Service.SetTransitionDirection(zoom.Id, TransitionDirection.FromTop).Message);
        Assert.Equal(Messages.Transition_NoDirection, f.Service.SetTransitionDirection(push.Id, (TransitionDirection)9).Message);
        Assert.Null(zoom.Direction);

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_locked_track_refuses_adding_retyping_and_turning()
    {
        var (f, clips) = Images(100, 100, 100);
        var push = Add(f, clips[0], clips[1], TransitionRules.Push);
        f.V1.IsLocked = true;
        var before = f.Snapshot();

        Assert.Contains("locked", f.Service.AddTransition(clips[1].Id, clips[2].Id, TransitionRules.Flash).Message);
        Assert.Contains("locked", f.Service.SetTransitionType(push.Id, TransitionRules.Zoom).Message);
        Assert.Contains("locked", f.Service.SetTransitionDirection(push.Id, TransitionDirection.FromLeft).Message);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_split_outside_the_zone_keeps_the_type_and_the_direction()
    {
        var (f, clips) = Images(100, 100);
        var push = Add(f, clips[0], clips[1], TransitionRules.Push);
        Ok(f.Service.SetTransitionDirection(push.Id, TransitionDirection.FromBottom));

        Ok(f.Service.Split(F(f, 40), new[] { clips[0].Id }));

        Assert.Equal((TransitionRules.Push, (TransitionDirection?)TransitionDirection.FromBottom), (push.TransitionTypeId, push.Direction));
        Assert.NotEqual(clips[0].Id, push.LeftClipId);                                     // the right part of A
        f.AssertValid();
    }

    // --- the 1.0 s rule (D044 §4) -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("flash", 29, false)]
    [InlineData("flash", 30, true)]
    [InlineData("dipToBlack", 29, false)]
    [InlineData("crossDissolve", 29, true)]
    [InlineData("zoom", 29, true)]
    public void A_fill_transition_closer_than_one_second_to_another_is_refused(string second, long framesApart, bool accepted)
    {
        var (f, clips) = Images(100, framesApart, 100);
        Add(f, clips[0], clips[1], TransitionRules.Flash);
        var before = f.Snapshot();

        var result = f.Service.AddTransition(clips[1].Id, clips[2].Id, second);

        Assert.Equal(accepted, result.Success);
        if (!accepted)
        {
            Assert.Contains(Messages.TransitionRule_FillPeaksTooClose, result.Message);
            Assert.Equal(before, f.Snapshot());
        }
    }

    [Fact]
    public void A_type_change_into_a_fill_too_close_to_another_is_refused()
    {
        var (f, clips) = Images(100, 20, 100);
        Add(f, clips[0], clips[1], TransitionRules.DipToBlack);
        var dissolve = Add(f, clips[1], clips[2], TransitionRules.CrossDissolve);
        var before = f.Snapshot();

        foreach (var fill in new[] { TransitionRules.Flash, TransitionRules.DipToBlack })
        {
            var result = f.Service.SetTransitionType(dissolve.Id, fill);
            Assert.False(result.Success);
            Assert.Contains(Messages.TransitionRule_FillPeaksTooClose, result.Message);
        }
        Ok(f.Service.SetTransitionType(dissolve.Id, TransitionRules.Zoom));               // not a fill: allowed
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    /// <summary>A | B with a flash at frame 30; then X [40, 70) and C | D with a flash at 75 — 45 frames (1.5 s) apart.</summary>
    private static (TimelineFixture F, ImageClip B, ImageClip X, ImageClip C, ImageClip D) TwoFlashes()
    {
        var f = new TimelineFixture();
        var image = f.Image();
        var a = f.PlaceImageFrames(f.V1, image, 0, 30, f.Rate);
        var b = f.PlaceImageFrames(f.V1, image, 30, 40, f.Rate);
        var x = f.PlaceImageFrames(f.V1, image, 40, 70, f.Rate);
        var c = f.PlaceImageFrames(f.V1, image, 70, 75, f.Rate);
        var d = f.PlaceImageFrames(f.V1, image, 75, 105, f.Rate);
        Add(f, a, b, TransitionRules.Flash);
        Add(f, c, d, TransitionRules.Flash);
        return (f, b, x, c, d);
    }

    [Fact]
    public void Moving_a_pair_with_its_flash_closer_than_one_second_is_refused()
    {
        var (f, _, x, c, d) = TwoFlashes();
        Ok(f.Service.DeleteClips(new[] { x.Id }));
        var before = f.Snapshot();

        var result = f.Service.MoveClips(new[] { c.Id, d.Id }, -30);                     // the second cut 75 → 45: 0.5 s apart

        Assert.False(result.Success);
        Assert.Contains(Messages.TransitionRule_FillPeaksTooClose, result.Message);
        Assert.Equal(before, f.Snapshot());
        Ok(f.Service.MoveClips(new[] { c.Id, d.Id }, -15));                               // 75 → 60: exactly 1 s apart
        Assert.Equal(2, f.V1.Transitions.Count);
    }

    [Fact]
    public void A_ripple_delete_that_brings_two_flashes_closer_than_one_second_is_refused()
    {
        var (f, _, x, _, _) = TwoFlashes();
        var before = f.Snapshot();

        var result = f.Service.RippleDeleteClips(new[] { x.Id });                         // 30 frames earlier: 15 apart

        Assert.False(result.Success);
        Assert.Contains(Messages.TransitionRule_FillPeaksTooClose, result.Message);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_ripple_trim_that_brings_two_flashes_closer_than_one_second_is_refused()
    {
        var (f, _, x, _, _) = TwoFlashes();
        var before = f.Snapshot();

        var result = f.Service.RippleTrimClip(x.Id, ClipEdge.End, F(f, 50));             // 20 frames earlier: 25 apart

        Assert.False(result.Success);
        Assert.Contains(Messages.TransitionRule_FillPeaksTooClose, result.Message);
        Assert.Equal(before, f.Snapshot());
        Ok(f.Service.RippleTrimClip(x.Id, ClipEdge.End, F(f, 55)));                       // 15 earlier: exactly 1 s apart
    }

    [Fact]
    public void A_speed_change_can_only_separate_a_cut_and_never_brings_fill_peaks_closer()
    {
        var f = new TimelineFixture();
        var video = f.Video(20, FrameRate.Fps25);
        Ok(f.Service.AddClip(video.Id));
        Ok(f.Service.Split(MediaTime.FromFrame(50, f.Rate)));
        Ok(f.Service.Split(MediaTime.FromFrame(75, f.Rate)));
        var (a, b, c) = (f.V1.Clips[0], f.V1.Clips[1], f.V1.Clips[2]);
        Add(f, a, b, TransitionRules.Flash);
        Add(f, b, c, TransitionRules.Flash);                                               // 25 frames = exactly 1 s

        // B's end moves (D022: the start stays): its cut with C separates and that flash goes in the same step.
        Ok(f.Service.SetClipSpeed(b.Id, ClipSpeed.FromSteps(40)));
        var flash = Assert.Single(f.V1.Transitions);
        Assert.Equal(a.Id, flash.LeftClipId);
        f.AssertValid();
    }

    [Fact]
    public void A_frame_rate_change_whose_rounding_brings_fill_peaks_closer_than_one_second_is_refused()
    {
        // At 25 fps the cuts at frames 63 and 88 are exactly 1 s apart; on the 29.97 grid they go to the nearest frames
        // 76 (75.52 up) and 105 (105.49 down): 29 frames = 0.968 s.
        foreach (var (type, accepted) in new[] { (TransitionRules.Flash, false), (TransitionRules.Zoom, true) })
        {
            var f = new TimelineFixture();
            f.Settings.FrameRate = FrameRate.Fps25;
            var image = f.Image();
            var a = f.PlaceImageFrames(f.V1, image, 0, 63, f.Rate);
            var b = f.PlaceImageFrames(f.V1, image, 63, 88, f.Rate);
            var c = f.PlaceImageFrames(f.V1, image, 88, 200, f.Rate);
            Add(f, a, b, type);
            Add(f, b, c, type);
            var before = f.Snapshot();

            var result = f.Service.SetFrameRate(FrameRate.Ntsc30);

            Assert.Equal(accepted, result.Success);
            if (!accepted)
            {
                Assert.Contains(Messages.TransitionRule_FillPeaksTooClose, result.Message);
                Assert.Equal(before, f.Snapshot());
            }
        }
    }
}
