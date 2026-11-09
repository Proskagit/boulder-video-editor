using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D048 §5, stage 3 criterion 2 (Phase 34): the strip's limits hold on the <b>resulting frame values</b>
/// (<c>ToNearestFrame</c>) at clip speeds 0.5× / 1× / 2× and at a fractional sequence rate (29.97 and 23.976 fps), not only
/// at 1× 25 fps (<see cref="OverlayStripEditTests"/>). Every handle is pushed far past its limit on a freshly laid-out clip;
/// afterwards the strip the timeline draws equals the segments the composition reads from the model, the §4.2 limits hold
/// (<c>Vs ≤ Ps ≤ S − 1</c>, <c>S + 2 ≤ Pe ≤ Ve</c>, <c>Vs ≤ As</c>, <c>As + 1 ≤ Ae ≤ S</c>), the value stops exactly at its
/// limit, and an overlap is refused with no change and no Undo step.
/// </summary>
public class OverlayStripSpeedRateTests
{
    private const long Far = 100_000;

    public static TheoryData<int, int, int> Cases => new()
    {
        // rate numerator, rate denominator, speed steps (20 = 1×)
        { 25, 1, 10 }, { 25, 1, 40 },
        { 30000, 1001, 10 }, { 30000, 1001, 20 }, { 30000, 1001, 40 },
        { 24000, 1001, 10 }, { 24000, 1001, 40 },
    };

    private sealed record Setup(TimelineFixture F, VideoClip Clip, TemplateClip Template, FrameRate Rate, long Vs, long Ve);

