using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 7 Step 8: adding a text clip (topmost video track, at the given time, defaults, one undo
/// step, rejections that change nothing) and the timeline edits text clips share with media clips.
/// </summary>
public class TextClipEditTests
{
    private static MediaTime F(long frame, FrameRate rate) => MediaTime.FromFrame(frame, rate);

    private static TextClip Added(TimelineFixture f, TimelineEditResult result)
    {
        Assert.True(result.Success, result.Message);
        var id = Assert.Single(result.ClipIds);
        return (TextClip)f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).Single(c => c.Id == id);
    }

    [Fact]
    public void Adds_a_text_clip_with_the_default_properties_at_the_given_time()
    {
        var f = new TimelineFixture();
        var clip = Added(f, f.Service.AddTextClip(F(12, f.Rate)));

        Assert.Same(clip, Assert.Single(f.V1.Clips));   // V1 is the only (so topmost) video track
        Assert.Equal("Text", clip.Text);
        Assert.Equal("Segoe UI", clip.FontFamily);
        Assert.Equal(48, clip.FontSize);
        Assert.Equal("#FFFFFF", clip.ColorHex);
        Assert.Equal(TextAlignment.Center, clip.Alignment);
        Assert.Equal(VisualProperties.Default, VisualProperties.Of(clip));
        Assert.Equal(F(12, f.Rate), clip.TimelineStart);
        Assert.Equal(F(150, f.Rate), clip.Duration);     // 5 s at the provisional 30 fps
        Assert.Equal(1, f.TimelineChangedCount);
        f.AssertValid();
    }

    [Fact]
    public void Goes_on_the_topmost_video_track()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddTrack(TrackType.Video).Success);
        Assert.True(f.Service.AddTrack(TrackType.Video).Success);
        var top = f.Project.Timeline.VideoTracks.OrderByDescending(t => t.Order).First();

        var clip = Added(f, f.Service.AddTextClip(MediaTime.Zero));

        Assert.Contains(clip, top.Clips);
        Assert.Empty(f.V1.Clips);
    }

    [Fact]
    public void Start_is_snapped_to_the_frame_grid_and_duration_is_whole_frames_at_29_97()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(20, FrameRate.Ntsc30).Id).Success); // locks 29.97 on V1
        Assert.True(f.Service.AddTrack(TrackType.Video).Success);
        var rate = FrameRate.Ntsc30;

        var clip = Added(f, f.Service.AddTextClip(F(100, rate) + new MediaTime(1234)));

        Assert.Equal(F(100, rate), clip.TimelineStart);
        Assert.Equal(F(250, rate) - F(100, rate), clip.Duration);  // round(5 s × 29.97) = 150 frames
        f.AssertValid();
    }

    [Fact]
    public void A_negative_time_starts_at_zero()
    {
        var f = new TimelineFixture();
        var clip = Added(f, f.Service.AddTextClip(new MediaTime(-5_000_000)));
        Assert.Equal(MediaTime.Zero, clip.TimelineStart);
    }

    [Fact]
    public void Is_one_undo_step_named_Add_Text_and_makes_the_project_dirty()
    {
        var f = new TimelineFixture();
        f.Project.IsDirty = false;
        var before = f.Snapshot();

        var clip = Added(f, f.Service.AddTextClip(MediaTime.Zero));
        Assert.Equal("Add Text", ((IUndoableCommand)f.UndoRedo.CurrentPosition).Description);
        Assert.True(f.Project.IsDirty);

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.False(f.UndoRedo.CanUndo);

        f.UndoRedo.Redo();
        Assert.Same(clip, Assert.Single(f.V1.Clips));
        Assert.Equal("Text", clip.Text);
        Assert.Equal(3, f.TimelineChangedCount);
    }

    [Fact]
    public void Overlapping_another_clip_is_rejected_without_changes()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(10, FrameRate.Fps25).Id).Success); // V1: 0–10 s
        var before = f.Snapshot();
        var changes = f.TimelineChangedCount;
        var history = f.UndoRedo.CurrentPosition;

        var result = f.Service.AddTextClip(F(100, f.Rate));

        Assert.False(result.Success);
        Assert.Contains("overlap", result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(changes, f.TimelineChangedCount);
        Assert.Same(history, f.UndoRedo.CurrentPosition);

        // Touching the end is fine; no new track was created either way.
        Added(f, f.Service.AddTextClip(F(250, f.Rate)));
        Assert.Single(f.Project.Timeline.VideoTracks);
    }

    [Fact]
    public void A_locked_topmost_track_is_rejected()
    {
        var f = new TimelineFixture();
        f.V1.IsLocked = true;
        var before = f.Snapshot();

        var result = f.Service.AddTextClip(MediaTime.Zero);

        Assert.False(result.Success);
        Assert.Contains("locked", result.Message);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Without_a_video_track_nothing_is_created()
    {
        var f = new TimelineFixture();
        f.Project.Timeline.VideoTracks.Clear();

        var result = f.Service.AddTextClip(MediaTime.Zero);

        Assert.False(result.Success);
        Assert.Equal("The timeline has no video track.", result.Message);
        Assert.Empty(f.Project.Timeline.VideoTracks);
        Assert.False(f.UndoRedo.CanUndo);
    }

    [Fact]
    public void Adding_text_does_not_lock_the_frame_rate()
    {
        var f = new TimelineFixture();
        Added(f, f.Service.AddTextClip(MediaTime.Zero));
        Assert.False(f.Settings.IsFrameRateLocked);
    }

    // --- Timeline edits on text clips ------------------------------------------------------------

    [Fact]
    public void Text_clips_trim_beyond_their_length_move_and_split_keeping_their_properties()
    {
        var f = new TimelineFixture();
        var clip = Added(f, f.Service.AddTextClip(MediaTime.Zero));
        Assert.True(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Text = new TextProperties("Line 1\nLine 2", "Arial", 72, "#FF8800", TextAlignment.Left),
            Visual = VisualProperties.Default with { PositionX = 10, Opacity = 0.5 }
        }).Success);

        // No source media: the end edge can grow freely.
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, F(600, f.Rate)).Success);
        Assert.Equal(F(600, f.Rate), clip.TimelineEnd);

        Assert.True(f.Service.MoveClips(new[] { clip.Id }, 30).Success);
        Assert.Equal(F(30, f.Rate), clip.TimelineStart);

        var split = f.Service.Split(F(300, f.Rate), new[] { clip.Id });
        Assert.True(split.Success, split.Message);
        var right = f.V1.Clips.OfType<TextClip>().Single(c => c.Id != clip.Id);
        Assert.Equal(F(300, f.Rate), clip.TimelineEnd);
        Assert.Equal(F(300, f.Rate), right.TimelineStart);
        Assert.Equal(TextProperties.Of(clip), TextProperties.Of(right));
        Assert.Equal(VisualProperties.Of(clip), VisualProperties.Of(right));
        f.AssertValid();
    }
}
