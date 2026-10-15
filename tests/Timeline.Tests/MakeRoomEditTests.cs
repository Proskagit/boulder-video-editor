using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 22 (D037 §3, variant B): a placement edit — a move, a drop (<c>AddClip</c> at a position), Paste, Duplicate, a new
/// text — pushes a clip it overlaps from that clip's start on right by exactly the overlap; later clips of the track move
/// only as far as they are then overlapped (a gap absorbs the push); a placed clip starting inside another clip is refused
/// (Q11); a pushed clip's partner follows by the same frames or the edit is refused; a dissolve between a pushed clip and a
/// clip that stays is refused (Q9); every result one undo step, every refusal none and no change.
/// </summary>
public class MakeRoomEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);

    /// <summary>A frame of the 25 fps grid the first video below fixes (D007) — for positions given before it is added.</summary>
    private static MediaTime F25(long n) => MediaTime.FromFrame(n, FrameRate.Fps25);
    private static (long, long) Span(TimelineFixture f, Clip c) => (c.TimelineStart.ToNearestFrame(f.Rate), c.TimelineEnd.ToNearestFrame(f.Rate));

    private static TextClip Text(TimelineFixture f, Track track, long start, long end)
    {
        var clip = new TextClip { Text = "T", TimelineStart = F(f, start), Duration = F(f, end) - F(f, start) };
        var index = track.Clips.FindIndex(c => c.TimelineStart > clip.TimelineStart);
        track.Clips.Insert(index < 0 ? track.Clips.Count : index, clip);
        return clip;
    }

    private static Track AddTrack(TimelineFixture f, TrackType type)
    {
        Ok(f.Service.AddTrack(type));
        return type == TrackType.Video ? f.Project.Timeline.VideoTracks[^1] : f.Project.Timeline.AudioTracks[^1];
    }

    /// <summary>A refused edit: the message, nothing changed, no undo step.</summary>
    private static void AssertRefused(TimelineFixture f, Func<TimelineEditResult> edit, string messagePart)
    {
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;
        var result = edit();
        Assert.False(result.Success);
        Assert.Contains(messagePart, result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    /// <summary>One undo step restores <paramref name="before"/> exactly, the redo the result.</summary>
    private static void AssertOneStep(TimelineFixture f, string before)
    {
        var after = f.Snapshot();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
    }

    [Fact]
    public void A_moved_clip_pushes_the_clip_it_overlaps_by_the_overlap_and_a_gap_absorbs_the_rest()
    {
        var f = new TimelineFixture();
        var x = Text(f, f.V1, 0, 10);
        var a = Text(f, f.V1, 20, 30);
        var b = Text(f, f.V1, 35, 45);
        var c = Text(f, f.V1, 60, 70);
        var before = f.Snapshot();

        Ok(f.Service.MoveClips(new[] { x.Id }, 18));

        Assert.Equal((18L, 28L), Span(f, x));
        Assert.Equal((28L, 38L), Span(f, a));            // by the overlap 8
        Assert.Equal((38L, 48L), Span(f, b));            // by its own overlap 3: the gap of 5 absorbed the rest
        Assert.Equal((60L, 70L), Span(f, c));            // not overlapped: stays
        f.AssertValid();
        Assert.Equal("Move Clip", (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description);
        AssertOneStep(f, before);
    }

    [Fact]
    public void A_clip_moved_to_start_inside_another_clip_is_refused()
    {
        var f = new TimelineFixture();
        Text(f, f.V1, 0, 10);
        var a = Text(f, f.V1, 20, 30);

        AssertRefused(f, () => f.Service.MoveClips(new[] { a.Id }, -15), "start inside another clip");
        Assert.Null(f.Service.CanMoveClips(new[] { a.Id }, -10));                          // touching is fine
        Assert.Contains("start inside", f.Service.CanMoveClips(new[] { a.Id }, -11));
    }

    [Fact]
    public void A_group_on_several_tracks_makes_room_on_each_of_them()
    {
        var f = new TimelineFixture();
        var v2 = AddTrack(f, TrackType.Video);
        var x = Text(f, f.V1, 0, 10);
        var a = Text(f, f.V1, 10, 20);
        var y = Text(f, v2, 0, 10);
        var b = Text(f, v2, 15, 25);
        var before = f.Snapshot();

        Ok(f.Service.MoveClips(new[] { x.Id, y.Id }, 5));

        Assert.Equal((15L, 25L), Span(f, a));
        Assert.Equal((15L, 25L), Span(f, b));            // touched, not overlapped
        Assert.Equal((5L, 15L), Span(f, y));
        AssertOneStep(f, before);
    }

    [Fact]
    public void A_push_that_would_run_into_a_placed_clip_is_refused()
    {
        var f = new TimelineFixture();
        var v2 = AddTrack(f, TrackType.Video);
        var x = Text(f, f.V1, 0, 10);
        var y = Text(f, f.V1, 30, 40);
        Text(f, v2, 12, 33);                             // pushed to [15, 36) by X, it would reach into Y at 35

        AssertRefused(f, () => f.Service.MoveClips(new[] { x.Id, y.Id }, 5, v2.Id), "no room");
    }

    [Fact]
    public void A_pushed_clip_keeps_its_speed_and_source_range()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id, f.V1.Id, F25(10)));
        var video = f.V1.Clips[0];
        Ok(f.Service.SetClipSpeed(video.Id, ClipSpeed.FromSteps(40)));                     // 2×
        var x = Text(f, f.V1, 0, 10);
        var state = ClipState.Capture(video);

        Ok(f.Service.MoveClips(new[] { x.Id }, 4));

        var after = ClipState.Capture(video);
        Assert.Equal(14L, after.Start.ToNearestFrame(f.Rate));
        Assert.Equal(state.End.ToNearestFrame(f.Rate) + 4, after.End.ToNearestFrame(f.Rate));
        Assert.Equal((state.SourceIn, state.SourceOut, state.Speed), (after.SourceIn, after.SourceOut, after.Speed));
        f.AssertValid();
    }

    // --- Linked partners (variant B) -------------------------------------------------------------------------------------

    /// <summary>A text X at [0, 10) on V1, a recording at [10, 110) with its sound unlinked to A1 and linked again.</summary>
    private static (TimelineFixture F, TextClip X, VideoClip V, AudioClip A) LinkedAfterText()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.VideoWithSound(20, FrameRate.Fps25).Id, f.V1.Id, F25(10)));
        var v = (VideoClip)f.V1.Clips[0];
        Ok(f.Service.TrimClip(v.Id, ClipEdge.End, F(f, 110)));
        Ok(f.Service.UnlinkAudio(v.Id));
        var a = (AudioClip)f.A1.Clips[0];
        Ok(f.Service.LinkClips(new[] { v.Id, a.Id }));
        var x = Text(f, f.V1, 0, 10);
        f.UndoRedo.Clear();
        return (f, x, v, a);
    }

    [Fact]
    public void A_pushed_clips_partner_follows_by_the_same_frames()
    {
        var (f, x, v, a) = LinkedAfterText();
        var before = f.Snapshot();

        Ok(f.Service.MoveClips(new[] { x.Id }, 5));

        Assert.Equal((15L, 115L), Span(f, v));
        Assert.Equal((15L, 115L), Span(f, a));
        f.AssertValid();
        AssertOneStep(f, before);
    }

    [Fact]
    public void A_pushed_clips_partner_that_has_no_room_refuses_the_edit_and_never_pushes_its_own_track()
    {
        var (f, x, _, _) = LinkedAfterText();
        Ok(f.Service.AddClip(f.Audio(10).Id, f.A1.Id, F(f, 110)));                         // right after the partner
        f.UndoRedo.Clear();

        AssertRefused(f, () => f.Service.MoveClips(new[] { x.Id }, 5), "has no room on A1");
    }

    [Fact]
    public void A_pushed_clips_partner_on_a_locked_track_refuses_the_edit()
    {
        var (f, x, _, _) = LinkedAfterText();
        Ok(f.Service.SetTrackLocked(f.A1.Id, true));

        AssertRefused(f, () => f.Service.MoveClips(new[] { x.Id }, 5), "Track A1 is locked.");
    }

    // --- Dissolves (Q9) -------------------------------------------------------------------------------------------------

    [Fact]
    public void Pushed_clips_with_a_dissolve_between_them_keep_it()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id, f.V1.Id, F25(50)));
        Ok(f.Service.Split(F(f, 150)));
        var (a, b) = (f.V1.Clips[0], f.V1.Clips[1]);
        Ok(f.Service.AddTransition(a.Id, b.Id, F(f, 20)));
        var x = Text(f, f.V1, 0, 30);
        var dissolve = f.V1.Transitions.Single();
        var before = f.Snapshot();

        var result = f.Service.MoveClips(new[] { x.Id }, 30);

        Ok(result);
        Assert.Null(result.Message);                                                       // nothing removed
        Assert.Equal((60L, 160L), Span(f, a));
        Assert.Equal((160L, 560L), Span(f, b));
        Assert.Same(dissolve, Assert.Single(f.V1.Transitions));
        f.AssertValid();
        AssertOneStep(f, before);
    }

    [Fact]
    public void A_push_that_would_open_a_dissolve_of_clips_that_are_not_moved_is_refused()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id));                            // [0, 500)
        Ok(f.Service.Split(F(f, 100)));
        Ok(f.Service.AddTransition(f.V1.Clips[0].Id, f.V1.Clips[1].Id, F(f, 20)));
        var x = Text(f, f.V1, 600, 620);

        AssertRefused(f, () => f.Service.MoveClips(new[] { x.Id }, -500), "Remove it first");   // X at the cut
        Assert.Single(f.V1.Transitions);
    }

    // --- The other placement edits (Q10) ----------------------------------------------------------------------------------

    [Fact]
    public void A_drop_makes_room_and_one_starting_inside_a_clip_is_refused()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 20, 40);
        var image = f.Image();
        var before = f.Snapshot();

        var result = f.Service.AddClip(image.Id, f.V1.Id, F(f, 10));

        Ok(result);
        var added = f.V1.Clips.Single(c => c.Id == result.ClipIds[0]);
        Assert.Equal(added.TimelineEnd, a.TimelineStart);
        f.AssertValid();
        AssertOneStep(f, before);

        AssertRefused(f, () => f.Service.AddClip(image.Id, f.V1.Id, added.TimelineStart + F(f, 1)), "start inside another clip");
    }

    [Fact]
    public void A_new_text_makes_room_on_the_topmost_track()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 100, 110);
        var before = f.Snapshot();

        var result = f.Service.AddTextClip(F(f, 50));

        Ok(result);
        var added = f.V1.Clips.Single(c => c.Id == result.ClipIds[0]);
        Assert.Equal(50L, added.TimelineStart.ToNearestFrame(f.Rate));
        Assert.Equal(added.TimelineEnd, a.TimelineStart);
        AssertOneStep(f, before);
    }

    [Fact]
    public void Paste_and_duplicate_make_room_at_their_exact_places()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 20);
        var b = Text(f, f.V1, 30, 40);
        var clipboard = f.Service.CopyClips(new[] { a.Id })!;
        var before = f.Snapshot();

        var pasted = f.Service.PasteClips(clipboard, F(f, 25));

        Ok(pasted);
        Assert.Equal((25L, 45L), Span(f, f.V1.Clips.Single(c => c.Id == pasted.ClipIds[0])));   // the playhead, unsnapped
        Assert.Equal((45L, 55L), Span(f, b));
        AssertOneStep(f, before);
        f.UndoRedo.Undo();

        var duplicated = f.Service.DuplicateClips(new[] { a.Id });

        Ok(duplicated);
        Assert.Equal((20L, 40L), Span(f, f.V1.Clips.Single(c => c.Id == duplicated.ClipIds[0])));
        Assert.Equal((40L, 50L), Span(f, b));
        f.AssertValid();
    }

    [Fact]
    public void Insert_and_overwrite_keep_their_own_rules()
    {
        var f = new TimelineFixture();
        var video = f.Video(20, FrameRate.Fps25);
        Ok(f.Service.AddClip(video.Id));                                                    // [0, 500)
        var later = Text(f, f.V1, 600, 610);

        Ok(f.Service.InsertClip(video.Id, F(f, 0), F(f, 50), F(f, 100)));
        Assert.Equal((650L, 660L), Span(f, later));                                         // the ripple of D031: every later clip

        Ok(f.Service.OverwriteClip(video.Id, F(f, 0), F(f, 50), F(f, 0)));
        Assert.Equal((650L, 660L), Span(f, later));                                         // overwrite moves nothing
        f.AssertValid();
    }
}
