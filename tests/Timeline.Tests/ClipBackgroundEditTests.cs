using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>D057 §3.7: a video clip's background is set in one undo step; the same value is no change; other kinds and a
/// locked track refuse it; the snapshot carries it as presentation only.</summary>
public class ClipBackgroundEditTests
{
    [Fact]
    public void Set_undo_redo_and_the_snapshot_carries_it()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(4, FrameRate.Fps30).Id).Success);
        var clip = f.V1.Clips.OfType<VideoClip>().Single();
        var before = PlaybackSnapshotBuilder.Build(f.Project, 1);

        Assert.True(f.Service.SetClipBackground(clip.Id, ClipBackground.Blur).Success);
        Assert.Equal(ClipBackground.Blur, clip.Background);
        var after = PlaybackSnapshotBuilder.Build(f.Project, 2);
        Assert.Equal(ClipBackground.Blur, after.VideoLayers.SelectMany(l => l.Spans).Single().Background);
        Assert.True(before.DiffersOnlyInPresentation(after));                // no decoder restarts for it

        Assert.True(f.Service.SetClipBackground(clip.Id, ClipBackground.Blur).NoChange);
        f.UndoRedo.Undo();
        Assert.Equal(ClipBackground.Black, clip.Background);
        f.UndoRedo.Redo();
        Assert.Equal(ClipBackground.Blur, clip.Background);
    }

    [Fact]
    public void A_text_clip_and_a_locked_track_refuse_it()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddTextClip(MediaTime.Zero).Success);
        Assert.False(f.Service.SetClipBackground(f.V1.Clips.Single().Id, ClipBackground.Blur).Success);

        var g = new TimelineFixture();
        Assert.True(g.Service.AddClip(g.Video(4, FrameRate.Fps30).Id).Success);
        g.V1.IsLocked = true;
        Assert.False(g.Service.SetClipBackground(g.V1.Clips.Single().Id, ClipBackground.Blur).Success);
    }
}
