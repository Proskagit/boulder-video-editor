using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 15 Step 15.5 (D030 §6, Q3): the Shift + edge drag — <see cref="ITimelineEditService.RippleTrimClip"/> on release,
/// <see cref="ITimelineEditService.PreviewRippleTrim"/> while dragging. It shares Shift+Q / Shift+W's planner: for the
/// same clip, edge and frame the whole timeline (clips, timing, source ranges, speed, fades, dissolves, markers, track
/// flags) is identical. Inward the clip keeps its start and the later clips of its track move left; outward they move
/// right and the source (and a dissolve's handle on that edge) limits the extension. Other tracks, markers and the
/// playhead stay; a locked track refuses; one undo step; the preview changes nothing. The ordinary drag is unchanged.
/// </summary>
public class RippleTrimDragTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;
    private static (long Start, long End) Frames(TimelineFixture f, Clip c) => (c.TimelineStart.ToNearestFrame(f.Rate), c.TimelineEnd.ToNearestFrame(f.Rate));

    /// <summary>The whole timeline as a ripple may change it: tracks (flags, order), every clip with its timing, source
    /// range, speed and fades, the dissolves, the markers (<see cref="TimelineFixture.Snapshot"/> plus the rest).</summary>
    private static string State(TimelineFixture f)
    {
        var sb = new StringBuilder(f.Snapshot());
        foreach (var t in f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks))
            sb.Append($"{t.Id}:order={t.Order}:m{t.IsMuted}h{t.IsHidden}l{t.IsLocked}\n");
        foreach (var m in f.Project.Timeline.Markers) sb.Append($"marker {m.Id}@{m.Position.Ticks}\n");
        return sb.ToString();
    }

    private static readonly FrameRate[] Rates = { FrameRate.Ntsc24, FrameRate.Fps25, FrameRate.Ntsc30, FrameRate.Fps30, FrameRate.Fps60 };

    public static TheoryData<int, int> RatesAndSpeeds()
    {
        var data = new TheoryData<int, int>();
        for (var rate = 0; rate < Rates.Length; rate++)
            foreach (var steps in new[] { 5, 10, 20, 40, 80 })
                data.Add(rate, steps);
        return data;
    }

    /// <summary>V1: A (a 20 s video, at <paramref name="speedSteps"/>/20) from 0, B right after it, a gap, C; a marker.</summary>
    private static (TimelineFixture F, Clip A, Clip B, Clip C) Three(FrameRate rate, int speedSteps)
    {
        var f = new TimelineFixture();
        var video = f.Video(20, rate);
        Ok(f.Service.AddClip(video.Id));
        var a = f.V1.Clips[0];
        if (speedSteps != 20) Ok(f.Service.SetClipSpeed(a.Id, ClipSpeed.FromSteps(speedSteps)));
        Ok(f.Service.AddClip(video.Id));
        var b = f.V1.Clips.Single(c => c != a);
        Ok(f.Service.AddClip(video.Id, f.V1.Id, b.TimelineEnd + F(f, 30)));
        var c = f.V1.Clips.Single(x => x != a && x != b);
        Ok(f.Service.AddMarker(b.TimelineEnd));
        return (f, a, b, c);
    }

    // --- equivalence with Shift+Q / Shift+W ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RatesAndSpeeds))]
    public void A_Shift_drag_inward_gives_the_same_timeline_as_Shift_Q_and_Shift_W_at_that_frame(int rateIndex, int speedSteps)
    {
        var (f, a, _, _) = Three(Rates[rateIndex], speedSteps);
        var (start, end) = Frames(f, a);
        foreach (var p in new[] { start + 1, start + (end - start) / 3, end - 1 })
            foreach (var edge in new[] { ClipEdge.Start, ClipEdge.End })
            {
                var before = State(f);

                Ok(f.Service.RippleTrimClip(a.Id, edge, F(f, p)));
                var dragged = State(f);
                var draggedTop = Top(f);
                f.UndoRedo.Undo();
                Assert.Equal(before, State(f));

                Ok(f.Service.TrimToPlayhead(TimelineFixture.Ids(a), edge, F(f, p), ripple: true));
                Assert.Equal(dragged, State(f));                                          // identical timeline
                f.AssertValid();
                f.UndoRedo.Undo();
                Assert.Equal(before, State(f));
                Assert.Equal(edge == ClipEdge.Start ? "Ripple Trim Start" : "Ripple Trim End", draggedTop);
            }
    }

    [Fact]
    public void The_dissolve_limits_and_messages_are_the_same_as_Shift_Q_and_Shift_W()
    {
        var (f, a, b, _, _) = Dissolved();
        foreach (var (clip, edge, frame) in new[] { (b, ClipEdge.Start, 150L), (b, ClipEdge.Start, 295L), (a, ClipEdge.End, 40L), (a, ClipEdge.End, 3L) })
        {
            var before = State(f);
            var drag = f.Service.RippleTrimClip(clip.Id, edge, F(f, frame));
            Ok(drag);
            var dragged = State(f);
            f.UndoRedo.Undo();
            var keys = f.Service.TrimToPlayhead(TimelineFixture.Ids(clip), edge, F(f, frame), ripple: true);
            Ok(keys);
            Assert.Equal(dragged, State(f));
            Assert.Equal(keys.Message, drag.Message);
            f.UndoRedo.Undo();
            Assert.Equal(before, State(f));
        }
    }

    // --- pointer → frame -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pointer_between_frames_goes_to_the_nearest_frame()
    {
        var (f, a, b, _) = Three(FrameRate.Ntsc30, 20);
        var frame = F(f, 101) - F(f, 100);
        var bStart = Frames(f, b).Start;

        foreach (var (time, expected) in new[]
                 {
                     (F(f, 100) + new MediaTime(frame.Ticks / 3), 100L),          // a third past frame 100
                     (F(f, 100) + new MediaTime(frame.Ticks * 2 / 3), 101L),      // two thirds: frame 101
                     (F(f, 100) - new MediaTime(frame.Ticks / 3), 100L),
                 })
        {
            Ok(f.Service.RippleTrimClip(a.Id, ClipEdge.End, time));
            Assert.Equal(expected, Frames(f, a).End);
            Assert.Equal(expected, Frames(f, b).Start);                                     // B follows A's new end
            Assert.True(a.TimelineEnd == F(f, expected), "the edge lies on the frame grid");
            f.UndoRedo.Undo();
        }
    }

    // --- outward --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(20)]
    [InlineData(10)]
    [InlineData(40)]
    public void Outward_the_end_lengthens_the_clip_and_moves_the_later_clips_right_up_to_the_end_of_the_source(int speedSteps)
    {
        var f = new TimelineFixture();
        var video = f.Video(8, FrameRate.Fps25);
        Ok(f.Service.AddClip(video.Id));
        var a = f.V1.Clips[0];
        if (speedSteps != 20) Ok(f.Service.SetClipSpeed(a.Id, ClipSpeed.FromSteps(speedSteps)));
        var (_, end0) = Frames(f, a);
        Ok(f.Service.TrimClip(a.Id, ClipEdge.End, F(f, end0 - 40)));                       // 40 frames of source left
        Ok(f.Service.AddClip(video.Id, f.V1.Id, F(f, end0)));                              // B after a 40-frame gap
        Ok(f.Service.AddClip(video.Id, f.V1.Id, F(f, end0 + 500)));                        // C after another gap
        var (b, c) = (f.V1.Clips[1], f.V1.Clips[2]);
        var (bStart, cStart) = (Frames(f, b).Start, Frames(f, c).Start);

        Ok(f.Service.RippleTrimClip(a.Id, ClipEdge.End, F(f, end0 - 30)));               // +10
        Assert.Equal(end0 - 30, Frames(f, a).End);
        Assert.Equal((bStart + 10, cStart + 10), (Frames(f, b).Start, Frames(f, c).Start));   // gaps kept
        f.AssertValid();
        f.UndoRedo.Undo();

        var result = f.Service.RippleTrimClip(a.Id, ClipEdge.End, F(f, end0 + 1000));   // far beyond the source
        Ok(result);
        Assert.Null(result.Message);                                                        // the source clamp is silent
        Assert.Equal(end0, Frames(f, a).End);                                               // every frame of the source
        Assert.Equal((bStart + 40, cStart + 40), (Frames(f, b).Start, Frames(f, c).Start));
        f.AssertValid();
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(5)]
    public void Outward_the_start_keeps_the_clip_where_it_is_with_earlier_source_and_stops_at_the_source_start(int speedSteps)
    {
        var f = new TimelineFixture();
        var video = f.Video(20, FrameRate.Fps25);
        Ok(f.Service.AddClip(video.Id));
        Ok(f.Service.Split(F(f, 100)));
        var (a, b) = (f.V1.Clips[0], f.V1.Clips[1]);
        Ok(f.Service.DeleteClips(TimelineFixture.Ids(a)));                                  // B [100, 500), source from 4 s
        Ok(f.Service.MoveClips(TimelineFixture.Ids(b), -100));                              // B at 0 (the timeline start)
        if (speedSteps != 20) Ok(f.Service.SetClipSpeed(b.Id, ClipSpeed.FromSteps(speedSteps)));
        Ok(f.Service.AddClip(video.Id));                                                    // C right after B
        var c = f.V1.Clips.Single(x => x != b);
        var (bStart, bEnd) = Frames(f, b);
        var cStart = Frames(f, c).Start;
        var sourceIn = ((MediaBackedClip)b).SourceIn;

        Ok(f.Service.RippleTrimClip(b.Id, ClipEdge.Start, F(f, bStart - 10)));            // +10 at the start
        Assert.Equal((bStart, bEnd + 10), Frames(f, b));                                     // still at 0
        Assert.True(((MediaBackedClip)b).SourceIn < sourceIn);
        Assert.Equal(cStart + 10, Frames(f, c).Start);
        f.AssertValid();
        f.UndoRedo.Undo();

        Ok(f.Service.RippleTrimClip(b.Id, ClipEdge.Start, F(f, bStart - 100000)));        // far beyond the source start
        Assert.Equal(bStart, Frames(f, b).Start);
        Assert.True(((MediaBackedClip)b).SourceIn >= MediaTime.Zero);
        var further = f.Service.RippleTrimClip(b.Id, ClipEdge.Start, F(f, bStart - 1));    // no source left before it
        Assert.True(further is { Success: true, NoChange: true }, further.Message);
        Assert.Equal(cStart + (Frames(f, b).End - bEnd), Frames(f, c).Start);
        f.AssertValid();
    }

    [Fact]
    public void Outward_on_a_dissolve_edge_keeps_the_handle_the_dissolve_needs()
    {
        var (f, a, b, c, dissolve) = Dissolved();

        var end = f.Service.RippleTrimClip(a.Id, ClipEdge.End, F(f, 1000));               // A has 400 frames after it
        Ok(end);
        Assert.Equal("The trim stopped where a dissolve needs the clip's frames.", end.Message);
        Assert.Equal((0L, 490L), Frames(f, a));                                              // 10 frames of handle left
        Assert.Equal((490L, 690L), Frames(f, b));
        Assert.Contains(dissolve, f.V1.Transitions);
        f.AssertValid();
        f.UndoRedo.Undo();

        var start = f.Service.RippleTrimClip(b.Id, ClipEdge.Start, F(f, -1000));          // B has 100 frames before it
        Ok(start);
        Assert.Equal((100L, 390L), Frames(f, b));                                            // +90: 10 left for the dissolve
        Assert.Equal((390L, 590L), Frames(f, c));
        Assert.Contains(dissolve, f.V1.Transitions);
        f.AssertValid();
    }

    // --- limits, gaps, other tracks, markers, playhead ------------------------------------------------------------------

    [Fact]
    public void Inward_past_the_other_edge_leaves_one_frame_without_a_dissolve_message()
    {
        var (f, a, b, _) = Three(FrameRate.Fps25, 20);
        var (start, end) = Frames(f, a);
        var bStart = Frames(f, b).Start;

        var result = f.Service.RippleTrimClip(a.Id, ClipEdge.End, F(f, start - 50));
        Ok(result);
        Assert.Null(result.Message);
        Assert.Equal((start, start + 1), Frames(f, a));
        Assert.Equal(bStart - (end - start - 1), Frames(f, b).Start);
        f.AssertValid();
    }

    [Fact]
    public void Other_tracks_markers_and_the_playhead_stay_a_locked_other_track_is_never_touched()
    {
        var (f, a, b, c) = Three(FrameRate.Fps25, 20);
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[1];
        var video = f.Project.MediaAssets[0];
        Ok(f.Service.AddClip(video.Id, v2.Id, b.TimelineStart));
        Ok(f.Service.AddClip(f.Audio(30).Id, f.A1.Id, b.TimelineStart));
        Ok(f.Service.SetTrackLocked(v2.Id, true));
        Ok(f.Service.SetTrackLocked(f.A1.Id, true));
        f.Project.Timeline.PlayheadPosition = F(f, 77);
        var others = new[] { v2.Clips[0], f.A1.Clips[0] }.Select(ClipState.Capture).ToList();
        var marker = f.Project.Timeline.Markers.Single().Position;
        var (bStart, cStart) = (Frames(f, b).Start, Frames(f, c).Start);

        Ok(f.Service.RippleTrimClip(a.Id, ClipEdge.End, F(f, 60)));

        var removed = bStart - 60;
        Assert.Equal((bStart - removed, cStart - removed), (Frames(f, b).Start, Frames(f, c).Start));
        Assert.Equal(others, new[] { v2.Clips[0], f.A1.Clips[0] }.Select(ClipState.Capture));
        Assert.Equal(marker, f.Project.Timeline.Markers.Single().Position);
        Assert.Equal(F(f, 77), f.Project.Timeline.PlayheadPosition);                        // a drag never moves it
    }

    [Fact]
    public void A_locked_track_refuses_the_ripple_drag_and_its_preview()
    {
        var (f, a, _, _) = Three(FrameRate.Fps25, 20);
        Ok(f.Service.SetTrackLocked(f.V1.Id, true));
        var before = State(f);
        var top = f.UndoRedo.CurrentPosition;

        Assert.Null(f.Service.PreviewRippleTrim(a.Id, ClipEdge.End, F(f, 60)));
        Assert.Equal("Track V1 is locked.", f.Service.RippleTrimClip(a.Id, ClipEdge.End, F(f, 60)).Message);
        Assert.Equal("Track V1 is locked.", f.Service.RippleTrimClip(a.Id, ClipEdge.Start, F(f, 60)).Message);
        Assert.Equal(before, State(f));
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void Hidden_and_muted_tracks_ripple_like_any_other()
    {
        var (f, a, b, _) = Three(FrameRate.Fps25, 20);
        Ok(f.Service.SetTrackHidden(f.V1.Id, true));
        Ok(f.Service.SetTrackMuted(f.V1.Id, true));
        Ok(f.Service.RippleTrimClip(a.Id, ClipEdge.End, F(f, 60)));
        Assert.Equal(60L, Frames(f, b).Start);
    }

    // --- preview, fades, undo / dirty -----------------------------------------------------------------------------------

    [Fact]
    public void The_preview_changes_nothing_and_shows_exactly_what_the_release_does()
    {
        var (f, a, b, c) = Three(FrameRate.Ntsc24, 40);
        var before = State(f);
        var top = f.UndoRedo.CurrentPosition;
        var dirty = f.Project.IsDirty;
        var changes = f.TimelineChangedCount;
        var target = F(f, Frames(f, a).Start + 37);

        var preview = f.Service.PreviewRippleTrim(a.Id, ClipEdge.Start, target);

        Assert.NotNull(preview);
        Assert.Equal(before, State(f));
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(dirty, f.Project.IsDirty);
        Assert.Equal(changes, f.TimelineChangedCount);                                       // no notification either
        Ok(f.Service.RippleTrimClip(a.Id, ClipEdge.Start, target));
        IReadOnlyDictionary<Guid, (MediaTime Start, MediaTime End)> expected =
            new[] { a, b, c }.ToDictionary(x => x.Id, x => (x.TimelineStart, x.TimelineEnd));
        Assert.Equal(expected.OrderBy(x => x.Key), preview!.Clips.OrderBy(x => x.Key));
        Assert.False(preview.Stopped);

        Assert.Empty(f.Service.PreviewRippleTrim(a.Id, ClipEdge.Start, a.TimelineStart)!.Clips);   // no change
    }

    [Fact]
    public void Fades_follow_their_edges_inward_and_stay_outward()
    {
        var (f, a, _, _) = Three(FrameRate.Fps25, 20);
        Ok(f.Service.SetClipProperties(a.Id, new ClipPropertyChange { Fade = new FadeProperties(F(f, 10), F(f, 300)) }));

        Ok(f.Service.RippleTrimClip(a.Id, ClipEdge.End, F(f, 200)));                     // 200 frames: the fade out cut
        Assert.Equal((F(f, 10), F(f, 200)), (a.FadeIn, a.FadeOut));
        f.UndoRedo.Undo();
        Assert.Equal((F(f, 10), F(f, 300)), (a.FadeIn, a.FadeOut));
    }

    [Fact]
    public async Task One_drag_is_one_undo_step_with_the_save_point_and_the_same_frame_is_no_step()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var (f, a, _, _) = Three(FrameRate.Fps25, 20);
            await f.Projects.SaveAsAsync(Path.Combine(root, "Project"));
            var saved = State(f);

            Ok(f.Service.RippleTrimClip(a.Id, ClipEdge.End, F(f, 300)));
            Assert.True(f.Project.IsDirty);
            var after = State(f);
            f.UndoRedo.Undo();
            Assert.Equal(saved, State(f));
            Assert.False(f.Project.IsDirty);
            f.UndoRedo.Redo();
            Assert.Equal(after, State(f));
            f.UndoRedo.Undo();

            var top = f.UndoRedo.CurrentPosition;
            var same = f.Service.RippleTrimClip(a.Id, ClipEdge.End, a.TimelineEnd);
            Assert.True(same is { Success: true, NoChange: true });
            Assert.Same(top, f.UndoRedo.CurrentPosition);
            Assert.False(f.Project.IsDirty);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // --- the ordinary drag is unchanged ---------------------------------------------------------------------------------

    [Fact]
    public void The_ordinary_edge_trim_still_opens_a_dissolve_cut_and_removes_the_dissolve_with_the_note()
    {
        var (f, _, b, c, _) = Dissolved();
        var cBefore = ClipState.Capture(c);

        var result = f.Service.TrimClip(b.Id, ClipEdge.Start, F(f, 150));

        Ok(result);
        Assert.Equal("A dissolve was removed: its clips no longer meet.", result.Message);
        Assert.Empty(f.V1.Transitions);
        Assert.Equal((150L, 300L), Frames(f, b));
        Assert.Equal(cBefore, ClipState.Capture(c));                                         // no ripple
    }

    /// <summary>One 20 s video at 25 fps on V1 split at 100 and 300: A [0, 100), B [100, 300), C [300, 500), and a
    /// 20-frame dissolve on the cut A | B — 10 frames on each side.</summary>
    private static (TimelineFixture F, Clip A, Clip B, Clip C, Transition D) Dissolved()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id));
        Ok(f.Service.Split(F(f, 100)));
        Ok(f.Service.Split(F(f, 300)));
        var clips = f.V1.Clips.OrderBy(c => c.TimelineStart).ToList();
        Ok(f.Service.AddTransition(clips[0].Id, clips[1].Id, F(f, 20)));
        return (f, clips[0], clips[1], clips[2], f.V1.Transitions.Single());
    }
}
