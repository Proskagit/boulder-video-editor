using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 15 Step 15.4 (D030 §5, Q1 / Q2 / Q4 / Q5 / Q6): trim to the playhead — Q / W plain, Shift+Q / Shift+W ripple.
/// Only selected clips with the playhead's frame strictly inside are trimmed. A plain trim is exactly the edge trim to
/// that frame (D008 / D022 / D025: source mapping, speed, fades); a ripple keeps the clip's start and moves the later
/// clips of its track by the trimmed whole frames (gaps kept; other tracks, markers stay). Dissolves are never removed:
/// a plain trim leaves a dissolve's cut edge alone (Q4), a trim stops where a dissolve needs the clip's frames (Q4 / Q6).
/// After a ripple trim of the start the playhead goes to the clip's start (Q5; several clips: the earliest). One undo
/// step per command; a refused command changes nothing and leaves no step.
/// </summary>
public class TrimToPlayheadTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;
    private static long Frame(TimelineFixture f, MediaTime t) => t.ToNearestFrame(f.Rate);
    private static (long Start, long End) Frames(TimelineFixture f, Clip c) => (Frame(f, c.TimelineStart), Frame(f, c.TimelineEnd));

    private static TimelineEditResult Trim(TimelineFixture f, ClipEdge edge, long playheadFrame, bool ripple, params Clip[] selected) =>
        f.Service.TrimToPlayhead(TimelineFixture.Ids(selected), edge, F(f, playheadFrame), ripple);

    public static TheoryData<int, int> RatesAndSpeeds()
    {
        var data = new TheoryData<int, int>();
        for (var rate = 0; rate < Rates.Length; rate++)
            foreach (var steps in new[] { 5, 10, 20, 40, 80 })               // 0.25×, 0.5×, 1×, 2×, 4×
                data.Add(rate, steps);
        return data;
    }

    private static readonly FrameRate[] Rates = { FrameRate.Ntsc24, FrameRate.Fps25, FrameRate.Ntsc30, FrameRate.Fps30, FrameRate.Fps60 };

    /// <summary>V1: clip A (a 20 s video at <paramref name="rate"/>, at <paramref name="speedSteps"/>/20 speed) from 0, then
    /// clip B of the same video right after it.</summary>
    private static (TimelineFixture F, Clip A, Clip B) Pair(FrameRate rate, int speedSteps)
    {
        var f = new TimelineFixture();
        var video = f.Video(20, rate);
        Ok(f.Service.AddClip(video.Id));
        var a = f.V1.Clips[0];
        if (speedSteps != 20) Ok(f.Service.SetClipSpeed(a.Id, ClipSpeed.FromSteps(speedSteps)));
        Ok(f.Service.AddClip(video.Id));
        return (f, a, f.V1.Clips.Single(c => c != a));
    }

    // --- plain = the edge trim, at every rate and speed ----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RatesAndSpeeds))]
    public void Plain_Q_and_W_are_the_edge_trim_to_the_playhead_frame_at_every_rate_and_speed(int rateIndex, int speedSteps)
    {
        foreach (var edge in new[] { ClipEdge.Start, ClipEdge.End })
        {
            var (f, a, b) = Pair(Rates[rateIndex], speedSteps);
            var (expected, ea, _) = Pair(Rates[rateIndex], speedSteps);
            var (start, end) = Frames(f, a);
            var p = start + (end - start) / 3;
            var bBefore = ClipState.Capture(b);

            Ok(Trim(f, edge, p, ripple: false, a));
            Ok(expected.Service.TrimClip(ea.Id, edge, F(expected, p)));

            Assert.Equal(ClipState.Capture(ea), ClipState.Capture(a));                       // tick for tick, D022 included
            Assert.Equal(edge == ClipEdge.Start ? (p, end) : (start, p), Frames(f, a));
            Assert.Equal(bBefore, ClipState.Capture(b));                                     // no ripple: the gap stays
            Assert.Equal(edge == ClipEdge.Start ? "Trim Start to Playhead" : "Trim End to Playhead", Top(f));
            f.AssertValid();
        }
    }

    [Theory]
    [MemberData(nameof(RatesAndSpeeds))]
    public void Ripple_keeps_the_start_and_moves_the_next_clip_by_whole_frames_at_every_rate_and_speed(int rateIndex, int speedSteps)
    {
        foreach (var edge in new[] { ClipEdge.Start, ClipEdge.End })
        {
            var (f, a, b) = Pair(Rates[rateIndex], speedSteps);
            var (expected, ea, eb) = Pair(Rates[rateIndex], speedSteps);
            var (start, end) = Frames(f, a);
            var p = start + (end - start) / 3;
            var removed = edge == ClipEdge.Start ? p - start : end - p;
            var (bStart, bEnd) = Frames(f, b);

            var result = Trim(f, edge, p, ripple: true, a);

            Ok(result);
            // The same as the edge trim followed by the move's rule for the clip (start) and the clips after it.
            Ok(expected.Service.TrimClip(ea.Id, edge, F(expected, p)));
            Ok(expected.Service.MoveClips(edge == ClipEdge.Start ? TimelineFixture.Ids(ea, eb) : TimelineFixture.Ids(eb), -removed));
            Assert.Equal(ClipState.Capture(ea), ClipState.Capture(a));
            Assert.Equal(ClipState.Capture(eb), ClipState.Capture(b));
            Assert.Equal((start, end - removed), Frames(f, a));
            Assert.Equal((bStart - removed, bEnd - removed), Frames(f, b));
            Assert.Equal(edge == ClipEdge.Start ? F(f, start) : null, result.Playhead);      // Q5
            Assert.Equal(edge == ClipEdge.Start ? "Ripple Trim Start to Playhead" : "Ripple Trim End to Playhead", Top(f));
            f.AssertValid();
        }
    }

    // --- eligibility (Q1) ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_playhead_must_be_strictly_inside_a_selected_clip()
    {
        var (f, a, _) = Pair(FrameRate.Fps25, 20);
        var (start, end) = Frames(f, a);
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        foreach (var ripple in new[] { false, true })
            foreach (var edge in new[] { ClipEdge.Start, ClipEdge.End })
            {
                Assert.Equal("The playhead is not inside the selected clip(s).", Trim(f, edge, start, ripple, a).Message);
                Assert.Equal("The playhead is not inside the selected clip(s).", Trim(f, edge, end, ripple, a).Message);
                Assert.Equal("Select the clip to trim: the playhead must be inside it.",
                    f.Service.TrimToPlayhead(Array.Empty<Guid>(), edge, F(f, start + 5), ripple).Message);
            }
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void One_frame_inside_trims_one_frame_and_the_last_frame_can_stay()
    {
        var (f, a, _) = Pair(FrameRate.Fps30, 20);
        var (start, end) = Frames(f, a);

        Ok(Trim(f, ClipEdge.Start, start + 1, false, a));
        Assert.Equal((start + 1, end), Frames(f, a));
        Ok(Trim(f, ClipEdge.End, end - 1, false, a));
        Assert.Equal((start + 1, end - 1), Frames(f, a));
        Ok(Trim(f, ClipEdge.End, start + 2, false, a));                                     // down to one frame
        Assert.Equal((start + 1, start + 2), Frames(f, a));
        Assert.Equal("The playhead is not inside the selected clip(s).", Trim(f, ClipEdge.Start, start + 1, false, a).Message);
        f.AssertValid();
    }

    [Fact]
    public void Several_selected_clips_with_the_playhead_inside_are_trimmed_in_one_step_the_others_are_left()
    {
        var f = new TimelineFixture();
        var video = f.Video(10, FrameRate.Fps25);
        Ok(f.Service.AddClip(video.Id));                                                     // V1 [0, 250)
        var music = f.Audio(10);
        Ok(f.Service.AddClip(music.Id, f.A1.Id, F(f, 50)));                                  // A1 [50, 300)
        Ok(f.Service.AddClip(video.Id, f.V1.Id, F(f, 400)));                                 // V1 [400, 650): no playhead
        var (v, a, later) = (f.V1.Clips[0], f.A1.Clips[0], f.V1.Clips[1]);
        var laterBefore = ClipState.Capture(later);
        var before = f.Snapshot();

        var result = Trim(f, ClipEdge.End, 100, false, v, a, later);

        Ok(result);
        Assert.Equal(new[] { v.Id, a.Id }.Order(), result.ClipIds.Order());
        Assert.Equal((0L, 100L), Frames(f, v));
        Assert.Equal((50L, 100L), Frames(f, a));
        Assert.Equal(laterBefore, ClipState.Capture(later));
        f.UndoRedo.Undo();                                                                   // one step for both
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_ripple_start_of_several_clips_moves_the_playhead_to_the_earliest_start()
    {
        var f = new TimelineFixture();
        var video = f.Video(10, FrameRate.Fps25);
        Ok(f.Service.AddClip(video.Id));                                                     // the first video fixes 25 fps
        var v = f.V1.Clips[0];
        Ok(f.Service.MoveClips(TimelineFixture.Ids(v), 40));                                 // V1 [40, 290)
        Ok(f.Service.AddClip(f.Audio(10).Id, f.A1.Id, F(f, 20)));                            // A1 [20, 270)
        var a = f.A1.Clips[0];

        var result = Trim(f, ClipEdge.Start, 100, true, v, a);

        Ok(result);
        Assert.Equal(F(f, 20), result.Playhead);
        Assert.Equal((40L, 230L), Frames(f, v));
        Assert.Equal((20L, 190L), Frames(f, a));
    }

    // --- gaps, adjacent clips, other tracks, markers -------------------------------------------------------------------

    [Fact]
    public void Ripple_keeps_the_gaps_between_the_later_clips_plain_leaves_them_as_they_are()
    {
        foreach (var ripple in new[] { false, true })
        {
            var f = new TimelineFixture();
            var video = f.Video(4, FrameRate.Fps25);
            Ok(f.Service.AddClip(video.Id, f.V1.Id, F(f, 0)));                               // X [0, 100)
            Ok(f.Service.AddClip(video.Id, f.V1.Id, F(f, 150)));                             // Y [150, 250)
            Ok(f.Service.AddClip(video.Id, f.V1.Id, F(f, 300)));                             // Z [300, 400)
            Ok(f.Service.AddClip(f.Audio(20).Id, f.A1.Id, F(f, 120)));                       // A1, other track
            Ok(f.Service.AddMarker(F(f, 200)));
            var (x, y, z) = (f.V1.Clips[0], f.V1.Clips[1], f.V1.Clips[2]);
            var audioBefore = ClipState.Capture(f.A1.Clips[0]);

            Ok(Trim(f, ClipEdge.End, 60, ripple, x));

            Assert.Equal((0L, 60L), Frames(f, x));
            Assert.Equal(ripple ? (110L, 210L) : (150L, 250L), Frames(f, y));
            Assert.Equal(ripple ? (260L, 360L) : (300L, 400L), Frames(f, z));               // the 50-frame gaps kept
            Assert.Equal(audioBefore, ClipState.Capture(f.A1.Clips[0]));                      // other tracks don't ripple
            Assert.Equal(F(f, 200), Assert.Single(f.Project.Timeline.Markers).Position);       // markers stay
            f.AssertValid();
        }
    }

    [Fact]
    public void A_plain_start_trim_next_to_an_adjacent_clip_opens_a_gap_and_leaves_the_neighbour()
    {
        var (f, a, b) = Pair(FrameRate.Fps25, 20);
        var aBefore = ClipState.Capture(a);
        var (bStart, bEnd) = Frames(f, b);

        Ok(Trim(f, ClipEdge.Start, bStart + 30, false, b));

        Assert.Equal(aBefore, ClipState.Capture(a));
        Assert.Equal((bStart + 30, bEnd), Frames(f, b));
    }

    // --- fades (D025 §2) --------------------------------------------------------------------------------------------------

    [Fact]
    public void Fades_stay_on_their_edges_and_are_cut_to_a_shorter_clip_undo_restores_them()
    {
        foreach (var ripple in new[] { false, true })
        {
            var (f, a, _) = Pair(FrameRate.Fps25, 20);                                       // A [0, 500)
            Ok(f.Service.SetClipProperties(a.Id, new ClipPropertyChange { Fade = new FadeProperties(F(f, 10), F(f, 300)) }));
            var before = f.Snapshot();

            Ok(Trim(f, ClipEdge.Start, 50, ripple, a));                                     // 450 frames: both fades fit
            Assert.Equal((F(f, 10), F(f, 300)), (a.FadeIn, a.FadeOut));
            Ok(Trim(f, ClipEdge.End, (ripple ? 0 : 50) + 200, ripple, a));                  // 200 frames: the fade out is cut
            Assert.Equal((F(f, 10), F(f, 200)), (a.FadeIn, a.FadeOut));

            f.UndoRedo.Undo();
            Assert.Equal((F(f, 10), F(f, 300)), (a.FadeIn, a.FadeOut));
            f.UndoRedo.Undo();
            Assert.Equal(before, f.Snapshot());
        }
    }

    // --- dissolves (Q4 / Q6) ------------------------------------------------------------------------------------------

    /// <summary>One 20 s video at 25 fps on V1 split at 100 and 300: A [0, 100), B [100, 300), C [300, 500), sharing the
    /// source (so each has handles), and a 20-frame dissolve on the cut A | B — 10 frames on each side.</summary>
    private static (TimelineFixture F, Clip A, Clip B, Clip C, Transition D) Dissolved()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id));
        Ok(f.Service.Split(F(f, 100)));
        Ok(f.Service.Split(F(f, 300)));
        var clips = f.V1.Clips.OrderBy(c => c.TimelineStart).ToList();
        var result = f.Service.AddTransition(clips[0].Id, clips[1].Id, F(f, 20));
        Ok(result);
        return (f, clips[0], clips[1], clips[2], f.V1.Transitions.Single());
    }

    [Fact]
    public void A_plain_trim_never_trims_a_dissolve_cut_edge_and_says_how_to_keep_the_dissolve()
    {
        var (f, a, b, _, dissolve) = Dissolved();
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        var start = Trim(f, ClipEdge.Start, 150, false, b);                                  // B's start is the cut
        var end = Trim(f, ClipEdge.End, 50, false, a);                                       // A's end is the cut

        Assert.False(start.Success);
        Assert.Equal("Not trimmed where a dissolve is on the clip's start: Shift+Q trims it and keeps the dissolve.", start.Message);
        Assert.False(end.Success);
        Assert.Equal("Not trimmed where a dissolve is on the clip's end: Shift+W trims it and keeps the dissolve.", end.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Contains(dissolve, f.V1.Transitions);
    }

    [Fact]
    public void A_plain_trim_of_a_cut_edge_skips_that_clip_but_trims_the_other_selected_clips()
    {
        var (f, _, b, _, _) = Dissolved();
        Ok(f.Service.AddClip(f.Audio(20).Id, f.A1.Id, F(f, 0)));
        var music = f.A1.Clips[0];

        var result = Trim(f, ClipEdge.Start, 150, false, b, music);

        Ok(result);
        Assert.Equal(new[] { music.Id }, result.ClipIds);
        Assert.Equal((100L, 300L), Frames(f, b));
        Assert.Equal(150L, Frames(f, music).Start);
        Assert.Contains("Shift+Q", result.Message);
    }

    [Fact]
    public void A_ripple_trim_of_a_cut_edge_keeps_the_dissolve_and_its_cut()
    {
        var (f, a, b, c, dissolve) = Dissolved();
        var duration = dissolve.Duration;
        var bSourceIn = ((MediaBackedClip)b).SourceIn;

        var result = Trim(f, ClipEdge.Start, 150, true, b);                                  // Shift+Q on B

        Ok(result);
        Assert.Null(result.Message);
        Assert.Equal((100L, 250L), Frames(f, b));                                            // B keeps its start: the cut
        Assert.Equal(bSourceIn + (F(f, 150) - F(f, 100)), ((MediaBackedClip)b).SourceIn);   // B now shows from frame 150's content
        Assert.Equal((250L, 450L), Frames(f, c));
        Assert.Equal((dissolve.LeftClipId, dissolve.RightClipId, dissolve.Duration), (a.Id, b.Id, duration));
        Assert.Contains(dissolve, f.V1.Transitions);
        Assert.Equal(F(f, 100), result.Playhead);
        f.AssertValid();

        f.UndoRedo.Undo();
        Ok(Trim(f, ClipEdge.End, 40, true, a));                                              // Shift+W on A
        Assert.Equal((0L, 40L), Frames(f, a));
        Assert.Equal((40L, 240L), Frames(f, b));
        Assert.Equal((240L, 440L), Frames(f, c));
        Assert.Contains(dissolve, f.V1.Transitions);
        f.AssertValid();
    }

    [Fact]
    public void A_ripple_trim_of_a_cut_edge_stops_where_the_dissolve_needs_the_clip_and_says_so()
    {
        var (f, a, b, c, dissolve) = Dissolved();

        var start = Trim(f, ClipEdge.Start, 295, true, b);                                   // wants 195 of B's 200 frames

        Ok(start);
        Assert.Equal("The trim stopped where a dissolve needs the clip's frames.", start.Message);
        Assert.Equal((100L, 110L), Frames(f, b));                                            // the dissolve's 10 frames stay
        Assert.Equal((110L, 310L), Frames(f, c));
        Assert.Contains(dissolve, f.V1.Transitions);
        f.AssertValid();

        f.UndoRedo.Undo();
        var end = Trim(f, ClipEdge.End, 3, true, a);                                         // wants 97 of A's 100 frames
        Ok(end);
        Assert.Equal("The trim stopped where a dissolve needs the clip's frames.", end.Message);
        Assert.Equal((0L, 10L), Frames(f, a));
        Assert.Equal((10L, 210L), Frames(f, b));
        Assert.Contains(dissolve, f.V1.Transitions);
        f.AssertValid();
    }

    [Fact]
    public void A_trim_toward_a_far_edge_dissolve_stops_where_the_dissolve_needs_the_clip()
    {
        var (f, a, b, c, dissolve) = Dissolved();
        var cBefore = ClipState.Capture(c);

        var w = Trim(f, ClipEdge.End, 103, false, b);                                       // W on B: its start has the dissolve
        Ok(w);
        Assert.Equal("The trim stopped where a dissolve needs the clip's frames.", w.Message);
        Assert.Equal((100L, 110L), Frames(f, b));
        Assert.Equal(cBefore, ClipState.Capture(c));                                         // plain: no ripple

        var q = Trim(f, ClipEdge.Start, 97, false, a);                                      // Q on A: its end has the dissolve
        Ok(q);
        Assert.Equal((90L, 100L), Frames(f, a));
        Assert.Contains(dissolve, f.V1.Transitions);
        f.AssertValid();
    }

    [Fact]
    public void A_clip_between_two_dissolves_keeps_room_for_both_and_a_trim_with_no_room_changes_nothing()
    {
        var (f, _, b, c, _) = Dissolved();
        Ok(f.Service.AddTransition(b.Id, c.Id, F(f, 30)));                                  // B | C: 15 frames on each side
        Assert.Equal(2, f.V1.Transitions.Count);

        Ok(Trim(f, ClipEdge.Start, 290, true, b));                                          // B keeps 10 + 15 frames
        Assert.Equal((100L, 125L), Frames(f, b));
        Assert.Equal(2, f.V1.Transitions.Count);
        f.AssertValid();

        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;
        var none = Trim(f, ClipEdge.End, 110, true, b);                                     // no room left
        Assert.False(none.Success);
        Assert.Equal("The trim stopped where a dissolve needs the clip's frames.", none.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    // --- locks, hidden / muted tracks --------------------------------------------------------------------------------------

    [Fact]
    public void A_locked_track_refuses_the_trim_and_a_locked_unselected_track_is_never_rippled()
    {
        var f = new TimelineFixture();
        var video = f.Video(10, FrameRate.Fps25);
        Ok(f.Service.AddClip(video.Id));                                                     // V1 [0, 250)
        Ok(f.Service.AddClip(video.Id));                                                     // V1 [250, 500)
        Ok(f.Service.AddClip(f.Audio(20).Id, f.A1.Id, F(f, 300)));                           // A1 [300, …): after the cut
        var (v, later, music) = (f.V1.Clips[0], f.V1.Clips[1], f.A1.Clips[0]);

        Ok(f.Service.SetTrackLocked(f.V1.Id, true));
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;
        foreach (var ripple in new[] { false, true })
            foreach (var edge in new[] { ClipEdge.Start, ClipEdge.End })
                Assert.Equal("Track V1 is locked.", Trim(f, edge, 100, ripple, v).Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);

        // A locked track whose selected clip doesn't contain the playhead is not touched; nor are other tracks — a locked
        // video track (V2) and an unlocked one (V3) with clips after the trimmed clip stay where they are.
        Ok(f.Service.SetTrackLocked(f.V1.Id, false));
        Ok(f.Service.SetTrackLocked(f.A1.Id, true));
        Ok(f.Service.AddTrack(TrackType.Video));
        Ok(f.Service.AddTrack(TrackType.Video));
        var (v2, v3) = (f.Project.Timeline.VideoTracks[1], f.Project.Timeline.VideoTracks[2]);
        Ok(f.Service.AddClip(video.Id, v2.Id, F(f, 300)));
        Ok(f.Service.AddClip(video.Id, v3.Id, F(f, 260)));
        Ok(f.Service.SetTrackLocked(v2.Id, true));
        var others = new[] { music, v2.Clips[0], v3.Clips[0] }.Select(ClipState.Capture).ToList();

        Ok(Trim(f, ClipEdge.End, 100, true, v, music));

        Assert.Equal((0L, 100L), Frames(f, v));
        Assert.Equal((100L, 350L), Frames(f, later));
        Assert.Equal(others, new[] { music, v2.Clips[0], v3.Clips[0] }.Select(ClipState.Capture));
        f.AssertValid();
    }

    [Fact]
    public void Hidden_and_muted_tracks_trim_like_any_other()
    {
        var (f, a, b) = Pair(FrameRate.Fps25, 20);
        Ok(f.Service.SetTrackHidden(f.V1.Id, true));
        Ok(f.Service.SetTrackMuted(f.V1.Id, true));
        var (bStart, _) = Frames(f, b);

        Ok(Trim(f, ClipEdge.End, 100, true, a));

        Assert.Equal((0L, 100L), Frames(f, a));
        Assert.Equal(100L, Frames(f, b).Start);
        Assert.NotEqual(bStart, Frames(f, b).Start);
    }

    // --- images and text, undo / redo, dirty ------------------------------------------------------------------------

    [Fact]
    public void Text_and_image_clips_trim_without_a_source()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddTextClip(MediaTime.Zero));
        var text = f.V1.Clips[0];
        var (start, end) = Frames(f, text);
        Ok(Trim(f, ClipEdge.Start, start + 10, true, text));
        Assert.Equal((start, end - 10), Frames(f, text));

        Ok(f.Service.AddClip(f.Image().Id, f.V1.Id, F(f, 500)));
        var image = f.V1.Clips.Single(c => c is ImageClip);
        var (iStart, _) = Frames(f, image);
        Ok(Trim(f, ClipEdge.Start, iStart + 20, false, image));
        Assert.Equal(iStart + 20, Frames(f, image).Start);
        Assert.Equal(MediaTime.Zero, ((ImageClip)image).SourceIn);
        f.AssertValid();
    }

    [Fact]
    public async Task Each_trim_is_one_undo_step_with_dirty_and_save_point_and_a_refused_one_leaves_the_project_clean()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var (f, a, b) = Pair(FrameRate.Fps25, 20);
            await f.Projects.SaveAsAsync(Path.Combine(root, "Project"));
            var saved = f.Snapshot();

            foreach (var ripple in new[] { false, true })
                foreach (var edge in new[] { ClipEdge.Start, ClipEdge.End })
                {
                    Ok(Trim(f, edge, 200, ripple, a));
                    Assert.True(f.Project.IsDirty);
                    var after = f.Snapshot();
                    f.UndoRedo.Undo();
                    Assert.Equal(saved, f.Snapshot());                                      // every tick back, one step
                    Assert.False(f.Project.IsDirty);
                    f.UndoRedo.Redo();
                    Assert.Equal(after, f.Snapshot());
                    f.UndoRedo.Undo();
                }

            var top = f.UndoRedo.CurrentPosition;
            Assert.False(Trim(f, ClipEdge.Start, 0, true, a).Success);                       // refused
            Assert.False(f.Project.IsDirty);
            Assert.Same(top, f.UndoRedo.CurrentPosition);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
