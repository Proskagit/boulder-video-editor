using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 17 Step 17.7 (D032 R7, AQ10): Insert / Overwrite of a video file's sound alone — an audio clip of the file on an
/// audio track, by the D031 SQ6 / SQ9 / SQ13 rules exactly as the whole file goes onto a video track (compared tick for
/// tick); a video without sound is refused; for an audio file the flag changes nothing.
/// </summary>
public class SoundOnlyInsertTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    /// <summary>A 20 s recording with sound on V1 at [0, 200) and its sound unlinked to A1 (both unlinked).</summary>
    private static (TimelineFixture F, MediaAsset Take) Recording()
    {
        var f = new TimelineFixture();
        var take = f.VideoWithSound(20, Rate);
        Ok(f.Service.AddClip(take.Id));
        Ok(f.Service.TrimClip(f.V1.Clips[0].Id, ClipEdge.End, F(200)));
        Ok(f.Service.UnlinkAudio(f.V1.Clips[0].Id));
        return (f, take);
    }

    private static List<ClipState> States(Track track) => track.Clips.Select(ClipState.Capture).ToList();

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 80)]
    [InlineData(false, 200)]
    [InlineData(true, 30)]
    [InlineData(true, 190)]
    public void The_sound_goes_onto_A1_as_the_picture_would_onto_V1(bool overwrite, long at)
    {
        var (f, take) = Recording();
        var before = f.Snapshot();
        var result = overwrite
            ? f.Service.OverwriteClip(take.Id, F(40), F(65), F(at), soundOnly: true)
            : f.Service.InsertClip(take.Id, F(40), F(65), F(at), soundOnly: true);
        Ok(result);

        var placed = Assert.IsType<AudioClip>(f.A1.Clips.Single(c => c.Id == result.ClipIds[0]));
        Assert.Equal(take.Id, placed.MediaAssetId);
        Assert.Equal((F(at), F(at + 25), F(40)), (placed.TimelineStart, placed.TimelineEnd, placed.SourceIn));
        Assert.Single(f.V1.Clips);                                  // the picture untouched

        // The same edit of the whole file onto V1 (the picture) gives V1 the shape A1 now has.
        var (g, gTake) = Recording();
        Ok(overwrite ? g.Service.OverwriteClip(gTake.Id, F(40), F(65), F(at)) : g.Service.InsertClip(gTake.Id, F(40), F(65), F(at)));
        Assert.Equal(States(g.V1), States(f.A1));
        f.AssertValid();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_video_without_sound_is_refused_and_an_audio_file_inserts_as_before()
    {
        var f = new TimelineFixture();
        var silent = f.Video(10, Rate);
        Ok(f.Service.AddClip(silent.Id));
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;
        var refused = f.Service.InsertClip(silent.Id, F(0), F(10), F(0), soundOnly: true);
        Assert.False(refused.Success);
        Assert.Equal($"{silent.FileName} has no sound.", refused.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);

        var music = f.Audio(10);
        Ok(f.Service.InsertClip(music.Id, F(0), F(10), F(0), soundOnly: true));
        Assert.Equal(music.Id, ((AudioClip)f.A1.Clips.Single()).MediaAssetId);
    }

    [Fact]
    public void The_target_is_an_audio_track_and_a_video_track_is_refused()
    {
        var (f, take) = Recording();
        Ok(f.Service.AddTrack(TrackType.Audio));
        var a2 = f.Project.Timeline.AudioTracks[1];
        Ok(f.Service.InsertClip(take.Id, F(0), F(10), F(0), a2.Id, soundOnly: true));
        Assert.Single(a2.Clips);
        Assert.Equal("Audio can only go on an audio track.", f.Service.InsertClip(take.Id, F(0), F(10), F(0), f.V1.Id, soundOnly: true).Message);
    }

    [Fact]
    public void A_sound_only_insert_ripples_the_audio_track_and_takes_linked_partners_along()
    {
        var (f, take) = Recording();
        Ok(f.Service.LinkClips(new[] { f.V1.Clips[0].Id, f.A1.Clips[0].Id }));
        Ok(f.Service.InsertClip(take.Id, F(0), F(25), F(0), soundOnly: true));     // before the pair on A1

        Assert.Equal(F(25), f.A1.Clips[1].TimelineStart);
        Assert.Equal(F(25), f.V1.Clips[0].TimelineStart);                          // AQ4: the partner moves along
        f.AssertValid();
    }

    [Fact]
    public void The_first_sound_only_insert_of_a_video_fixes_the_project_rate()
    {
        var f = new TimelineFixture();
        var take = f.VideoWithSound(10, FrameRate.Fps30);
        Assert.False(f.Settings.IsFrameRateLocked);
        Ok(f.Service.InsertClip(take.Id, null, null, MediaTime.Zero, soundOnly: true));
        Assert.True(f.Settings.IsFrameRateLocked);
        Assert.Equal(FrameRate.Fps30, f.Settings.FrameRate);
        Assert.IsType<AudioClip>(f.A1.Clips.Single());
    }
}
