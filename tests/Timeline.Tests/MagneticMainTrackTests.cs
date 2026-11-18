using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D069 §6: the magnetic main track. While the magnet is on, V1 has no gaps — a removal, a move or a trim closes them in the
/// same Undo step; a drop or a move onto V1 goes to the cut nearest the drop point and makes room; turning the magnet on
/// closes the existing gaps; the linked sound, the anchored templates and the other video tracks' clips that start inside a
/// moved V1 clip go with it; titles never land on V1. With the magnet off V1 behaves as before.
/// </summary>
public class MagneticMainTrackTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private static (long, long) Span(Clip c) => (c.TimelineStart.ToNearestFrame(Rate), c.TimelineEnd.ToNearestFrame(Rate));

    /// <summary>A 25 fps project whose V1 holds video clips of the given frame spans; the magnet as given.</summary>
    private static (TimelineFixture F, List<VideoClip> Clips) Layout(bool magnet, params (long Start, long End)[] spans)
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        f.Settings.IsFrameRateLocked = true;
        var clips = new List<VideoClip>();
        foreach (var (start, end) in spans)
        {
            var asset = f.Video(60, Rate, $"v{clips.Count}.mp4");
            clips.Add(f.Place<VideoClip>(f.V1, asset, F(start).Ticks, F(end).Ticks));
        }
        f.Project.Timeline.MagneticMainTrack = magnet;
        return (f, clips);
    }

    private static void AssertOneStep(TimelineFixture f, string before)
    {
        var after = f.Snapshot();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
    }

    [Fact]
    public void Deleting_a_clip_closes_its_gap_in_one_step()
    {
        var (f, c) = Layout(true, (0, 10), (10, 20), (20, 30));
        var before = f.Snapshot();

        Ok(f.Service.DeleteClips(new[] { c[1].Id }));

        Assert.Equal((0L, 10L), Span(c[0]));
        Assert.Equal((10L, 20L), Span(c[2]));
        f.AssertValid();
        AssertOneStep(f, before);
    }

    [Fact]
    public void Without_the_magnet_a_deletion_leaves_the_gap()
    {
        var (f, c) = Layout(false, (0, 10), (10, 20), (20, 30));

        Ok(f.Service.DeleteClips(new[] { c[1].Id }));

        Assert.Equal((20L, 30L), Span(c[2]));
    }

    [Fact]
    public void A_clip_moved_past_the_end_lines_up_after_the_last_one_and_the_rest_close_up()
    {
        var (f, c) = Layout(true, (0, 10), (10, 20), (20, 30));

        Ok(f.Service.MoveClips(new[] { c[0].Id }, 50, insertOnDrop: true));

        Assert.Equal((0L, 10L), Span(c[1]));
        Assert.Equal((10L, 20L), Span(c[2]));
        Assert.Equal((20L, 30L), Span(c[0]));
        f.AssertValid();
    }

    [Theory]
    [InlineData(12, 10)]   // the first half of the clip: before it
    [InlineData(17, 20)]   // the second half: after it
    public void A_clip_dropped_inside_a_clip_goes_to_the_nearest_cut(long dropFrame, long expectedStart)
    {
        var (f, c) = Layout(true, (0, 10), (10, 20), (20, 30));
        var dropped = f.Video(60, Rate, "new.mp4");

        var result = f.Service.AddClip(dropped.Id, f.V1.Id, F(dropFrame), insertOnDrop: true);

        Ok(result);
        var added = f.V1.Clips.Single(x => x.Id == result.ClipIds[0]);
        Assert.Equal(expectedStart, Span(added).Item1);
        var spans = f.V1.Clips.Select(Span).OrderBy(s => s.Item1).ToList();
        for (var i = 1; i < spans.Count; i++) Assert.Equal(spans[i - 1].Item2, spans[i].Item1);   // no gap, no overlap
        Assert.Equal(0L, spans[0].Item1);
        f.AssertValid();
    }

    [Fact]
    public void A_dragged_clip_dropped_into_the_first_half_of_another_swaps_with_it()
    {
        var (f, c) = Layout(true, (0, 10), (10, 20), (20, 30));

        Ok(f.Service.MoveClips(new[] { c[1].Id }, -7, insertOnDrop: true));   // B's start at 3: inside A's first half

        Assert.Equal((0L, 10L), Span(c[1]));
        Assert.Equal((10L, 20L), Span(c[0]));
        Assert.Equal((20L, 30L), Span(c[2]));
    }

    [Fact]
    public void Turning_the_magnet_on_closes_the_gaps_in_one_step_with_the_switch()
    {
        var (f, c) = Layout(false, (5, 15), (30, 40), (40, 50));
        var before = f.Snapshot();

        Ok(f.Service.SetMagneticMainTrack(true));

        Assert.True(f.Project.Timeline.MagneticMainTrack);
        Assert.Equal((0L, 10L), Span(c[0]));
        Assert.Equal((10L, 20L), Span(c[1]));
        Assert.Equal((20L, 30L), Span(c[2]));
        f.UndoRedo.Undo();
        Assert.False(f.Project.Timeline.MagneticMainTrack);
        Assert.Equal(before, f.Snapshot());

        f.UndoRedo.Redo();
        Ok(f.Service.SetMagneticMainTrack(false));                         // off: nothing moves
        Assert.Equal((10L, 20L), Span(c[1]));
        Assert.False(f.Project.Timeline.MagneticMainTrack);
    }

    [Fact]
    public void Trimming_a_clips_end_shorter_pulls_the_clips_after_it()
    {
        var (f, c) = Layout(true, (0, 10), (10, 20), (20, 30));

        Ok(f.Service.TrimClip(c[0].Id, ClipEdge.End, F(6)));

        Assert.Equal((0L, 6L), Span(c[0]));
        Assert.Equal((6L, 16L), Span(c[1]));
        Assert.Equal((16L, 26L), Span(c[2]));
    }

    [Fact]
    public void The_clips_of_other_video_tracks_starting_inside_a_moved_clip_go_with_it_and_audio_stays()
    {
        var (f, c) = Layout(true, (0, 10), (10, 20), (20, 30));
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[^1];
        var overB = new TextClip { Text = "B", TimelineStart = F(12), Duration = F(5) };
        v2.Clips.Add(overB);
        var music = f.Place<AudioClip>(f.A1, f.Audio(60), F(12).Ticks, F(40).Ticks);

        Ok(f.Service.DeleteClips(new[] { c[0].Id }));

        Assert.Equal((0L, 10L), Span(c[1]));
        Assert.Equal((2L, 7L), Span(overB));                                // with B, by −10
        Assert.Equal((12L, 40L), Span(music));                              // an audio track's own clip stays
    }

    [Fact]
    public void A_carried_clip_that_would_collide_on_its_track_stays_instead_of_refusing_the_edit()
    {
        var (f, c) = Layout(true, (0, 10), (10, 20));
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[^1];
        var overA = new TextClip { Text = "A", TimelineStart = F(2), Duration = F(6) };
        var overB = new TextClip { Text = "B", TimelineStart = F(12), Duration = F(5) };
        v2.Clips.Add(overA);
        v2.Clips.Add(overB);

        Ok(f.Service.DeleteClips(new[] { c[0].Id }));                       // overB by −10 would land on overA

        Assert.Equal((0L, 10L), Span(c[1]));
        Assert.Equal((12L, 17L), Span(overB));
        f.AssertValid();
    }

    [Fact]
    public void The_linked_sound_of_a_moved_clip_follows_it()
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        f.Settings.IsFrameRateLocked = true;
        var a = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "a.mp4"), F(0).Ticks, F(10).Ticks);
        var b = f.Place<VideoClip>(f.V1, f.VideoWithSound(60, Rate, "b.mp4"), F(10).Ticks, F(20).Ticks);
        Ok(f.Service.UnlinkAudio(b.Id));
        var sound = f.A1.Clips.OfType<AudioClip>().Single();
        Ok(f.Service.LinkClips(new[] { b.Id, sound.Id }));                  // a linked pair (D032 R1)
        f.Project.Timeline.MagneticMainTrack = true;

        Ok(f.Service.DeleteClips(new[] { a.Id }));

        Assert.Equal((0L, 10L), Span(b));
        Assert.Equal((0L, 10L), Span(sound));
        f.AssertValid();
    }

    [Fact]
    public void A_title_with_only_the_main_track_gets_a_track_of_its_own_in_the_same_step()
    {
        var (f, c) = Layout(true, (0, 10), (10, 20));
        var before = f.Snapshot();

        var result = f.Service.AddTextClip(F(12));

        Ok(result);
        Assert.Equal(2, f.Project.Timeline.VideoTracks.Count);
        Assert.Equal(2, f.V1.Clips.Count);
        Assert.Single(f.Project.Timeline.VideoTracks[^1].Clips.OfType<TextClip>());
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.Single(f.Project.Timeline.VideoTracks);
    }

    [Fact]
    public void A_template_anchored_to_a_moved_clip_moves_with_it()
    {
        var (f, c) = Layout(true, (0, 10), (10, 30));
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[^1];
        var plate = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(12), Duration = F(10), AnchorClipId = c[1].Id };
        v2.Clips.Add(plate);

        Ok(f.Service.DeleteClips(new[] { c[0].Id }));

        Assert.Equal((0L, 20L), Span(c[1]));
        Assert.Equal((2L, 12L), Span(plate));
    }

    // --- the review's cases (2026-11-18) -----------------------------------------------------------------------------

    [Fact]
    public void A_nudge_the_magnet_takes_back_is_no_edit_no_undo_step_and_not_dirty()
    {
        var (f, c) = Layout(true, (0, 10), (10, 20));
        f.Project.IsDirty = false;
        var before = f.Snapshot();

        var result = f.Service.MoveClips(new[] { c[1].Id }, 1);

        Assert.True(result.Success && result.NoChange, result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.False(f.UndoRedo.CanUndo);
    }

    [Theory]
    [InlineData(12, 10)]
    [InlineData(17, 20)]
    public void A_paste_inside_a_clip_goes_to_the_nearest_cut(long at, long expectedStart)
    {
        var (f, c) = Layout(true, (0, 10), (10, 20), (20, 30));
        var clipboard = f.Service.CopyClips(new[] { c[0].Id })!;
        var before = f.Snapshot();

        var result = f.Service.PasteClips(clipboard, F(at));

        Assert.True(result.Success, result.Message);
        var pasted = f.V1.Clips.Single(x => x.Id == result.ClipIds[0]);
        Assert.Equal(expectedStart, Span(pasted).Item1);
        var spans = f.V1.Clips.Select(Span).OrderBy(s => s.Item1).ToList();
        for (var i = 1; i < spans.Count; i++) Assert.Equal(spans[i - 1].Item2, spans[i].Item1);
        f.AssertValid();
        AssertOneStep(f, before);
    }

    [Fact]
    public void The_first_video_that_fixes_the_frame_rate_lines_up_from_zero()
    {
        var f = new TimelineFixture();
        Assert.False(f.Settings.IsFrameRateLocked);
        f.Project.Timeline.MagneticMainTrack = true;
        var video = f.Video(4, Rate);

        var result = f.Service.AddClip(video.Id, f.V1.Id, MediaTime.FromSeconds(5));

        Assert.True(result.Success, result.Message);
        Assert.True(f.Settings.IsFrameRateLocked);
        Assert.Equal(MediaTime.Zero, f.V1.Clips.Single().TimelineStart);
    }

    [Fact]
    public void Removing_a_files_clips_closes_their_gaps_in_one_step()
    {
        var (f, c) = Layout(true, (0, 10), (10, 20), (20, 30));
        var before = f.Snapshot();

        Assert.True(f.Service.RemoveMedia(c[1].MediaAssetId).Success);

        Assert.Equal((10L, 20L), Span(c[2]));
        f.AssertValid();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_linked_sound_that_cannot_follow_refuses_the_deletion_and_changes_nothing()
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        f.Settings.IsFrameRateLocked = true;
        var a = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "a.mp4"), F(0).Ticks, F(10).Ticks);
        var b = f.Place<VideoClip>(f.V1, f.VideoWithSound(60, Rate, "b.mp4"), F(10).Ticks, F(20).Ticks);
        Ok(f.Service.UnlinkAudio(b.Id));
        var sound = f.A1.Clips.OfType<AudioClip>().Single();
        Ok(f.Service.LinkClips(new[] { b.Id, sound.Id }));
        f.Place<AudioClip>(f.A1, f.Audio(60), F(2).Ticks, F(8).Ticks);         // music where the sound would go
        f.Project.Timeline.MagneticMainTrack = true;
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        var result = f.Service.DeleteClips(new[] { a.Id });

        Assert.False(result.Success);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void A_locked_main_track_keeps_its_gaps_when_the_magnet_is_turned_on()
    {
        var (f, c) = Layout(false, (5, 15), (30, 40));
        f.V1.IsLocked = true;

        Ok(f.Service.SetMagneticMainTrack(true));

        Assert.True(f.Project.Timeline.MagneticMainTrack);
        Assert.Equal((5L, 15L), Span(c[0]));
        Assert.Equal((30L, 40L), Span(c[1]));
    }

    [Fact]
    public void A_deletion_that_carries_a_title_a_template_and_a_linked_sound_is_undone_exactly()
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        f.Settings.IsFrameRateLocked = true;
        var a = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "a.mp4"), F(0).Ticks, F(10).Ticks);
        var b = f.Place<VideoClip>(f.V1, f.VideoWithSound(60, Rate, "b.mp4"), F(10).Ticks, F(30).Ticks);
        Ok(f.Service.UnlinkAudio(b.Id));
        var sound = f.A1.Clips.OfType<AudioClip>().Single();
        Ok(f.Service.LinkClips(new[] { b.Id, sound.Id }));
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[^1];
        var title = new TextClip { Text = "T", TimelineStart = F(11), Duration = F(3) };
        var plate = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(15), Duration = F(10), AnchorClipId = b.Id };
        v2.Clips.Add(title);
        v2.Clips.Add(plate);
        f.Project.Timeline.MagneticMainTrack = true;
        var before = f.Snapshot();

        Ok(f.Service.DeleteClips(new[] { a.Id }));

        Assert.Equal((0L, 20L), Span(b));
        Assert.Equal((0L, 20L), Span(sound));
        Assert.Equal((1L, 4L), Span(title));
        Assert.Equal((5L, 15L), Span(plate));
        f.AssertValid();
        AssertOneStep(f, before);
    }

    /// <summary>The partner the move rule already planned (PlanPartnerShifts) follows the closing too — never shifted twice.</summary>
    [Fact]
    public void A_linked_pair_moved_into_a_gap_closes_up_with_its_sound_shifted_once()
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        f.Settings.IsFrameRateLocked = true;
        var a = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "a.mp4"), F(0).Ticks, F(10).Ticks);
        var b = f.Place<VideoClip>(f.V1, f.VideoWithSound(60, Rate, "b.mp4"), F(10).Ticks, F(20).Ticks);
        Ok(f.Service.UnlinkAudio(b.Id));
        var sound = f.A1.Clips.OfType<AudioClip>().Single();
        Ok(f.Service.LinkClips(new[] { b.Id, sound.Id }));
        f.Project.Timeline.MagneticMainTrack = true;
        var before = f.Snapshot();

        Ok(f.Service.MoveClips(new[] { a.Id }, 40, insertOnDrop: true));   // A past the end: B (and its sound) close up

        Assert.Equal((0L, 10L), Span(b));
        Assert.Equal((0L, 10L), Span(sound));
        Assert.Equal((10L, 20L), Span(a));
        f.AssertValid();
        AssertOneStep(f, before);

        var top = f.UndoRedo.CurrentPosition;
        var nudged = f.Service.MoveClips(new[] { a.Id, b.Id, sound.Id }, 3, insertOnDrop: true);   // the magnet takes it back
        Assert.True(nudged.NoChange, nudged.Message);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }
}
