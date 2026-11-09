using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>D057 §3.2: several files at once — one after another at the end of their tracks in the given order, one Undo
/// step, nothing added when one cannot be, the first video fixing the frame rate once (D007).</summary>
public sealed class AddClipsTests
{
    private readonly TimelineFixture _f = new();

    [Fact]
    public void The_files_go_one_after_another_in_the_given_order_in_one_undo_step()
    {
        var a = _f.Video(4, FrameRate.Fps30, "a.mp4");
        var b = _f.Video(2, FrameRate.Fps30, "b.mp4");
        var c = _f.Video(3, FrameRate.Fps30, "c.mp4");
        var result = _f.Service.AddClips(new[] { b.Id, c.Id, a.Id });

        Assert.True(result.Success);
        var clips = _f.V1.Clips.OfType<VideoClip>().OrderBy(x => x.TimelineStart).ToList();
        Assert.Equal(new[] { b.Id, c.Id, a.Id }, clips.Select(x => x.MediaAssetId));
        Assert.Equal(MediaTime.Zero, clips[0].TimelineStart);
        Assert.Equal(clips[0].TimelineEnd, clips[1].TimelineStart);
        Assert.Equal(clips[1].TimelineEnd, clips[2].TimelineStart);
        Assert.Equal(result.ClipIds, clips.Select(x => x.Id));

        _f.UndoRedo.Undo();
        Assert.Empty(_f.V1.Clips);
        Assert.False(_f.Settings.IsFrameRateLocked);                     // the rate's fixing is part of the same step
        _f.UndoRedo.Redo();
        Assert.Equal(3, _f.V1.Clips.Count);
    }

    [Fact]
    public void Only_the_first_video_fixes_the_frame_rate()
    {
        var first = _f.Video(2, FrameRate.Fps25, "first.mp4");
        var second = _f.Video(2, FrameRate.Fps60, "second.mp4");
        Assert.True(_f.Service.AddClips(new[] { first.Id, second.Id }).Success);
        Assert.Equal(FrameRate.Fps25, _f.Settings.FrameRate);
        Assert.True(_f.Settings.IsFrameRateLocked);
    }

    [Fact]
    public void Audio_and_video_go_to_their_own_tracks_after_what_is_there()
    {
        var video = _f.Video(2, FrameRate.Fps30, "v.mp4");
        Assert.True(_f.Service.AddClip(video.Id).Success);
        var music = _f.Audio(5, "m.mp3");
        var more = _f.Video(1, FrameRate.Fps30, "w.mp4");
        Assert.True(_f.Service.AddClips(new[] { music.Id, more.Id }).Success);
        Assert.Single(_f.A1.Clips);
        Assert.Equal(2, _f.V1.Clips.Count);
        Assert.Equal(MediaTime.FromSeconds(2), _f.V1.Clips.Single(x => ((VideoClip)x).MediaAssetId == more.Id).TimelineStart);
    }

    [Fact]
    public void One_file_that_cannot_be_added_refuses_the_whole_operation()
    {
        var good = _f.Video(2, FrameRate.Fps30, "good.mp4");
        var history = _f.UndoRedo.CanUndo;
        var result = _f.Service.AddClips(new[] { good.Id, Guid.NewGuid() });
        Assert.False(result.Success);
        Assert.Empty(_f.V1.Clips);
        Assert.Equal(history, _f.UndoRedo.CanUndo);
    }
}
