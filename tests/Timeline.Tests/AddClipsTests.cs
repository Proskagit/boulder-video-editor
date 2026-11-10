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

    // --- The owner's acceptance of 2026-11-10: several files dropped on a track -----------------------------------------

    private static TextClip Text(TimelineFixture f, long startFrame, long endFrame)
    {
        var clip = new TextClip { Text = "T", TimelineStart = MediaTime.FromFrame(startFrame, f.Rate), Duration = MediaTime.FromFrame(endFrame - startFrame, f.Rate) };
        f.V1.Clips.Add(clip);
        f.V1.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    [Fact]
    public void Dropped_files_go_one_after_another_from_the_drop_in_one_undo_step()
    {
        var a = _f.Video(2, FrameRate.Fps30, "a.mp4");
        var b = _f.Video(3, FrameRate.Fps30, "b.mp4");
        Assert.True(_f.Service.AddClip(_f.Video(1, FrameRate.Fps30, "first.mp4").Id).Success);   // fixes 30 fps
        var before = _f.Snapshot();
        var result = _f.Service.AddClips(new[] { a.Id, b.Id }, _f.V1.Id, MediaTime.FromSeconds(10), insertOnDrop: true);

        Assert.True(result.Success, result.Message);
        var added = _f.V1.Clips.OfType<VideoClip>().Where(c => result.ClipIds.Contains(c.Id)).OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal(new[] { a.Id, b.Id }, added.Select(c => c.MediaAssetId));
        Assert.Equal(MediaTime.FromSeconds(10), added[0].TimelineStart);
        Assert.Equal(added[0].TimelineEnd, added[1].TimelineStart);
        _f.UndoRedo.Undo();
        Assert.Equal(before, _f.Snapshot());
    }

    [Fact]
    public void A_group_dropped_on_a_clip_starts_after_it_and_pushes_what_is_in_the_way()
    {
        Assert.True(_f.Service.AddClip(_f.Video(1, FrameRate.Fps30, "first.mp4").Id).Success);
        var x = Text(_f, 300, 400);
        var later = Text(_f, 400, 450);
        var a = _f.Video(2, FrameRate.Fps30, "a.mp4");
        var b = _f.Video(2, FrameRate.Fps30, "b.mp4");

        var preview = _f.Service.PreviewAddClips(new[] { a.Id, b.Id }, _f.V1.Id, MediaTime.FromFrame(350, _f.Rate), insertOnDrop: true);
        Assert.NotNull(preview);
        Assert.Equal(MediaTime.FromFrame(400, _f.Rate), preview!.Start);                   // after x
        Assert.Equal(MediaTime.FromFrame(520, _f.Rate), preview.End);                      // two 2 s files
        Assert.True(preview.MovesOthers);                                                   // «later» is in the way

        Assert.True(_f.Service.AddClips(new[] { a.Id, b.Id }, _f.V1.Id, MediaTime.FromFrame(350, _f.Rate), insertOnDrop: true).Success);
        Assert.Equal(MediaTime.FromFrame(300, _f.Rate), x.TimelineStart);
        Assert.Equal(MediaTime.FromFrame(520, _f.Rate), later.TimelineStart);
    }

    [Fact]
    public void The_ghost_says_when_nothing_moves_and_never_refuses_on_its_own()
    {
        Assert.True(_f.Service.AddClip(_f.Video(1, FrameRate.Fps30, "first.mp4").Id).Success);
        var a = _f.Video(2, FrameRate.Fps30, "a.mp4");
        var free = _f.Service.PreviewAddClips(new[] { a.Id }, _f.V1.Id, MediaTime.FromSeconds(20), insertOnDrop: true);
        Assert.NotNull(free);
        Assert.False(free!.MovesOthers);
        Assert.Equal(_f.Snapshot(), _f.Snapshot());                                         // a dry run: nothing changed
        Assert.Single(_f.V1.Clips);
    }
}
