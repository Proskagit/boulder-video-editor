using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 12 Step 12.5 (D027 §2): ripple delete of the selected clips and close gap. On each track that loses a clip
/// every later clip moves left by the length of the removed clips before it; gaps that are not removed move along;
/// other tracks, the playhead and the markers stay. A removed clip's dissolve is removed (D025's note); every other
/// dissolve keeps its anchors and length; none is created where clips now meet; fades are unchanged. Close gap removes
/// one existing empty span of one track. Each is one Undo step, the result valid, rejected edits change nothing.
/// </summary>
public class RippleEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;
    private static long Start(TimelineFixture f, Clip c) => c.TimelineStart.ToNearestFrame(f.Rate);
    private static long End(TimelineFixture f, Clip c) => c.TimelineEnd.ToNearestFrame(f.Rate);
    private static (long, long) Span(TimelineFixture f, Clip c) => (Start(f, c), End(f, c));

    private static TextClip Text(TimelineFixture f, Track track, long start, long end)
    {
        var clip = new TextClip { Text = "T", TimelineStart = F(f, start), Duration = F(f, end - start) };
        var index = track.Clips.FindIndex(c => c.TimelineStart > clip.TimelineStart);
        track.Clips.Insert(index < 0 ? track.Clips.Count : index, clip);
        return clip;
    }

    /// <summary>A 20 s, 25 fps video on V1 split into A [0, 100), B [100, 300), C [300, 500): one source, so every cut
    /// has handles on both sides.</summary>
    private static (TimelineFixture F, Clip A, Clip B, Clip C) ThreeCuts()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id));
        Ok(f.Service.Split(F(f, 100)));
        Ok(f.Service.Split(F(f, 300)));
        return (f, f.V1.Clips[0], f.V1.Clips[1], f.V1.Clips[2]);
    }

    private static void AssertUndoRedoExact(TimelineFixture f, string before)
    {
        var after = f.Snapshot();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
    }

    // --- ripple delete --------------------------------------------------------------------------------------------------

    [Fact]
    public void Ripple_deleting_a_clip_moves_the_later_clips_of_its_track_left_by_its_length()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 30);
        var b = Text(f, f.V1, 30, 80);
        var c = Text(f, f.V1, 80, 100);
        var before = f.Snapshot();

        var result = f.Service.RippleDeleteClips(new[] { b.Id });

        Ok(result);
        Assert.Equal(new[] { b.Id }, result.ClipIds);
        Assert.Equal(new[] { a, c }, f.V1.Clips);
        Assert.Equal((0L, 30L), Span(f, a));
        Assert.Equal((30L, 50L), Span(f, c));                            // no gap where B was
        Assert.Equal("Ripple Delete Clip", Top(f));
        f.AssertValid();
        AssertUndoRedoExact(f, before);
    }

    [Fact]
    public void Gaps_that_are_not_removed_move_along_with_the_clips_after_them()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 20);
        var b = Text(f, f.V1, 50, 70);                                    // a gap of 30 frames before B
        var c = Text(f, f.V1, 70, 90);

        Ok(f.Service.RippleDeleteClips(new[] { a.Id }));

        Assert.Equal((30L, 50L), Span(f, b));                            // the gap stays, 30 frames from 0
        Assert.Equal((50L, 70L), Span(f, c));
    }

    [Fact]
    public void Several_clips_of_one_track_shift_each_later_clip_by_the_removed_length_before_it()
    {
        var f = new TimelineFixture();
        var x1 = Text(f, f.V1, 0, 10);
        var mid = Text(f, f.V1, 10, 30);
        var x2 = Text(f, f.V1, 40, 55);                                    // after a 10-frame gap
        var last = Text(f, f.V1, 55, 60);

        var result = f.Service.RippleDeleteClips(new[] { x1.Id, x2.Id });

        Ok(result);
        Assert.Equal(new[] { mid, last }, f.V1.Clips);
        Assert.Equal((0L, 20L), Span(f, mid));                           // by X1 (10)
        Assert.Equal((30L, 35L), Span(f, last));                         // by X1 + X2 (25); the gap of 10 kept
        Assert.Equal("Ripple Delete Clips", Top(f));
        f.AssertValid();
    }

    [Fact]
    public void A_multi_track_selection_closes_each_track_by_its_own_clips_and_leaves_the_other_tracks()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[^1];
        var v1a = Text(f, f.V1, 0, 40);
        var v1b = Text(f, f.V1, 40, 60);
        var music = f.Audio(20);
        var a1a = f.Place<AudioClip>(f.A1, music, 0, F(f, 10).Ticks);
        var a1b = f.Place<AudioClip>(f.A1, music, F(f, 10).Ticks, F(f, 50).Ticks, F(f, 10).Ticks);
        var other = Text(f, v2, 20, 80);                                   // not selected, on another track
        f.Project.Timeline.PlayheadPosition = F(f, 45);
        var marker = new Marker { Position = F(f, 42) };
        f.Project.Timeline.Markers.Add(marker);

        Ok(f.Service.RippleDeleteClips(new[] { v1a.Id, a1a.Id }));

        Assert.Equal((0L, 20L), Span(f, v1b));                           // V1 by 40
        Assert.Equal((0L, 40L), Span(f, a1b));                           // A1 by 10
        Assert.Equal((20L, 80L), Span(f, other));                        // V2 untouched
        Assert.Equal(F(f, 45), f.Project.Timeline.PlayheadPosition);
        Assert.Equal(F(f, 42), marker.Position);
        f.AssertValid();
    }

    [Fact]
    public void The_dissolve_of_a_removed_clip_is_removed_and_none_is_created_where_clips_now_meet()
    {
        var (f, a, b, c) = ThreeCuts();
        Ok(f.Service.AddTransition(a.Id, b.Id, F(f, 20)));
        var before = f.Snapshot();

        var result = f.Service.RippleDeleteClips(new[] { b.Id });

        Ok(result);
        Assert.Equal("A dissolve was removed: its clips no longer meet.", result.Message);
        Assert.Empty(f.V1.Transitions);                                  // A and C meet now, without a dissolve
        Assert.Equal(End(f, a), Start(f, c));
        f.AssertValid();
        AssertUndoRedoExact(f, before);
    }

    [Fact]
    public void A_dissolve_after_the_removed_clip_moves_with_its_clips_and_keeps_its_anchors_and_length()
    {
        var (f, a, b, c) = ThreeCuts();
        var dissolveId = f.Service.AddTransition(b.Id, c.Id, F(f, 20)).TransitionId;
        var dissolve = f.V1.Transitions.Single(t => t.Id == dissolveId);
        var fadeOut = F(f, 15);
        Ok(f.Service.SetClipProperties(c.Id, new ClipPropertyChange { Fade = new FadeProperties(MediaTime.Zero, fadeOut) }));
        var before = f.Snapshot();

        var result = f.Service.RippleDeleteClips(new[] { a.Id });

        Ok(result);
        Assert.Null(result.Message);                                      // no dissolve removed
        Assert.Equal((0L, 200L), Span(f, b));
        Assert.Equal((200L, 400L), Span(f, c));
        Assert.Same(dissolve, Assert.Single(f.V1.Transitions));
        Assert.Equal((b.Id, c.Id, F(f, 20)), (dissolve.LeftClipId, dissolve.RightClipId, dissolve.Duration));
        Assert.Equal(fadeOut, c.FadeOut);                                 // fades unchanged
        Assert.Equal(MediaTime.Zero, b.FadeIn);
        var zone = PlaybackSnapshotBuilder.Build(f.Project, 1).VideoLayers.Single().Dissolves.Single();
        Assert.Equal(F(f, 200), zone.Cut);                                // the Preview / export draw it at the new cut
        f.AssertValid();
        AssertUndoRedoExact(f, before);
    }

    [Fact]
    public void A_dissolve_before_the_removed_clip_is_not_touched()
    {
        var (f, a, b, c) = ThreeCuts();
        Ok(f.Service.AddTransition(a.Id, b.Id, F(f, 20)));
        var dissolve = f.V1.Transitions.Single();

        Ok(f.Service.RippleDeleteClips(new[] { c.Id }));

        Assert.Same(dissolve, Assert.Single(f.V1.Transitions));
        Assert.Equal((0L, 100L, 100L, 300L), (Start(f, a), End(f, a), Start(f, b), End(f, b)));
    }

    [Fact]
    public void A_clip_at_another_speed_moves_with_its_speed_and_source_range()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id));
        Ok(f.Service.Split(F(f, 100)));
        var (a, b) = ((MediaBackedClip)f.V1.Clips[0], (MediaBackedClip)f.V1.Clips[1]);
        Ok(f.Service.SetClipSpeed(b.Id, ClipSpeed.FromSteps(40)));
        var (sourceIn, sourceOut, frames) = (b.SourceIn, b.SourceOut, End(f, b) - Start(f, b));

        Ok(f.Service.RippleDeleteClips(new[] { a.Id }));

        Assert.Equal(0L, Start(f, b));
        Assert.Equal(frames, End(f, b));
        Assert.Equal((sourceIn, sourceOut, ClipSpeed.FromSteps(40)), (b.SourceIn, b.SourceOut, b.Speed));
        f.AssertValid();
    }

    [Fact]
    public void At_a_fractional_rate_the_clips_stay_on_the_frame_grid()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Ntsc30).Id));
        Ok(f.Service.Split(F(f, 37)));
        Ok(f.Service.Split(F(f, 211)));
        var (a, c) = (f.V1.Clips[0], f.V1.Clips[2]);

        Ok(f.Service.RippleDeleteClips(new[] { f.V1.Clips[1].Id }));

        Assert.Equal(End(f, a), Start(f, c));
        Assert.True(c.TimelineStart.IsOnFrameGrid(f.Rate));
        Assert.True(c.TimelineEnd.IsOnFrameGrid(f.Rate));
        f.AssertValid();
    }

    [Fact]
    public void A_clip_on_a_locked_track_is_not_ripple_deleted()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 10);
        Text(f, f.V1, 10, 20);
        f.V1.IsLocked = true;
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        var result = f.Service.RippleDeleteClips(new[] { a.Id });

        Assert.False(result.Success);
        Assert.Equal("Track V1 is locked.", result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void Nothing_selected_changes_nothing_and_a_missing_clip_is_reported()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.RippleDeleteClips(Array.Empty<Guid>()).NoChange);
        Assert.Equal("A selected clip no longer exists.", f.Service.RippleDeleteClips(new[] { Guid.NewGuid() }).Message);
    }

    // --- close gap ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Close_gap_removes_the_empty_span_between_two_clips()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 20);
        var b = Text(f, f.V1, 35, 50);
        var c = Text(f, f.V1, 60, 70);                                    // a later gap stays
        var before = f.Snapshot();

        var result = f.Service.CloseGap(f.V1.Id, F(f, 27));

        Ok(result);
        Assert.Equal(new[] { b.Id, c.Id }, result.ClipIds);
        Assert.Equal((0L, 20L), Span(f, a));
        Assert.Equal((20L, 35L), Span(f, b));
        Assert.Equal((45L, 55L), Span(f, c));
        Assert.Equal("Close Gap", Top(f));
        f.AssertValid();
        AssertUndoRedoExact(f, before);
    }

    [Fact]
    public void Close_gap_before_the_first_clip_moves_it_to_the_start()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 12, 30);

        Ok(f.Service.CloseGap(f.V1.Id, MediaTime.Zero));

        Assert.Equal((0L, 18L), Span(f, a));
    }

    [Fact]
    public void Close_gap_needs_an_existing_gap()
    {
        var f = new TimelineFixture();
        Text(f, f.V1, 0, 20);
        Text(f, f.V1, 30, 40);
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        Assert.Equal("There is no gap there.", f.Service.CloseGap(f.V1.Id, F(f, 5)).Message);            // in a clip
        Assert.Equal("There is no gap there: no clip follows it on the track.", f.Service.CloseGap(f.V1.Id, F(f, 45)).Message);
        Assert.Equal("There is no gap there: no clip follows it on the track.", f.Service.CloseGap(f.A1.Id, F(f, 5)).Message);
        Assert.Equal("That track no longer exists.", f.Service.CloseGap(Guid.NewGuid(), F(f, 25)).Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void Close_gap_on_a_locked_track_is_refused()
    {
        var f = new TimelineFixture();
        Text(f, f.V1, 10, 20);
        f.V1.IsLocked = true;

        Assert.Equal("Track V1 is locked.", f.Service.CloseGap(f.V1.Id, F(f, 5)).Message);
    }

    [Fact]
    public void Close_gap_moves_a_dissolve_with_its_clips_and_touches_no_other_track()
    {
        var (f, a, b, c) = ThreeCuts();
        Ok(f.Service.AddTransition(b.Id, c.Id, F(f, 20)));
        Ok(f.Service.RippleDeleteClips(new[] { a.Id }));                  // B [0, 200), C [200, 400)
        Ok(f.Service.MoveClips(new[] { b.Id, c.Id }, 30));                // a 30-frame gap before them
        var dissolve = f.V1.Transitions.Single();
        var title = Text(f, AddTrack(f), 0, 100);                         // another track: untouched

        Ok(f.Service.CloseGap(f.V1.Id, F(f, 10)));

        Assert.Equal((0L, 200L), Span(f, b));
        Assert.Equal((200L, 400L), Span(f, c));
        Assert.Same(dissolve, Assert.Single(f.V1.Transitions));
        Assert.Equal(F(f, 20), dissolve.Duration);
        Assert.Equal((0L, 100L), Span(f, title));
        f.AssertValid();
    }

    [Fact]
    public void Close_gap_before_a_clip_takes_the_gap_right_before_it()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 10);
        var b = Text(f, f.V1, 25, 40);
        var c = Text(f, f.V1, 40, 50);

        Assert.Equal("There is no gap before this clip.", f.Service.CloseGapBefore(a.Id).Message);   // at 0
        Assert.Equal("There is no gap before this clip.", f.Service.CloseGapBefore(c.Id).Message);   // B ends there
        Ok(f.Service.CloseGapBefore(b.Id));

        Assert.Equal((10L, 25L), Span(f, b));
        Assert.Equal((25L, 35L), Span(f, c));
    }

    private static Track AddTrack(TimelineFixture f)
    {
        Ok(f.Service.AddTrack(TrackType.Video));
        return f.Project.Timeline.VideoTracks[^1];
    }
}
