using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 21 (D036 §3): the outline animation edited through the edit service — switching it on or off and every change
/// one undo step, consecutive changes of one setting merged (D017), different settings not; out-of-range values, other
/// clips and a locked track refused; the colour untouched; Split / Duplicate / Paste copy it; Trim, Slip, Speed and Move
/// leave it — and the snapshot keeps it on the same source moment, the second half of a split continuing it.
/// </summary>
public class HoldAnimationEditTests
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

    /// <summary>The number of undo steps (undone and redone again).</summary>
    private static int Depth(TimelineFixture f)
    {
        var n = 0;
        while (f.UndoRedo.CanUndo) { f.UndoRedo.Undo(); n++; }
        for (var i = 0; i < n; i++) f.UndoRedo.Redo();
        return n;
    }

    private static HoldSchedule ScheduleOf(TimelineFixture f, VideoClip clip) =>
        PlaybackSnapshotBuilder.Build(f.Project, 1).VideoLayers.SelectMany(l => l.Spans).Single(s => s.ClipId == clip.Id).Outline!.Schedule!;

    [Fact]
    public void Switching_on_and_off_and_each_change_is_one_undo_step_and_the_colour_stays()
    {
        var f = WithMarkedVideo(out var clip);
        var colour = clip.HoldMarkup!.ColorHex;
        var holds = clip.HoldMarkup.Holds;

        var on = new HoldAnimation { StartSourceTime = MediaTime.FromSeconds(2) };
        Assert.True(f.Service.SetHoldAnimation(clip.Id, on).Success);
        Assert.Equal(on, clip.HoldMarkup!.Animation);
        Assert.Equal("Animate Holds", f.UndoRedo.NextUndo!.Description);
        Assert.Equal(colour, clip.HoldMarkup.ColorHex);
        Assert.Equal(holds, clip.HoldMarkup.Holds);

        Assert.True(f.Service.SetHoldAnimation(clip.Id, on with { Order = HoldAnimationOrder.MarkingOrder }).Success);
        Assert.Equal("Change Hold Animation", f.UndoRedo.NextUndo!.Description);
        Assert.True(f.Service.SetHoldAnimation(clip.Id, null).Success);
        Assert.Equal("Turn Off Hold Animation", f.UndoRedo.NextUndo!.Description);
        Assert.Null(clip.HoldMarkup!.Animation);

        f.UndoRedo.Undo();
        Assert.Equal(HoldAnimationOrder.MarkingOrder, clip.HoldMarkup!.Animation!.Order);
        f.UndoRedo.Undo();
        Assert.Equal(on, clip.HoldMarkup!.Animation);
        f.UndoRedo.Undo();
        Assert.Null(clip.HoldMarkup!.Animation);
        Assert.Equal(3, clip.HoldMarkup.Holds.Length);                  // the holds were never touched
        f.UndoRedo.Redo();
        Assert.Equal(on, clip.HoldMarkup!.Animation);
    }

    [Fact]
    public void Consecutive_changes_of_one_setting_merge_and_different_settings_do_not()
    {
        var f = WithMarkedVideo(out var clip);
        var a = new HoldAnimation();
        Assert.True(f.Service.SetHoldAnimation(clip.Id, a).Success);
        var steps = Depth(f);

        foreach (var s in new[] { 0.6, 0.7, 0.8 })
            Assert.True(f.Service.SetHoldAnimation(clip.Id, a = a with { Interval = MediaTime.FromSeconds(s) }).Success);
        Assert.Equal(steps + 1, Depth(f));                   // the interval spinner: one step

        Assert.True(f.Service.SetHoldAnimation(clip.Id, a = a with { AppearFade = MediaTime.FromSeconds(0.5) }).Success);
        Assert.True(f.Service.SetHoldAnimation(clip.Id, a = a with { AppearFade = MediaTime.FromSeconds(0.75) }).Success);
        Assert.Equal(steps + 2, Depth(f));

        Assert.True(f.Service.SetHoldAnimation(clip.Id, a = a with { StayUntilClipEnd = true }).Success);
        Assert.Equal(steps + 3, Depth(f));

        f.UndoRedo.Undo();
        f.UndoRedo.Undo();
        Assert.Equal(HoldAnimation.DefaultAppearFade, clip.HoldMarkup!.Animation!.AppearFade);   // both fade edits at once
        Assert.Equal(MediaTime.FromSeconds(0.8), clip.HoldMarkup.Animation.Interval);
        f.UndoRedo.Undo();
        Assert.Equal(HoldAnimation.DefaultInterval, clip.HoldMarkup!.Animation!.Interval);

        // The same values again are no step; back to where a merged step started removes it.
        Assert.True(f.Service.SetHoldAnimation(clip.Id, clip.HoldMarkup.Animation).Success);
        Assert.Equal(steps, Depth(f));
        var start = clip.HoldMarkup.Animation;
        Assert.True(f.Service.SetHoldAnimation(clip.Id, start with { DisappearFade = MediaTime.FromSeconds(1) }).Success);
        Assert.True(f.Service.SetHoldAnimation(clip.Id, start).Success);
        Assert.Equal(steps, Depth(f));

        // Switching on and off never merges.
        Assert.True(f.Service.SetHoldAnimation(clip.Id, null).Success);
        Assert.True(f.Service.SetHoldAnimation(clip.Id, start).Success);
        Assert.Equal(steps + 2, Depth(f));
    }

    [Fact]
    public void Out_of_range_values_other_clips_and_a_locked_track_are_refused()
    {
        var f = WithMarkedVideo(out var clip);
        Assert.False(f.Service.SetHoldAnimation(clip.Id, new HoldAnimation { Interval = MediaTime.FromSeconds(0.01) }).Success);
        Assert.False(f.Service.SetHoldAnimation(clip.Id, new HoldAnimation { StayAfterLast = MediaTime.FromSeconds(100) }).Success);
        Assert.False(f.Service.SetHoldAnimation(clip.Id, new HoldAnimation { AppearFade = MediaTime.FromSeconds(-1) }).Success);
        Assert.Null(clip.HoldMarkup!.Animation);

        Assert.True(f.Service.AddTextClip(MediaTime.FromSeconds(30)).Success);
        var text = f.V1.Clips.OfType<TextClip>().Single();
        Assert.False(f.Service.SetHoldAnimation(text.Id, new HoldAnimation()).Success);

        Assert.True(f.Service.SetTrackLocked(f.V1.Id, true).Success);
        var locked = f.Service.SetHoldAnimation(clip.Id, new HoldAnimation());
        Assert.False(locked.Success);
        Assert.Contains("locked", locked.Message);
        Assert.Null(clip.HoldMarkup!.Animation);
    }

    [Fact]
    public void A_corrected_contour_and_an_added_or_removed_hold_show_in_the_animation_at_once()
    {
        var f = WithMarkedVideo(out var clip);
        Assert.True(f.Service.SetHoldAnimation(clip.Id, new HoldAnimation()).Success);
        var holds = clip.HoldMarkup!.Holds;
        Assert.Equal(new long[] { 25, 13, 0 }, ScheduleOf(f, clip).AppearFrames.ToArray());   // y 0.3, 0.6, 0.8: bottom first

        // D035: the top hold dragged to the bottom — it now appears first; the animation itself is unchanged.
        var animation = clip.HoldMarkup.Animation;
        Assert.True(f.Service.ReplaceHoldContour(clip.Id, holds[0].Id, Square(0.5, 0.95)).Success);
        Assert.Same(animation, clip.HoldMarkup!.Animation);
        Assert.Equal(new long[] { 0, 25, 13 }, ScheduleOf(f, clip).AppearFrames.ToArray());

        Assert.True(f.Service.RemoveHold(clip.Id, holds[1].Id).Success);
        Assert.Equal(new long[] { 0, 13 }, ScheduleOf(f, clip).AppearFrames.ToArray());
    }

    [Fact]
    public void Trim_slip_speed_and_move_keep_it_on_the_same_source_moment_and_a_split_half_continues_it()
    {
        var f = WithMarkedVideo(out var clip);
        var rate = f.Rate;
        var start = MediaTime.FromSeconds(4);
        Assert.True(f.Service.SetHoldAnimation(clip.Id, new HoldAnimation { StartSourceTime = start }).Success);
        var animation = clip.HoldMarkup!.Animation;

        // The source time under the start frame is the start, whatever the edit.
        void AssertOnSource()
        {
            var n0 = ScheduleOf(f, clip).StartFrame;
            Assert.True(HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, n0, rate) >= start);
            Assert.True(HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, n0 - 1, rate) < start);
        }

        AssertOnSource();
        Assert.Equal(100, ScheduleOf(f, clip).StartFrame);
        Assert.True(f.Service.TrimClip(clip.Id, ClipEdge.Start, MediaTime.FromSeconds(1)).Success);
        AssertOnSource();
        Assert.Equal(100, ScheduleOf(f, clip).StartFrame);                // trimming the head never moves it
        Assert.True(f.Service.SlipClip(clip.Id, -10).Success);
        AssertOnSource();
        Assert.True(f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(40)).Success);
        AssertOnSource();
        Assert.True(f.Service.MoveClips(new[] { clip.Id }, 30).Success);
        AssertOnSource();
        Assert.Same(animation, clip.HoldMarkup!.Animation);
        var interval = ScheduleOf(f, clip).AppearFrames.Order().ToArray();
        Assert.Equal(13, interval[1] - interval[0]);                       // 0.5 s of output time at 2× too

        // Split inside the animation: both halves carry it; the right one continues where the left one was.
        var whole = ScheduleOf(f, clip);
        var at = MediaTime.FromFrame(whole.StartFrame + 20, rate);
        Assert.True(f.Service.Split(at).Success);
        var halves = f.V1.Clips.OfType<VideoClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal(2, halves.Count);
        Assert.All(halves, h => Assert.Equal(animation, h.HoldMarkup!.Animation));
        foreach (var half in halves)                                       // no restart in the right half
        {
            var schedule = ScheduleOf(f, half);
            Assert.Equal(whole.StartFrame, schedule.StartFrame);
            Assert.Equal(whole.AppearFrames.ToArray(), schedule.AppearFrames.ToArray());
            Assert.Equal(whole.DisappearStartFrame, schedule.DisappearStartFrame);
        }

        // Duplicate and paste copy it.
        Assert.True(f.Service.DuplicateClips(new[] { halves[1].Id }).Success);
        var copy = f.V1.Clips.OfType<VideoClip>().Single(c => !halves.Contains(c));
        Assert.Equal(animation, copy.HoldMarkup!.Animation);
        var clipboard = f.Service.CopyClips(new[] { halves[0].Id })!;
        var existing = f.V1.Clips.ToHashSet();
        Assert.True(f.Service.PasteClips(clipboard, MediaTime.FromSeconds(80)).Success);
        Assert.Equal(animation, f.V1.Clips.OfType<VideoClip>().Single(c => !existing.Contains(c)).HoldMarkup!.Animation);
    }
}
