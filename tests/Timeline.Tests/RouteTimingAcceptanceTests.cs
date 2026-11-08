using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D048 §5, stage 2 (Phase 33): the acceptance rows that had no dedicated test before the 2026-11-08 evidence task —
/// criterion 4 (§3.4 frame-exact for every listed <c>A</c>: 0, one frame, 1 s, 3 s, just under 7 s, exactly 7 s, 30 s, at
/// 30 fps as §3.4's own examples) and criterion 5 / 7 (§3.5 rows "Make room", "Extension", "Slip", "Copy + Paste", and
/// several templates anchored to one clip). Each edit is one Undo step that restores the model.
/// </summary>
public class RouteTimingAcceptanceTests
{
    private static ImmutableArray<NormalizedPoint> Square(double x, double y, double r = 0.02) => ImmutableArray.Create(
        new NormalizedPoint(x - r, y - r), new NormalizedPoint(x + r, y - r), new NormalizedPoint(x + r, y + r), new NormalizedPoint(x - r, y + r));

    private static VideoClip Marked(TimelineFixture f, VideoClip clip)
    {
        foreach (var y in new[] { 0.3, 0.6, 0.8 })
            Assert.True(f.Service.AddHold(clip.Id, new Hold(Guid.NewGuid(), Square(0.5, y), RouteColor.HexToOkLab("#E53935"),
                RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero)).Success);
        return clip;
    }

    private static TemplateClip[] TemplatesOf(TimelineFixture f, VideoClip video) => f.Project.Timeline.VideoTracks
        .SelectMany(t => t.Clips).OfType<TemplateClip>().Where(p => p.AnchorClipId == video.Id).ToArray();

    private static TemplateClip TemplateOf(TimelineFixture f, VideoClip video) => TemplatesOf(f, video).Single();

