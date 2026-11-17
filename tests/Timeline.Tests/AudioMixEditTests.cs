using System.Collections.Immutable;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D064 §1–§4 in the edit service: the volume points (source time) through Split, Trim, moves, Duplicate and Unlink Audio;
/// a video clip's sound-only fades (whole frames, the outer edges after a split); track volume, solo, the master and the
/// ducking as undo steps; the ducking of a deleted speech track cleared in the same step; «Закрепить в точки».
/// </summary>
public class AudioMixEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;
    private static VolumePoint P(double seconds, double gain) => new(Guid.NewGuid(), MediaTime.FromSeconds(seconds), gain);

    /// <summary>V1 with a 4 s video with sound (frames 0–100 at 25 fps), A1 with a 4 s audio clip.</summary>
    private static (TimelineFixture F, VideoClip Video, AudioClip Audio) WithMedia()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.VideoWithSound(4, FrameRate.Fps25).Id));
        Ok(f.Service.AddClip(f.Audio(4).Id));
        return (f, (VideoClip)f.V1.Clips[0], (AudioClip)f.A1.Clips[0]);
    }

    private static TimelineEditResult SetPoints(TimelineFixture f, Clip clip, params VolumePoint[] points) =>
        f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = AudioProperties.Of(clip)!.Value with { Volumes = VolumeEnvelope.Of(points) } });

    [Fact]
    public void Points_are_one_undo_step_and_invalid_points_are_refused()
    {
        var (f, video, audio) = WithMedia();
        Ok(SetPoints(f, audio, P(1, 0.5), P(2, 1.5)));
        Assert.Equal(2, audio.Volumes!.Count);
        Assert.Equal("Change Volume Points", Top(f));
        f.UndoRedo.Undo();
        Assert.Null(audio.Volumes);
        f.UndoRedo.Redo();
        Assert.Equal(2, audio.Volumes!.Count);

        var bad = new VolumeEnvelope(ImmutableArray.Create(P(1, 3)));
        Assert.False(f.Service.SetClipProperties(video.Id, new ClipPropertyChange { Audio = AudioProperties.Of(video)!.Value with { Volumes = bad } }).Success);
        Assert.Null(video.Volumes);
    }

    [Fact]
    public void A_split_gives_both_parts_the_points_so_the_sound_at_the_cut_is_unchanged()
    {
        var (f, _, audio) = WithMedia();
        Ok(SetPoints(f, audio, P(1, 0.25), P(3, 1)));
        var before = PlaybackSnapshotBuilder.Build(f.Project, 1).AudioSpans.Single(s => s.ClipId == audio.Id);
        var gainAtTwo = AudioGainCurve.Of(before, f.Rate).At(96_000);

        Ok(f.Service.Split(F(f, 50), TimelineFixture.Ids(audio)));

        var parts = f.A1.Clips.Cast<AudioClip>().ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(audio.Volumes, p.Volumes));
        var spans = PlaybackSnapshotBuilder.Build(f.Project, 2).AudioSpans.Where(s => parts.Any(p => p.Id == s.ClipId)).OrderBy(s => s.TimelineStart).ToList();
        Assert.Equal(gainAtTwo, AudioGainCurve.Of(spans[1], f.Rate).At(96_000));      // the right part's first sample
        Assert.Equal(AudioGainCurve.Of(before, f.Rate).At(95_999), AudioGainCurve.Of(spans[0], f.Rate).At(95_999));
    }

    [Fact]
    public void Moving_and_trimming_keep_the_points_on_the_same_sound()
    {
        var (f, _, audio) = WithMedia();
        Ok(SetPoints(f, audio, P(2, 0.5)));
        Ok(f.Service.MoveClips(TimelineFixture.Ids(audio), 25));                      // 1 s later
        Assert.Equal(MediaTime.FromSeconds(2), audio.Volumes!.Points[0].Time);         // source time: unchanged
        Ok(f.Service.TrimClip(audio.Id, ClipEdge.Start, F(f, 50)));
        Assert.Equal(MediaTime.FromSeconds(2), audio.Volumes!.Points[0].Time);
        Assert.Equal(MediaTime.FromSeconds(1), audio.SourceIn);
    }

    [Fact]
    public void Sound_fades_are_whole_frames_on_the_video_clip_only_and_a_split_keeps_the_outer_edges()
    {
        var (f, video, audio) = WithMedia();
        Ok(f.Service.SetClipProperties(video.Id, new ClipPropertyChange
        {
            Audio = AudioProperties.Of(video)!.Value with { SoundFadeIn = MediaTime.FromSeconds(0.41), SoundFadeOut = MediaTime.FromSeconds(1) }
        }));
        Assert.Equal(F(f, 10), video.SoundFadeIn);                                     // 10.25 frames → 10
        Assert.Equal(F(f, 25), video.SoundFadeOut);
        Assert.Equal(MediaTime.Zero, video.FadeIn);                                    // the picture's fade is untouched
        Assert.Equal("Change Sound Fade", Top(f));

        Assert.False(f.Service.SetClipProperties(audio.Id, new ClipPropertyChange
        {
            Audio = AudioProperties.Of(audio)!.Value with { SoundFadeIn = MediaTime.FromSeconds(1) }
        }).Success);
        Assert.False(f.Service.SetClipProperties(video.Id, new ClipPropertyChange
        {
            Audio = AudioProperties.Of(video)!.Value with { SoundFadeIn = MediaTime.FromSeconds(5) }      // longer than the clip
        }).Success);

        Ok(f.Service.Split(F(f, 50), TimelineFixture.Ids(video)));
        var parts = f.V1.Clips.Cast<VideoClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal((F(f, 10), MediaTime.Zero), (parts[0].SoundFadeIn, parts[0].SoundFadeOut));
        Assert.Equal((MediaTime.Zero, F(f, 25)), (parts[1].SoundFadeIn, parts[1].SoundFadeOut));
        f.UndoRedo.Undo();
        Assert.Equal((F(f, 10), F(f, 25)), (video.SoundFadeIn, video.SoundFadeOut));
    }

    [Fact]
    public void The_snapshot_carries_the_sound_fades_and_the_points()
    {
        var (f, video, _) = WithMedia();
        video.SoundFadeIn = F(f, 5);
        video.Volumes = VolumeEnvelope.Of(new[] { P(1, 0.5) });
        var span = PlaybackSnapshotBuilder.Build(f.Project, 1).AudioSpans.Single(s => s.ClipId == video.Id);
        Assert.Equal(5, span.SoundFadeInFrames);
        Assert.Equal(video.Volumes, span.Volumes);
        Assert.Equal(0, span.FadeInFrames);
    }

    [Fact]
    public void Unlink_audio_gives_the_audio_clip_the_points_the_clean_up_and_the_longer_fade()
    {
        var (f, video, _) = WithMedia();
        video.Volumes = VolumeEnvelope.Of(new[] { P(1, 0.5) });
        video.Cleanup = new AudioCleanup(30, 0);
        video.FadeIn = F(f, 5);
        video.SoundFadeIn = F(f, 12);
        Ok(f.Service.UnlinkAudio(video.Id));
        var sound = f.Project.Timeline.AudioTracks.SelectMany(t => t.Clips).OfType<AudioClip>().Single(c => c.MediaAssetId == video.MediaAssetId);
        Assert.Equal(video.Volumes, sound.Volumes);
        Assert.Equal(new AudioCleanup(30, 0), sound.Cleanup);
        Assert.Equal(F(f, 12), sound.FadeIn);
    }

    [Fact]
    public void Duplicate_copies_the_points_and_the_clean_up()
    {
        var (f, _, audio) = WithMedia();
        audio.Volumes = VolumeEnvelope.Of(new[] { P(1, 0.5) });
        audio.Cleanup = new AudioCleanup(0, 70);
        Ok(f.Service.DuplicateClips(TimelineFixture.Ids(audio)));
        var copy = f.Project.Timeline.AudioTracks.SelectMany(t => t.Clips).OfType<AudioClip>().Single(c => c.Id != audio.Id);
        Assert.Equal(audio.Volumes, copy.Volumes);
        Assert.Equal(audio.Cleanup, copy.Cleanup);
    }

    [Fact]
    public void Solo_keeps_only_the_solo_tracks_in_the_snapshot_and_is_allowed_on_a_locked_track()
    {
        var (f, video, audio) = WithMedia();
        Ok(f.Service.SetTrackLocked(f.A1.Id, true));
        Ok(f.Service.SetTrackSolo(f.A1.Id, true));
        Assert.Equal("Solo Track", Top(f));
        Assert.Equal(new[] { audio.Id }, PlaybackSnapshotBuilder.Build(f.Project, 1).AudioSpans.Select(s => s.ClipId));
        Ok(f.Service.SetTrackMuted(f.A1.Id, true));                                    // mute beats solo
        Assert.Empty(PlaybackSnapshotBuilder.Build(f.Project, 2).AudioSpans);
        f.UndoRedo.Undo();
        f.UndoRedo.Undo();
        Assert.Equal(2, PlaybackSnapshotBuilder.Build(f.Project, 3).AudioSpans.Length);
        Assert.Contains(PlaybackSnapshotBuilder.Build(f.Project, 3).AudioSpans, s => s.ClipId == video.Id);
    }

    [Fact]
    public void Track_volume_and_the_master_multiply_into_the_span_and_a_gesture_is_one_step()
    {
        var (f, video, audio) = WithMedia();
        var gesture = new object();
        Ok(f.Service.SetTrackVolume(f.A1.Id, 1.5, gesture));
        Ok(f.Service.SetTrackVolume(f.A1.Id, 0.5, gesture));
        Ok(f.Service.SetMasterVolume(0.8));
        var spans = PlaybackSnapshotBuilder.Build(f.Project, 1).AudioSpans;
        Assert.Equal(0.5 * 0.8, spans.Single(s => s.ClipId == audio.Id).TrackGain, 12);
        Assert.Equal(0.8, spans.Single(s => s.ClipId == video.Id).TrackGain, 12);
        Assert.Equal(0.4, spans.Single(s => s.ClipId == audio.Id).EffectiveGain, 12);

        f.UndoRedo.Undo();                                                              // the master
        f.UndoRedo.Undo();                                                              // the whole drag
        Assert.Equal(1.0, f.A1.Volume);
        Assert.False(f.Service.SetTrackVolume(f.A1.Id, 2.5).Success);
        Assert.True(f.Service.SetMasterVolume(1.0).NoChange);
    }

    [Fact]
    public void A_mix_change_is_presentation_only_but_a_cleaned_source_is_not()
    {
        var (f, _, audio) = WithMedia();
        var a = PlaybackSnapshotBuilder.Build(f.Project, 1);
        Ok(f.Service.SetTrackVolume(f.A1.Id, 0.5));
        audio.Volumes = VolumeEnvelope.Of(new[] { P(1, 0.5) });
        var b = PlaybackSnapshotBuilder.Build(f.Project, 2);
        Assert.True(a.DiffersOnlyInPresentation(b));

        audio.Cleanup = new AudioCleanup(40, 0);
        var analyses = new AudioAnalyses(new CleanedAudio(ImmutableDictionary<(Guid, AudioCleanup), string>.Empty
            .Add((audio.MediaAssetId, new AudioCleanup(40, 0)), @"C:\cache\x.wav")), SpeechAnalyses.None);
        var c = PlaybackSnapshotBuilder.Build(f.Project, 3, audioAnalyses: analyses);
        Assert.Equal(@"C:\cache\x.wav", c.AudioSpans.Single(s => s.ClipId == audio.Id).CleanedSource!.FilePath);
        Assert.False(b.DiffersOnlyInPresentation(c));
        var compare = PlaybackSnapshotBuilder.Build(f.Project, 4, audioAnalyses: analyses with { OriginalSound = true });
        Assert.Null(compare.AudioSpans.Single(s => s.ClipId == audio.Id).CleanedSource);
    }

    [Fact]
    public void Ducking_is_validated_and_follows_the_speech_on_the_other_track()
    {
        var (f, video, audio) = WithMedia();
        Assert.False(f.Service.SetTrackDucking(f.A1.Id, DuckingSettings.Default(f.A1.Id)).Success);   // itself
        Assert.False(f.Service.SetTrackDucking(f.A1.Id, DuckingSettings.Default(Guid.NewGuid())).Success);
        Assert.False(f.Service.SetTrackDucking(f.A1.Id, DuckingSettings.Default(f.V1.Id) with { DepthDb = -60 }).Success);
        Ok(f.Service.SetTrackDucking(f.A1.Id, DuckingSettings.Default(f.V1.Id) with { AttackMs = 0 }));
        Assert.Equal("Change Ducking", Top(f));

        // The video's sound has speech from 1 s to 2 s of its source.
        var p = new float[(int)(4 * 48_000 / SpeechActivity.WindowSamples)];
        for (var i = (int)(48_000 / SpeechActivity.WindowSamples) + 1; i < 2 * 48_000 / SpeechActivity.WindowSamples; i++) p[i] = 0.95f;
        var speech = new SpeechAnalyses(ImmutableDictionary<(Guid, AudioCleanup), SpeechActivity>.Empty
            .Add((video.MediaAssetId, AudioCleanup.None), new SpeechActivity(p)));
        var snapshot = PlaybackSnapshotBuilder.Build(f.Project, 1, audioAnalyses: new AudioAnalyses(CleanedAudio.None, speech));
        var music = snapshot.AudioSpans.Single(s => s.ClipId == audio.Id);
        Assert.NotNull(music.Ducking);
        Assert.Null(snapshot.AudioSpans.Single(s => s.ClipId == video.Id).Ducking);
        var curve = AudioGainCurve.Of(music, f.Rate);
        Assert.Equal(1f, curve.At(10_000));
        Assert.Equal((float)Math.Pow(10, DuckingSettings.DefaultDepthDb / 20), curve.At(72_000), 5);

        // Muting the speech track changes what is heard, not where the speech is.
        f.V1.IsMuted = true;
        Assert.NotNull(PlaybackSnapshotBuilder.Build(f.Project, 2, audioAnalyses: new AudioAnalyses(CleanedAudio.None, speech))
            .AudioSpans.Single(s => s.ClipId == audio.Id).Ducking);
    }

    [Fact]
    public void Deleting_the_speech_track_turns_the_ducking_off_in_the_same_step()
    {
        var (f, _, _) = WithMedia();
        Ok(f.Service.AddTrack(TrackType.Audio));
        var speech = f.Project.Timeline.AudioTracks[1];
        Ok(f.Service.SetTrackDucking(f.A1.Id, DuckingSettings.Default(speech.Id)));
        Ok(f.Service.DeleteTrack(speech.Id));
        Assert.Null(f.A1.Ducking);
        f.UndoRedo.Undo();
        Assert.Equal(speech.Id, f.A1.Ducking!.SpeechTrackId);
    }

    [Fact]
    public void Baking_writes_the_curve_into_the_points_and_turns_the_ducking_off_in_one_step()
    {
        var (f, video, audio) = WithMedia();
        audio.Volumes = VolumeEnvelope.Of(new[] { P(0, 0.5), P(4, 0.5) });
        Ok(f.Service.SetTrackDucking(f.A1.Id, DuckingSettings.Default(f.V1.Id)));
        var curve = new DuckingCurve(new[] { (48_000L, 96_000L) }, -12, 4_800, 9_600);

        Ok(f.Service.BakeDucking(f.A1.Id, curve));

        Assert.Null(f.A1.Ducking);
        Assert.Equal("Bake Ducking", Top(f));
        var span = PlaybackSnapshotBuilder.Build(f.Project, 1).AudioSpans.Single(s => s.ClipId == audio.Id);
        Assert.Null(span.Ducking);
        var baked = AudioGainCurve.Of(span, f.Rate);
        foreach (var k in new long[] { 0, 40_000, 45_000, 48_000, 70_000, 95_999, 100_000, 120_000, 180_000 })
            Assert.Equal(0.5 * curve.Gain(k), baked.At(k), 3);                         // as the ducking sounded, with the clip's own points
        f.UndoRedo.Undo();
        Assert.Equal(2, audio.Volumes!.Count);
        Assert.NotNull(f.A1.Ducking);
        Assert.Null(video.Volumes);
    }
}
