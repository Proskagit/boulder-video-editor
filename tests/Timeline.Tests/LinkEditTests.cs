using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 17 Step 17.4 (D032 R3, R4, R5, AQ8, AQ9, AQ11, AQ12): Unlink Audio moves a video clip's sound into an audio clip
/// of the same file without changing what is heard; Link / Unlink set and clear an explicit link; a link left without its
/// other clip is cleared in the same step, never on a locked track. One undo step each; a refused edit changes nothing.
/// </summary>
public class LinkEditTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    /// <summary>V1: a 20 s video with sound at [0, 100) from source frame 25; A1 empty.</summary>
    private static (TimelineFixture F, MediaAsset Video, VideoClip Clip) Setup()
    {
        var f = new TimelineFixture();
        var video = f.VideoWithSound(20, Rate);
        Ok(f.Service.AddClip(video.Id));
        Ok(f.Service.TrimClip(f.V1.Clips[0].Id, ClipEdge.Start, F(25)));
        Ok(f.Service.MoveClips(new[] { f.V1.Clips[0].Id }, -25));
        Ok(f.Service.TrimClip(f.V1.Clips[0].Id, ClipEdge.End, F(100)));
        return (f, video, (VideoClip)f.V1.Clips[0]);
    }

    private static void AssertRefused(TimelineFixture f, TimelineEditResult result, string message, string before, object? top)
    {
        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    // --- Unlink Audio ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Unlink_audio_puts_the_sound_on_A1_as_an_audio_clip_of_the_same_file_and_undo_restores_it()
    {
        var (f, video, clip) = Setup();
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = new AudioProperties(1.5, false), Fade = new FadeProperties(F(5), F(7)) }));
        var before = f.Snapshot();

        var result = f.Service.UnlinkAudio(clip.Id);

        Ok(result);
        Assert.Equal("Sound unlinked to track A1.", result.Message);
        var sound = Assert.IsType<AudioClip>(Assert.Single(f.A1.Clips));
        Assert.Equal(new[] { clip.Id, sound.Id }, result.ClipIds);
        Assert.Equal(video.Id, sound.MediaAssetId);
        Assert.Equal(ClipState.Capture(clip), ClipState.Capture(sound));
        Assert.Equal((1.5, false, F(5), F(7)), (sound.Volume, sound.IsMuted, sound.FadeIn, sound.FadeOut));
        Assert.Null(sound.LinkId);
        Assert.True(clip.IsAudioDetached);
        Assert.Null(clip.LinkId);
        Assert.Equal("Unlink Audio", f.UndoRedo.NextUndo?.Description);
        f.AssertValid();

        var after = f.Snapshot();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.False(clip.IsAudioDetached);
        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
    }

    [Fact]
    public void Unlink_audio_keeps_speed_and_mute_and_a_muted_video_track_mutes_the_audio_clip()
    {
        var (f, _, clip) = Setup();
        Ok(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(40)));
        Ok(f.Service.SetTrackMuted(f.V1.Id, true));

        Ok(f.Service.UnlinkAudio(clip.Id));

        var sound = (AudioClip)f.A1.Clips.Single();
        Assert.Equal(ClipState.Capture(clip), ClipState.Capture(sound));
        Assert.Equal(clip.Speed, sound.Speed);
        Assert.True(sound.IsMuted);   // AQ8: the sound was silent and stays silent
    }

    [Fact]
    public void Unlink_audio_gives_the_audio_clip_the_effective_fades_only()
    {
        // V1: A [0, 100) | B [100, 200) with a 10-frame dissolve on the cut; A fades in 5 and out 20 frames — the fade
        // out sits on the dissolve's edge, so A's sound has none there (D025 PO-8, AQ11).
        var (f, video, a) = Setup();
        Ok(f.Service.AddClip(video.Id, f.V1.Id, F(100)));
        var b = f.V1.Clips[1];
        Ok(f.Service.TrimClip(b.Id, ClipEdge.Start, F(100) + F(26)));   // B's source after A's: handles for the dissolve
        Ok(f.Service.MoveClips(new[] { b.Id }, -26));
        Ok(f.Service.AddTransition(a.Id, b.Id, F(10)));
        (a.FadeIn, a.FadeOut) = (new MediaTime(F(5).Ticks + 3), F(20));   // as a file may hold it (not a whole frame)

        Ok(f.Service.UnlinkAudio(a.Id));

        var sound = (AudioClip)f.A1.Clips.Single();
        Assert.Equal(new MediaTime(F(5).Ticks + 3), sound.FadeIn);   // applied to the sound: kept exactly as stored
        Assert.Equal(MediaTime.Zero, sound.FadeOut);                 // not applied (dissolve edge): none
        Assert.Equal(F(20), a.FadeOut);                              // the picture keeps its stored fade
        Assert.Single(f.V1.Transitions);
    }

    [Fact]
    public void Unlink_audio_takes_the_first_free_unlocked_unmuted_audio_track_else_adds_one_in_the_same_step()
    {
        var (f, _, clip) = Setup();
        var music = f.Audio(30);
        Ok(f.Service.AddTrack(TrackType.Audio));
        Ok(f.Service.AddTrack(TrackType.Audio));
        var (a1, a2, a3) = (f.A1, f.Project.Timeline.AudioTracks[1], f.Project.Timeline.AudioTracks[2]);
        Ok(f.Service.AddClip(music.Id, a1.Id, F(90)));   // A1 busy over [90, …)
        Ok(f.Service.SetTrackMuted(a2.Id, true));        // A2 muted
        Ok(f.Service.UnlinkAudio(clip.Id));
        Assert.Single(a3.Clips);                         // A3: the first that takes it as heard
        f.UndoRedo.Undo();

        Ok(f.Service.SetTrackLocked(a3.Id, true));
        var tracks = f.Project.Timeline.AudioTracks.Count;
        var before = f.Snapshot();
        var result = f.Service.UnlinkAudio(clip.Id);
        Ok(result);
        Assert.Equal(tracks + 1, f.Project.Timeline.AudioTracks.Count);
        var added = f.Project.Timeline.AudioTracks[^1];
        Assert.Equal("A4", added.Name);
        Assert.Single(added.Clips);
        Assert.Equal("Sound unlinked to track A4.", result.Message);

        f.UndoRedo.Undo();                               // one step: the track goes too
        Assert.Equal(tracks, f.Project.Timeline.AudioTracks.Count);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Unlink_audio_is_refused_for_a_clip_without_sound_an_unlinked_or_linked_clip_a_locked_track_and_an_audio_clip()
    {
        var f = new TimelineFixture();
        var silent = f.Video(20, Rate);
        Ok(f.Service.AddClip(silent.Id));
        var top = f.UndoRedo.CurrentPosition;
        AssertRefused(f, f.Service.UnlinkAudio(f.V1.Clips[0].Id), "The clip has no sound.", f.Snapshot(), top);

        var (g, video, clip) = Setup();
        Ok(g.Service.UnlinkAudio(clip.Id));
        AssertRefused(g, g.Service.UnlinkAudio(clip.Id), "The clip's sound is already unlinked.", g.Snapshot(), g.UndoRedo.CurrentPosition);
        var sound = g.A1.Clips.Single();
        AssertRefused(g, g.Service.UnlinkAudio(sound.Id), "Only a video clip's sound can be unlinked.", g.Snapshot(), g.UndoRedo.CurrentPosition);

        Ok(g.Service.AddClip(video.Id, g.V1.Id, F(200)));
        var other = g.V1.Clips[1];
        Ok(g.Service.LinkClips(new[] { other.Id, sound.Id }));
        AssertRefused(g, g.Service.UnlinkAudio(other.Id), "The clip is linked: Unlink it first.", g.Snapshot(), g.UndoRedo.CurrentPosition);

        var (h, _, locked) = Setup();
        Ok(h.Service.SetTrackLocked(h.V1.Id, true));
        AssertRefused(h, h.Service.UnlinkAudio(locked.Id), "Track V1 is locked.", h.Snapshot(), h.UndoRedo.CurrentPosition);
    }

    // --- Link / Unlink ------------------------------------------------------------------------------------------------

    [Fact]
    public void Link_joins_one_video_and_one_audio_clip_without_moving_them_and_unlink_clears_both()
    {
        var (f, _, clip) = Setup();
        var music = f.Audio(30);
        Ok(f.Service.AddClip(music.Id, f.A1.Id, F(40)));   // any offset, any file
        var audio = (AudioClip)f.A1.Clips.Single();
        var before = f.Snapshot();

        var result = f.Service.LinkClips(new[] { audio.Id, clip.Id });

        Ok(result);
        Assert.NotNull(clip.LinkId);
        Assert.Equal(clip.LinkId, audio.LinkId);
        Assert.False(clip.IsAudioDetached);                // R4: Link never touches the video clip's own sound
        Assert.Equal("Link Clips", f.UndoRedo.NextUndo?.Description);
        f.AssertValid();
        var linked = f.Snapshot();
        Assert.Equal(before.Replace("link=,", "*"), linked.Replace($"link={clip.LinkId},", "*"));   // only the links changed

        Ok(f.Service.UnlinkClips(new[] { clip.Id }));      // one side is enough: both lose the link
        Assert.Null(clip.LinkId);
        Assert.Null(audio.LinkId);
        Assert.Equal(before, f.Snapshot());
        f.UndoRedo.Undo();
        Assert.Equal(linked, f.Snapshot());
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());

        Assert.True(f.Service.UnlinkClips(new[] { clip.Id }).NoChange);
    }

    [Fact]
    public void Link_is_refused_for_anything_but_one_unlinked_video_and_audio_clip_on_unlocked_tracks()
    {
        var (f, video, clip) = Setup();
        var music = f.Audio(30);
        Ok(f.Service.AddClip(music.Id, f.A1.Id));
        Ok(f.Service.AddClip(video.Id, f.V1.Id, F(200)));
        var (audio, second) = (f.A1.Clips[0], f.V1.Clips[1]);
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        const string select = "Select one video clip and one audio clip to link them.";
        AssertRefused(f, f.Service.LinkClips(new[] { clip.Id }), select, before, top);
        AssertRefused(f, f.Service.LinkClips(new[] { clip.Id, second.Id }), select, before, top);
        AssertRefused(f, f.Service.LinkClips(new[] { clip.Id, second.Id, audio.Id }), select, before, top);

        Ok(f.Service.SetTrackLocked(f.A1.Id, true));
        before = f.Snapshot();
        top = f.UndoRedo.CurrentPosition;
        AssertRefused(f, f.Service.LinkClips(new[] { clip.Id, audio.Id }), "Track A1 is locked.", before, top);
        Ok(f.Service.SetTrackLocked(f.A1.Id, false));

        Ok(f.Service.LinkClips(new[] { clip.Id, audio.Id }));
        before = f.Snapshot();
        top = f.UndoRedo.CurrentPosition;
        AssertRefused(f, f.Service.LinkClips(new[] { second.Id, audio.Id }), "A selected clip is already linked: Unlink it first.", before, top);
    }

    [Fact]
    public void Unlink_is_refused_when_the_partner_is_on_a_locked_track()
    {
        var (f, _, clip) = Setup();
        var music = f.Audio(30);
        Ok(f.Service.AddClip(music.Id, f.A1.Id));
        Ok(f.Service.LinkClips(new[] { clip.Id, f.A1.Clips[0].Id }));
        Ok(f.Service.SetTrackLocked(f.A1.Id, true));

        AssertRefused(f, f.Service.UnlinkClips(new[] { clip.Id }), "Track A1 is locked.", f.Snapshot(), f.UndoRedo.CurrentPosition);
    }

    // --- a link left without its other clip ---------------------------------------------------------------------------

    [Fact]
    public void Deleting_a_track_clears_the_partners_link_in_the_same_step_and_is_refused_when_the_partner_is_locked()
    {
        var (f, _, clip) = Setup();
        var music = f.Audio(30);
        Ok(f.Service.AddTrack(TrackType.Video));
        Ok(f.Service.AddClip(music.Id, f.A1.Id));
        var audio = (AudioClip)f.A1.Clips[0];
        Ok(f.Service.LinkClips(new[] { clip.Id, audio.Id }));
        var before = f.Snapshot();

        Ok(f.Service.DeleteTrack(f.V1.Id));
        Assert.Null(audio.LinkId);
        f.AssertValid();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(clip.LinkId, audio.LinkId);

        Ok(f.Service.SetTrackLocked(f.A1.Id, true));
        Assert.Equal("Track A1 is locked.", f.Service.GetDeleteTrackBlockReason(f.V1.Id));
        AssertRefused(f, f.Service.DeleteTrack(f.V1.Id), "Track A1 is locked.", f.Snapshot(), f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void Removing_the_media_of_one_side_clears_the_other_sides_link_in_the_same_step()
    {
        var (f, _, clip) = Setup();
        var music = f.Audio(30);
        Ok(f.Service.AddClip(music.Id, f.A1.Id));
        var audio = (AudioClip)f.A1.Clips[0];
        Ok(f.Service.LinkClips(new[] { clip.Id, audio.Id }));
        var before = f.Snapshot();

        Ok(f.Service.RemoveMedia(music.Id));
        Assert.Null(clip.LinkId);
        f.AssertValid();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());

        Ok(f.Service.SetTrackLocked(f.V1.Id, true));
        var result = f.Service.RemoveMedia(music.Id);
        Assert.False(result.Success);
        Assert.Contains("Track V1 is locked.", result.Message);
        Assert.Equal(clip.LinkId, audio.LinkId);
    }
}
