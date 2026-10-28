using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D048 stage 2 through the edit service, at 25 fps on a 20 s clip (500 frames): the standard layout (§3.4) and the overlay
/// rules that follow the clip — move, delete, split (§3.6), speed, trims (§3.8), copy (§3.10) — and the no-overlap
/// invariant for direct edits (§4.2); each edit one Undo step that restores the clip with its overlays.
/// </summary>
public class RouteTimingEditTests
{
    private static TimelineFixture WithMarkedVideo(out VideoClip clip)
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id).Success);
        clip = (VideoClip)f.V1.Clips[0];
        foreach (var y in new[] { 0.3, 0.6, 0.8 })
            Assert.True(f.Service.AddHold(clip.Id, new Hold(Guid.NewGuid(), Square(0.5, y), RouteColor.HexToOkLab("#E53935"),
                RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero)).Success);
        return f;
    }

    private static ImmutableArray<NormalizedPoint> Square(double x, double y, double r = 0.02) => ImmutableArray.Create(
        new NormalizedPoint(x - r, y - r), new NormalizedPoint(x + r, y - r), new NormalizedPoint(x + r, y + r), new NormalizedPoint(x - r, y + r));

    private static MediaTime Frame(long n) => MediaTime.FromFrame(n, FrameRate.Fps25);

    private static long F(MediaTime t) => t.ToNearestFrame(FrameRate.Fps25);

    private static TemplateClip TemplateOf(TimelineFixture f, VideoClip video) => f.Project.Timeline.VideoTracks
        .SelectMany(t => t.Clips).OfType<TemplateClip>().Single(p => p.AnchorClipId == video.Id);

    /// <summary>The orange <c>[As, Ae)</c> in timeline frames.</summary>
    private static (long Start, long End) Orange(VideoClip clip)
    {
        var a = clip.HoldMarkup!.Animation!;
        Assert.Equal(HoldAnimationMode.Duration, a.Mode);
        var start = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, a.StartSourceTime, FrameRate.Fps25);
        return (start, start + HoldAnimationRule.DurationFrames(a, FrameRate.Fps25));
    }

    /// <summary>The status frame <c>S</c> on the timeline (the composition's rule).</summary>
    private static long Status(TemplateClip t) => F(t.TimelineStart) + F(t.StatusAt) - F(t.CounterStart);

    private static TemplateClip LaidOut(TimelineFixture f, VideoClip clip, long tFrame, BoulderStatus status = BoulderStatus.Top)
    {
        Assert.True(f.Service.LayOutIntroAndFinal(clip.Id, Frame(tFrame)).Success);
        var template = TemplateOf(f, clip);
        if (status != BoulderStatus.None)
            Assert.True(f.Service.SetClipProperties(template.Id,
                new ClipPropertyChange { Template = TemplateProperties.Of(template)!.Value with { Status = status } }).Success);
        return template;
    }

    [Fact]
    public void The_standard_layout_is_the_5_s_animation_ending_at_T_and_the_plate_from_T_minus_7_s_in_one_undo_step()
    {
        var f = WithMarkedVideo(out var clip);
        var tracks = f.Project.Timeline.VideoTracks.Count;
        var result = f.Service.LayOutIntroAndFinal(clip.Id, Frame(250));
        Assert.True(result.Success, result.Message);
        Assert.Null(result.Message);

        Assert.Equal((125, 250), Orange(clip));                       // [T − 5 s, T)
        var template = TemplateOf(f, clip);
        Assert.Equal(TemplateKind.Boulder, template.Kind);
        Assert.Equal((75L, 500L), (F(template.TimelineStart), F(template.TimelineEnd)));   // T − 7 s … the clip's end
        Assert.Equal(375, Status(template));                          // the last 5 s
        Assert.Equal(tracks + 1, f.Project.Timeline.VideoTracks.Count);   // a track above the clip

        f.UndoRedo.Undo();
        Assert.Null(clip.HoldMarkup!.Animation);
        Assert.Empty(f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>());
        Assert.Equal(tracks, f.Project.Timeline.VideoTracks.Count);
        Assert.Equal(3, clip.HoldMarkup.Holds.Length);
    }

    [Fact]
    public void A_short_intro_is_split_2_to_5_and_with_no_time_before_T_there_is_no_animation_and_a_message()
    {
        var f = WithMarkedVideo(out var clip);
        Assert.True(f.Service.LayOutIntroAndFinal(clip.Id, Frame(75)).Success);   // 3 s before T
        Assert.Equal((75 - 54, 75), Orange(clip));                                // Fa = ⌊(750 + 7) / 14⌋ = 54, Fp = 21
        Assert.Equal(0, F(TemplateOf(f, clip).TimelineStart));

        var g = WithMarkedVideo(out var other);
        var result = g.Service.LayOutIntroAndFinal(other.Id, MediaTime.Zero);
        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Null(other.HoldMarkup!.Animation);
        Assert.Equal((0L, 500L), (F(TemplateOf(g, other).TimelineStart), F(TemplateOf(g, other).TimelineEnd)));
    }

    [Fact]
    public void With_T_in_the_last_5_s_the_lilac_starts_at_T_and_at_the_clips_end_there_is_none()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 450, BoulderStatus.None);
        Assert.Equal((325, 450), Orange(clip));
        Assert.Equal(450, Status(template));                          // S = T, touching the orange, no overlap

        var g = WithMarkedVideo(out var other);
        Assert.True(g.Service.LayOutIntroAndFinal(other.Id, Frame(500)).Success);   // T = Ve
        Assert.Equal((375, 500), Orange(other));
        Assert.Equal(BoulderStatus.None, TemplateOf(g, other).Status);
        Assert.True(Status(TemplateOf(g, other)) >= 500);             // no lilac frame
    }

    [Fact]
    public void A_second_layout_updates_the_animation_and_the_template_never_duplicating_them()
    {
        var f = WithMarkedVideo(out var clip);
        LaidOut(f, clip, 250, BoulderStatus.None);
        Assert.True(f.Service.LayOutIntroAndFinal(clip.Id, Frame(300)).Success);
        Assert.Single(f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>());
        Assert.Equal((175, 300), Orange(clip));
        Assert.Equal(125, F(TemplateOf(f, clip).TimelineStart));
    }

    [Fact]
    public void Moving_the_clip_carries_its_template_and_deleting_it_deletes_the_template_each_one_undo_step()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 250);
        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(clip), 25).Success);
        Assert.Equal(100, F(template.TimelineStart));
        Assert.Equal(400, Status(template));
        Assert.Equal((150, 275), Orange(clip));

        Assert.True(f.Service.DeleteClips(TimelineFixture.Ids(clip)).Success);
        Assert.Empty(f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>());
        f.UndoRedo.Undo();
        Assert.Same(template, TemplateOf(f, clip));
        f.UndoRedo.Undo();
        Assert.Equal(75, F(template.TimelineStart));
        f.AssertValid();
    }

    [Fact]
    public void A_split_inside_the_orange_deletes_the_whole_markup_and_undo_restores_it()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 250);
        var markup = clip.HoldMarkup;
        Assert.True(f.Service.Split(Frame(200), TimelineFixture.Ids(clip)).Success);
        var parts = f.V1.Clips.OfType<VideoClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.All(parts, p => Assert.Null(p.HoldMarkup));
        // The template spanning the cut is split too; its right part belongs to the right fragment.
        var templates = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().OrderBy(t => t.TimelineStart).ToList();
        Assert.Equal(2, templates.Count);
        Assert.Equal(parts[0].Id, templates[0].AnchorClipId);
        Assert.Equal(parts[1].Id, templates[1].AnchorClipId);
        Assert.Equal(375, Status(templates[1]));                      // Top / Attempt with the fragment holding StatusAt

        f.UndoRedo.Undo();
        Assert.Same(markup, clip.HoldMarkup);
        Assert.Same(template, TemplateOf(f, clip));
    }

    [Fact]
    public void A_split_in_the_green_keeps_the_markup_with_the_animations_start()
    {
        var f = WithMarkedVideo(out var clip);
        LaidOut(f, clip, 250);
        Assert.True(f.Service.Split(Frame(100), TimelineFixture.Ids(clip)).Success);   // before the orange
        var parts = f.V1.Clips.OfType<VideoClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Null(parts[0].HoldMarkup);
        Assert.Equal((125, 250), Orange(parts[1]));

        var g = WithMarkedVideo(out var other);
        LaidOut(g, other, 250);
        Assert.True(g.Service.Split(Frame(300), TimelineFixture.Ids(other)).Success);  // after the orange
        var otherParts = g.V1.Clips.OfType<VideoClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal((125, 250), Orange(otherParts[0]));
        Assert.Null(otherParts[1].HoldMarkup);
    }

    [Fact]
    public void A_speed_change_scales_every_linked_boundary_with_the_clips_duration()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 250);
        Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(40)).Success);
        Assert.Equal(250, F(clip.Duration));
        Assert.Equal((38L, 250L), (F(template.TimelineStart), F(template.TimelineEnd)));   // 75 → 38, the end stays the end
        Assert.Equal(188, Status(template));                                                // 375 → 188
        Assert.Equal(125, Orange(clip).End);                                                // T: 250 → 125

        f.UndoRedo.Undo();
        Assert.Equal((75L, 500L), (F(template.TimelineStart), F(template.TimelineEnd)));
        Assert.Equal((125, 250), Orange(clip));
    }

    /// <summary>The overlays after a speed change, in timeline frames: the template, its status frame, the orange.</summary>
    private static (long, long, long, long, long, BoulderStatus) Overlays(TimelineFixture f, VideoClip clip)
    {
        var t = TemplateOf(f, clip);
        var (a, e) = Orange(clip);
        return (F(t.TimelineStart), F(t.TimelineEnd), Status(t), a, e, t.Status);
    }

    [Theory]
    [InlineData(new[] { 23, 37, 61 })]     // 1.15× → 1.85× → 3.05×
    [InlineData(new[] { 13, 7, 29 })]      // 0.65× → 0.35× → 1.45×
    [InlineData(new[] { 47, 19 })]         // 2.35× → 0.95×
    public void Speed_changes_merged_into_one_step_scale_the_overlays_from_its_first_state(int[] steps)
    {
        var direct = WithMarkedVideo(out var reference);
        LaidOut(direct, reference, 250);
        Assert.True(direct.Service.SetClipSpeed(reference.Id, ClipSpeed.FromSteps(steps[^1])).Success);
        var expected = Overlays(direct, reference);

        var f = WithMarkedVideo(out var clip);
        LaidOut(f, clip, 250);
        var original = Overlays(f, clip);
        var before = f.UndoRedo.NextUndo;
        foreach (var step in steps) Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(step)).Success);
        Assert.Equal(expected, Overlays(f, clip));                    // as one change from the first state
        Assert.Equal(F(reference.Duration), F(clip.Duration));

        f.UndoRedo.Undo();                                            // one step for all of them
        Assert.Same(before, f.UndoRedo.NextUndo);
        Assert.Equal(original, Overlays(f, clip));
        Assert.Equal(ClipSpeed.Normal, clip.Speed);
        f.UndoRedo.Redo();
        Assert.Equal(expected, Overlays(f, clip));
    }

    [Fact]
    public void A_speed_change_back_to_the_first_speed_restores_the_overlays_exactly()
    {
        var f = WithMarkedVideo(out var clip);
        LaidOut(f, clip, 250);
        var original = Overlays(f, clip);
        var before = f.UndoRedo.NextUndo;
        Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(61)).Success);
        Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(37)).Success);
        Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.Normal).Success);   // the arrows back to 1×
        Assert.Equal(original, Overlays(f, clip));
        Assert.Same(before, f.UndoRedo.NextUndo);                     // nothing left to undo of it
    }

    [Fact]
    public void A_speed_up_keeps_a_two_frame_lilac_and_deletes_a_one_frame_one()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 496);                         // Top, lilac [496, 500): four frames
        Assert.Equal(BoulderStatus.Top, template.Status);

        Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(40)).Success);   // 2×: [248, 250)
        Assert.Equal(BoulderStatus.Top, template.Status);
        Assert.Equal((248L, 250L), (Status(template), F(template.TimelineEnd)));
        f.UndoRedo.Undo();
        Assert.Equal(496, Status(template));

        Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(80)).Success);   // 4×: [124, 125) — one frame
        Assert.Equal(BoulderStatus.None, template.Status);           // the lilac deleted, the green kept
        Assert.Equal(124, F(template.TimelineEnd));                   // its tail at S' (§3.8)
        f.UndoRedo.Undo();
        Assert.Equal(BoulderStatus.Top, template.Status);
        Assert.Equal(496, Status(template));
    }

    [Fact]
    public void A_trim_of_the_template_clip_never_leaves_its_status_under_two_frames()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 250);                         // Top, template [75, 500), lilac [375, 500)
        var steps = f.UndoRedo.NextUndo;

        foreach (var end in new long[] { 376, 375, 300 })            // one frame left, none, cut away before S
        {
            var refused = f.Service.TrimClip(template.Id, ClipEdge.End, Frame(end));
            Assert.False(refused.Success);
            Assert.EndsWith(Messages.Overlay_StatusTooShort, refused.Message);   // "Can't trim: …"
            Assert.Equal(500, F(template.TimelineEnd));
            Assert.Same(steps, f.UndoRedo.NextUndo);
        }
        var head = f.Service.TrimClip(template.Id, ClipEdge.Start, Frame(199));   // the moment moves with the start: S = 499
        Assert.False(head.Success);
        Assert.EndsWith(Messages.Overlay_StatusTooShort, head.Message);
        Assert.Same(steps, f.UndoRedo.NextUndo);

        Assert.True(f.Service.TrimClip(template.Id, ClipEdge.End, Frame(377)).Success);   // two frames: accepted
        Assert.Equal((375L, 377L), (Status(template), F(template.TimelineEnd)));
        Assert.Equal(BoulderStatus.Top, template.Status);

        var moved = f.Service.MoveClips(TimelineFixture.Ids(template), -10);   // a move keeps the length
        Assert.True(moved.Success, moved.Message);
        Assert.Equal((365L, 367L), (Status(template), F(template.TimelineEnd)));
    }

    [Fact]
    public void A_tail_trim_into_the_orange_deletes_the_markup_and_a_trim_into_the_lilac_shifts_it_keeping_its_length()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 250);
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, Frame(450)).Success);
        Assert.Equal((125, 250), Orange(clip));                       // not reached: kept
        Assert.Equal(450, F(template.TimelineEnd));
        Assert.Equal(325, Status(template));                          // [375, 500) → [325, 450)
        Assert.Equal(BoulderStatus.Top, template.Status);

        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, Frame(225)).Success);
        Assert.Null(clip.HoldMarkup);                                 // the orange cut: the whole markup
        Assert.Equal(225, F(template.TimelineEnd));                   // the green kept to the new end
        Assert.Equal(BoulderStatus.None, template.Status);            // the end before the lilac's start

        f.UndoRedo.Undo();
        Assert.NotNull(clip.HoldMarkup);
        Assert.Equal(BoulderStatus.Top, template.Status);
    }

    [Fact]
    public void A_shifted_lilac_that_would_overlap_the_orange_is_deleted_alone()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 400);                         // orange [275, 400), lilac [400, 500)
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, Frame(450)).Success);   // shifted to [350, 450): over the orange
        Assert.Equal((275, 400), Orange(clip));
        Assert.Equal(BoulderStatus.None, template.Status);
        Assert.Equal((225L, 450L), (F(template.TimelineStart), F(template.TimelineEnd)));   // T − 7 s = 225
    }

    [Fact]
    public void A_head_trim_into_the_orange_shortens_the_rest_and_the_green_starts_at_the_new_boundary()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 250);
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, Frame(175)).Success);
        Assert.Equal((175, 250), Orange(clip));                       // the rest, T kept
        Assert.Equal(175, F(template.TimelineStart));
        Assert.Equal(375, Status(template));
        Assert.Equal(BoulderStatus.Top, template.Status);

        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, Frame(400)).Success);   // reaching the lilac
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(BoulderStatus.None, template.Status);
        Assert.Equal((400L, 500L), (F(template.TimelineStart), F(template.TimelineEnd)));
    }

    [Fact]
    public void Direct_edits_never_put_the_lilac_over_the_orange_nor_the_template_outside_its_clip()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 250);
        var steps = f.UndoRedo.NextUndo;

        var over = f.Service.SetClipProperties(template.Id,
            new ClipPropertyChange { Template = TemplateProperties.Of(template)!.Value with { StatusAt = Frame(100) } });
        Assert.False(over.Success);                                   // S = 175: inside the orange [125, 250)
        Assert.Same(steps, f.UndoRedo.NextUndo);

        var outside = f.Service.MoveClips(TimelineFixture.Ids(template), -100);
        Assert.False(outside.Success);
        Assert.Equal(75, F(template.TimelineStart));
        Assert.Same(steps, f.UndoRedo.NextUndo);
    }

    [Fact]
    public void With_T_at_the_clips_end_Top_or_Attempt_set_in_the_Inspector_is_refused_with_no_change_and_no_undo_step()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 500, BoulderStatus.None);    // T = Ve: no frame after T
        var before = TemplateProperties.Of(template)!.Value;
        var steps = f.UndoRedo.NextUndo;

        foreach (var status in new[] { BoulderStatus.Top, BoulderStatus.Attempt })
        {
            var result = f.Service.SetClipProperties(template.Id, new ClipPropertyChange { Template = before with { Status = status } });
            Assert.False(result.Success);
            Assert.Equal(Messages.Overlay_StatusTooShort, result.Message);
        }
        var moved = f.Service.SetClipProperties(template.Id,
            new ClipPropertyChange { Template = before with { Status = BoulderStatus.Top, StatusAt = Frame(10) } });
        Assert.False(moved.Success);                                  // no moment of the template is free of the orange
        Assert.Equal(before, TemplateProperties.Of(template));
        Assert.Same(steps, f.UndoRedo.NextUndo);

        // The other fields and the moment without a status keep D045's behaviour.
        Assert.True(f.Service.SetClipProperties(template.Id, new ClipPropertyChange { Template = before with { Route = "R1" } }).Success);
        Assert.True(f.Service.SetClipProperties(template.Id,
            new ClipPropertyChange { Template = TemplateProperties.Of(template)!.Value with { StatusAt = Frame(10) } }).Success);
    }

    [Fact]
    public void With_one_frame_left_after_T_Top_is_refused_and_with_two_frames_it_is_accepted()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 499, BoulderStatus.None);    // T = Ve − 1 frame
        Assert.Equal((374, 499), Orange(clip));
        Assert.Equal(499, Status(template));                          // a lilac [499, 500) would be one frame
        var before = TemplateProperties.Of(template)!.Value;
        var steps = f.UndoRedo.NextUndo;
        var refused = f.Service.SetClipProperties(template.Id, new ClipPropertyChange { Template = before with { Status = BoulderStatus.Top } });
        Assert.False(refused.Success);
        Assert.Equal(Messages.Overlay_StatusTooShort, refused.Message);
        Assert.Equal(before, TemplateProperties.Of(template));
        Assert.Same(steps, f.UndoRedo.NextUndo);

        var g = WithMarkedVideo(out var other);
        var two = LaidOut(g, other, 498, BoulderStatus.None);        // T = Ve − 2 frames
        Assert.Equal(498, Status(two));                               // the lilac [498, 500)
        var accepted = g.Service.SetClipProperties(two.Id,
            new ClipPropertyChange { Template = TemplateProperties.Of(two)!.Value with { Status = BoulderStatus.Attempt } });
        Assert.True(accepted.Success, accepted.Message);
        Assert.Equal(BoulderStatus.Attempt, two.Status);
        Assert.Equal(498, Status(two));
    }

    [Fact]
    public void Moving_the_status_moment_keeps_at_least_two_frames_of_lilac()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 250);                         // Top, template [75, 500), S = 375
        var steps = f.UndoRedo.NextUndo;
        TimelineEditResult MoveTo(long s) => f.Service.SetClipProperties(template.Id, new ClipPropertyChange
        {
            Template = TemplateProperties.Of(template)!.Value with { StatusAt = template.CounterStart + Frame(s - 75) },
            Gesture = new ClipPropertyGesture("Set Status Moment")
        });

        var one = MoveTo(499);                                        // «На указатель» on the last frame
        Assert.False(one.Success);
        Assert.Equal(Messages.Overlay_StatusTooShort, one.Message);
        Assert.Equal(375, Status(template));
        Assert.Same(steps, f.UndoRedo.NextUndo);

        Assert.True(MoveTo(498).Success);                             // two frames left
        Assert.Equal(498, Status(template));
    }

    [Fact]
    public void The_layout_at_T_equal_to_the_clips_end_still_works_and_clears_an_earlier_status()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 250);                         // Top over the last 5 s
        Assert.Equal(BoulderStatus.Top, template.Status);

        var result = f.Service.LayOutIntroAndFinal(clip.Id, Frame(500));
        Assert.True(result.Success, result.Message);
        Assert.Same(template, TemplateOf(f, clip));                   // updated, not duplicated
        Assert.Equal((375, 500), Orange(clip));
        Assert.Equal((325L, 500L), (F(template.TimelineStart), F(template.TimelineEnd)));
        Assert.Equal(BoulderStatus.None, template.Status);           // no lilac
        Assert.True(Status(template) >= 500);

        f.UndoRedo.Undo();
        Assert.Equal(BoulderStatus.Top, template.Status);
        Assert.Equal((125, 250), Orange(clip));

        // T = Ve − 1: allowed too; a one-frame lilac would show nothing, so the status is cleared (§3.4).
        Assert.True(f.Service.LayOutIntroAndFinal(clip.Id, Frame(499)).Success);
        Assert.Equal(499, Status(template));
        Assert.Equal(BoulderStatus.None, template.Status);
    }

    [Fact]
    public void The_Duration_animations_length_is_one_undo_step_and_its_orange_stays_inside_the_clip()
    {
        var f = WithMarkedVideo(out var clip);
        LaidOut(f, clip, 250, BoulderStatus.None);                    // orange [125, 250)
        var laid = clip.HoldMarkup!.Animation!;
        var steps = f.UndoRedo.NextUndo;

        var shorter = f.Service.SetHoldAnimation(clip.Id, laid with { Duration = Frame(100) });
        Assert.True(shorter.Success, shorter.Message);
        Assert.Equal((125, 225), Orange(clip));                       // the start (a source time) kept
        Assert.NotSame(steps, f.UndoRedo.NextUndo);
        f.UndoRedo.Undo();
        Assert.Equal((125, 250), Orange(clip));
        Assert.Same(steps, f.UndoRedo.NextUndo);

        var past = f.Service.SetHoldAnimation(clip.Id, laid with { Duration = Frame(400) });   // [125, 525) past Ve = 500
        Assert.False(past.Success);
        Assert.Equal(Messages.Overlay_OrangeOutsideClip, past.Message);
        Assert.Equal(laid, clip.HoldMarkup!.Animation);
        Assert.Same(steps, f.UndoRedo.NextUndo);

        var exact = f.Service.SetHoldAnimation(clip.Id, laid with { Duration = Frame(375) });  // [125, 500): Ae = Ve allowed
        Assert.True(exact.Success, exact.Message);
    }

    [Fact]
    public void A_longer_Duration_animation_over_the_lilac_is_refused_and_an_Interval_one_may_still_run_past_the_clip()
    {
        var f = WithMarkedVideo(out var clip);
        LaidOut(f, clip, 250);                                        // Top, lilac [375, 500)
        var laid = clip.HoldMarkup!.Animation!;
        var steps = f.UndoRedo.NextUndo;

        var over = f.Service.SetHoldAnimation(clip.Id, laid with { Duration = Frame(300) });   // [125, 425) over S = 375
        Assert.False(over.Success);
        Assert.Equal(Messages.Overlay_LilacOverlap, over.Message);
        Assert.Same(steps, f.UndoRedo.NextUndo);
        Assert.True(f.Service.SetHoldAnimation(clip.Id, laid with { Duration = Frame(250) }).Success);   // Ae = S: a touch

        var g = WithMarkedVideo(out var other);
        Assert.True(g.Service.SetHoldAnimation(other.Id, new HoldAnimation { StartSourceTime = MediaTime.FromSeconds(19) }).Success);
    }

    // --- Insert / Overwrite (D031) over a clip with overlays: a split at the point, then the right part moved / trimmed ---

    private static List<TemplateClip> Templates(TimelineFixture f) =>
        f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().OrderBy(t => t.TimelineStart).ToList();

    private static VideoClip VideoAt(TimelineFixture f, long startFrame) =>
        f.V1.Clips.OfType<VideoClip>().Single(c => F(c.TimelineStart) == startFrame);

    /// <summary>Every template is anchored to a video clip that exists, and no clip has two of them here.</summary>
    private static void AssertAnchorsValid(TimelineFixture f)
    {
        var videos = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Select(v => v.Id).ToHashSet();
        var anchors = Templates(f).Where(t => t.AnchorClipId is not null).Select(t => t.AnchorClipId!.Value).ToList();
        Assert.All(anchors, a => Assert.Contains(a, videos));
        Assert.Equal(anchors.Count, anchors.Distinct().Count());
    }

    [Fact]
    public void Insert_in_the_green_splits_the_template_and_its_right_part_moves_with_the_clips_right_part()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 250);                         // orange [125, 250), template [75, 500), S = 375
        var steps = f.UndoRedo.NextUndo;
        var source = f.Video(20, FrameRate.Fps25);

        Assert.True(f.Service.InsertClip(source.Id, Frame(0), Frame(50), Frame(300), f.V1.Id).Success);
        var right = VideoAt(f, 350);
        Assert.Equal((125, 250), Orange(clip));                       // the markup with the animation's start (left)
        Assert.Null(right.HoldMarkup);
        var parts = Templates(f);
        Assert.Equal(2, parts.Count);
        Assert.Equal((75L, 300L, clip.Id), (F(parts[0].TimelineStart), F(parts[0].TimelineEnd), parts[0].AnchorClipId!.Value));
        Assert.Equal((350L, 550L, right.Id), (F(parts[1].TimelineStart), F(parts[1].TimelineEnd), parts[1].AnchorClipId!.Value));
        Assert.Equal(425, Status(parts[1]));                          // the lilac moved with the picture
        Assert.Equal(BoulderStatus.Top, parts[1].Status);
        AssertAnchorsValid(f);

        f.UndoRedo.Undo();                                            // one step
        Assert.Same(steps, f.UndoRedo.NextUndo);
        Assert.Equal((75L, 500L), (F(template.TimelineStart), F(template.TimelineEnd)));
        Assert.Equal(clip.Id, Assert.Single(Templates(f)).AnchorClipId);
        Assert.Equal(375, Status(template));
        f.UndoRedo.Redo();
        Assert.Equal(2, Templates(f).Count);
        AssertAnchorsValid(f);
    }

    [Fact]
    public void Insert_inside_the_orange_deletes_the_markup_and_a_template_after_the_point_moves_re_anchored()
    {
        var f = WithMarkedVideo(out var clip);
        LaidOut(f, clip, 250);
        var source = f.Video(20, FrameRate.Fps25);
        Assert.True(f.Service.InsertClip(source.Id, Frame(0), Frame(25), Frame(200), f.V1.Id).Success);   // inside [125, 250)
        Assert.Null(clip.HoldMarkup);
        Assert.Null(VideoAt(f, 225).HoldMarkup);
        Assert.Equal(2, Templates(f).Count);
        f.UndoRedo.Undo();
        Assert.Equal((125, 250), Orange(clip));

        var g = WithMarkedVideo(out var other);
        var template = LaidOut(g, other, 250);
        var otherSource = g.Video(20, FrameRate.Fps25);
        Assert.True(g.Service.InsertClip(otherSource.Id, Frame(0), Frame(25), Frame(50), g.V1.Id).Success);   // before the template
        var right = VideoAt(g, 75);
        Assert.Equal((150, 275), Orange(right));                      // the markup on the right part, moved with the picture
        Assert.Equal((100L, 525L, right.Id), (F(template.TimelineStart), F(template.TimelineEnd), template.AnchorClipId!.Value));
        Assert.Equal(400, Status(template));
        AssertAnchorsValid(g);
        g.UndoRedo.Undo();
        Assert.Equal((75L, 500L, other.Id), (F(template.TimelineStart), F(template.TimelineEnd), template.AnchorClipId!.Value));
    }

    [Fact]
    public void Overwrite_head_trims_the_right_part_of_the_template_keeping_or_deleting_the_lilac()
    {
        var f = WithMarkedVideo(out var clip);
        LaidOut(f, clip, 250);                                        // template [75, 500), S = 375
        var source = f.Video(20, FrameRate.Fps25);
        Assert.True(f.Service.OverwriteClip(source.Id, Frame(0), Frame(50), Frame(300), f.V1.Id).Success);   // [300, 350)
        var right = VideoAt(f, 350);
        var parts = Templates(f);
        Assert.Equal((75L, 300L), (F(parts[0].TimelineStart), F(parts[0].TimelineEnd)));
        Assert.Equal((350L, 500L, right.Id), (F(parts[1].TimelineStart), F(parts[1].TimelineEnd), parts[1].AnchorClipId!.Value));
        Assert.Equal(375, Status(parts[1]));                          // the moment stays on the picture
        Assert.Equal(BoulderStatus.Top, parts[1].Status);
        AssertAnchorsValid(f);
        f.UndoRedo.Undo();
        Assert.Single(Templates(f));

        // Overwriting through S reaches the lilac: deleted on both sides (no continuation, §3.8 / §1.25).
        Assert.True(f.Service.OverwriteClip(source.Id, Frame(0), Frame(100), Frame(300), f.V1.Id).Success);  // [300, 400)
        parts = Templates(f);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(BoulderStatus.None, p.Status));
        Assert.Equal((400L, 500L), (F(parts[1].TimelineStart), F(parts[1].TimelineEnd)));
        f.UndoRedo.Undo();
        Assert.Equal(BoulderStatus.Top, Assert.Single(Templates(f)).Status);
    }

    [Fact]
    public void Overwrite_into_the_orange_of_the_right_part_shortens_it_from_the_new_start()
    {
        var f = WithMarkedVideo(out var clip);
        LaidOut(f, clip, 250, BoulderStatus.None);                    // orange [125, 250)
        var source = f.Video(20, FrameRate.Fps25);
        Assert.True(f.Service.OverwriteClip(source.Id, Frame(0), Frame(100), Frame(100), f.V1.Id).Success);  // [100, 200)
        Assert.Null(clip.HoldMarkup);                                 // the cut at 100 is before the orange: all on the right
        var right = VideoAt(f, 200);
        Assert.Equal((200, 250), Orange(right));                      // the rest [200, 250), T kept
        var parts = Templates(f);
        Assert.Equal((200L, 500L, right.Id), (F(parts[1].TimelineStart), F(parts[1].TimelineEnd), parts[1].AnchorClipId!.Value));
        AssertAnchorsValid(f);
        f.UndoRedo.Undo();
        Assert.Equal((125, 250), Orange(clip));
    }

    // --- Ripple shifts of a clip with overlays (§3.5: moved with V) ---------------------------------------------------------

    /// <summary>A [0, 500) without holds, B [500, 1000) laid out at 750 with Top: orange [625, 750), template [575, 1000),
    /// lilac [875, 1000).</summary>
    private static TimelineFixture TwoClips(out VideoClip a, out VideoClip b, out TemplateClip template)
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id).Success);
        Assert.True(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id).Success);
        a = (VideoClip)f.V1.Clips.Single(c => F(c.TimelineStart) == 0);
        b = (VideoClip)f.V1.Clips.Single(c => F(c.TimelineStart) == 500);
        foreach (var y in new[] { 0.3, 0.6, 0.8 })
            Assert.True(f.Service.AddHold(b.Id, new Hold(Guid.NewGuid(), Square(0.5, y), RouteColor.HexToOkLab("#E53935"),
                RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero)).Success);
        template = LaidOut(f, b, 750);
        Assert.Equal((625, 750), Orange(b));
        Assert.Equal((575L, 1000L, 875L), (F(template.TimelineStart), F(template.TimelineEnd), Status(template)));
        return f;
    }

    /// <summary>The overlays of B relative to B's start: the template, its status, the orange; the lilac's length.</summary>
    private static (long, long, long, long, long, long) Relative(VideoClip b, TemplateClip t)
    {
        var vs = F(b.TimelineStart);
        var (oa, oe) = Orange(b);
        return (F(t.TimelineStart) - vs, F(t.TimelineEnd) - vs, Status(t) - vs, oa - vs, oe - vs, F(t.TimelineEnd) - Status(t));
    }

    [Fact]
    public void A_ripple_delete_moves_the_next_clips_overlays_with_it_and_keeps_its_unanchored_neighbours()
    {
        var f = TwoClips(out var a, out var b, out var template);
        Assert.True(f.Service.AddTemplateClip(TemplateKind.Boulder, Frame(1100)).Success);   // unanchored, after B
        var loose = Templates(f).Single(t => t.AnchorClipId is null);
        var relative = Relative(b, template);
        var steps = f.UndoRedo.NextUndo;

        Assert.True(f.Service.RippleDeleteClips(TimelineFixture.Ids(a)).Success);
        Assert.Equal(0, F(b.TimelineStart));
        Assert.Equal(relative, Relative(b, template));                // every interval moved by −500, the lilac's length kept
        Assert.Equal(b.Id, template.AnchorClipId);
        Assert.Equal(BoulderStatus.Top, template.Status);
        Assert.Equal((1100L, 1300L), (F(loose.TimelineStart), F(loose.TimelineEnd)));   // not anchored: not moved
        Assert.Null(loose.AnchorClipId);
        AssertAnchorsValid(f);

        f.UndoRedo.Undo();
        Assert.Same(steps, f.UndoRedo.NextUndo);
        Assert.Equal((575L, 1000L, 875L), (F(template.TimelineStart), F(template.TimelineEnd), Status(template)));
        Assert.Equal((625, 750), Orange(b));
        f.UndoRedo.Redo();
        Assert.Equal(relative, Relative(b, template));
        Assert.Equal(0, F(b.TimelineStart));
    }

    [Fact]
    public void A_ripple_trim_and_a_closed_gap_move_the_overlays_by_the_same_delta()
    {
        var f = TwoClips(out var a, out var b, out var template);
        var relative = Relative(b, template);

        Assert.True(f.Service.RippleTrimClip(a.Id, ClipEdge.End, Frame(400)).Success);
        Assert.Equal(400, F(b.TimelineStart));
        Assert.Equal(relative, Relative(b, template));
        f.UndoRedo.Undo();
        Assert.Equal(500, F(b.TimelineStart));
        Assert.Equal(relative, Relative(b, template));

        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(b), 60).Success);                // a gap [500, 560)
        Assert.Equal(relative, Relative(b, template));
        var steps = f.UndoRedo.NextUndo;
        Assert.True(f.Service.CloseGapBefore(b.Id).Success);
        Assert.Equal(500, F(b.TimelineStart));
        Assert.Equal(relative, Relative(b, template));
        f.UndoRedo.Undo();
        Assert.Same(steps, f.UndoRedo.NextUndo);
        Assert.Equal((560, 635L), (F(b.TimelineStart), F(template.TimelineStart)));
        f.UndoRedo.Redo();
        Assert.Equal((500, 575L), (F(b.TimelineStart), F(template.TimelineStart)));
    }

    [Fact]
    public void A_ripple_that_would_put_the_template_over_another_clip_of_its_track_is_refused()
    {
        var f = TwoClips(out var a, out var b, out var template);
        Assert.True(f.Service.AddTemplateClip(TemplateKind.Boulder, Frame(300)).Success);    // unanchored [300, 500) on V2
        var loose = Templates(f).Single(t => t.AnchorClipId is null);
        var steps = f.UndoRedo.NextUndo;

        var result = f.Service.RippleDeleteClips(TimelineFixture.Ids(a));                    // would carry it to [75, 500)
        Assert.False(result.Success);
        Assert.Same(steps, f.UndoRedo.NextUndo);
        Assert.Equal(500, F(b.TimelineStart));
        Assert.Equal((575L, 1000L), (F(template.TimelineStart), F(template.TimelineEnd)));
        Assert.Equal((300L, 500L), (F(loose.TimelineStart), F(loose.TimelineEnd)));
        Assert.Contains(a, f.V1.Clips);
    }

    [Fact]
    public void An_unanchored_template_keeps_D045s_status_moment_after_its_end()
    {
        var f = WithMarkedVideo(out _);
        Assert.True(f.Service.AddTemplateClip(TemplateKind.Boulder, MediaTime.Zero).Success);
        var template = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Single();
        Assert.Null(template.AnchorClipId);

        var result = f.Service.SetClipProperties(template.Id, new ClipPropertyChange
        {
            Template = TemplateProperties.Of(template)!.Value with { Status = BoulderStatus.Top, StatusAt = template.Duration }
        });
        Assert.True(result.Success, result.Message);                  // the status simply doesn't appear (D045 §17)
        Assert.Equal(BoulderStatus.Top, template.Status);

        var oneFrame = f.Service.SetClipProperties(template.Id, new ClipPropertyChange
        {
            Template = TemplateProperties.Of(template)!.Value with { StatusAt = template.Duration - Frame(1) }
        });
        Assert.True(oneFrame.Success, oneFrame.Message);              // no two-frame minimum without an anchor
    }

    [Fact]
    public void Duplicating_the_clip_copies_its_template_anchored_to_the_copy()
    {
        var f = WithMarkedVideo(out var clip);
        var template = LaidOut(f, clip, 250);
        Assert.True(f.Service.DuplicateClips(TimelineFixture.Ids(clip)).Success);
        var copy = f.V1.Clips.OfType<VideoClip>().Single(c => c.Id != clip.Id);
        var copied = TemplateOf(f, copy);
        Assert.NotEqual(template.Id, copied.Id);
        Assert.Equal(clip.Id, template.AnchorClipId);
        Assert.Equal(F(copy.TimelineStart) + 75, F(copied.TimelineStart));
        f.AssertValid();
    }
}
