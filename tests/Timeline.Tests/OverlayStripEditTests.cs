using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D048 stage 3 (§4) through the edit service, at 25 fps on a 20 s clip laid out at <c>T</c> = 250 with Top: the strip's
/// segments (green [75, 375), orange [125, 250), lilac [375, 500)) and every handle of §4.2 — the limits stop the value,
/// the no-overlap limits refuse with a message and change nothing, each gesture is one Undo step that restores the whole
/// state, a gesture that changes nothing leaves no step, the green body has no handle.
/// </summary>
public class OverlayStripEditTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static (TimelineFixture F, VideoClip Clip, TemplateClip Template) LaidOut(BoulderStatus status = BoulderStatus.Top)
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(20, Rate).Id).Success);
        var clip = (VideoClip)f.V1.Clips[0];
        foreach (var y in new[] { 0.3, 0.6, 0.8 })
            Assert.True(f.Service.AddHold(clip.Id, new Hold(Guid.NewGuid(), Square(0.5, y), RouteColor.HexToOkLab("#E53935"),
                RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero)).Success);
        Assert.True(f.Service.LayOutIntroAndFinal(clip.Id, MediaTime.FromFrame(250, Rate)).Success);
        var template = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Single();
        if (status != BoulderStatus.None)
            Assert.True(f.Service.SetClipProperties(template.Id,
                new ClipPropertyChange { Template = TemplateProperties.Of(template)!.Value with { Status = status } }).Success);
        return (f, clip, template);
    }

    private static ImmutableArray<NormalizedPoint> Square(double x, double y, double r = 0.02) => ImmutableArray.Create(
        new NormalizedPoint(x - r, y - r), new NormalizedPoint(x + r, y - r), new NormalizedPoint(x + r, y + r), new NormalizedPoint(x - r, y + r));

    private static OverlayStripState Strip(TimelineFixture f, VideoClip clip) => f.Service.OverlayStripOf(clip.Id)!;

    /// <summary>The segments as the composition reads them: the template's bounds and status frame, the animation's frames.</summary>
    private static (long Ps, long S, long Pe, long As, long Ae) Model(VideoClip clip, TemplateClip t)
    {
        long ps = t.TimelineStart.ToNearestFrame(Rate);
        var a = clip.HoldMarkup!.Animation!;
        var start = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, a.StartSourceTime, Rate);
        return (ps, ps + t.StatusAt.ToNearestFrame(Rate) - t.CounterStart.ToNearestFrame(Rate), t.TimelineEnd.ToNearestFrame(Rate),
            start, start + HoldAnimationRule.DurationFrames(a, Rate));
    }

    /// <summary>One gesture: applied, then undone to exactly the state before, then redone.</summary>
    private static void OneStep(TimelineFixture f, VideoClip clip, TemplateClip template, OverlayStripHandle handle, long delta,
        (long Ps, long S, long Pe, long As, long Ae) expected)
    {
        var before = Model(clip, template);
        var result = f.Service.EditOverlayStrip(clip.Id, handle, delta);
        Assert.True(result.Success, result.Message);
        Assert.Equal(expected, Model(clip, template));
        f.UndoRedo.Undo();
        Assert.Equal(before, Model(clip, template));
        f.UndoRedo.Redo();
        Assert.Equal(expected, Model(clip, template));
    }

    private static void Refused(TimelineFixture f, VideoClip clip, TemplateClip template, OverlayStripHandle handle, long delta, string message)
    {
        var before = Model(clip, template);
        var step = f.UndoRedo.NextUndo;
        var result = f.Service.EditOverlayStrip(clip.Id, handle, delta);
        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.Equal(before, Model(clip, template));
        Assert.Same(step, f.UndoRedo.NextUndo);                              // no step
    }

    [Fact]
    public void The_strip_shows_the_green_the_orange_and_the_lilac_and_nothing_without_overlays()
    {
        var (f, clip, _) = LaidOut();
        var strip = Strip(f, clip);
        Assert.Equal(new FrameSpan(0, 500), strip.Clip);
        Assert.Equal(new FrameSpan(75, 375), strip.Green);
        Assert.Equal(new FrameSpan(125, 250), strip.Orange);
        Assert.Equal(new FrameSpan(375, 500), strip.Lilac);

        var (g, other, _) = LaidOut(BoulderStatus.None);
        Assert.Equal(new FrameSpan(75, 500), Strip(g, other).Green);          // no status: the green to the template's end
        Assert.Null(Strip(g, other).Lilac);

        var bare = new TimelineFixture();
        Assert.True(bare.Service.AddClip(bare.Video(20, Rate).Id).Success);
        Assert.Null(bare.Service.OverlayStripOf(bare.V1.Clips[0].Id));
    }

    [Fact]
    public void The_green_left_edge_moves_Ps_only_and_stops_at_the_clip_and_before_S()
    {
        var (f, clip, template) = LaidOut();
        OneStep(f, clip, template, OverlayStripHandle.GreenStart, -100, (0, 375, 500, 125, 250));      // stops at Vs
        OneStep(f, clip, template, OverlayStripHandle.GreenStart, 1000, (374, 375, 500, 125, 250));    // stops at S − 1
    }

    [Fact]
    public void The_status_boundary_moves_S_keeps_two_lilac_frames_and_never_reaches_into_the_orange()
    {
        var (f, clip, template) = LaidOut();
        OneStep(f, clip, template, OverlayStripHandle.Status, 50, (75, 425, 500, 125, 250));
        OneStep(f, clip, template, OverlayStripHandle.Status, 1000, (75, 498, 500, 125, 250));         // Pe − 2 (§1.25)
        Refused(f, clip, template, OverlayStripHandle.Status, -400, Messages.Overlay_LilacOverlap);    // S = 98 < Ae = 250
        OneStep(f, clip, template, OverlayStripHandle.Status, -248, (75, 250, 500, 125, 250));         // touching T: allowed
    }

    [Fact]
    public void The_lilac_end_and_body_keep_two_frames_stay_in_the_clip_and_refuse_an_overlap()
    {
        var (f, clip, template) = LaidOut();
        OneStep(f, clip, template, OverlayStripHandle.LilacEnd, -200, (75, 375, 377, 125, 250));       // S + 2
        OneStep(f, clip, template, OverlayStripHandle.LilacBody, 50, (75, 425, 427, 125, 250));
        OneStep(f, clip, template, OverlayStripHandle.LilacBody, 500, (75, 498, 500, 125, 250));       // stops at Ve
        Refused(f, clip, template, OverlayStripHandle.LilacBody, -300, Messages.Overlay_LilacOverlap);

        var step = f.UndoRedo.NextUndo;
        Assert.True(f.Service.EditOverlayStrip(clip.Id, OverlayStripHandle.LilacBody, 5).Success);    // at Ve already
        Assert.Equal((75L, 498L, 500L, 125L, 250L), Model(clip, template));
        Assert.Same(step, f.UndoRedo.NextUndo);                                                       // unchanged, no step
    }

    [Fact]
    public void The_orange_edges_and_body_change_the_animation_inside_the_clip_and_refuse_an_overlap_of_the_lilac()
    {
        var (f, clip, template) = LaidOut();
        OneStep(f, clip, template, OverlayStripHandle.OrangeStart, -200, (75, 375, 500, 0, 250));      // stops at Vs; T kept
        OneStep(f, clip, template, OverlayStripHandle.OrangeStart, 1000, (75, 375, 500, 249, 250));    // one frame at least
        OneStep(f, clip, template, OverlayStripHandle.OrangeEnd, 100, (75, 375, 500, 249, 350));
        Refused(f, clip, template, OverlayStripHandle.OrangeEnd, 100, Messages.Overlay_LilacOverlap);  // Ae 450 > S
        OneStep(f, clip, template, OverlayStripHandle.OrangeEnd, 25, (75, 375, 500, 249, 375));        // Ae = S: touching
        Refused(f, clip, template, OverlayStripHandle.OrangeBody, 10, Messages.Overlay_LilacOverlap);
        OneStep(f, clip, template, OverlayStripHandle.OrangeBody, -1000, (75, 375, 500, 0, 126));      // duration kept

        Assert.Equal(HoldAnimationMode.Duration, clip.HoldMarkup!.Animation!.Mode);
        Assert.Equal(3, clip.HoldMarkup.Holds.Length);
    }

    [Fact]
    public void Without_a_status_the_lilac_handles_have_nothing_to_move_and_the_orange_runs_free()
    {
        var (f, clip, template) = LaidOut(BoulderStatus.None);
        var result = f.Service.EditOverlayStrip(clip.Id, OverlayStripHandle.Status, 10);
        Assert.Equal((false, Messages.Strip_NoSegment), (result.Success, result.Message));
        OneStep(f, clip, template, OverlayStripHandle.OrangeEnd, 200, (75, 375, 500, 125, 450));       // no lilac: up to Ve
    }
}
