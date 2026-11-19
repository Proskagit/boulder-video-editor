using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>D069 §7: a video clip's colour — one Undo step a change, the changes of one gesture merged; refused on a locked
/// track, for a clip without a picture of its own and out of range; a split and a duplicate keep it.</summary>
public class ClipColorEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);

    private static (TimelineFixture F, VideoClip Clip) Setup()
    {
        var f = new TimelineFixture();
        var clip = f.Place<VideoClip>(f.V1, f.Video(10, FrameRate.Fps25), 0, MediaTime.FromSeconds(4).Ticks);
        return (f, clip);
    }

    [Fact]
    public void A_change_is_one_undo_step_and_a_gesture_merges_into_one()
    {
        var (f, clip) = Setup();
        var warm = ColorCorrection.Neutral.With(6, 30);

        Ok(f.Service.SetClipColor(clip.Id, warm));
        Assert.Equal(warm, clip.Color);
        f.UndoRedo.Undo();
        Assert.True(clip.Color.IsNeutral);

        var drag = new object();
        Ok(f.Service.SetClipColor(clip.Id, ColorCorrection.Neutral.With(0, 10), drag));
        Ok(f.Service.SetClipColor(clip.Id, ColorCorrection.Neutral.With(0, 20), drag));
        Ok(f.Service.SetClipColor(clip.Id, ColorCorrection.Neutral.With(0, 35), drag));
        Assert.Equal(35, clip.Color.Exposure);
        f.UndoRedo.Undo();
        Assert.True(clip.Color.IsNeutral);
        Assert.False(f.UndoRedo.CanUndo);
    }

    [Fact]
    public void A_locked_track_a_text_and_an_out_of_range_value_are_refused()
    {
        var (f, clip) = Setup();
        Assert.False(f.Service.SetClipColor(clip.Id, ColorCorrection.Neutral.With(2, 101)).Success);
        f.V1.IsLocked = true;
        Assert.False(f.Service.SetClipColor(clip.Id, ColorCorrection.Neutral.With(2, 10)).Success);
        f.V1.IsLocked = false;
        var text = new TextClip { Text = "T", TimelineStart = MediaTime.FromSeconds(5), Duration = MediaTime.FromSeconds(1) };
        f.V1.Clips.Add(text);
        Assert.False(f.Service.SetClipColor(text.Id, ColorCorrection.Neutral.With(2, 10)).Success);
        Assert.True(clip.Color.IsNeutral);
    }

    [Fact]
    public void A_split_and_a_duplicate_keep_the_colour()
    {
        var (f, clip) = Setup();
        var look = new ColorCorrection(10, 0, 20, -10, 15, 5, 30, -5);
        Ok(f.Service.SetClipColor(clip.Id, look));

        Ok(f.Service.Split(MediaTime.FromSeconds(2), new[] { clip.Id }));
        Assert.All(f.V1.Clips.OfType<VideoClip>(), c => Assert.Equal(look, c.Color));

        Ok(f.Service.DuplicateClips(new[] { clip.Id }));
        Assert.All(f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>(), c => Assert.Equal(look, c.Color));
    }

    [Fact]
    public void The_playback_snapshot_carries_the_colour_to_the_picture()
    {
        var (f, clip) = Setup();
        var look = ColorCorrection.Neutral.With(5, -100);
        Ok(f.Service.SetClipColor(clip.Id, look));

        var snapshot = Core.Playback.PlaybackSnapshotBuilder.Build(f.Project, 1);

        Assert.Equal(look, snapshot.VideoLayers.SelectMany(l => l.Spans).Single(p => p.ClipId == clip.Id).Color);
    }
}