    private static (long Start, long End)? Orange(VideoClip clip, FrameRate rate)
    {
        if (clip.HoldMarkup?.Animation is not { } a) return null;
        Assert.Equal(HoldAnimationMode.Duration, a.Mode);
        var start = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, a.StartSourceTime, rate);
        return (start, start + HoldAnimationRule.DurationFrames(a, rate));
    }

    private static (long Ps, long S, long Pe) Plate(TemplateClip t, FrameRate rate)
    {
        long ps = t.TimelineStart.ToNearestFrame(rate);
        return (ps, ps + t.StatusAt.ToNearestFrame(rate) - t.CounterStart.ToNearestFrame(rate), t.TimelineEnd.ToNearestFrame(rate));
    }

    private static void SetTop(TimelineFixture f, TemplateClip template) =>
        Assert.True(f.Service.SetClipProperties(template.Id,
            new ClipPropertyChange { Template = TemplateProperties.Of(template)!.Value with { Status = BoulderStatus.Top } }).Success);

    /// <summary>A 40 s clip at 0 on a 30 fps sequence (Ve = 1200), marked with three holds.</summary>
    private static (TimelineFixture F, VideoClip Clip) At30Fps()
    {
        var f = new TimelineFixture();
        if (f.Rate != FrameRate.Fps30) Assert.True(f.Service.SetFrameRate(FrameRate.Fps30).Success);
        Assert.True(f.Service.AddClip(f.Video(40, FrameRate.Fps30).Id).Success);
        return (f, Marked(f, (VideoClip)f.V1.Clips[0]));
    }

    // ---- criterion 4: §3.4 for every listed A ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]     // T at the clip's start: no animation, a message
    [InlineData(1)]     // one frame
    [InlineData(30)]    // 1 s
    [InlineData(90)]    // 3 s: Fa 64, Fp 26 (§3.4's example)
    [InlineData(209)]   // just under 7 s
    [InlineData(210)]   // exactly 7 s: Fa 150 (5 s), Fp 60 (2 s)
    [InlineData(900)]   // 30 s: the standard layout
    public void The_layout_is_frame_exact_for_every_listed_A(long a)
    {
        var (f, clip) = At30Fps();
        var rate = FrameRate.Fps30;
        const long ve = 1200;
        var t = a;                                                       // Vs = 0
        var steps = f.UndoRedo.NextUndo;

        var result = f.Service.LayOutIntroAndFinal(clip.Id, MediaTime.FromFrame(t, rate));
        Assert.True(result.Success, result.Message);

        var i = Math.Min(210, a);
        var fa = (10 * i + 7) / 14;
        var fp = i - fa;
        Assert.Equal(i, fa + fp);
        if (fa >= 1)
        {
            Assert.Null(result.Message);
            Assert.Equal((t - fa, t), Orange(clip, rate));                 // [T − Fa, T), ends exactly at T
        }
        else
        {
            Assert.NotNull(result.Message);                                // A = 0: reported, not an error
            Assert.Null(clip.HoldMarkup!.Animation);
        }
        var template = TemplateOf(f, clip);
        var (ps, s, pe) = Plate(template, rate);
        Assert.Equal(t - i, ps);                                           // Ps = T − I
        Assert.Equal(ve, pe);                                              // Pe = Ve
        Assert.Equal(Math.Max(Math.Max(ve - 150, t), ps + 1), s);          // S = max(Ve − 5 s, T, Ps + 1)
        Assert.True(ps >= 0 && ps < s && s <= pe, $"Ps {ps}, S {s}, Pe {pe}");
        if (Orange(clip, rate) is { } o) Assert.True(o.Start >= 0 && o.End <= s, $"orange {o}, S {s}");

        f.UndoRedo.Undo();                                                 // one step
        Assert.Same(steps, f.UndoRedo.NextUndo);
        Assert.Empty(TemplatesOf(f, clip));
        Assert.Null(clip.HoldMarkup!.Animation);
    }

    // ---- criterion 5 / 6: §3.5 rows ----------------------------------------------------------------------------------------

    /// <summary>A 30 s file trimmed to [5 s, 25 s) of its source at timeline 0 (handles on both sides), laid out at T = 10 s
    /// with Top — 25 fps: orange [125, 250), plate [75, 500), S = 375.</summary>
    private static (TimelineFixture F, VideoClip Clip, TemplateClip Template) WithHandles()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(30, FrameRate.Fps25).Id).Success);
        var clip = Marked(f, (VideoClip)f.V1.Clips[0]);
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, MediaTime.FromFrame(625, f.Rate)).Success);
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, MediaTime.FromFrame(125, f.Rate)).Success);
        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(clip), -125).Success);
        Assert.Equal((0L, 500L), (clip.TimelineStart.ToNearestFrame(f.Rate), clip.TimelineEnd.ToNearestFrame(f.Rate)));
        Assert.True(f.Service.LayOutIntroAndFinal(clip.Id, MediaTime.FromFrame(250, f.Rate)).Success);
        var template = TemplateOf(f, clip);
        SetTop(f, template);
        Assert.Equal((125L, 250L), Orange(clip, f.Rate));
        Assert.Equal((75L, 375L, 500L), Plate(template, f.Rate));
        return (f, clip, template);
    }

    [Fact]
    public void An_extension_of_the_clip_head_or_tail_outward_touches_no_overlay()
    {
        var (f, clip, template) = WithHandles();
        var before = (Orange(clip, f.Rate), Plate(template, f.Rate));

        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.End, MediaTime.FromFrame(600, f.Rate)).Success);     // tail outward
        Assert.Equal(600, clip.TimelineEnd.ToNearestFrame(f.Rate));
        Assert.Equal(before, (Orange(clip, f.Rate), Plate(template, f.Rate)));   // the lilac not carried to the new end

        f.UndoRedo.Undo();
        Assert.Equal(500, clip.TimelineEnd.ToNearestFrame(f.Rate));
        Assert.Equal(before, (Orange(clip, f.Rate), Plate(template, f.Rate)));

        // Head outward: move the clip right first so the head has room on the timeline, then extend it.
        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(clip), 100).Success);
        var moved = (Orange(clip, f.Rate), Plate(template, f.Rate));
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, MediaTime.FromFrame(50, f.Rate)).Success);   // head outward
        Assert.Equal(50, clip.TimelineStart.ToNearestFrame(f.Rate));
        Assert.Equal(moved, (Orange(clip, f.Rate), Plate(template, f.Rate)));    // the orange never stretched or moved
        f.UndoRedo.Undo();
        Assert.Equal(moved, (Orange(clip, f.Rate), Plate(template, f.Rate)));
    }

    [Fact]
    public void A_slip_moves_the_source_anchored_animation_with_the_picture_and_leaves_the_template()
    {
        var (f, clip, template) = WithHandles();
        var plate = Plate(template, f.Rate);
        var (as0, ae0) = Orange(clip, f.Rate)!.Value;
        var sourceIn = clip.SourceIn.ToNearestFrame(f.Rate);

        var result = f.Service.SlipClip(clip.Id, -20);   // 20 frames earlier source
        Assert.True(result.Success, result.Message);
        var shift = sourceIn - clip.SourceIn.ToNearestFrame(f.Rate);
        Assert.Equal(20, shift);
        Assert.Equal((as0 + shift, ae0 + shift), Orange(clip, f.Rate));   // the animation follows its source frames
        Assert.Equal(plate, Plate(template, f.Rate));                      // the template is not touched

        f.UndoRedo.Undo();
        Assert.Equal((as0, ae0), Orange(clip, f.Rate));
        Assert.Equal(plate, Plate(template, f.Rate));
    }

    [Fact]
    public void Making_room_for_a_drop_pushes_the_clip_with_its_template_in_one_undo_step()
    {
        var (f, clip, template) = WithHandles();
        var (orange, plate) = (Orange(clip, f.Rate)!.Value, Plate(template, f.Rate));
        var other = f.Video(4, FrameRate.Fps25, "other.mp4");               // 100 frames

        var result = f.Service.AddClip(other.Id, f.V1.Id, MediaTime.Zero);  // dropped at V's start: V makes room
        Assert.True(result.Success, result.Message);
        Assert.Equal(100, clip.TimelineStart.ToNearestFrame(f.Rate));
        Assert.Equal((orange.Start + 100, orange.End + 100), Orange(clip, f.Rate));
        Assert.Equal((plate.Ps + 100, plate.S + 100, plate.Pe + 100), Plate(template, f.Rate));
        Assert.Equal(clip.Id, template.AnchorClipId);
        f.AssertValid();

        f.UndoRedo.Undo();
        Assert.Equal(0, clip.TimelineStart.ToNearestFrame(f.Rate));
        Assert.Equal(orange, Orange(clip, f.Rate));
        Assert.Equal(plate, Plate(template, f.Rate));
    }

    [Fact]
    public void Copy_and_paste_of_the_clip_and_of_a_split_fragment_give_independent_templates_anchored_to_the_copy()
    {
        var (f, clip, template) = WithHandles();
        var clipboard = f.Service.CopyClips(TimelineFixture.Ids(clip))!;
        var result = f.Service.PasteClips(clipboard, MediaTime.FromFrame(1000, f.Rate));
        Assert.True(result.Success, result.Message);
        var copy = f.V1.Clips.OfType<VideoClip>().Single(c => c.Id != clip.Id);
        var copied = TemplateOf(f, copy);
        Assert.NotEqual(template.Id, copied.Id);
        Assert.Equal(clip.Id, template.AnchorClipId);                      // the original keeps its own
        var (ps, s, pe) = Plate(copied, f.Rate);
        Assert.Equal((1075L, 1375L, 1500L), (ps, s, pe));                  // the same layout relative to the copy
        Assert.Equal((1125L, 1250L), Orange(copy, f.Rate));
        f.AssertValid();

        // Independent: editing the copy's strip leaves the original.
        Assert.True(f.Service.EditOverlayStrip(copy.Id, OverlayStripHandle.Status, 50).Success);
        Assert.Equal((75L, 375L, 500L), Plate(template, f.Rate));

        // A split fragment (the right part, after the orange) pasted: its part of the template comes with it.
        Assert.True(f.Service.Split(MediaTime.FromFrame(300, f.Rate), TimelineFixture.Ids(clip)).Success);
        var right = f.V1.Clips.OfType<VideoClip>().Single(c => c.TimelineStart.ToNearestFrame(f.Rate) == 300);
        var rightTemplates = TemplatesOf(f, right);
        Assert.Single(rightTemplates);
        var fragment = f.Service.CopyClips(TimelineFixture.Ids(right))!;
        Assert.True(f.Service.PasteClips(fragment, MediaTime.FromFrame(2000, f.Rate)).Success);
        var pasted = f.V1.Clips.OfType<VideoClip>().Single(c => c.TimelineStart.ToNearestFrame(f.Rate) == 2000);
        var pastedTemplate = TemplateOf(f, pasted);
        Assert.NotEqual(rightTemplates[0].Id, pastedTemplate.Id);
        Assert.Equal(2000 + Plate(rightTemplates[0], f.Rate).Ps - 300, Plate(pastedTemplate, f.Rate).Ps);
        f.AssertValid();
    }

    [Fact]
    public void A_split_of_the_clip_exactly_at_S_leaves_the_green_left_and_the_lilac_right_each_anchored_to_its_part()
    {
        var (f, clip, template) = WithHandles();                               // S = 375
        var steps = f.UndoRedo.NextUndo;
        Assert.True(f.Service.Split(MediaTime.FromFrame(375, f.Rate), TimelineFixture.Ids(clip)).Success);
        var right = f.V1.Clips.OfType<VideoClip>().Single(c => c.TimelineStart.ToNearestFrame(f.Rate) == 375);

        var left = TemplateOf(f, clip);
        Assert.Equal(75, Plate(left, f.Rate).Ps);
        Assert.Equal(375, left.TimelineEnd.ToNearestFrame(f.Rate));            // the green up to the cut
        Assert.Equal(BoulderStatus.Top, TemplateProperties.Of(TemplateOf(f, right))!.Value.Status);
        var (rps, rs, rpe) = Plate(TemplateOf(f, right), f.Rate);
        Assert.Equal((375L, 375L, 500L), (rps, rs, rpe));                      // the lilac starts the right part
        Assert.Equal((125L, 250L), Orange(clip, f.Rate));                      // the markup goes with the animation (left)
        Assert.Null(right.HoldMarkup?.Animation);
        f.AssertValid();

        f.UndoRedo.Undo();
        Assert.Same(steps, f.UndoRedo.NextUndo);
        Assert.Equal((75L, 375L, 500L), Plate(TemplateOf(f, clip), f.Rate));
    }

    [Fact]
    public void A_slower_speed_whose_scaled_template_would_overlap_a_clip_on_its_track_is_refused_with_nothing_changed()
    {
        var (f, clip, template) = WithHandles();                               // template [75, 500) on V2
        var track = f.Project.Timeline.VideoTracks.Single(t => t.Clips.Contains(template));
        Assert.Equal(track, f.Project.Timeline.VideoTracks.OrderByDescending(t => t.Order).First());   // the topmost track
        Assert.True(f.Service.AddTemplateClip(TemplateKind.Rec, MediaTime.FromFrame(600, f.Rate)).Success);
        Assert.Equal(2, track.Clips.Count);
        var before = (Orange(clip, f.Rate), Plate(template, f.Rate), clip.TimelineEnd);
        var steps = f.UndoRedo.NextUndo;

        var result = f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(10));   // 0.5×: the template would reach 1000
        Assert.False(result.Success);
        Assert.Equal(before, (Orange(clip, f.Rate), Plate(template, f.Rate), clip.TimelineEnd));
        Assert.Same(steps, f.UndoRedo.NextUndo);
    }

    [Fact]
    public void Animate_off_keeps_the_markup_and_Delete_of_the_markup_keeps_the_template_one_undo_step_each()
    {
        var (f, clip, template) = WithHandles();
        var plate = Plate(template, f.Rate);

        Assert.True(f.Service.SetHoldAnimation(clip.Id, null).Success);        // «Анимировать» off ≠ Delete (§3.9)
        Assert.Null(clip.HoldMarkup!.Animation);
        Assert.Equal(3, clip.HoldMarkup.Holds.Length);                         // static contours stay
        Assert.Equal(plate, Plate(TemplateOf(f, clip), f.Rate));
        f.UndoRedo.Undo();
        Assert.Equal((125L, 250L), Orange(clip, f.Rate));

        Assert.True(f.Service.DeleteHoldMarkup(clip.Id).Success);              // Delete of the orange (§3.9)
        Assert.True(clip.HoldMarkup is null || clip.HoldMarkup.Holds.IsEmpty);
        Assert.Null(clip.HoldMarkup?.Animation);
        Assert.Equal(plate, Plate(TemplateOf(f, clip), f.Rate));               // the template untouched
        f.UndoRedo.Undo();                                                     // one step restores the markup and its state
        Assert.Equal(3, clip.HoldMarkup!.Holds.Length);
        Assert.Equal((125L, 250L), Orange(clip, f.Rate));
        Assert.Equal(BoulderStatus.Top, TemplateProperties.Of(template)!.Value.Status);
    }

    [Fact]
    public void Several_templates_anchored_to_one_clip_each_follow_it_and_deleting_one_keeps_the_others()
    {
        var (f, clip, template) = WithHandles();
        // The template's own split (§3.5 "Template's own edits"): both parts stay anchored to V.
        Assert.True(f.Service.Split(MediaTime.FromFrame(200, f.Rate), new[] { template.Id }).Success);
        var parts = TemplatesOf(f, clip).OrderBy(t => t.TimelineStart).ToArray();
        Assert.Equal(2, parts.Length);
        var before = parts.Select(p => Plate(p, f.Rate).Ps).ToArray();

        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(clip), 50).Success);   // both carried
        Assert.Equal(before.Select(ps => ps + 50), parts.Select(p => Plate(p, f.Rate).Ps));

        Assert.True(f.Service.DeleteClips(new[] { parts[0].Id }).Success);          // deleting one keeps the other
        Assert.Equal(new[] { parts[1].Id }, TemplatesOf(f, clip).Select(t => t.Id));
        Assert.Equal(3, clip.HoldMarkup!.Holds.Length);                            // V and its markup untouched
        f.AssertValid();

        f.UndoRedo.Undo();
        Assert.Equal(2, TemplatesOf(f, clip).Length);
    }
}
