using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 15 Step 15.3 (D030 §4): mute, hide and lock of a track. Each change is one undoable command that marks the
/// project dirty and is undone back to the save point; the flags are saved in the existing v3 track fields. Hide is
/// picture only (video tracks; refused on an audio track), mute is sound only, neither changes the sequence length;
/// a locked track keeps playing and exporting, refuses every ordinary edit, but still takes mute / hide (D030 Q13).
/// The lock is checked when an edit is planned — Undo / Redo are not blocked by it.
/// </summary>
public class TrackStateEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;

    /// <summary>Every track's flags, then the clips and dissolves (<see cref="TimelineFixture.Snapshot"/>).</summary>
    private static string State(TimelineFixture f)
    {
        var sb = new StringBuilder();
        foreach (var t in f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks))
            sb.Append($"{t.Id}:{t.Name}:order={t.Order}:m{t.IsMuted}h{t.IsHidden}l{t.IsLocked}\n");
        return sb.Append(f.Snapshot()).ToString();
    }

    /// <summary>V1 with a 4 s video with sound (frames 0–100 at 25 fps), A1 with a 4 s audio clip.</summary>
    private static (TimelineFixture F, VideoClip Video, AudioClip Audio) WithMedia()
    {
        var f = new TimelineFixture();
        var video = f.AddAsset("climb.mp4", MediaKind.Video, new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(4), FrameRate = FrameRate.Fps25, Width = 1920, Height = 1080,
            VideoCodec = "h264", AudioCodec = "aac", AudioChannels = 2, AudioSampleRate = 48000
        });
        Ok(f.Service.AddClip(video.Id));
        Ok(f.Service.AddClip(f.Audio(4).Id));
        return (f, (VideoClip)f.V1.Clips[0], (AudioClip)f.A1.Clips[0]);
    }

    private static PlaybackSnapshot Build(TimelineFixture f) => PlaybackSnapshotBuilder.Build(f.Project, 1);

    // --- video track: mute / hide / lock ----------------------------------------------------------------------------

    [Fact]
    public void Muting_a_video_track_silences_its_video_clips_only_in_one_undo_step()
    {
        var (f, video, audio) = WithMedia();
        var before = State(f);
        var changes = f.TimelineChangedCount;

        Ok(f.Service.SetTrackMuted(f.V1.Id, true));

        Assert.True(f.V1.IsMuted);
        Assert.Equal("Mute Track", Top(f));
        Assert.Equal(changes + 1, f.TimelineChangedCount);
        var snapshot = Build(f);
        Assert.Equal(new[] { audio.Id }, snapshot.AudioSpans.Select(s => s.ClipId));            // the video's sound is gone
        Assert.Contains(snapshot.VideoLayers.Single().Spans, s => s.ClipId == video.Id);       // the picture stays
        Assert.Equal(f.Project.Timeline.Duration(), snapshot.Duration);

        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
        Assert.Equal(2, Build(f).AudioSpans.Length);
        f.UndoRedo.Redo();
        Assert.True(f.V1.IsMuted);
        Assert.Equal("Mute Track", Top(f));

        Ok(f.Service.SetTrackMuted(f.V1.Id, false));
        Assert.False(f.V1.IsMuted);
        Assert.Equal("Unmute Track", Top(f));
    }

    [Fact]
    public void Hiding_a_video_track_removes_its_picture_but_keeps_its_sound_and_the_length()
    {
        var (f, video, _) = WithMedia();
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[1];
        Ok(f.Service.AddTextClip(MediaTime.Zero));                 // on the top video track, V2
        var duration = f.Project.Timeline.Duration();
        var before = State(f);

        Ok(f.Service.SetTrackHidden(f.V1.Id, true));

        Assert.True(f.V1.IsHidden);
        Assert.Equal("Hide Track", Top(f));
        var snapshot = Build(f);
        Assert.Equal(new[] { v2.Id }, snapshot.VideoLayers.Select(l => l.TrackId));             // only V2 is drawn
        Assert.Contains(snapshot.AudioSpans, s => s.ClipId == video.Id);                       // V1's sound still plays
        Assert.Equal(duration, snapshot.Duration);
        Assert.Equal(duration, f.Project.Timeline.Duration());

        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
        Assert.Equal(2, Build(f).VideoLayers.Length);
        f.UndoRedo.Redo();
        Assert.True(f.V1.IsHidden);

        Ok(f.Service.SetTrackHidden(f.V1.Id, false));
        Assert.Equal("Show Track", Top(f));
        Assert.Equal(2, Build(f).VideoLayers.Length);
    }

    [Fact]
    public void Locking_a_video_track_is_one_undo_step_and_the_locked_track_still_plays_in_full()
    {
        var (f, video, audio) = WithMedia();
        var unlocked = Build(f);
        var before = State(f);

        Ok(f.Service.SetTrackLocked(f.V1.Id, true));

        Assert.True(f.V1.IsLocked);
        Assert.Equal("Lock Track", Top(f));
        var locked = Build(f);
        Assert.Equal(unlocked.VideoLayers.Single().Spans.Select(s => (s.ClipId, s.TimelineStart, s.TimelineEnd)),
            locked.VideoLayers.Single().Spans.Select(s => (s.ClipId, s.TimelineStart, s.TimelineEnd)));
        Assert.Equal(new[] { video.Id, audio.Id }.Order(), locked.AudioSpans.Select(s => s.ClipId).Order());
        Assert.Equal(unlocked.Duration, locked.Duration);

        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
        f.UndoRedo.Redo();
        Assert.True(f.V1.IsLocked);
        Ok(f.Service.SetTrackLocked(f.V1.Id, false));
        Assert.Equal("Unlock Track", Top(f));
        Assert.False(f.V1.IsLocked);
    }

    // --- audio track: mute / lock, no hide ----------------------------------------------------------------------------

    [Fact]
    public void Muting_an_audio_track_removes_its_clips_from_the_mix_in_one_undo_step()
    {
        var (f, video, _) = WithMedia();
        var before = State(f);

        Ok(f.Service.SetTrackMuted(f.A1.Id, true));

        Assert.True(f.A1.IsMuted);
        Assert.Equal("Mute Track", Top(f));
        var snapshot = Build(f);
        Assert.Equal(new[] { video.Id }, snapshot.AudioSpans.Select(s => s.ClipId));
        Assert.Equal(f.Project.Timeline.Duration(), snapshot.Duration);

        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
        f.UndoRedo.Redo();
        Assert.True(f.A1.IsMuted);
    }

    [Fact]
    public void Locking_an_audio_track_refuses_its_edits_and_keeps_its_sound()
    {
        var (f, _, audio) = WithMedia();

        Ok(f.Service.SetTrackLocked(f.A1.Id, true));

        Assert.True(f.A1.IsLocked);
        Assert.Contains(Build(f).AudioSpans, s => s.ClipId == audio.Id);
        var before = State(f);
        Assert.Equal("Track A1 is locked.", f.Service.DeleteClips(TimelineFixture.Ids(audio)).Message);
        Assert.False(f.Service.MoveClips(TimelineFixture.Ids(audio), 5).Success);
        Assert.False(f.Service.TrimClip(audio.Id, ClipEdge.End, F(f, 50)).Success);
        Assert.False(f.Service.SetClipProperties(audio.Id, new ClipPropertyChange { Audio = new AudioProperties(0.5, false) }).Success);
        Assert.Equal(before, State(f));

        f.UndoRedo.Undo();                                                                     // the lock itself is undoable
        Assert.False(f.A1.IsLocked);
    }

    [Fact]
    public void An_audio_track_cannot_be_hidden_and_nothing_changes()
    {
        var (f, _, _) = WithMedia();
        var before = State(f);
        var top = f.UndoRedo.CurrentPosition;

        var result = f.Service.SetTrackHidden(f.A1.Id, true);

        Assert.False(result.Success);
        Assert.Equal("Track A1 is an audio track: it has no picture to hide.", result.Message);
        Assert.False(f.A1.IsHidden);
        Assert.Equal(before, State(f));
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    // --- the same state, a missing track ------------------------------------------------------------------------------

    [Fact]
    public void Setting_the_state_a_track_already_has_changes_nothing_and_leaves_no_undo_step()
    {
        var (f, _, _) = WithMedia();
        var top = f.UndoRedo.CurrentPosition;
        var changes = f.TimelineChangedCount;

        foreach (var result in new[]
                 {
                     f.Service.SetTrackMuted(f.V1.Id, false), f.Service.SetTrackHidden(f.V1.Id, false),
                     f.Service.SetTrackLocked(f.V1.Id, false), f.Service.SetTrackMuted(f.A1.Id, false),
                     f.Service.SetTrackLocked(f.A1.Id, false)
                 })
        {
            Assert.True(result.Success);
            Assert.True(result.NoChange);
        }
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(changes, f.TimelineChangedCount);
    }

    [Fact]
    public void A_track_that_no_longer_exists_is_reported()
    {
        var f = new TimelineFixture();
        var gone = Guid.NewGuid();
        Assert.Equal("That track no longer exists.", f.Service.SetTrackMuted(gone, true).Message);
        Assert.Equal("That track no longer exists.", f.Service.SetTrackHidden(gone, true).Message);
        Assert.Equal("That track no longer exists.", f.Service.SetTrackLocked(gone, true).Message);
    }

    // --- dirty / save point -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_toggle_makes_the_project_dirty_and_undo_to_the_save_point_makes_it_clean()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var (f, _, _) = WithMedia();
            await f.Projects.SaveAsAsync(Path.Combine(root, "Project"));
            Assert.False(f.Project.IsDirty);

            foreach (var toggle in new Func<TimelineEditResult>[]
                     {
                         () => f.Service.SetTrackMuted(f.V1.Id, true), () => f.Service.SetTrackHidden(f.V1.Id, true),
                         () => f.Service.SetTrackLocked(f.V1.Id, true), () => f.Service.SetTrackMuted(f.A1.Id, true),
                         () => f.Service.SetTrackLocked(f.A1.Id, true)
                     })
            {
                Ok(toggle());
                Assert.True(f.Project.IsDirty);
                f.UndoRedo.Undo();
                Assert.False(f.Project.IsDirty);                                                // back at the save point
                f.UndoRedo.Redo();
                Assert.True(f.Project.IsDirty);
                f.UndoRedo.Undo();
            }
            Assert.False(f.Project.IsDirty);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // --- lock: mute / hide still allowed, ordinary edits refused, undo not blocked -------------------------------------

    [Fact]
    public void Mute_and_hide_change_on_a_locked_track_as_undoable_steps()
    {
        var (f, _, _) = WithMedia();
        Ok(f.Service.SetTrackLocked(f.V1.Id, true));
        Ok(f.Service.SetTrackLocked(f.A1.Id, true));

        Ok(f.Service.SetTrackMuted(f.V1.Id, true));
        Ok(f.Service.SetTrackHidden(f.V1.Id, true));
        Ok(f.Service.SetTrackMuted(f.A1.Id, true));

        Assert.True(f.V1 is { IsLocked: true, IsMuted: true, IsHidden: true });
        Assert.True(f.A1 is { IsLocked: true, IsMuted: true });
        Assert.Empty(Build(f).VideoLayers);
        Assert.Empty(Build(f).AudioSpans);

        f.UndoRedo.Undo();
        f.UndoRedo.Undo();
        f.UndoRedo.Undo();
        Assert.True(f.V1 is { IsLocked: true, IsMuted: false, IsHidden: false });
        Assert.True(f.A1 is { IsLocked: true, IsMuted: false });
    }

    [Fact]
    public void A_locked_track_refuses_every_ordinary_edit_and_nothing_changes()
    {
        var f = new TimelineFixture();
        var asset = f.Video(20, FrameRate.Fps25);
        Ok(f.Service.AddClip(asset.Id));
        Ok(f.Service.Split(F(f, 100)));
        var (a, b) = (f.V1.Clips[0], f.V1.Clips[1]);
        var dissolve = f.Service.AddTransition(a.Id, b.Id, F(f, 10));
        Ok(dissolve);
        Ok(f.Service.AddClip(asset.Id, f.V1.Id, F(f, 600)));                                  // after a gap
        var c = f.V1.Clips[2];
        var clipboard = f.Service.CopyClips(TimelineFixture.Ids(a));
        Assert.NotNull(clipboard);
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[1];

        Ok(f.Service.SetTrackLocked(f.V1.Id, true));
        var before = State(f);
        var top = f.UndoRedo.CurrentPosition;

        var attempts = new (string Edit, TimelineEditResult Result)[]
        {
            ("move", f.Service.MoveClips(TimelineFixture.Ids(a), 5)),
            ("move to another track", f.Service.MoveClips(TimelineFixture.Ids(c), 0, v2.Id)),
            ("trim", f.Service.TrimClip(c.Id, ClipEdge.End, F(f, 650))),
            ("split", f.Service.Split(F(f, 50), TimelineFixture.Ids(a))),
            ("delete", f.Service.DeleteClips(TimelineFixture.Ids(a))),
            ("ripple delete", f.Service.RippleDeleteClips(TimelineFixture.Ids(a))),
            ("close gap", f.Service.CloseGapBefore(c.Id)),
            ("paste", f.Service.PasteClips(clipboard!, F(f, 1200))),
            ("duplicate", f.Service.DuplicateClips(TimelineFixture.Ids(c))),
            ("properties", f.Service.SetClipProperties(a.Id, new ClipPropertyChange { Visual = VisualProperties.Of(a)!.Value with { Opacity = 0.5 } })),
            ("speed", f.Service.SetClipSpeed(c.Id, ClipSpeed.FromSteps(40))),
            ("remove dissolve", f.Service.RemoveTransition(dissolve.TransitionId!.Value)),
            ("dissolve length", f.Service.SetTransitionDuration(dissolve.TransitionId!.Value, F(f, 6))),
            ("add clip", f.Service.AddClip(asset.Id, f.V1.Id, F(f, 1500))),
            ("delete track", f.Service.DeleteTrack(f.V1.Id)),
            ("move track", f.Service.MoveTrack(f.V1.Id, 1)),
            ("move past it", f.Service.MoveTrack(v2.Id, -1)),
            ("remove media", f.Service.RemoveMedia(asset.Id)),
        };

        foreach (var (edit, result) in attempts)
        {
            Assert.False(result.Success, $"{edit} was not refused");
            Assert.Contains("locked", result.Message);
        }
        Assert.Equal(before, State(f));
        Assert.Same(top, f.UndoRedo.CurrentPosition);

        Ok(f.Service.SetTrackLocked(f.V1.Id, false));                                         // unlocked: edits work again
        Ok(f.Service.MoveClips(TimelineFixture.Ids(c), 5));
    }

    [Fact]
    public void Undo_and_redo_are_not_blocked_by_the_current_lock_state()
    {
        var (f, video, _) = WithMedia();
        Ok(f.Service.MoveClips(TimelineFixture.Ids(video), 10));
        f.V1.IsLocked = true;                     // the track is locked now, by state outside this history (e.g. a file)

        f.UndoRedo.Undo();                                                                     // the move, recorded unlocked
        Assert.Equal(MediaTime.Zero, video.TimelineStart);
        f.UndoRedo.Redo();
        Assert.Equal(F(f, 10), video.TimelineStart);
        Assert.False(f.Service.MoveClips(TimelineFixture.Ids(video), 10).Success);            // new edits are refused

        // A step recorded while the track was locked (a mute) is undone and redone the same way.
        Ok(f.Service.SetTrackMuted(f.V1.Id, true));
        f.UndoRedo.Undo();
        Assert.False(f.V1.IsMuted);
        f.UndoRedo.Redo();
        Assert.True(f.V1.IsMuted);
        Assert.True(f.V1.IsLocked);
    }

    // --- persistence ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_flags_survive_save_and_reopen_in_format_3_and_save_again_byte_for_byte()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var (f, _, _) = WithMedia();
            Ok(f.Service.AddTrack(TrackType.Video));
            var v2 = f.Project.Timeline.VideoTracks[1];
            Ok(f.Service.SetTrackHidden(v2.Id, true));
            Ok(f.Service.SetTrackMuted(f.V1.Id, true));
            Ok(f.Service.SetTrackLocked(f.V1.Id, true));
            Ok(f.Service.SetTrackMuted(f.A1.Id, true));
            Ok(f.Service.SetTrackLocked(f.A1.Id, true));
            var folder = Path.Combine(root, "Project");
            await f.Projects.SaveAsAsync(folder);
            var file = Path.Combine(folder, "project.json");
            var json = File.ReadAllText(file);
            Assert.Contains("\"formatVersion\": 3", json);
            var expected = f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks)
                .Select(t => (t.Id, t.IsMuted, t.IsHidden, t.IsLocked)).ToList();
            var bytes = File.ReadAllBytes(file);

            var reopened = await f.Projects.OpenAsync(folder);

            Assert.Equal(expected, reopened.Timeline.VideoTracks.Concat(reopened.Timeline.AudioTracks)
                .Select(t => (t.Id, t.IsMuted, t.IsHidden, t.IsLocked)));
            Assert.False(reopened.IsDirty);
            await f.Projects.SaveAsync();
            Assert.Equal(bytes, File.ReadAllBytes(file));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
