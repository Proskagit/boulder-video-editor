using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// The shortest clip a trim leaves (D008: trims clamp to a one-frame minimum; <see cref="TimelineValidator"/> rejects
/// anything shorter or off the frame grid) — checked at Step 10.9 after a clip was dragged down to "about one pixel":
/// exactly one frame of the project rate at any rate and speed, with fades cut to it, a dissolve's zone part kept, split
/// refused, exact undo / redo, a save that reads back identically, and the clip on exactly one frame of the snapshot.
/// The trim works in frames, never in pixels, so the zoom plays no part.
/// </summary>
public class OneFrameClipTests
{
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static long Frames(TimelineFixture f, Clip clip) => TransitionRules.ClipFrames(clip, f.Rate);

    private static (TimelineFixture F, VideoClip Clip) Video(FrameRate rate, int speedSteps = 20)
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(10, rate).Id).Success);
        var clip = (VideoClip)f.V1.Clips.Single();
        if (speedSteps != 20) Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(speedSteps)).Success);
        Assert.True(f.Service.MoveClips(new[] { clip.Id }, 30).Success);
        return (f, clip);
    }

    public static TheoryData<int, int, int> RatesAndSpeeds => new()
    {
        { 25, 1, 20 }, { 24000, 1001, 20 }, { 30000, 1001, 20 }, { 30000, 1001, 40 }, { 24000, 1001, 5 }
    };

    [Theory]
    [MemberData(nameof(RatesAndSpeeds))]
    public void Dragging_either_edge_past_the_other_leaves_exactly_one_frame(int num, int den, int speedSteps)
    {
        var (f, clip) = Video(new FrameRate(num, den), speedSteps);
        var start = clip.TimelineStart;
        var before = f.Snapshot();

        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, start - F(f, 20)).Success);   // far past the start
        Assert.Equal(1, Frames(f, clip));
        Assert.Equal(F(f, 1), clip.Duration);
        Assert.True(clip.TimelineEnd.IsOnFrameGrid(f.Rate));
        f.AssertValid();
        var oneFrame = f.Snapshot();

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        f.UndoRedo.Redo();
        Assert.Equal(oneFrame, f.Snapshot());

        f.UndoRedo.Undo();
        var end = clip.TimelineEnd;
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, end + F(f, 20)).Success);    // far past the end
        Assert.Equal(1, Frames(f, clip));
        Assert.Equal(end, clip.TimelineEnd);
        f.AssertValid();

        // A target between grid points rounds to the nearest frame and still never goes below one frame.
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, end - new MediaTime(1)).Success);
        Assert.Equal(1, Frames(f, clip));
    }

    [Fact]
    public void Fades_are_cut_to_the_one_frame_and_render_as_one_ramp_step()
    {
        var (f, clip) = Video(FrameRate.Fps25);
        Assert.True(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Fade = new FadeProperties(F(f, 20), F(f, 30)) }).Success);
        var before = f.Snapshot();

        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, clip.TimelineStart).Success);

        Assert.Equal((F(f, 1), F(f, 1)), (clip.FadeIn, clip.FadeOut));
        Assert.Equal((1L, 1L), FadeRule.EffectiveFrames(clip, f.Rate));
        var s = clip.TimelineStart.ToFrameFloor(f.Rate);
        Assert.Equal(0.25, FadeRule.PictureFactor(s, s, s + 1, 1, 1));        // ramp(0, 1)² = ½ · ½
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_far_edge_trim_next_to_a_dissolve_stops_at_the_zone()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id).Success);
        Assert.True(f.Service.Split(F(f, 100)).Success);
        var (a, b) = (f.V1.Clips[0], f.V1.Clips[1]);
        Assert.True(f.Service.AddTransition(a.Id, b.Id, F(f, 11)).Success);           // 5 frames of A, 6 of B

        Assert.True(f.Service.TrimClip(b.Id, ClipEdge.End, b.TimelineStart).Success);
        Assert.Equal(6, Frames(f, b));                                               // not one: the zone stays inside B
        Assert.True(f.Service.TrimClip(a.Id, ClipEdge.Start, a.TimelineEnd).Success);
        Assert.Equal(5, Frames(f, a));
        Assert.Single(f.V1.Transitions);
        f.AssertValid();

        Assert.True(f.Service.TrimClip(b.Id, ClipEdge.Start, b.TimelineEnd).Success);  // the cut edge: the dissolve goes
        Assert.Empty(f.V1.Transitions);
        Assert.Equal(1, Frames(f, b));
        f.AssertValid();
    }

    [Fact]
    public void A_one_frame_clip_cannot_be_split()
    {
        var (f, clip) = Video(FrameRate.Fps25);
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, clip.TimelineStart).Success);
        var before = f.Snapshot();

        foreach (var at in new[] { clip.TimelineStart, clip.TimelineEnd, clip.TimelineStart + new MediaTime(F(f, 1).Ticks / 2) })
        {
            Assert.False(f.Service.Split(at, new[] { clip.Id }).Success);
            Assert.Equal(before, f.Snapshot());
        }
    }

    [Fact]
    public void A_one_frame_clip_saves_and_reads_back_identically()
    {
        var (f, clip) = Video(new FrameRate(30000, 1001), speedSteps: 40);
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, clip.TimelineStart).Success);
        var folder = Path.Combine(Path.GetTempPath(), "aive-one-frame", Guid.NewGuid().ToString("N"));

        var json = ProjectSerializer.Serialize(f.Project, folder);
        var loaded = ProjectSerializer.Deserialize(json, folder, _ => true);

        var back = (VideoClip)loaded.Timeline.VideoTracks[0].Clips.Single();
        Assert.Equal((clip.TimelineStart, clip.Duration, clip.SourceIn, clip.SourceOut, clip.Speed),
            (back.TimelineStart, back.Duration, back.SourceIn, back.SourceOut, back.Speed));
        Assert.Equal(json, ProjectSerializer.Serialize(loaded, folder));
    }

    [Fact]
    public void The_snapshot_shows_a_one_frame_clip_on_exactly_its_frame()
    {
        var (f, clip) = Video(FrameRate.Fps25);
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, clip.TimelineStart).Success);
        var snapshot = PlaybackSnapshotBuilder.Build(f.Project, 1);
        var n = clip.TimelineStart.ToFrameFloor(f.Rate);

        Assert.Empty(snapshot.LayersAt(MediaTime.FromFrame(n - 1, f.Rate)));
        Assert.Equal(clip.Id, Assert.Single(snapshot.LayersAt(MediaTime.FromFrame(n, f.Rate))).ClipId);
        Assert.Empty(snapshot.LayersAt(MediaTime.FromFrame(n + 1, f.Rate)));
    }
}
