using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D057 §3.12 (amends D037 Q11 for drags): a new file dropped on a clip goes right after it; an existing clip dragged so that
/// it starts inside a clip X goes to X's start, X and the later clips moving right only as far as needed (D037 §3), X never
/// split; partners, dissolves and locked tracks keep D037's rules; the nudge, Paste and a drop without the flag keep Q11.
/// </summary>
public class DropOnOccupiedTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static (long, long) Span(TimelineFixture f, Clip c) => (c.TimelineStart.ToNearestFrame(f.Rate), c.TimelineEnd.ToNearestFrame(f.Rate));

    private static TextClip Text(TimelineFixture f, Track track, long start, long end)
    {
        var clip = new TextClip { Text = "T", TimelineStart = F(f, start), Duration = F(f, end) - F(f, start) };
        var index = track.Clips.FindIndex(c => c.TimelineStart > clip.TimelineStart);
        track.Clips.Insert(index < 0 ? track.Clips.Count : index, clip);
        return clip;
    }

    private static void AssertOneStep(TimelineFixture f, string before)
    {
        var after = f.Snapshot();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
    }

    [Fact]
    public void A_new_file_dropped_on_a_clip_goes_right_after_it_and_pushes_the_later_ones()
    {
        var f = new TimelineFixture();
        var x = Text(f, f.V1, 0, 100);
        var later = Text(f, f.V1, 120, 200);
        var image = f.Image();
        var before = f.Snapshot();

        var result = f.Service.AddClip(image.Id, f.V1.Id, F(f, 40), insertOnDrop: true);

        Ok(result);
        var added = f.V1.Clips.Single(c => c.Id == result.ClipIds[0]);
        Assert.Equal(F(f, 100), added.TimelineStart);
        Assert.Equal((0L, 100L), Span(f, x));                               // X stays, not split
        Assert.Equal(Math.Max(120, added.TimelineEnd.ToNearestFrame(f.Rate)), later.TimelineStart.ToNearestFrame(f.Rate));   // as far as needed
        f.AssertValid();
        AssertOneStep(f, before);
    }

    [Fact]
    public void Without_the_flag_a_drop_inside_a_clip_is_still_refused()
    {
        var f = new TimelineFixture();
        Text(f, f.V1, 0, 100);
        var result = f.Service.AddClip(f.Image().Id, f.V1.Id, F(f, 40));
        Assert.False(result.Success);
    }

    [Fact]
    public void An_existing_clip_dragged_into_a_clip_goes_to_its_start_and_pushes_it_as_far_as_needed()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 50);
        var x = Text(f, f.V1, 100, 200);
        var y = Text(f, f.V1, 260, 300);                                    // a gap of 60 after X
        var moved = Text(f, f.V1, 400, 440);
        var before = f.Snapshot();

        Ok(f.Service.MoveClips(new[] { moved.Id }, 150 - 400, insertOnDrop: true));   // its start dropped in X's middle

        Assert.Equal((100L, 140L), Span(f, moved));
        Assert.Equal((140L, 240L), Span(f, x));                              // X pushed to the moved clip's end, not split
        Assert.Equal((260L, 300L), Span(f, y));                              // the gap absorbed the push
        Assert.Equal((0L, 50L), Span(f, a));
        f.AssertValid();
        AssertOneStep(f, before);
    }

    [Fact]
    public void The_push_moves_the_later_clips_only_when_they_are_overlapped()
    {
        var f = new TimelineFixture();
        var x = Text(f, f.V1, 100, 200);
        var y = Text(f, f.V1, 200, 260);                                    // no gap: pushed too
        var moved = Text(f, f.V1, 400, 460);

        Ok(f.Service.MoveClips(new[] { moved.Id }, 120 - 400, insertOnDrop: true));

        Assert.Equal((100L, 160L), Span(f, moved));
        Assert.Equal((160L, 260L), Span(f, x));
        Assert.Equal((260L, 320L), Span(f, y));
    }

    [Fact]
    public void Onto_another_track_the_rule_is_that_tracks()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[1];
        var x = Text(f, v2, 100, 200);
        var moved = Text(f, f.V1, 0, 30);

        Ok(f.Service.MoveClips(new[] { moved.Id }, 150, v2.Id, insertOnDrop: true));

        Assert.Same(v2, f.Project.Timeline.VideoTracks.Single(t => t.Clips.Contains(moved)));
        Assert.Equal((100L, 130L), Span(f, moved));
        Assert.Equal((130L, 230L), Span(f, x));
    }

    [Fact]
    public void The_preview_shows_what_the_release_does()
    {
        var f = new TimelineFixture();
        var x = Text(f, f.V1, 100, 200);
        var moved = Text(f, f.V1, 400, 440);
        var preview = f.Service.PreviewMove(new[] { moved.Id }, 150 - 400, insertOnDrop: true);
        Assert.NotNull(preview);
        Assert.Equal(F(f, 100), preview!.Clips[moved.Id].Start);
        Assert.Equal(F(f, 140), preview.Clips[x.Id].Start);
        Assert.Null(f.Service.CanMoveClips(new[] { moved.Id }, 150 - 400, insertOnDrop: true));
        Assert.NotNull(f.Service.CanMoveClips(new[] { moved.Id }, 150 - 400));          // the nudge's rule: Q11
    }

    [Fact]
    public void A_dissolve_the_push_would_open_still_refuses()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id));                         // [0, 500)
        Ok(f.Service.Split(F(f, 100)));
        Ok(f.Service.AddTransition(f.V1.Clips[0].Id, f.V1.Clips[1].Id, F(f, 20)));
        var moved = Text(f, f.V1, 600, 620);
        var before = f.Snapshot();

        var result = f.Service.MoveClips(new[] { moved.Id }, 150 - 600, insertOnDrop: true);    // into the second part
        Assert.False(result.Success);                                         // the push would open the dissolve (Q9)
        Assert.Equal(before, f.Snapshot());
        Assert.Single(f.V1.Transitions);
    }
}
