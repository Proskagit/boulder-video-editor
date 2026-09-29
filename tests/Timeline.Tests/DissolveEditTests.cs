using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 10 Step 10.6 (D025 §3–§5): adding, removing and resizing cross dissolves — whole frames, zone fit, source
/// handles (1× and other speeds, images and text unlimited) — and what every timeline edit does to them: kept, removed
/// automatically in the same undo step (with a status note), rejected or clamped. Undo / Redo restore clips and
/// dissolves exactly (<see cref="TimelineFixture.Snapshot"/> includes the dissolves).
/// </summary>
public class DissolveEditTests
{
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;

    /// <summary>A <paramref name="seconds"/> s video on V1 split at <paramref name="splitAt"/>: A and B share one source,
    /// so A's source continues past its end (B's part) and B's before its start (A's part).</summary>
    private static (TimelineFixture F, Clip A, Clip B) Cut(double seconds = 20, long splitAt = 100, FrameRate? rate = null)
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(seconds, rate ?? FrameRate.Fps25).Id));
        Ok(f.Service.Split(MediaTime.FromFrame(splitAt, f.Rate)));
        return (f, f.V1.Clips[0], f.V1.Clips[1]);
    }

    private static Transition AddDissolve(TimelineFixture f, Clip a, Clip b, long frames)
    {
        var result = f.Service.AddTransition(a.Id, b.Id, F(f, frames));
        Ok(result);
        return f.V1.Transitions.Single(t => t.Id == result.TransitionId);
    }

    // --- add ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Adding_a_dissolve_anchors_it_on_the_cut_in_one_undo_step()
    {
        var (f, a, b) = Cut();
        var before = f.Snapshot();

        var result = f.Service.AddTransition(a.Id, b.Id, new MediaTime(F(f, 20).Ticks + 1234));   // nearest whole frame

        Ok(result);
        var dissolve = Assert.Single(f.V1.Transitions);
        Assert.Equal(result.TransitionId, dissolve.Id);
        Assert.Equal((TransitionRules.CrossDissolve, a.Id, b.Id, F(f, 20)), (dissolve.TransitionTypeId, dissolve.LeftClipId, dissolve.RightClipId, dissolve.Duration));
        Assert.Equal("Add Dissolve", Top(f));
        f.AssertValid();
        var after = f.Snapshot();

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
    }

    [Fact]
    public void A_dissolve_needs_two_touching_clips_of_one_unlocked_video_track_and_two_frames()
    {
        var (f, a, b) = Cut();
        var before = f.Snapshot();

        Assert.Contains("at least 2 frames", f.Service.AddTransition(a.Id, b.Id, F(f, 1)).Message);
        Assert.False(f.Service.AddTransition(b.Id, a.Id, F(f, 10)).Success);         // the wrong way round: B doesn't end where A starts
        Assert.False(f.Service.AddTransition(a.Id, a.Id, F(f, 10)).Success);

        Ok(f.Service.AddTrack(TrackType.Video));
        Ok(f.Service.AddClip(f.Image().Id, f.Project.Timeline.VideoTracks[1].Id, F(f, 100)));
        var image = f.Project.Timeline.VideoTracks[1].Clips.Single();
        Assert.Contains("same track", f.Service.AddTransition(a.Id, image.Id, F(f, 10)).Message);

        f.V1.IsLocked = true;
        Assert.Contains("locked", f.Service.AddTransition(a.Id, b.Id, F(f, 10)).Message);
        f.V1.IsLocked = false;

        AddDissolve(f, a, b, 10);
        Assert.Contains("already has a dissolve", f.Service.AddTransition(a.Id, b.Id, F(f, 12)).Message);
        Assert.Single(f.V1.Transitions);
        Assert.NotEqual(before, f.Snapshot());
    }

    [Fact]
    public void Untrimmed_clips_have_no_handles_and_get_no_dissolve()
    {
        var f = new TimelineFixture();
        var video = f.Video(4, FrameRate.Fps25);
        Ok(f.Service.AddClip(video.Id));
        Ok(f.Service.AddClip(video.Id));                                   // appended: A ends where B starts, both whole
        var (a, b) = (f.V1.Clips[0], f.V1.Clips[1]);
        var before = f.Snapshot();

        Assert.Equal(0, f.Service.MaxTransitionFrames(a.Id, b.Id));
        var result = f.Service.AddTransition(a.Id, b.Id, F(f, 10));

        Assert.False(result.Success);
        Assert.Equal(TimelineEditService.NotEnoughMedia, result.Message);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void The_longest_dissolve_is_limited_by_the_zone_and_the_handles_and_named_in_the_message()
    {
        var (f, a, b) = Cut(seconds: 4, splitAt: 60);                     // 100 frames: A [0, 60), B [60, 100)
        Ok(f.Service.TrimClip(b.Id, ClipEdge.End, F(f, 70)));              // B keeps 10 frames; A's handle after = 40
        // ⌊F/2⌋ ≤ min(60 in A, 60 of B's handle) and ⌈F/2⌉ ≤ min(10 in B, 40 of A's handle) → F ≤ 20.
        Assert.Equal(20, f.Service.MaxTransitionFrames(a.Id, b.Id));

        var tooLong = f.Service.AddTransition(a.Id, b.Id, F(f, 21));
        Assert.False(tooLong.Success);
        Assert.Contains("longest that fits is 20 frames", tooLong.Message);
        Assert.Empty(f.V1.Transitions);

        AddDissolve(f, a, b, 20);
        f.AssertValid();
    }

    [Fact]
    public void Handles_at_another_speed_are_counted_at_that_speed()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(4, FrameRate.Fps25).Id));
        Ok(f.Service.SetClipSpeed(f.V1.Clips[0].Id, ClipSpeed.FromSteps(40)));   // 2×: 50 frames
        Ok(f.Service.Split(F(f, 20)));
        var (a, b) = (f.V1.Clips[0], f.V1.Clips[1]);                             // A [0, 20), B [20, 50), both 2×

        // A: 50 − 20 = 30 frames of source after it; B: 1.6 s of source before it = 20 frames at 2×.
        // ⌊F/2⌋ ≤ min(20, 20), ⌈F/2⌉ ≤ min(30, 30) → F ≤ 41.
        Assert.Equal(41, f.Service.MaxTransitionFrames(a.Id, b.Id));
        AddDissolve(f, a, b, 41);
        Assert.False(f.Service.SetTransitionDuration(f.V1.Transitions[0].Id, F(f, 42)).Success);
        f.AssertValid();
    }

    [Fact]
    public void Images_and_text_have_unlimited_handles()
    {
        var f = new TimelineFixture();                                            // provisional 30 fps
        Ok(f.Service.AddClip(f.Image().Id));                                       // [0, 150)
        Ok(f.Service.AddTextClip(F(f, 150)));                                      // [150, 300), the same (only) track
        var (image, text) = (f.V1.Clips[0], f.V1.Clips[1]);

        Assert.Equal(300, f.Service.MaxTransitionFrames(image.Id, text.Id));      // only the zone limits it
        AddDissolve(f, image, text, 300);
        f.AssertValid();
    }

    [Fact]
    public void An_odd_dissolve_at_ntsc_puts_the_floor_half_before_the_cut()
    {
        var (f, a, b) = Cut(seconds: 10, splitAt: 100, rate: FrameRate.Ntsc30);   // 299 frames
        // A: 299 − 100 = 199 after; B: 100 before; ⌊F/2⌋ ≤ min(100, 100), ⌈F/2⌉ ≤ min(199, 199) → 201.
        Assert.Equal(201, f.Service.MaxTransitionFrames(a.Id, b.Id));
        var dissolve = AddDissolve(f, a, b, 7);

        Assert.Equal(7, TransitionRules.Frames(dissolve.Duration, f.Rate));
        Assert.Equal((3L, 4L), TransitionRules.Zone(7));
        f.AssertValid();
    }

    // --- remove and resize ----------------------------------------------------------------------------------------------

    [Fact]
    public void Removing_a_dissolve_is_one_undo_step()
    {
        var (f, a, b) = Cut();
        var dissolve = AddDissolve(f, a, b, 10);
        var with = f.Snapshot();

        Ok(f.Service.RemoveTransition(dissolve.Id));
        Assert.Empty(f.V1.Transitions);
        Assert.Equal("Remove Dissolve", Top(f));

        f.UndoRedo.Undo();
        Assert.Equal(with, f.Snapshot());
        Assert.Same(dissolve, Assert.Single(f.V1.Transitions));
    }

    [Fact]
    public void Resizing_merges_consecutive_changes_and_respects_the_limits()
    {
        var (f, a, b) = Cut();
        var dissolve = AddDissolve(f, a, b, 20);
        var added = f.Snapshot();

        Ok(f.Service.SetTransitionDuration(dissolve.Id, F(f, 24)));
        Ok(f.Service.SetTransitionDuration(dissolve.Id, F(f, 30)));
        Assert.Equal(F(f, 30), dissolve.Duration);
        Assert.Equal("Change Dissolve Duration", Top(f));
        Assert.True(f.Service.SetTransitionDuration(dissolve.Id, F(f, 30)).NoChange);
        Assert.Contains("at least 2", f.Service.SetTransitionDuration(dissolve.Id, F(f, 1)).Message);
        Assert.Contains("longest that fits is 201", f.Service.SetTransitionDuration(dissolve.Id, F(f, 250)).Message);   // ⌊F/2⌋ ≤ 100 in A

        f.UndoRedo.Undo();                                                        // both resizes at once
        Assert.Equal(added, f.Snapshot());
    }

    [Fact]
    public void A_locked_track_rejects_removing_and_resizing()
    {
        var (f, a, b) = Cut();
        var dissolve = AddDissolve(f, a, b, 20);
        f.V1.IsLocked = true;

        Assert.False(f.Service.RemoveTransition(dissolve.Id).Success);
        Assert.False(f.Service.SetTransitionDuration(dissolve.Id, F(f, 30)).Success);
        Assert.Single(f.V1.Transitions);
    }

    [Fact]
    public void Dissolves_on_both_edges_of_a_clip_share_its_frames()
    {
        var (f, a, b) = Cut();
        Ok(f.Service.Split(F(f, 200)));
        var c = f.V1.Clips[2];                                                    // A [0, 100), B [100, 200), C [200, 500)
        var first = AddDissolve(f, a, b, 40);                                     // 20 frames in B

        // B has 100 − 20 = 80 frames left; C's handle before is 200, B's after 300 → ⌊F/2⌋ ≤ 80, ⌈F/2⌉ ≤ 300 → 161.
        Assert.Equal(161, f.Service.MaxTransitionFrames(b.Id, c.Id));
        AddDissolve(f, b, c, 161);
        Assert.False(f.Service.SetTransitionDuration(first.Id, F(f, 42)).Success);   // 21 + 80 > 100
        f.AssertValid();
    }

    // --- timeline edits (D025 §5) -----------------------------------------------------------------------------------------

    [Fact]
    public void Moving_one_clip_removes_the_dissolve_with_a_note_and_undo_restores_it()
    {
        var (f, a, b) = Cut();
        AddDissolve(f, a, b, 20);
        var before = f.Snapshot();

        var result = f.Service.MoveClips(new[] { b.Id }, 10);

        Ok(result);
        Assert.Empty(f.V1.Transitions);
        Assert.Equal("A dissolve was removed: its clips no longer meet.", result.Message);
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Moving_both_clips_keeps_the_dissolve_also_on_another_track()
    {
        var (f, a, b) = Cut();
        var dissolve = AddDissolve(f, a, b, 20);
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[1];
        var before = f.Snapshot();

        var moved = f.Service.MoveClips(new[] { a.Id, b.Id }, 15);
        Ok(moved);
        Assert.Null(moved.Message);
        Assert.Same(dissolve, Assert.Single(f.V1.Transitions));

        Ok(f.Service.MoveClips(new[] { a.Id, b.Id }, 0, v2.Id));
        Assert.Empty(f.V1.Transitions);
        Assert.Same(dissolve, Assert.Single(v2.Transitions));
        f.AssertValid();

        f.UndoRedo.Undo();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Trimming_the_cut_edge_removes_the_dissolve()
    {
        var (f, a, b) = Cut();
        AddDissolve(f, a, b, 20);

        var result = f.Service.TrimClip(a.Id, ClipEdge.End, F(f, 90));

        Ok(result);
        Assert.Empty(f.V1.Transitions);
        Assert.NotNull(result.Message);
        f.AssertValid();
    }

    [Fact]
    public void Trimming_a_far_edge_is_clamped_so_the_zone_still_fits()
    {
        var (f, a, b) = Cut();
        var dissolve = AddDissolve(f, a, b, 20);                                  // 10 frames in A, 10 in B

        Ok(f.Service.TrimClip(a.Id, ClipEdge.Start, F(f, 95)));                  // wants 5 frames: clamped to 10
        Assert.Equal(F(f, 90), a.TimelineStart);
        Ok(f.Service.TrimClip(b.Id, ClipEdge.End, F(f, 102)));                   // wants 2 frames: clamped to 10
        Assert.Equal(F(f, 110), b.TimelineEnd);

        Assert.Same(dissolve, Assert.Single(f.V1.Transitions));
        f.AssertValid();
    }

    [Fact]
    public void Split_inside_the_zone_is_rejected_and_elsewhere_keeps_the_dissolve_on_the_cut()
    {
        var (f, a, b) = Cut();
        var dissolve = AddDissolve(f, a, b, 20);                                  // zone [90, 110)
        var before = f.Snapshot();

        Assert.Equal("Can't split inside a dissolve.", f.Service.Split(F(f, 95)).Message);
        Assert.Equal("Can't split inside a dissolve.", f.Service.Split(F(f, 105)).Message);
        Assert.Equal(before, f.Snapshot());

        Ok(f.Service.Split(F(f, 90), new[] { a.Id }));                            // A's right part [90, 100) holds the zone
        var aRight = f.V1.Clips.Single(c => c.TimelineStart == F(f, 90));
        Assert.Equal((aRight.Id, b.Id), (dissolve.LeftClipId, dissolve.RightClipId));

        Ok(f.Service.Split(F(f, 110), new[] { b.Id }));                           // B's left part keeps its id
        Assert.Equal((aRight.Id, b.Id), (dissolve.LeftClipId, dissolve.RightClipId));
        Assert.Equal(F(f, 110), b.TimelineEnd);
        f.AssertValid();

        f.UndoRedo.Undo();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Deleting_a_clip_removes_its_dissolves_and_undo_brings_both_back()
    {
        var (f, a, b) = Cut();
        AddDissolve(f, a, b, 20);
        var before = f.Snapshot();

        var result = f.Service.DeleteClips(new[] { b.Id });

        Ok(result);
        Assert.Empty(f.V1.Transitions);
        Assert.NotNull(result.Message);
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_speed_change_of_A_removes_the_dissolve_in_the_same_step()
    {
        var (f, a, b) = Cut();
        AddDissolve(f, a, b, 20);
        var before = f.Snapshot();

        var result = f.Service.SetClipSpeed(a.Id, ClipSpeed.FromSteps(40));      // A ends at 50: the cut opens

        Ok(result);
        Assert.Empty(f.V1.Transitions);
        Assert.NotNull(result.Message);
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_speed_change_of_B_that_leaves_too_short_a_handle_is_rejected()
    {
        var (f, a, b) = Cut(splitAt: 10);                                         // B's handle before: 10 frames at 1×
        AddDissolve(f, a, b, 20);                                                 // needs 10 before the cut
        var before = f.Snapshot();

        var faster = f.Service.SetClipSpeed(b.Id, ClipSpeed.FromSteps(40));     // at 2× only 5
        Assert.False(faster.Success);
        Assert.Contains(TimelineEditService.NotEnoughMedia, faster.Message);
        Assert.Equal(before, f.Snapshot());

        Ok(f.Service.SetClipSpeed(b.Id, ClipSpeed.FromSteps(10)));               // at 0.5× 20: fine
        Assert.Single(f.V1.Transitions);
        f.AssertValid();
    }

    [Fact]
    public void A_frame_rate_regrid_keeps_a_dissolve_whose_clips_still_meet()
    {
        var f = new TimelineFixture();                                            // provisional 30 fps
        Ok(f.Service.AddClip(f.Image().Id));
        Ok(f.Service.AddTextClip(F(f, 150)));
        var (image, text) = (f.V1.Clips[0], f.V1.Clips[1]);
        var dissolve = AddDissolve(f, image, text, 30);                           // 1 s

        Ok(f.Service.AddTrack(TrackType.Video));
        Ok(f.Service.AddClip(f.Video(2, FrameRate.Fps25).Id, f.Project.Timeline.VideoTracks[1].Id, MediaTime.Zero));   // fixes 25 fps

        Assert.Equal(FrameRate.Fps25, f.Rate);
        Assert.Same(dissolve, Assert.Single(f.V1.Transitions));
        Assert.Equal(25, TransitionRules.Frames(dissolve.Duration, f.Rate));
        f.AssertValid();
    }

    [Fact]
    public void Edits_that_dont_touch_a_dissolve_leave_it_alone_even_without_handles_now()
    {
        var f = new TimelineFixture();
        var source = f.Video(20, FrameRate.Fps25);
        Ok(f.Service.AddClip(source.Id));
        var video = f.V1.Clips[0];
        Ok(f.Service.TrimClip(video.Id, ClipEdge.End, F(f, 100)));               // [0, 100): 4 s of a 20 s source
        Ok(f.Service.AddClip(f.Image().Id));                                       // [100, 225)
        var image = f.V1.Clips[1];
        var dissolve = AddDissolve(f, video, image, 20);                           // needs 10 frames after the video
        Ok(f.Service.AddClip(f.Video(4, FrameRate.Fps25).Id));                   // appended after the image
        var other = f.V1.Clips[2];

        // The source is replaced by a shorter file (4.08 s): 2 frames left after the video — the dissolve's handle is
        // gone (rendering holds the last frame, D025 §4), yet an edit that doesn't touch the dissolve is not rejected.
        source.Metadata!.Duration = MediaTime.FromFrame(102, FrameRate.Fps25);
        Ok(f.Service.MoveClips(new[] { other.Id }, 50));
        Assert.Same(dissolve, Assert.Single(f.V1.Transitions));

        // An edit that touches it checks the handles.
        var touched = f.Service.SetTransitionDuration(dissolve.Id, F(f, 22));
        Assert.False(touched.Success);
        Assert.Contains("the longest that fits is 4 frames", touched.Message);   // ⌈F/2⌉ ≤ 2 frames of handle
        Assert.Equal(F(f, 20), dissolve.Duration);
    }
}