    private static Setup LaidOut(int num, int den, int steps)
    {
        var rate = new FrameRate(num, den);
        var f = new TimelineFixture();
        if (f.Rate != rate) Assert.True(f.Service.SetFrameRate(rate).Success);
        Assert.True(f.Service.AddClip(f.Video(20, rate).Id).Success);
        var clip = (VideoClip)f.V1.Clips[0];
        if (steps != ClipSpeed.StepsPerUnit) Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(steps)).Success);
        foreach (var y in new[] { 0.3, 0.6, 0.8 })
            Assert.True(f.Service.AddHold(clip.Id, new Hold(Guid.NewGuid(), Square(0.5, y), RouteColor.HexToOkLab("#E53935"),
                RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero)).Success);
        var (vs, ve) = (clip.TimelineStart.ToNearestFrame(rate), clip.TimelineEnd.ToNearestFrame(rate));
        Assert.True(f.Service.LayOutIntroAndFinal(clip.Id, MediaTime.FromFrame(vs + (ve - vs) / 2, rate)).Success);
        var template = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Single();
        Assert.True(f.Service.SetClipProperties(template.Id,
            new ClipPropertyChange { Template = TemplateProperties.Of(template)!.Value with { Status = BoulderStatus.Top } }).Success);
        var s = new Setup(f, clip, template, rate, vs, ve);
        AssertConsistent(s);
        return s;
    }

    private static ImmutableArray<NormalizedPoint> Square(double x, double y, double r = 0.02) => ImmutableArray.Create(
        new NormalizedPoint(x - r, y - r), new NormalizedPoint(x + r, y - r), new NormalizedPoint(x + r, y + r), new NormalizedPoint(x - r, y + r));

    /// <summary>The segments as the composition reads them: the template's bounds and status frame, the animation's frames.</summary>
    private static (long Ps, long S, long Pe, long As, long Ae) Model(Setup s)
    {
        var (clip, t, rate) = (s.Clip, s.Template, s.Rate);
        long ps = t.TimelineStart.ToNearestFrame(rate);
        var status = ps + t.StatusAt.ToNearestFrame(rate) - t.CounterStart.ToNearestFrame(rate);
        if (clip.HoldMarkup?.Animation is not { } a) return (ps, status, t.TimelineEnd.ToNearestFrame(rate), -1, -1);   // markup deleted
        var start = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, a.StartSourceTime, rate);
        return (ps, status, t.TimelineEnd.ToNearestFrame(rate), start, start + HoldAnimationRule.DurationFrames(a, rate));
    }

    /// <summary>The drawn strip equals the model, and the §4.2 limits hold on the frame values.</summary>
    private static void AssertConsistent(Setup s)
    {
        var m = Model(s);
        var strip = s.F.Service.OverlayStripOf(s.Clip.Id)!;
        Assert.Equal(new FrameSpan(s.Vs, s.Ve), strip.Clip);
        Assert.Equal(new FrameSpan(m.Ps, m.S), strip.Green);
        Assert.Equal(m.As < 0 ? null : new FrameSpan(m.As, m.Ae), strip.Orange);
        Assert.Equal(new FrameSpan(m.S, m.Pe), strip.Lilac);
        Assert.InRange(m.Ps, s.Vs, m.S - 1);
        Assert.InRange(m.Pe, m.S + 2, s.Ve);
        if (m.As < 0) return;
        Assert.InRange(m.As, m.Ps, m.Ae - 1);                                                     // D053 §4: inside the green
        Assert.True(m.Ae <= m.S, $"the orange ({m.Ae}) overlaps the lilac ({m.S})");
    }

    private static (long Ps, long S, long Pe, long As, long Ae) Applied(Setup s, OverlayStripHandle handle, long delta)
    {
        var before = Model(s);
        var step = s.F.UndoRedo.NextUndo;
        var result = s.F.Service.EditOverlayStrip(s.Clip.Id, handle, delta);
        Assert.True(result.Success, result.Message);
        var after = Model(s);
        AssertConsistent(s);
        if (after == before)
        {
            Assert.Same(step, s.F.UndoRedo.NextUndo);       // already at the limit: nothing changed, no step
            return after;
        }
        s.F.UndoRedo.Undo();
        Assert.Equal(before, Model(s));
        AssertConsistent(s);
        s.F.UndoRedo.Redo();
        Assert.Equal(after, Model(s));
        return after;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_handle_stops_exactly_at_its_limit_on_the_frames(int num, int den, int steps)
    {
        var s = LaidOut(num, den, steps);
        var m = Model(s);
        Assert.Equal(s.Vs, Applied(s, OverlayStripHandle.GreenStart, -Far).Ps);

        s = LaidOut(num, den, steps);
        m = Model(s);
        var cut = Applied(s, OverlayStripHandle.GreenStart, Far);                                 // D053 §4: past the orange
        Assert.Equal(m.S - 1, cut.Ps);
        Assert.Equal(m.S - 1 >= m.Ae ? (-1L, -1L) : (m.S - 1, m.Ae), (cut.As, cut.Ae));            // cut from the left, or deleted

        s = LaidOut(num, den, steps);
        m = Model(s);
        Assert.Equal(m.Pe - 2, Applied(s, OverlayStripHandle.Status, Far).S);                    // two lilac frames (§1.25)

        s = LaidOut(num, den, steps);
        m = Model(s);
        Assert.Equal(m.S + 2, Applied(s, OverlayStripHandle.LilacEnd, -Far).Pe);

        s = LaidOut(num, den, steps);
        var lilac = Applied(s, OverlayStripHandle.LilacBody, Far);
        Assert.Equal(s.Ve, lilac.Pe);
        Assert.Equal(m.Pe - m.S, lilac.Pe - lilac.S);                                             // its length kept

        s = LaidOut(num, den, steps);
        m = Model(s);
        var orange = Applied(s, OverlayStripHandle.OrangeStart, -Far);
        Assert.Equal((m.Ps, m.Ae), (orange.As, orange.Ae));                                       // stops at Ps; T kept

        s = LaidOut(num, den, steps);
        m = Model(s);
        orange = Applied(s, OverlayStripHandle.OrangeStart, Far);
        Assert.Equal((m.Ae - 1, m.Ae), (orange.As, orange.Ae));                                   // one frame at least

        s = LaidOut(num, den, steps);
        m = Model(s);
        orange = Applied(s, OverlayStripHandle.OrangeBody, -Far);
        Assert.Equal(m.Ps, orange.As);                                                            // stops at Ps
        Assert.Equal(m.Ae - m.As, orange.Ae - orange.As);                                         // the duration kept
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void An_overlap_of_the_orange_and_the_lilac_is_refused_with_no_change_and_no_step(int num, int den, int steps)
    {
        foreach (var (handle, delta) in new[]
                 {
                     (OverlayStripHandle.Status, -Far), (OverlayStripHandle.LilacBody, -Far),
                     (OverlayStripHandle.OrangeEnd, Far), (OverlayStripHandle.OrangeBody, Far),
                 })
        {
            var s = LaidOut(num, den, steps);
            var before = Model(s);
            var step = s.F.UndoRedo.NextUndo;
            var result = s.F.Service.EditOverlayStrip(s.Clip.Id, handle, delta);
            Assert.False(result.Success, $"{handle} {delta}");
            Assert.Equal(Messages.Overlay_LilacOverlap, result.Message);
            Assert.Equal(before, Model(s));
            Assert.Same(step, s.F.UndoRedo.NextUndo);
            AssertConsistent(s);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Touching_is_allowed_the_orange_may_end_exactly_where_the_lilac_starts(int num, int den, int steps)
    {
        var s = LaidOut(num, den, steps);
        var m = Model(s);
        var touched = Applied(s, OverlayStripHandle.OrangeEnd, m.S - m.Ae);
        Assert.Equal(touched.S, touched.Ae);

        s = LaidOut(num, den, steps);
        m = Model(s);
        touched = Applied(s, OverlayStripHandle.Status, m.Ae - m.S);
        Assert.Equal(touched.Ae, touched.S);
    }
}
