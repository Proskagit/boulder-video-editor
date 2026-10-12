using System.Text.RegularExpressions;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 17 Step 17.5 (D032 R5, R6, L5, AQ2–AQ7, AQ13): an edit of a linked clip takes its partner along by the very rule
/// it applies — so every result equals the same edit made to both clips of an unlinked pair (compared tick for tick); a
/// ripple moves the partners of the clips it moves and nothing else of their tracks; a partner on a locked track refuses
/// the whole edit and leaves no undo step; Alt + edge trims one clip alone (J / L cuts).
/// </summary>
public class LinkedEditTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static (long, long) Frames(Clip c) => (c.TimelineStart.ToNearestFrame(Rate), c.TimelineEnd.ToNearestFrame(Rate));

    /// <summary>The snapshot without the clip ids and link values: two timelines made by different edits compare equal when
    /// their clips are the same.</summary>
    private static string Shape(TimelineFixture f) =>
        Regex.Replace(Regex.Replace(f.Snapshot(), @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "#"), @"link=[#]?", "link=");

    /// <summary>V1: a 20 s recording (video with sound) at [20, 120) from source frame 50, its sound unlinked to A1 at the
    /// same place; the two linked when <paramref name="linked"/>. Undo history cleared.</summary>
    private static (TimelineFixture F, MediaAsset Take, VideoClip V, AudioClip A) Pair(bool linked = true)
    {
        var f = new TimelineFixture();
        var take = f.VideoWithSound(20, Rate);
        Ok(f.Service.AddClip(take.Id, f.V1.Id, F(20)));
        var v = (VideoClip)f.V1.Clips[0];
        Ok(f.Service.TrimClip(v.Id, ClipEdge.Start, F(70)));                  // source from frame 50
        Ok(f.Service.MoveClips(new[] { v.Id }, -50));
        Ok(f.Service.TrimClip(v.Id, ClipEdge.End, F(120)));
        Ok(f.Service.UnlinkAudio(v.Id));
        var a = (AudioClip)f.A1.Clips[0];
        if (linked) Ok(f.Service.LinkClips(new[] { v.Id, a.Id }));
        f.UndoRedo.Clear();
        return (f, take, v, a);
    }

    /// <summary>Runs <paramref name="linkedEdit"/> on a linked pair and <paramref name="unlinkedEdit"/> on the same pair
    /// unlinked; both timelines must be the same, and one Undo must restore the linked one exactly.</summary>
    private static TimelineFixture AssertLikeBoth(Func<TimelineFixture, VideoClip, AudioClip, TimelineEditResult> linkedEdit,
        Action<TimelineFixture, VideoClip, AudioClip> unlinkedEdit, Action<TimelineFixture>? arrange = null)
    {
        var (f, _, v, a) = Pair();
        var (g, _, gv, ga) = Pair(linked: false);
        arrange?.Invoke(f);
        arrange?.Invoke(g);
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        Ok(linkedEdit(f, v, a));
        unlinkedEdit(g, gv, ga);

        Assert.Equal(Shape(g), Shape(f));
        f.AssertValid();
        var after = f.Snapshot();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);                         // one step
        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
        return f;
    }

    private static void AssertRefusedByLockedPartner(Func<TimelineFixture, VideoClip, AudioClip, TimelineEditResult> edit,
        Action<TimelineFixture>? arrange = null)
    {
        var (f, _, v, a) = Pair();
        arrange?.Invoke(f);
        Ok(f.Service.SetTrackLocked(f.A1.Id, true));
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        var result = edit(f, v, a);

        Assert.False(result.Success);
        Assert.Contains("Track A1 is locked.", result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    // --- move (AQ7) ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Moving_a_linked_clip_moves_its_partner_by_the_same_frames() =>
        AssertLikeBoth((f, v, _) => f.Service.MoveClips(new[] { v.Id }, 15),
            (g, v, a) => Ok(g.Service.MoveClips(new[] { v.Id, a.Id }, 15)));

    [Fact]
    public void Dragging_a_linked_pair_to_another_video_track_changes_only_the_video_clips_track()
    {
        var f = AssertLikeBoth((f, v, a) => f.Service.MoveClips(new[] { v.Id, a.Id }, -5, f.Project.Timeline.VideoTracks[1].Id),
            (g, v, a) =>
            {
                Ok(g.Service.MoveClips(new[] { v.Id }, -5, g.Project.Timeline.VideoTracks[1].Id));
                Ok(g.Service.MoveClips(new[] { a.Id }, -5));
            },
            arrange: f => Ok(f.Service.AddTrack(TrackType.Video)));
        Assert.Empty(f.V1.Clips);
        Assert.Single(f.Project.Timeline.VideoTracks[1].Clips);
    }

    [Fact]
    public void A_partner_that_would_overlap_a_clip_of_its_track_refuses_the_move()
    {
        var (f, _, v, _) = Pair();
        var music = f.Audio(30);
        Ok(f.Service.AddClip(music.Id, f.A1.Id, F(130)));
        f.UndoRedo.Clear();
        var before = f.Snapshot();

        var result = f.Service.MoveClips(new[] { v.Id }, 20);

        Assert.False(result.Success);
        Assert.Contains("overlap on track A1", result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.False(f.UndoRedo.CanUndo);
    }

    // --- trim (AQ3, R6) ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ClipEdge.End, 100)]
    [InlineData(ClipEdge.End, 140)]
    [InlineData(ClipEdge.Start, 35)]
    [InlineData(ClipEdge.Start, 5)]
    public void Trimming_a_linked_clip_trims_its_partners_same_edge(ClipEdge edge, long frame) =>
        AssertLikeBoth((f, v, _) => f.Service.TrimClip(v.Id, edge, F(frame)),
            (g, v, a) => { Ok(g.Service.TrimClip(v.Id, edge, F(frame))); Ok(g.Service.TrimClip(a.Id, edge, F(frame))); });

    [Fact]
    public void Where_the_partner_is_limited_both_take_the_smaller_trim()
    {
        var (f, _, v, a) = Pair();
        var music = f.Audio(30);
        Ok(f.Service.AddClip(music.Id, f.A1.Id, F(130)));                    // A1 has room for 10 more frames only

        Ok(f.Service.TrimClip(v.Id, ClipEdge.End, F(150)));

        Assert.Equal((20L, 130L), Frames(v));
        Assert.Equal((20L, 130L), Frames(a));
    }

    [Fact]
    public void Alt_edge_trims_one_clip_alone_for_a_J_cut_and_an_L_cut()
    {
        var (f, _, v, a) = Pair();

        Ok(f.Service.TrimClip(a.Id, ClipEdge.Start, F(10), alone: true));   // J: the sound starts 10 frames early
        Ok(f.Service.TrimClip(a.Id, ClipEdge.End, F(135), alone: true));    // L: and goes on 15 frames longer

        Assert.Equal((20L, 120L), Frames(v));
        Assert.Equal((10L, 135L), Frames(a));
        Assert.Equal(v.LinkId, a.LinkId);
        f.AssertValid();

        Ok(f.Service.MoveClips(new[] { v.Id }, 30));                         // the offset is kept from now on
        Assert.Equal((50L, 150L), Frames(v));
        Assert.Equal((40L, 165L), Frames(a));
    }

    [Fact]
    public void The_trim_preview_shows_the_clamped_trim_of_the_pair()
    {
        var (f, _, v, _) = Pair();
        var music = f.Audio(30);
        Ok(f.Service.AddClip(music.Id, f.A1.Id, F(130)));

        Assert.Equal((F(20), F(130)), f.Service.PreviewTrim(v.Id, ClipEdge.End, F(150)));
        Assert.Equal((F(20), F(150)), f.Service.PreviewTrim(v.Id, ClipEdge.End, F(150), alone: true));
    }

    [Theory]
    [InlineData(ClipEdge.Start)]
    [InlineData(ClipEdge.End)]
    public void Trim_to_the_playhead_trims_the_partner_covering_the_playhead(ClipEdge edge) =>
        AssertLikeBoth((f, v, _) => f.Service.TrimToPlayhead(new[] { v.Id }, edge, F(60), ripple: false),
            (g, v, a) => Ok(g.Service.TrimToPlayhead(new[] { v.Id, a.Id }, edge, F(60), ripple: false)));

    // --- ripple trim (AQ3, AQ4) ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ClipEdge.End, 90)]
    [InlineData(ClipEdge.End, 130)]
    [InlineData(ClipEdge.Start, 40)]
    public void A_ripple_trim_gives_the_partner_the_same_change_and_moves_only_the_rippled_tracks_clips(ClipEdge edge, long frame)
    {
        var (f, take, v, a) = Pair();
        Ok(f.Service.AddClip(take.Id, f.V1.Id, F(200)));                     // later on V1: moves
        var music = f.Audio(30);
        Ok(f.Service.AddClip(music.Id, f.A1.Id, F(200)));                    // later on A1: stays (AQ4)
        var (laterV, laterA) = (f.V1.Clips[1], f.A1.Clips[1]);
        var change = edge == ClipEdge.End ? frame - 120 : 20 - frame;
        var before = f.Snapshot();

        Ok(f.Service.RippleTrimClip(v.Id, edge, F(frame)));

        Assert.Equal((20L, 120 + change), Frames(v));
        Assert.Equal((20L, 120 + change), Frames(a));
        Assert.Equal(ClipState.Capture(v) with { Speed = default }, ClipState.Capture(a) with { Speed = default });
        Assert.Equal(200 + change, Frames(laterV).Item1);
        Assert.Equal(200L, Frames(laterA).Item1);
        f.AssertValid();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_ripple_trim_stops_where_the_partner_meets_its_next_clip()
    {
        var (f, _, v, a) = Pair();
        var music = f.Audio(30);
        Ok(f.Service.AddClip(music.Id, f.A1.Id, F(130)));                    // the partner can grow 10 frames: its track doesn't ripple

        Ok(f.Service.RippleTrimClip(v.Id, ClipEdge.End, F(150)));

        Assert.Equal((20L, 130L), Frames(v));
        Assert.Equal((20L, 130L), Frames(a));
        Assert.Equal(130L, Frames(f.A1.Clips[1]).Item1);
        f.AssertValid();
    }

    [Fact]
    public void Shift_W_on_a_linked_pair_lets_the_video_clip_lead_and_the_audio_clip_follow()
    {
        var (f, take, v, a) = Pair();
        Ok(f.Service.AddClip(take.Id, f.V1.Id, F(200)));
        var music = f.Audio(30);
        Ok(f.Service.AddClip(music.Id, f.A1.Id, F(200)));

        Ok(f.Service.TrimToPlayhead(new[] { a.Id, v.Id }, ClipEdge.End, F(80), ripple: true));

        Assert.Equal((20L, 80L), Frames(v));
        Assert.Equal((20L, 80L), Frames(a));
        Assert.Equal(160L, Frames(f.V1.Clips[1]).Item1);
        Assert.Equal(200L, Frames(f.A1.Clips[1]).Item1);
    }

    // --- split (AQ5) ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Splitting_a_linked_clip_splits_its_partner_and_links_the_right_parts()
    {
        var f = AssertLikeBoth((f, v, _) => f.Service.Split(F(70), new[] { v.Id }),
            (g, v, a) => Ok(g.Service.Split(F(70), new[] { v.Id, a.Id })));
        var (leftV, rightV) = ((VideoClip)f.V1.Clips[0], (VideoClip)f.V1.Clips[1]);
        var (leftA, rightA) = ((AudioClip)f.A1.Clips[0], (AudioClip)f.A1.Clips[1]);
        Assert.Equal(leftV.LinkId, leftA.LinkId);
        Assert.NotNull(rightV.LinkId);
        Assert.Equal(rightV.LinkId, rightA.LinkId);
        Assert.NotEqual(leftV.LinkId, rightV.LinkId);
    }

    [Fact]
    public void A_partner_not_under_the_split_point_is_not_split_and_stays_with_the_left_part()
    {
        var (f, _, v, a) = Pair();
        Ok(f.Service.TrimClip(a.Id, ClipEdge.End, F(60), alone: true));

        Ok(f.Service.Split(F(90), new[] { v.Id }));

        Assert.Single(f.A1.Clips);
        Assert.Equal(v.LinkId, a.LinkId);
        Assert.Null(((VideoClip)f.V1.Clips[1]).LinkId);
        f.AssertValid();
    }

    // --- speed (AQ6) and slip (AQ7) ------------------------------------------------------------------------------------

    [Fact]
    public void A_speed_change_applies_to_the_partner_in_one_step() =>
        AssertLikeBoth((f, v, _) => f.Service.SetClipSpeed(v.Id, ClipSpeed.FromSteps(40)),
            (g, v, a) => { Ok(g.Service.SetClipSpeed(v.Id, ClipSpeed.FromSteps(40))); Ok(g.Service.SetClipSpeed(a.Id, ClipSpeed.FromSteps(40))); });

    [Fact]
    public void A_slip_slips_the_partner_by_the_same_frames_and_both_stop_at_the_nearer_limit()
    {
        AssertLikeBoth((f, v, _) => f.Service.SlipClip(v.Id, -12),
            (g, v, a) => { Ok(g.Service.SlipClip(v.Id, -12)); Ok(g.Service.SlipClip(a.Id, -12)); });

        var (h, _, v2, a2) = Pair();
        Ok(h.Service.TrimClip(a2.Id, ClipEdge.Start, F(10), alone: true));  // the sound now starts 10 frames earlier in the source
        var result = h.Service.SlipClip(v2.Id, -100);                       // the sound's source start is the nearer limit
        Ok(result);
        Assert.Equal("The slip stopped at the start of the source.", result.Message);
        Assert.Equal(MediaTime.Zero, a2.SourceIn);
        Assert.Equal(F(10), v2.SourceIn);
    }

    // --- delete (AQ2) --------------------------------------------------------------------------------------------------

    [Fact]
    public void Deleting_a_linked_clip_deletes_its_partner() =>
        AssertLikeBoth((f, v, _) => f.Service.DeleteClips(new[] { v.Id }),
            (g, v, a) => Ok(g.Service.DeleteClips(new[] { v.Id, a.Id })));

    [Fact]
    public void Ripple_deleting_a_linked_clip_closes_up_each_track() =>
        AssertLikeBoth((f, v, _) => f.Service.RippleDeleteClips(new[] { v.Id }),
            (g, v, a) => Ok(g.Service.RippleDeleteClips(new[] { v.Id, a.Id })),
            arrange: f =>
            {
                var music = f.Audio(30);
                Ok(f.Service.AddClip(f.Project.MediaAssets[0].Id, f.V1.Id, F(150)));
                Ok(f.Service.AddClip(music.Id, f.A1.Id, F(150)));
            });

    // --- ripples move the partners of the clips they move (AQ4) --------------------------------------------------------

    [Fact]
    public void A_ripple_on_the_video_track_moves_the_partner_and_nothing_else_of_the_audio_track()
    {
        var (f, take, v, a) = Pair();
        var music = f.Audio(30);
        f.Place<VideoClip>(f.V1, take, 0, F(10).Ticks);                      // V1: [0, 10) before the pair
        Ok(f.Service.AddClip(music.Id, f.A1.Id, F(200)));                    // A1: a clip that must stay
        var first = f.V1.Clips[0];
        var before = f.Snapshot();

        Ok(f.Service.RippleDeleteClips(new[] { first.Id }));                 // the pair moves left by 10 on both tracks
        Assert.Equal((10L, 110L), Frames(v));
        Assert.Equal((10L, 110L), Frames(a));
        Assert.Equal(200L, Frames(f.A1.Clips[1]).Item1);
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());

        Ok(f.Service.CloseGap(f.V1.Id, F(15)));                              // the 10-frame gap before the pair
        Assert.Equal((10L, 110L), Frames(v));
        Assert.Equal((10L, 110L), Frames(a));
        Assert.Equal(200L, Frames(f.A1.Clips[1]).Item1);
    }

    [Fact]
    public void An_insert_before_a_linked_pair_moves_the_partner_with_it()
    {
        var (f, take, v, a) = Pair();
        Ok(f.Service.InsertClip(take.Id, F(0), F(25), F(10)));               // V1 at 10: the pair moves right by 25

        Assert.Equal((45L, 145L), Frames(v));
        Assert.Equal((45L, 145L), Frames(a));
        f.AssertValid();
    }

    // --- Insert / Overwrite through a linked clip (AQ13) ---------------------------------------------------------------

    [Fact]
    public void An_insert_inside_a_linked_clip_splits_its_partner_too_and_moves_its_right_part() =>
        AssertLikeBoth((f, v, _) => f.Service.InsertClip(f.Project.MediaAssets[0].Id, F(0), F(25), F(60)),
            (g, v, a) =>
            {
                Ok(g.Service.InsertClip(g.Project.MediaAssets[0].Id, F(0), F(25), F(60)));
                Ok(g.Service.Split(F(60), new[] { a.Id }));
                Ok(g.Service.MoveClips(new[] { g.A1.Clips[1].Id }, 25));
            });

    [Fact]
    public void An_overwrite_trimming_a_linked_clip_trims_its_partner_the_same_way() =>
        AssertLikeBoth((f, v, _) => f.Service.OverwriteClip(f.Project.MediaAssets[0].Id, F(0), F(30), F(100)),
            (g, v, a) =>
            {
                Ok(g.Service.OverwriteClip(g.Project.MediaAssets[0].Id, F(0), F(30), F(100)));
                Ok(g.Service.TrimClip(a.Id, ClipEdge.End, F(100)));
            });

    [Fact]
    public void An_overwrite_inside_a_linked_clip_splits_and_trims_its_partner_too() =>
        AssertLikeBoth((f, v, _) => f.Service.OverwriteClip(f.Project.MediaAssets[0].Id, F(0), F(20), F(50)),
            (g, v, a) =>
            {
                Ok(g.Service.OverwriteClip(g.Project.MediaAssets[0].Id, F(0), F(20), F(50)));
                Ok(g.Service.Split(F(50), new[] { a.Id }));
                Ok(g.Service.TrimClip(g.A1.Clips[1].Id, ClipEdge.Start, F(70)));
            });

    [Fact]
    public void An_overwrite_covering_a_linked_clip_removes_it_and_leaves_the_partner_unlinked()
    {
        var (f, take, v, a) = Pair();
        Ok(f.Service.OverwriteClip(take.Id, F(0), F(110), F(15)));
        Assert.DoesNotContain(v, f.V1.Clips);
        Assert.Contains(a, f.A1.Clips);
        Assert.Null(a.LinkId);
        f.AssertValid();
    }

    // --- copy / paste (R5) ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_copied_pair_is_pasted_as_a_new_linked_pair_and_one_side_alone_unlinked()
    {
        var (f, _, v, a) = Pair();
        Ok(f.Service.PasteClips(f.Service.CopyClips(new[] { v.Id, a.Id })!, F(300)));
        var (pv, pa) = ((VideoClip)f.V1.Clips[1], (AudioClip)f.A1.Clips[1]);
        Assert.NotNull(pv.LinkId);
        Assert.Equal(pv.LinkId, pa.LinkId);
        Assert.NotEqual(v.LinkId, pv.LinkId);

        Ok(f.Service.PasteClips(f.Service.CopyClips(new[] { v.Id })!, F(500)));
        Assert.Null(((VideoClip)f.V1.Clips[2]).LinkId);
        f.AssertValid();
    }

    // --- a locked partner refuses every edit (L5) ----------------------------------------------------------------------

    private static readonly Dictionary<string, Func<TimelineFixture, VideoClip, AudioClip, TimelineEditResult>> EditsByName = new()
    {
        ["move"] = (f, v, _) => f.Service.MoveClips(new[] { v.Id }, 5),
        ["trim"] = (f, v, _) => f.Service.TrimClip(v.Id, ClipEdge.End, F(100)),
        ["trim to the playhead"] = (f, v, _) => f.Service.TrimToPlayhead(new[] { v.Id }, ClipEdge.End, F(60), ripple: false),
        ["ripple trim"] = (f, v, _) => f.Service.RippleTrimClip(v.Id, ClipEdge.End, F(100)),
        ["split"] = (f, v, _) => f.Service.Split(F(60), new[] { v.Id }),
        ["split under the playhead"] = (f, _, _) => f.Service.Split(F(60)),
        ["speed"] = (f, v, _) => f.Service.SetClipSpeed(v.Id, ClipSpeed.FromSteps(40)),
        ["slip"] = (f, v, _) => f.Service.SlipClip(v.Id, -5),
        ["delete"] = (f, v, _) => f.Service.DeleteClips(new[] { v.Id }),
        ["ripple delete"] = (f, v, _) => f.Service.RippleDeleteClips(new[] { v.Id }),
        ["close gap"] = (f, _, _) => f.Service.CloseGap(f.V1.Id, F(5)),
        ["insert before"] = (f, _, _) => f.Service.InsertClip(f.Project.MediaAssets[0].Id, F(0), F(10), F(5)),
        ["insert inside"] = (f, _, _) => f.Service.InsertClip(f.Project.MediaAssets[0].Id, F(0), F(10), F(60)),
        ["overwrite"] = (f, _, _) => f.Service.OverwriteClip(f.Project.MediaAssets[0].Id, F(0), F(10), F(110)),
    };

    public static TheoryData<string> Edits => new(EditsByName.Keys);

    [Theory]
    [MemberData(nameof(Edits))]
    public void Every_edit_of_a_linked_clip_is_refused_as_a_whole_when_the_partner_is_locked(string edit) =>
        AssertRefusedByLockedPartner(EditsByName[edit]);

    [Fact]
    public void Alt_edge_on_a_clip_whose_partner_is_locked_is_allowed()
    {
        var (f, _, v, a) = Pair();
        Ok(f.Service.SetTrackLocked(f.A1.Id, true));
        Ok(f.Service.TrimClip(v.Id, ClipEdge.End, F(100), alone: true));
        Assert.Equal((20L, 100L), Frames(v));
        Assert.Equal((20L, 120L), Frames(a));
    }

    [Fact]
    public void An_unlinked_pair_edits_as_before()
    {
        var (f, _, v, a) = Pair(linked: false);
        Ok(f.Service.SetTrackLocked(f.A1.Id, true));
        Ok(f.Service.MoveClips(new[] { v.Id }, 5));
        Ok(f.Service.TrimClip(v.Id, ClipEdge.End, F(100)));
        Ok(f.Service.Split(F(60), new[] { v.Id }));
        Assert.Equal((20L, 120L), Frames(a));
        Assert.Single(f.A1.Clips);
    }
}
