using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>D061: Follow and Occlusion are set in one undo step each; the same value is no change; other kinds and a locked
/// track refuse them; Split and Duplicate copy them; they are presentation only.</summary>
public class ClipFollowEditTests
{
    [Fact]
    public void Follow_on_pick_off_with_undo_and_redo()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(4, FrameRate.Fps30).Id).Success);
        var clip = f.V1.Clips.OfType<VideoClip>().Single();
        var before = PlaybackSnapshotBuilder.Build(f.Project, 1);

        Assert.True(f.Service.SetClipFollow(clip.Id, FollowSettings.Whole()).Success);
        Assert.Equal(FollowSettings.Whole(), clip.Follow);
        var picked = FollowSettings.Whole(FollowSmoothness.Fast, MediaTime.FromSeconds(1), new NormalizedPoint(0.4, 0.5));
        Assert.True(f.Service.SetClipFollow(clip.Id, picked).Success);
        Assert.True(f.Service.SetClipFollow(clip.Id, picked).NoChange);
        Assert.True(before.DiffersOnlyInPresentation(PlaybackSnapshotBuilder.Build(f.Project, 2)));

        f.UndoRedo.Undo();
        Assert.Equal(FollowSettings.Whole(), clip.Follow);
        f.UndoRedo.Undo();
        Assert.Null(clip.Follow);
        f.UndoRedo.Redo();
        f.UndoRedo.Redo();
        Assert.Equal(picked, clip.Follow);
        Assert.True(f.Service.SetClipFollow(clip.Id, null).Success);
        Assert.Null(clip.Follow);
    }

    [Fact]
    public void Follow_on_switches_Occlusion_on_in_the_same_undo_step()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(4, FrameRate.Fps30).Id).Success);
        var clip = f.V1.Clips.OfType<VideoClip>().Single();

        Assert.True(f.Service.SetClipFollow(clip.Id, FollowSettings.Whole(), hideOutlinesBehindPeople: true).Success);
        Assert.NotNull(clip.Follow);
        Assert.True(clip.HidesOutlinesBehindPeople);
        f.UndoRedo.Undo();                                    // one step back: both off
        Assert.Null(clip.Follow);
        Assert.False(clip.HidesOutlinesBehindPeople);
        f.UndoRedo.Redo();
        Assert.True(clip.HidesOutlinesBehindPeople);

        // The user switches Occlusion off; Follow stays; switching Follow off leaves Occlusion as it is.
        Assert.True(f.Service.SetClipHidesOutlinesBehindPeople(clip.Id, false).Success);
        Assert.NotNull(clip.Follow);
        Assert.True(f.Service.SetClipFollow(clip.Id, null).Success);
        Assert.False(clip.HidesOutlinesBehindPeople);

        // Occlusion already on: switching Follow on is one Follow step only.
        Assert.True(f.Service.SetClipHidesOutlinesBehindPeople(clip.Id, true).Success);
        Assert.True(f.Service.SetClipFollow(clip.Id, FollowSettings.Whole(), hideOutlinesBehindPeople: true).Success);
        f.UndoRedo.Undo();
        Assert.Null(clip.Follow);
        Assert.True(clip.HidesOutlinesBehindPeople);
    }

    [Fact]
    public void Overlapping_or_unordered_parts_are_refused_without_an_undo_step()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(4, FrameRate.Fps30).Id).Success);
        var clip = f.V1.Clips.OfType<VideoClip>().Single();
        var overlap = new FollowSettings(System.Collections.Immutable.ImmutableArray.Create(new FollowPart(MediaTime.FromSeconds(1), null),
            new FollowPart(MediaTime.FromSeconds(2), MediaTime.FromSeconds(3))));
        Assert.False(f.Service.SetClipFollow(clip.Id, overlap).Success);
        Assert.False(f.Service.SetClipFollow(clip.Id, new FollowSettings(System.Collections.Immutable.ImmutableArray<FollowPart>.Empty)).Success);
        Assert.Null(clip.Follow);
        f.UndoRedo.Undo();                                                   // the only step is still the clip's add
        Assert.Empty(f.V1.Clips);
    }

    [Fact]
    public void Occlusion_on_and_off_with_undo()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(4, FrameRate.Fps30).Id).Success);
        var clip = f.V1.Clips.OfType<VideoClip>().Single();
        Assert.True(f.Service.SetClipHidesOutlinesBehindPeople(clip.Id, true).Success);
        Assert.True(clip.HidesOutlinesBehindPeople);
        Assert.True(f.Service.SetClipHidesOutlinesBehindPeople(clip.Id, true).NoChange);
        f.UndoRedo.Undo();
        Assert.False(clip.HidesOutlinesBehindPeople);
    }

    [Fact]
    public void A_text_clip_and_a_locked_track_refuse_them()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddTextClip(MediaTime.Zero).Success);
        Assert.False(f.Service.SetClipFollow(f.V1.Clips.Single().Id, FollowSettings.Whole()).Success);
        Assert.False(f.Service.SetClipHidesOutlinesBehindPeople(f.V1.Clips.Single().Id, true).Success);

        var g = new TimelineFixture();
        Assert.True(g.Service.AddClip(g.Video(4, FrameRate.Fps30).Id).Success);
        g.V1.IsLocked = true;
        Assert.False(g.Service.SetClipFollow(g.V1.Clips.Single().Id, FollowSettings.Whole()).Success);
    }

    [Fact]
    public void A_split_keeps_Follow_and_Occlusion_on_both_halves()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(4, FrameRate.Fps30).Id).Success);
        var clip = f.V1.Clips.OfType<VideoClip>().Single();
        var picked = FollowSettings.Whole(FollowSmoothness.Normal, MediaTime.FromSeconds(1), new NormalizedPoint(0.4, 0.5));
        Assert.True(f.Service.SetClipFollow(clip.Id, picked).Success);
        Assert.True(f.Service.SetClipHidesOutlinesBehindPeople(clip.Id, true).Success);
        Assert.True(f.Service.Split(MediaTime.FromSeconds(2), new[] { clip.Id }).Success);
        Assert.All(f.V1.Clips.OfType<VideoClip>(), c =>
        {
            Assert.Equal(picked, c.Follow);
            Assert.True(c.HidesOutlinesBehindPeople);
        });
    }
}
