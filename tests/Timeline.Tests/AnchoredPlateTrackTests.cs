using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>D069 amendment 2 (the owner's check): two clips on one canvas each keep their own «Боулдер» — a plate the edit
/// carries onto another plate's place goes to a free track above its clip (or a new one, in the same step) instead of pushing
/// the other plate.</summary>
public class AnchoredPlateTrackTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static (long, long) Span(Clip c) => (c.TimelineStart.ToNearestFrame(Rate), c.TimelineEnd.ToNearestFrame(Rate));
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_clip_moved_with_its_plate_over_another_plate_keeps_both_plates(bool magnet)
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        f.Settings.IsFrameRateLocked = true;
        var a = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "a.mp4"), F(0).Ticks, F(100).Ticks);
        var b = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "b.mp4"), F(100).Ticks, F(200).Ticks);
        Ok(f.Service.AddTrack(TrackType.Video));                                     // V2: the plates
        Ok(f.Service.AddTrack(TrackType.Video));                                     // V3: where B goes
        var v2 = f.Project.Timeline.VideoTracks[1];
        var v3 = f.Project.Timeline.VideoTracks[2];
        var plateA = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(10), Duration = F(50), AnchorClipId = a.Id };
        var plateB = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(110), Duration = F(50), AnchorClipId = b.Id };
        v2.Clips.Add(plateA);
        v2.Clips.Add(plateB);
        f.Project.Timeline.MagneticMainTrack = magnet;
        var before = f.Snapshot();
        var tracks = f.Project.Timeline.VideoTracks.Count;

        Ok(f.Service.MoveClips(new[] { b.Id }, -100, v3.Id));                       // B over A on the next track, from 0

        Assert.Equal((0L, 100L), Span(b));
        Assert.Equal((10L, 60L), Span(plateA));                                       // A's plate stays where it was
        Assert.Contains(plateA, v2.Clips);
        Assert.Equal((10L, 60L), Span(plateB));                                       // B's plate with B …
        var plateTrack = f.Project.Timeline.VideoTracks.Single(t => t.Clips.Contains(plateB));
        Assert.True(plateTrack.Order > v3.Order);                                     // … above B, on a track of its own
        Assert.Equal(tracks + 1, f.Project.Timeline.VideoTracks.Count);
        f.AssertValid();

        f.UndoRedo.Undo();                                                            // one step: the new track goes too
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(tracks, f.Project.Timeline.VideoTracks.Count);
    }

    // --- D071 amendment 2 (the owner's check of Phase 52) --------------------------------------------------------------------

    /// <summary>V1 with A [0, 100) and B [100, 200), V2 with A's plate [10, 60), V3 with C [300, 400) and its plate on V4.</summary>
    private static (TimelineFixture F, VideoClip A, VideoClip C, TemplateClip PlateA, TemplateClip PlateC) Layout()
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        f.Settings.IsFrameRateLocked = true;
        var a = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "a.mp4"), F(0).Ticks, F(100).Ticks);
        f.Place<VideoClip>(f.V1, f.Video(60, Rate, "b.mp4"), F(100).Ticks, F(200).Ticks);
        Ok(f.Service.AddTrack(TrackType.Video));
        Ok(f.Service.AddTrack(TrackType.Video));
        Ok(f.Service.AddTrack(TrackType.Video));
        var (v2, v3, v4) = (f.Project.Timeline.VideoTracks[1], f.Project.Timeline.VideoTracks[2], f.Project.Timeline.VideoTracks[3]);
        var plateA = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(10), Duration = F(50), AnchorClipId = a.Id };
        v2.Clips.Add(plateA);
        var c = f.Place<VideoClip>(v3, f.Video(60, Rate, "c.mp4"), F(300).Ticks, F(400).Ticks);
        var plateC = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(310), Duration = F(50), AnchorClipId = c.Id };
        v4.Clips.Add(plateC);
        return (f, a, c, plateA, plateC);
    }

    [Fact]
    public void A_clip_dropped_onto_another_clips_plate_is_refused_and_the_plate_stays()
    {
        var (f, _, c, plateA, _) = Layout();
        var v2 = f.Project.Timeline.VideoTracks[1];
        var before = f.Snapshot();

        // C onto V2 at 0: it would push A's plate [10, 60).
        var result = f.Service.MoveClips(new[] { c.Id }, -300, v2.Id);

        Assert.False(result.Success);
        Assert.Equal(string.Format(Core.Localization.Messages.MakeRoom_OtherClipsPlate, Core.Localization.Messages.Verb_Move), result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal((10L, 60L), Span(plateA));
        Assert.Null(f.Service.PreviewMove(new[] { c.Id }, -300, v2.Id));            // the ghost is red
    }

    [Fact]
    public void A_clip_moved_down_takes_its_plate_to_the_lowest_free_track_and_the_emptied_tracks_go()
    {
        var (f, _, c, plateA, plateC) = Layout();
        var (v2, v3) = (f.Project.Timeline.VideoTracks[1], f.Project.Timeline.VideoTracks[2]);
        var before = f.Snapshot();
        var tracks = f.Project.Timeline.VideoTracks.Count;

        Ok(f.Service.MoveClips(new[] { c.Id }, 0, f.V1.Id));                          // C from V3 down onto V1 at 300

        Assert.Contains(c, f.V1.Clips);
        Assert.Contains(plateC, v2.Clips);                                            // down beside A's plate on V2
        Assert.Contains(plateA, v2.Clips);
        Assert.Equal((310L, 360L), Span(plateC));
        Assert.Equal(tracks - 2, f.Project.Timeline.VideoTracks.Count);               // V3 and V4 emptied by the move: removed
        Assert.DoesNotContain(v3, f.Project.Timeline.VideoTracks);
        f.AssertValid();

        f.UndoRedo.Undo();                                                            // one step: the tracks come back
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(tracks, f.Project.Timeline.VideoTracks.Count);
    }

    [Fact]
    public void An_empty_track_the_user_made_and_a_move_in_time_keep_their_tracks()
    {
        var (f, a, c, _, plateC) = Layout();
        Ok(f.Service.AddTrack(TrackType.Video));                                     // V5: empty, the user's
        var tracks = f.Project.Timeline.VideoTracks.Count;
        var v4 = f.Project.Timeline.VideoTracks[3];

        Ok(f.Service.MoveClips(new[] { c.Id }, 50));                                 // C in time on its own track
        Assert.Contains(plateC, v4.Clips);                                            // its plate follows in time, same track
        Assert.Equal(tracks, f.Project.Timeline.VideoTracks.Count);
        f.AssertValid();
    }

    [Fact]
    public void A_plate_on_a_locked_track_stays_when_its_clip_moves_to_another_track()
    {
        var (f, _, c, _, plateC) = Layout();
        var v4 = f.Project.Timeline.VideoTracks[3];
        v4.IsLocked = true;

        Ok(f.Service.MoveClips(new[] { c.Id }, 0, f.V1.Id));                          // C straight down onto V1

        Assert.Contains(plateC, v4.Clips);                                            // D008: the locked track unchanged
        Assert.Contains(v4, f.Project.Timeline.VideoTracks);
        f.AssertValid();
    }

    [Fact]
    public void A_move_to_another_track_keeps_the_main_track_the_users_empty_track_and_a_track_a_ducking_listens_to()
    {
        var (f, a, c, _, _) = Layout();
        var tracks = f.Project.Timeline.VideoTracks;
        var v3 = tracks[2];
        Ok(f.Service.AddTrack(TrackType.Video));                                     // V5: empty, the user's
        var v5 = tracks[^1];
        f.Project.Timeline.AudioTracks[0].Ducking = new Core.Audio.DuckingSettings(v3.Id, -15, 250, 500, 50);   // A1 ducks under V3

        Ok(f.Service.MoveClips(new[] { c.Id }, 0, f.V1.Id));                          // V3 emptied, but A1 listens to it

        Assert.Contains(v3, tracks);                                                  // kept: no ducking left pointing nowhere
        Assert.Contains(v5, tracks);                                                  // the user's empty track kept
        Assert.Equal(v3.Id, f.Project.Timeline.AudioTracks[0].Ducking!.SpeechTrackId);
        f.AssertValid();

        // The main track is never removed: A and B moved up off V1 together (V1 emptied).
        var main = f.V1;
        var b = main.Clips[1];
        Ok(f.Service.MoveClips(new[] { a.Id, b.Id }, 1000, v5.Id));
        Assert.Contains(main, tracks);
        f.AssertValid();
    }

    [Fact]
    public void A_drop_from_the_media_browser_onto_another_clips_plate_is_refused()
    {
        var (f, _, _, plateA, _) = Layout();
        var v2 = f.Project.Timeline.VideoTracks[1];
        var image = f.Image();
        var before = f.Snapshot();

        var result = f.Service.AddClip(image.Id, v2.Id, F(0), insertOnDrop: true);   // at 0 on V2: A's plate [10, 60) in the way

        Assert.False(result.Success);
        Assert.Contains(string.Format(Core.Localization.Messages.MakeRoom_OtherClipsPlate, "").Trim(':', ' ').Split(':').Last().Trim(), result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal((10L, 60L), Span(plateA));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_clip_moved_into_the_free_part_of_its_plates_track_takes_its_plate_to_a_new_track_above(bool magnet)
    {
        // D071 amendment 3 (the owner): V1 the clips, V2 the plates — B into V2's free part at 300 used to be refused (its own
        // plate followed it onto V2 and overlapped it).
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        f.Settings.IsFrameRateLocked = true;
        var a = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "a.mp4"), F(0).Ticks, F(100).Ticks);
        var b = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "b.mp4"), F(100).Ticks, F(200).Ticks);
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[1];
        var plateA = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(10), Duration = F(50), AnchorClipId = a.Id };
        var plateB = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(110), Duration = F(50), AnchorClipId = b.Id };
        v2.Clips.Add(plateA);
        v2.Clips.Add(plateB);
        f.Project.Timeline.MagneticMainTrack = magnet;
        var before = f.Snapshot();

        Ok(f.Service.MoveClips(new[] { b.Id }, 200, v2.Id));                          // B to V2 at 300 — free there

        Assert.Contains(b, v2.Clips);
        Assert.Equal((300L, 400L), Span(b));
        Assert.Equal(3, f.Project.Timeline.VideoTracks.Count);                        // a new V3 for B's plate
        Assert.Contains(plateB, f.Project.Timeline.VideoTracks[2].Clips);
        Assert.Equal((310L, 360L), Span(plateB));
        Assert.Contains(plateA, v2.Clips);
        f.AssertValid();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(2, f.Project.Timeline.VideoTracks.Count);
    }

    [Fact]
    public void A_clip_dragged_above_the_top_track_goes_onto_a_new_track_with_its_plate_above_it()
    {
        // D071 amendment 3 (the owner): with only V1 and the plates' V2, a clip is put over the others by dragging it above V2.
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        f.Settings.IsFrameRateLocked = true;
        var a = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "a.mp4"), F(0).Ticks, F(100).Ticks);
        var b = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "b.mp4"), F(100).Ticks, F(200).Ticks);
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[1];
        var plateB = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(110), Duration = F(50), AnchorClipId = b.Id };
        v2.Clips.Add(new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(10), Duration = F(50), AnchorClipId = a.Id });
        v2.Clips.Add(plateB);
        var before = f.Snapshot();

        Ok(f.Service.MoveClips(new[] { b.Id }, -100, MoveTargets.NewVideoTrack));      // B over A, on a new track

        var tracks = f.Project.Timeline.VideoTracks;
        Assert.Equal(4, tracks.Count);                                                 // V3 for B, V4 for its plate
        Assert.Contains(b, tracks[2].Clips);
        Assert.Equal((0L, 100L), Span(b));
        Assert.Contains(plateB, tracks[3].Clips);
        Assert.Equal((10L, 60L), Span(plateB));
        Assert.Contains(a, f.V1.Clips);
        f.AssertValid();
        Assert.NotNull(f.Service.PreviewMove(new[] { b.Id }, 0, MoveTargets.NewVideoTrack));   // the ghost: allowed
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(2, f.Project.Timeline.VideoTracks.Count);
    }
}
