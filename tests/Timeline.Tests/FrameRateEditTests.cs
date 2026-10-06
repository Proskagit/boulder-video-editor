using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 13 Step 13.5 (D028 §4, FR-1…FR-4): <see cref="ITimelineEditService.SetFrameRate"/>. One of the offered rates,
/// as one Undo step that locks it and re-grids the timeline by D007's rule on every track (locked and hidden too);
/// fades and dissolves keep their time, markers their <see cref="MediaTime"/>, the playhead goes to its nearest frame.
/// Refused edits change nothing. FR-1: every dissolve's source handles are checked when the rate changes, also where
/// no clip moved.
/// </summary>
public class FrameRateEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;
    private static MediaTime At(long frame, FrameRate rate) => MediaTime.FromFrame(frame, rate);
    private static void MarkSaved(TimelineFixture f) => f.UndoRedo.MarkSavePoint(f.UndoRedo.CurrentPosition);

    private static MediaAsset Video(TimelineFixture f, FrameRate rate, double seconds = 20, string name = "clip.mp4") =>
        f.AddAsset(name, MediaKind.Video, new MediaMetadata { Duration = MediaTime.FromSeconds(seconds), FrameRate = rate, Width = 1920, Height = 1080 });

    /// <summary>A locked 25 fps project: V1 red [0, 10) + green [10, 37) with a 6-frame dissolve, a fade on red; a text
    /// on V2; music on A1; two markers; the playhead at frame 7.</summary>
    private sealed class Scene
    {
        public readonly TimelineFixture F = new();
        public readonly VideoClip Red, Green;
        public readonly TextClip Title;
        public readonly AudioClip Music;
        public readonly Track V2;
        public readonly Transition Dissolve;
        public static readonly FrameRate Rate = FrameRate.Fps25;

        public Scene()
        {
            var media = Video(F, Rate, name: "pattern.mp4");
            Ok(F.Service.AddClip(media.Id, F.V1.Id, MediaTime.Zero));    // locks 25 fps
            Ok(F.Service.TrimClip(F.V1.Clips[0].Id, ClipEdge.End, At(37, Rate)));
            Ok(F.Service.Split(At(10, Rate)));
            (Red, Green) = ((VideoClip)F.V1.Clips[0], (VideoClip)F.V1.Clips[1]);
            Ok(F.Service.AddTransition(Red.Id, Green.Id, At(6, Rate)));
            Dissolve = F.V1.Transitions.Single();
            Ok(F.Service.SetClipProperties(Red.Id, new ClipPropertyChange { Fade = new FadeProperties(At(3, Rate), MediaTime.Zero) }));

            Ok(F.Service.AddTrack(TrackType.Video));
            V2 = F.Project.Timeline.VideoTracks.OrderByDescending(t => t.Order).First();
            Ok(F.Service.AddTextClip(At(3, Rate)));
            Title = (TextClip)V2.Clips.Single();

            Ok(F.Service.AddClip(F.Audio(20).Id, F.A1.Id, At(1, Rate)));
            Music = (AudioClip)F.A1.Clips.Single();
            Ok(F.Service.AddMarker(At(5, Rate)));
            Ok(F.Service.AddMarker(At(12, Rate)));
            F.Project.Timeline.PlayheadPosition = At(7, Rate);
            MarkSaved(F);
        }

        public string Markers() => string.Join(",", F.Project.Timeline.Markers.Select(m => $"{m.Id}@{m.Position.Ticks}"));
    }

    [Fact]
    public void A_rate_change_is_one_undo_step_that_re_grids_every_clip_and_locks_the_rate()
    {
        var s = new Scene();
        var f = s.F;
        var before = f.Snapshot();
        var markers = s.Markers();
        var (fade, dissolve) = (s.Red.FadeIn, s.Dissolve.Duration);
        var changes = f.TimelineChangedCount;
        var ntsc = FrameRate.Ntsc30;

        var result = f.Service.SetFrameRate(ntsc);

        Ok(result);
        Assert.Contains("29.97", result.Message);
        Assert.Equal((ntsc, true), (f.Settings.FrameRate, f.Settings.IsFrameRateLocked));
        // Each edge to its nearest 29.97 frame: 10 / 25 s = 0.4 s → frame 12 (0.4004 s), 37 / 25 s = 1.48 s → frame 44.
        Assert.Equal((At(0, ntsc), At(12, ntsc)), (s.Red.TimelineStart, s.Red.TimelineEnd));
        Assert.Equal((At(12, ntsc), At(44, ntsc)), (s.Green.TimelineStart, s.Green.TimelineEnd));
        Assert.Equal((At(4, ntsc), At(153, ntsc)), (s.Title.TimelineStart, s.Title.TimelineEnd));     // 0.12 s → 3.60 → 4; 5.12 s → 153.45 → 153
        Assert.Equal(At(1, ntsc), s.Music.TimelineStart);                                                   // 0.04 s → 1.2 → 1
        Assert.Equal(s.Red.TimelineEnd, s.Green.TimelineStart);                                             // they still meet
        Assert.Equal(s.Red.TimelineEnd - s.Red.TimelineStart, s.Red.SourceOut - s.Red.SourceIn);            // source follows
        Assert.Equal(fade, s.Red.FadeIn);                                                                   // fades keep their time
        Assert.Equal(dissolve, s.Dissolve.Duration);                                                        // dissolves too
        Assert.Same(s.Dissolve, s.F.V1.Transitions.Single());
        Assert.Equal(markers, s.Markers());                                                                  // markers keep their MediaTime
        Assert.Equal(At(8, ntsc), f.Project.Timeline.PlayheadPosition);                                     // 0.28 s → 8.39 → 8
        Assert.Equal("Set Frame Rate", Top(f));
        Assert.True(f.Project.IsDirty);
        Assert.Equal(changes + 1, f.TimelineChangedCount);
        f.AssertValid();
        var after = f.Snapshot();

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(markers, s.Markers());
        Assert.Equal(At(7, Scene.Rate), f.Project.Timeline.PlayheadPosition);   // back on the 25 fps grid
        Assert.False(f.Project.IsDirty);

        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
        Assert.Equal(At(8, ntsc), f.Project.Timeline.PlayheadPosition);
        Assert.True(f.Project.IsDirty);
    }

    [Fact]
    public void Locked_and_hidden_tracks_are_re_gridded_too_and_do_not_block_the_change()
    {
        var s = new Scene();
        var f = s.F;
        s.V2.IsLocked = true;
        f.V1.IsHidden = true;
        f.A1.IsLocked = true;

        Ok(f.Service.SetFrameRate(FrameRate.Fps30));

        Assert.Equal(At(4, FrameRate.Fps30), s.Title.TimelineStart);    // 0.12 s → 3.6 → 4 on the locked V2
        Assert.Equal(At(12, FrameRate.Fps30), s.Green.TimelineStart);   // hidden V1
        Assert.Equal(At(1, FrameRate.Fps30), s.Music.TimelineStart);    // locked A1
        Assert.True(s.V2.IsLocked && f.V1.IsHidden && f.A1.IsLocked);
        f.AssertValid();
    }

    [Fact]
    public void A_clip_with_a_speed_keeps_its_source_range_and_speed()
    {
        var s = new Scene();
        var f = s.F;
        Ok(f.Service.SetClipSpeed(s.Green.Id, ClipSpeed.FromSteps(2 * ClipSpeed.StepsPerUnit)));
        var (sourceIn, sourceOut, speed) = (s.Green.SourceIn, s.Green.SourceOut, s.Green.Speed);

        var fps50 = new FrameRate(50, 1);
        Ok(f.Service.SetFrameRate(fps50));

        Assert.Equal((sourceIn, sourceOut, speed), (s.Green.SourceIn, s.Green.SourceOut, s.Green.Speed));
        Assert.Equal(SpeedTiming.FramesFor(sourceOut - sourceIn, speed, fps50),
            s.Green.TimelineEnd.ToNearestFrame(fps50) - s.Green.TimelineStart.ToNearestFrame(fps50));
        f.AssertValid();
    }

    [Fact]
    public void Markers_keep_their_time_even_when_two_land_on_one_frame()
    {
        var f = new TimelineFixture();
        var fps60 = FrameRate.Fps60;
        f.Settings.FrameRate = fps60;
        f.Settings.IsFrameRateLocked = true;
        Ok(f.Service.AddMarker(At(10, fps60)));
        Ok(f.Service.AddMarker(At(11, fps60)));
        var positions = f.Project.Timeline.Markers.Select(m => m.Position).ToList();

        Ok(f.Service.SetFrameRate(FrameRate.Fps24));

        Assert.Equal(positions, f.Project.Timeline.Markers.Select(m => m.Position));    // not moved, not merged
        Assert.All(positions, p => Assert.Equal(4, p.ToNearestFrame(FrameRate.Fps24)));  // both on frame 4 now
        Ok(f.Service.RemoveMarkerAt(At(4, FrameRate.Fps24)));                             // removed one at a time
        Assert.Single(f.Project.Timeline.Markers);
    }

    // --- same rate (FR-4) and refused rates ----------------------------------------------------------------------------

    [Fact]
    public void The_provisional_rate_chosen_again_is_only_locked_in_one_undo_step()
    {
        var f = new TimelineFixture();
        var image = f.Image();
        Ok(f.Service.AddClip(image.Id, f.V1.Id, MediaTime.Zero));     // an image doesn't lock the rate
        MarkSaved(f);
        var before = f.Snapshot();
        Assert.False(f.Settings.IsFrameRateLocked);

        Ok(f.Service.SetFrameRate(FrameRate.Default));

        Assert.True(f.Settings.IsFrameRateLocked);
        Assert.Equal(FrameRate.Default, f.Settings.FrameRate);
        Assert.Equal(before.Replace("locked=False", "locked=True"), f.Snapshot());
        Assert.True(f.Project.IsDirty);
        f.UndoRedo.Undo();
        Assert.False(f.Settings.IsFrameRateLocked);
        Assert.False(f.Project.IsDirty);

        // Locked now, a later first video no longer sets the rate.
        f.UndoRedo.Redo();
        Ok(f.Service.AddClip(Video(f, FrameRate.Fps25).Id, f.V1.Id, At(200, FrameRate.Default)));
        Assert.Equal(FrameRate.Default, f.Settings.FrameRate);
    }

    [Fact]
    public void The_current_rate_of_a_locked_project_changes_nothing()
    {
        var s = new Scene();
        var (top, changes) = (s.F.UndoRedo.CurrentPosition, s.F.TimelineChangedCount);

        var result = s.F.Service.SetFrameRate(Scene.Rate);

        Assert.True(result.Success && result.NoChange);
        Assert.Same(top, s.F.UndoRedo.CurrentPosition);
        Assert.Equal(changes, s.F.TimelineChangedCount);
        Assert.False(s.F.Project.IsDirty);
    }

    public static TheoryData<int, int> NotOffered => new() { { 15, 1 }, { 120, 1 }, { 48, 1 }, { 2997, 100 }, { 25, 2 } };

    [Theory]
    [MemberData(nameof(NotOffered))]
    public void A_rate_that_is_not_offered_is_refused_without_a_change(int numerator, int denominator)
    {
        var s = new Scene();
        AssertRefused(s.F, () => s.F.Service.SetFrameRate(new FrameRate(numerator, denominator)), "not one of the project frame rates");
    }

    [Fact]
    public void An_invalid_rate_is_refused_without_a_change()
    {
        var s = new Scene();
        AssertRefused(s.F, () => s.F.Service.SetFrameRate(default), "not one of the project frame rates");
    }

    [Fact]
    public void A_re_grid_that_can_not_be_done_is_refused_whole()
    {
        // At 60 fps a one-frame clip between two neighbours; at 24 fps its edges (10 / 60 s, 11 / 60 s) both land on
        // frame 4 and there is no free frame on either side.
        var f = new TimelineFixture();
        f.Settings.FrameRate = FrameRate.Fps60;
        f.Settings.IsFrameRateLocked = true;
        var image = f.Image("still.png");
        f.PlaceImageFrames(f.V1, image, 0, 10, FrameRate.Fps60);
        f.PlaceImageFrames(f.V1, image, 10, 11, FrameRate.Fps60);
        f.PlaceImageFrames(f.V1, image, 11, 20, FrameRate.Fps60);
        Ok(f.Service.AddMarker(At(5, FrameRate.Fps60)));
        f.Project.Timeline.PlayheadPosition = At(3, FrameRate.Fps60);
        MarkSaved(f);

        AssertRefused(f, () => f.Service.SetFrameRate(FrameRate.Fps24), "no room on the new frame grid");
    }

    [Fact]
    public void A_dissolve_that_would_be_shorter_than_two_frames_refuses_the_change()
    {
        // Two frames at 60 fps are 1 / 30 s: 0.8 → 1 frame at 24 fps.
        var f = new TimelineFixture();
        f.Settings.FrameRate = FrameRate.Fps60;
        f.Settings.IsFrameRateLocked = true;
        var media = Video(f, FrameRate.Fps60);
        var a = f.Place<VideoClip>(f.V1, media, 0, At(60, FrameRate.Fps60).Ticks, At(10, FrameRate.Fps60).Ticks);
        var b = f.Place<VideoClip>(f.V1, media, At(60, FrameRate.Fps60).Ticks, At(120, FrameRate.Fps60).Ticks, At(100, FrameRate.Fps60).Ticks);
        Ok(f.Service.AddTransition(a.Id, b.Id, At(2, FrameRate.Fps60)));
        MarkSaved(f);
        var (before, top) = (f.Snapshot(), f.UndoRedo.CurrentPosition);

        var result = f.Service.SetFrameRate(FrameRate.Fps24);

        Assert.False(result.Success);
        Assert.Contains("at least 2 frames", result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.False(f.Project.IsDirty);
    }

    // --- FR-1 ----------------------------------------------------------------------------------------------------------

    /// <summary>A locked 30 fps project: A [0, 100) and B [100, 200) meet; B starts one 30 fps frame into its source; a
    /// 3-frame dissolve (1 frame before the cut in A, 2 after it in B) needs 1 frame of B's source before B — exactly what
    /// B has. Every edge is also a 60 fps edge, so a change to 60 fps moves no clip.</summary>
    private static (TimelineFixture F, Transition Dissolve) TightDissolve(bool locked = true)
    {
        var f = new TimelineFixture();
        var fps30 = FrameRate.Fps30;
        f.Settings.FrameRate = fps30;
        f.Settings.IsFrameRateLocked = locked;
        var media = Video(f, fps30);
        var a = f.Place<VideoClip>(f.V1, media, 0, At(100, fps30).Ticks, At(200, fps30).Ticks);
        var b = f.Place<VideoClip>(f.V1, media, At(100, fps30).Ticks, At(200, fps30).Ticks, At(1, fps30).Ticks);
        Ok(f.Service.AddTransition(a.Id, b.Id, At(3, fps30)));
        MarkSaved(f);
        return (f, f.V1.Transitions.Single());
    }

    [Fact]
    public void FR1_a_rate_change_that_moves_no_clip_still_checks_every_dissolves_source_handles()
    {
        // At 60 fps the same 0.1 s dissolve is 6 frames, 3 before the cut: B needs 0.05 s of source before it and has
        // only 1 / 30 s. No clip moves (30 fps edges are 60 fps edges), so before the FR-1 fix the dissolve was not
        // "touched", its handles were not checked and the change left an invalid dissolve.
        var (f, dissolve) = TightDissolve();
        var (before, top, changes) = (f.Snapshot(), f.UndoRedo.CurrentPosition, f.TimelineChangedCount);

        var result = f.Service.SetFrameRate(FrameRate.Fps60);

        Assert.False(result.Success);
        Assert.Contains(TimelineEditService.NotEnoughMedia, result.Message);
        Assert.Equal(FrameRate.Fps30, f.Settings.FrameRate);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(dissolve, f.V1.Transitions.Single());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(changes, f.TimelineChangedCount);
        Assert.False(f.Project.IsDirty);
    }

    [Fact]
    public void FR1_the_first_video_fixing_the_rate_checks_every_dissolves_source_handles_too()
    {
        // The same timeline on the provisional 30 fps; the first video added at 60 fps would fix the rate and re-grid it.
        var (f, dissolve) = TightDissolve(locked: false);
        var before = f.Snapshot();

        var result = f.Service.AddClip(Video(f, FrameRate.Fps60, name: "sixty.mp4").Id, f.V1.Id, At(300, FrameRate.Fps30));

        Assert.False(result.Success);
        Assert.Contains(TimelineEditService.NotEnoughMedia, result.Message);
        Assert.Equal((FrameRate.Fps30, false), (f.Settings.FrameRate, f.Settings.IsFrameRateLocked));
        Assert.Equal(before, f.Snapshot());
        Assert.Same(dissolve, f.V1.Transitions.Single());
    }

    [Fact]
    public void FR1_with_enough_source_the_same_dissolve_is_kept_with_its_time()
    {
        // The tight timeline with B starting ten 30 fps frames into its source: at 60 fps B has 20 frames before it.
        var (f, dissolve) = TightDissolve();
        var b = f.V1.Clips[1];
        f.UndoRedo.Undo();                                                      // the dissolve goes …
        ((VideoClip)b).SourceIn = At(10, FrameRate.Fps30);                      // … B gets more source before it …
        ((VideoClip)b).SourceOut = At(110, FrameRate.Fps30);
        f.UndoRedo.Redo();                                                      // … and the very same dissolve is back
        var duration = dissolve.Duration;

        Ok(f.Service.SetFrameRate(FrameRate.Fps60));

        Assert.Same(dissolve, f.V1.Transitions.Single());
        Assert.Equal(duration, dissolve.Duration);
        Assert.Equal(6, TransitionRules.Frames(dissolve.Duration, FrameRate.Fps60));
        f.AssertValid();
    }

    // --- clipboard, snapshot, persistence -------------------------------------------------------------------------------

    [Fact]
    public void Clips_copied_before_a_rate_change_are_refused_on_paste_and_valid_again_after_undo()
    {
        var s = new Scene();
        var f = s.F;
        var clipboard = f.Service.CopyClips(new[] { s.Title.Id })!;
        Ok(f.Service.SetFrameRate(FrameRate.Fps30));
        var top = f.UndoRedo.CurrentPosition;

        var paste = f.Service.PasteClips(clipboard, At(400, FrameRate.Fps30));

        Assert.False(paste.Success);
        Assert.Equal("The project frame rate changed since the clips were copied. Copy them again.", paste.Message);
        Assert.Same(top, f.UndoRedo.CurrentPosition);

        f.UndoRedo.Undo();
        Ok(f.Service.PasteClips(clipboard, At(400, Scene.Rate)));
    }

    [Fact]
    public void The_snapshot_plays_on_the_new_grid()
    {
        var s = new Scene();
        Ok(s.F.Service.SetFrameRate(new FrameRate(50, 1)));

        var snapshot = PlaybackSnapshotBuilder.Build(s.F.Project, version: 1);

        Assert.Equal(new FrameRate(50, 1), snapshot.FrameRate);
        Assert.Equal(s.F.Project.Timeline.Duration(), snapshot.Duration);
        Assert.True(snapshot.Duration.IsOnFrameGrid(new FrameRate(50, 1)));
    }

    [Fact]
    public void The_new_rate_and_its_lock_survive_save_and_reopen()
    {
        var s = new Scene();
        Ok(s.F.Service.SetFrameRate(FrameRate.Ntsc24));

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(s.F.Project, @"C:\P"), @"C:\P", _ => true);

        Assert.Equal((FrameRate.Ntsc24, true), (loaded.Settings.FrameRate, loaded.Settings.IsFrameRateLocked));
        Assert.Equal(s.Green.TimelineStart, loaded.Timeline.VideoTracks[0].Clips[1].TimelineStart);
    }

    private static string Markers(TimelineFixture f) =>
        string.Join(",", f.Project.Timeline.Markers.Select(m => $"{m.Id}@{m.Position.Ticks}"));

    private static void AssertRefused(TimelineFixture f, Func<TimelineEditResult> act, string reason)
    {
        var (before, markers, playhead, top, changes) =
            (f.Snapshot(), Markers(f), f.Project.Timeline.PlayheadPosition, f.UndoRedo.CurrentPosition, f.TimelineChangedCount);
        var (rate, locked) = (f.Settings.FrameRate, f.Settings.IsFrameRateLocked);

        var result = act();

        Assert.False(result.Success);
        Assert.Contains(reason, result.Message);
        Assert.Equal((rate, locked), (f.Settings.FrameRate, f.Settings.IsFrameRateLocked));
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(markers, Markers(f));
        Assert.Equal(playhead, f.Project.Timeline.PlayheadPosition);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(changes, f.TimelineChangedCount);
        Assert.False(f.Project.IsDirty);
    }
}
