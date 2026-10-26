using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Timeline.Commands;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 19 Steps 19.1–19.2 (D034 §5, §6): the hold markup of a video clip edited through the edit service — every
/// confirm, removal and style change one undo step, style changes merging (D017), the Auto colour and the keyline
/// recomputed from the samples stored with the holds (also holds from different frames) only when the set of holds
/// changes; refused on other clips and locked tracks; Split, Duplicate and Paste give independent copies.
/// </summary>
public class HoldMarkupEditTests
{
    private static TimelineFixture WithVideo(out VideoClip clip)
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(10, FrameRate.Ntsc30).Id).Success);
        clip = (VideoClip)f.V1.Clips[0];
        return f;
    }

    private static ImmutableArray<NormalizedPoint> Square(double x, double y, double r = 0.02) => ImmutableArray.Create(
        new NormalizedPoint(x - r, y - r), new NormalizedPoint(x + r, y - r), new NormalizedPoint(x + r, y + r), new NormalizedPoint(x - r, y + r));

    /// <summary>A hold with the surface colour of <paramref name="surfaceHex"/> on a wall of <paramref name="wallHex"/>.</summary>
    private static Hold Hold(string surfaceHex, string wallHex = "#E8DCC8", long sourceTicks = 0, double x = 0.5) =>
        new(Guid.NewGuid(), Square(x, 0.5), RouteColor.HexToOkLab(surfaceHex), RouteColor.HexToOkLab(wallHex), new MediaTime(sourceTicks));

    [Fact]
    public void Adding_and_removing_holds_are_one_undo_step_each_and_resolve_the_colour()
    {
        var f = WithVideo(out var clip);
        var red = Hold("#E53935");
        Assert.True(f.Service.AddHold(clip.Id, red).Success);
        Assert.Single(clip.HoldMarkup!.Holds);
        Assert.Equal(RouteColor.Resolve(clip.HoldMarkup).ColorHex, clip.HoldMarkup.ColorHex);
        Assert.Equal("Add Hold", f.UndoRedo.NextUndo!.Description);

        var green = Hold("#43A047", sourceTicks: 5_000_000);
        Assert.True(f.Service.AddHold(clip.Id, green).Success);
        Assert.True(f.Service.RemoveHold(clip.Id, red.Id).Success);
        Assert.Equal("Remove Hold", f.UndoRedo.NextUndo!.Description);
        Assert.Equal(new[] { green.Id }, clip.HoldMarkup!.Holds.Select(h => h.Id));

        f.UndoRedo.Undo();
        Assert.Equal(new[] { red.Id, green.Id }, clip.HoldMarkup!.Holds.Select(h => h.Id));
        f.UndoRedo.Undo();
        f.UndoRedo.Undo();
        Assert.Null(clip.HoldMarkup);
        f.UndoRedo.Redo();
        Assert.Single(clip.HoldMarkup!.Holds);
    }

    [Fact]
    public void Auto_is_recomputed_from_the_stored_samples_of_holds_marked_on_different_frames()
    {
        var f = WithVideo(out var clip);
        var holds = new[] { Hold("#FFD600", sourceTicks: 0, x: 0.2), Hold("#1E88E5", sourceTicks: 3_000_000, x: 0.4), Hold("#E53935", sourceTicks: 9_000_000, x: 0.6) };
        foreach (var h in holds) Assert.True(f.Service.AddHold(clip.Id, h).Success);

        var expected = RouteColor.Choose(holds).Hex;
        Assert.Equal(expected, clip.HoldMarkup!.ColorHex);

        Assert.True(f.Service.RemoveHold(clip.Id, holds[1].Id).Success);
        Assert.Equal(RouteColor.Choose(new[] { holds[0], holds[2] }).Hex, clip.HoldMarkup!.ColorHex);
    }

    [Fact]
    public void With_an_unchanged_set_of_holds_the_colour_is_not_recomputed()
    {
        var f = WithVideo(out var clip);
        Assert.True(f.Service.AddHold(clip.Id, Hold("#E53935")).Success);
        var colour = clip.HoldMarkup!.ColorHex;

        // A width change keeps the Auto colour; a style call with the same values is no step.
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup) with { StrokeWidth1080 = 6 }).Success);
        Assert.Equal(colour, clip.HoldMarkup!.ColorHex);
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup)).NoChange);

        // Even a stored Auto colour the maximin would not choose today (a project from a build with another palette or
        // T) stays through width and keyline edits; the keyline is re-evaluated for it. Switching Manual → Auto
        // recomputes.
        var stored = RouteColor.Palette.First(p => p.Hex != colour).Hex;
        clip.HoldMarkup = clip.HoldMarkup! with { ColorHex = stored };
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup) with { StrokeWidth1080 = 8 }).Success);
        Assert.Equal(stored, clip.HoldMarkup!.ColorHex);
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup) with { Keyline = KeylineMode.Off }).Success);
        Assert.Equal((stored, false), (clip.HoldMarkup!.ColorHex, clip.HoldMarkup.KeylineActive));
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup) with { ColorMode = HoldColorMode.Manual, ManualColorHex = stored }).Success);
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup!) with { ColorMode = HoldColorMode.Auto, ManualColorHex = null }).Success);
        Assert.Equal(colour, clip.HoldMarkup!.ColorHex);
    }

    [Fact]
    public void Manual_colour_is_kept_and_auto_returns_the_colour_of_the_current_set()
    {
        var f = WithVideo(out var clip);
        var hold = Hold("#E53935");
        Assert.True(f.Service.AddHold(clip.Id, hold).Success);
        var auto = clip.HoldMarkup!.ColorHex;

        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup) with { ColorMode = HoldColorMode.Manual, ManualColorHex = "#123456" }).Success);
        Assert.Equal("#123456", clip.HoldMarkup!.ColorHex);
        Assert.True(f.Service.AddHold(clip.Id, Hold("#43A047", x: 0.3)).Success);
        Assert.Equal("#123456", clip.HoldMarkup!.ColorHex);           // manual: kept when holds change

        Assert.True(f.Service.RemoveHold(clip.Id, clip.HoldMarkup.Holds[1].Id).Success);
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup!) with { ColorMode = HoldColorMode.Auto, ManualColorHex = null }).Success);
        Assert.Equal(auto, clip.HoldMarkup!.ColorHex);                 // back to Auto: the colour of the current set at once
        Assert.False(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup) with { ColorMode = HoldColorMode.Manual, ManualColorHex = "pink" }).Success);
    }

    [Fact]
    public void The_keyline_follows_its_mode_for_the_automatic_and_the_manual_colour()
    {
        var f = WithVideo(out var clip);
        // A hold the colour of the manual choice on a wall of the same colour: the score is 0, below any threshold.
        Assert.True(f.Service.AddHold(clip.Id, Hold("#FF00FF", "#FF00FF")).Success);
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup!) with { ColorMode = HoldColorMode.Manual, ManualColorHex = "#FF00FF" }).Success);
        Assert.True(clip.HoldMarkup!.KeylineActive);                    // Auto, low contrast → on

        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup) with { Keyline = KeylineMode.Off }).Success);
        Assert.False(clip.HoldMarkup!.KeylineActive);                   // Off → never
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup) with { ColorMode = HoldColorMode.Auto, Keyline = KeylineMode.On }).Success);
        Assert.True(clip.HoldMarkup!.KeylineActive);                    // On → always, also for the automatic colour
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup) with { Keyline = KeylineMode.Auto }).Success);
        Assert.Equal(RouteColor.RouteScore(RouteColor.HexToOkLab(clip.HoldMarkup!.ColorHex), clip.HoldMarkup.Holds) < RouteColor.KeylineThreshold,
            clip.HoldMarkup.KeylineActive);
    }

    [Fact]
    public void Consecutive_changes_of_one_style_setting_merge_into_one_step()
    {
        var f = WithVideo(out var clip);
        Assert.True(f.Service.AddHold(clip.Id, Hold("#E53935")).Success);
        foreach (var w in new[] { 5.0, 6, 7.5 })
            Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup!) with { StrokeWidth1080 = w }).Success);
        Assert.Equal("Change Outline Width", f.UndoRedo.NextUndo!.Description);

        f.UndoRedo.Undo();
        Assert.Equal(HoldMarkup.DefaultStrokeWidth1080, clip.HoldMarkup!.StrokeWidth1080);   // one step back to 4
        Assert.Equal("Add Hold", f.UndoRedo.NextUndo!.Description);

        // Another setting is its own step.
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup) with { StrokeWidth1080 = 5 }).Success);
        Assert.True(f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(clip.HoldMarkup!) with { Keyline = KeylineMode.On }).Success);
        Assert.Equal("Change Keyline", f.UndoRedo.NextUndo!.Description);
        f.UndoRedo.Undo();
        Assert.Equal(5, clip.HoldMarkup!.StrokeWidth1080);
    }

    [Fact]
    public void Markup_is_refused_on_other_clips_on_a_locked_track_and_for_invalid_values()
    {
        var f = WithVideo(out var clip);
        Assert.True(f.Service.AddTextClip(MediaTime.FromSeconds(20)).Success);
        var text = f.V1.Clips.OfType<TextClip>().Single();
        Assert.False(f.Service.AddHold(text.Id, Hold("#E53935")).Success);

        Assert.False(f.Service.AddHold(clip.Id, new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0, 0), new NormalizedPoint(1, 1)),
            default, default, MediaTime.Zero)).Success);                                         // too few points
        Assert.False(f.Service.SetHoldStyle(clip.Id, new HoldStyle(0.5, HoldColorMode.Auto, null, KeylineMode.Auto)).Success);  // too thin
        Assert.False(f.Service.RemoveHold(clip.Id, Guid.NewGuid()).Success);

        Assert.True(f.Service.SetTrackLocked(f.V1.Id, true).Success);
        var locked = f.Service.AddHold(clip.Id, Hold("#E53935"));
        Assert.False(locked.Success);
        Assert.Contains("locked", locked.Message);
        Assert.Null(clip.HoldMarkup);
    }

    [Fact]
    public void Split_duplicate_and_paste_give_independent_copies_of_the_markup()
    {
        var f = WithVideo(out var clip);
        Assert.True(f.Service.AddHold(clip.Id, Hold("#E53935", sourceTicks: 1234567)).Success);
        var markup = clip.HoldMarkup!;

        Assert.True(f.Service.Split(MediaTime.FromSeconds(4)).Success);
        var halves = f.V1.Clips.OfType<VideoClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal(2, halves.Count);
        Assert.All(halves, h => Assert.True(markup.SameAs(h.HoldMarkup!)));

        // An edit of one half leaves the other as it was.
        Assert.True(f.Service.AddHold(halves[1].Id, Hold("#43A047", x: 0.2)).Success);
        Assert.Single(halves[0].HoldMarkup!.Holds);
        Assert.Equal(2, halves[1].HoldMarkup!.Holds.Length);

        var duplicated = f.Service.DuplicateClips(new[] { halves[1].Id });   // placed after the last clip
        Assert.True(duplicated.Success, duplicated.Message);
        var copy = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(c => c != halves[0] && c != halves[1]);
        Assert.True(halves[1].HoldMarkup!.SameAs(copy.HoldMarkup!));
        Assert.Equal(1234567, copy.HoldMarkup!.Holds[0].SourceTime.Ticks);
        Assert.True(f.Service.RemoveHold(copy.Id, copy.HoldMarkup.Holds[0].Id).Success);
        Assert.Equal(2, halves[1].HoldMarkup!.Holds.Length);

        var clipboard = f.Service.CopyClips(new[] { halves[0].Id })!;
        var existing = f.V1.Clips.ToHashSet();
        var pasteResult = f.Service.PasteClips(clipboard, MediaTime.FromSeconds(60));
        Assert.True(pasteResult.Success, pasteResult.Message);
        var pasted = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(c => !existing.Contains(c));
        Assert.True(halves[0].HoldMarkup!.SameAs(pasted.HoldMarkup!));
    }

    [Fact]
    public void Trim_slip_speed_and_move_leave_the_markup_as_it_is()
    {
        var f = WithVideo(out var clip);
        Assert.True(f.Service.AddHold(clip.Id, Hold("#E53935")).Success);
        var markup = clip.HoldMarkup!;

        var trim = f.Service.TrimClip(clip.Id, ClipEdge.Start, MediaTime.FromSeconds(1));
        Assert.True(trim.Success, trim.Message);
        var slip = f.Service.SlipClip(clip.Id, -3);   // earlier source: the trim left a head
        Assert.True(slip.Success, slip.Message);
        var speed = f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(10));
        Assert.True(speed.Success, speed.Message);
        var move = f.Service.MoveClips(new[] { clip.Id }, 30);
        Assert.True(move.Success, move.Message);
        Assert.Same(markup, clip.HoldMarkup);
    }

    [Fact]
    public void The_edit_service_cannot_decode_frames_its_colour_comes_from_the_stored_samples_only()
    {
        // D034 §5: the route colour is recomputed without decoding a frame — the service takes no decoder at all.
        foreach (var ctor in typeof(TimelineEditService).GetConstructors())
            Assert.DoesNotContain(ctor.GetParameters(), p =>
                p.ParameterType.Name.Contains("Decoder") || p.ParameterType.Name.Contains("Playback") || p.ParameterType.Name.Contains("Segmentation"));
    }

    [Fact]
    public void AddHolds_adds_several_holds_in_one_undo_step_and_resolves_the_colour()
    {
        var f = WithVideo(out var clip);
        var holds = new[] { Hold("#E53935", x: 0.2), Hold("#43A047", x: 0.4), Hold("#1E88E5", x: 0.6) };
        Assert.True(f.Service.AddHolds(clip.Id, holds).Success);
        Assert.Equal(3, clip.HoldMarkup!.Holds.Length);
        Assert.Equal("Add Holds", f.UndoRedo.NextUndo!.Description);

        f.UndoRedo.Undo();
        Assert.Null(clip.HoldMarkup);
        f.UndoRedo.Redo();
        Assert.Equal(3, clip.HoldMarkup!.Holds.Length);
    }

    [Fact]
    public void AddHolds_is_a_no_change_when_every_hold_is_invalid_or_a_duplicate()
    {
        var f = WithVideo(out var clip);
        var good = Hold("#E53935");
        var tooFew = new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0, 0), new NormalizedPoint(1, 1)), default, default, MediaTime.Zero);
        var duplicate = good;

        Assert.True(f.Service.AddHolds(clip.Id, new[] { good, tooFew }).Success);
        Assert.Single(clip.HoldMarkup!.Holds);

        Assert.True(f.Service.AddHolds(clip.Id, new[] { duplicate }).NoChange);
        Assert.Single(clip.HoldMarkup.Holds);
    }

    [Fact]
    public void AddHolds_refuses_other_clips_and_locked_tracks()
    {
        var f = WithVideo(out var clip);
        Assert.True(f.Service.AddTextClip(MediaTime.FromSeconds(20)).Success);
        var text = f.V1.Clips.OfType<TextClip>().Single();
        Assert.False(f.Service.AddHolds(text.Id, new[] { Hold("#E53935") }).Success);

        Assert.True(f.Service.SetTrackLocked(f.V1.Id, true).Success);
        var locked = f.Service.AddHolds(clip.Id, new[] { Hold("#E53935") });
        Assert.False(locked.Success);
        Assert.Contains("locked", locked.Message);
    }

    [Fact]
    public void A_markup_change_raises_one_timeline_notification_per_step()
    {
        var f = WithVideo(out var clip);
        var before = f.TimelineChangedCount;
        Assert.True(f.Service.AddHold(clip.Id, Hold("#E53935")).Success);
        Assert.Equal(before + 1, f.TimelineChangedCount);
        f.UndoRedo.Undo();
        Assert.Equal(before + 2, f.TimelineChangedCount);
    }
    // --- Phase 20 (D035): the manual correction of a hold's contour ---------------------------------------------------

    [Fact]
    public void A_contour_correction_keeps_the_hold_its_samples_and_the_colour_and_is_one_undo_step()
    {
        var f = WithVideo(out var clip);
        var hold = Hold("#E53935", sourceTicks: 7_654_321);
        Assert.True(f.Service.AddHold(clip.Id, hold).Success);
        Assert.True(f.Service.AddHold(clip.Id, Hold("#1E88E5", x: 0.2)).Success);
        var (colour, keyline) = (clip.HoldMarkup!.ColorHex, clip.HoldMarkup.KeylineActive);
        var before = clip.HoldMarkup;

        var corrected = hold.Points.SetItem(1, new NormalizedPoint(0.53, 0.47));
        var result = f.Service.ReplaceHoldContour(clip.Id, hold.Id, corrected);
        Assert.True(result.Success, result.Message);
        Assert.Equal("Edit Hold Outline", f.UndoRedo.NextUndo!.Description);

        var edited = clip.HoldMarkup!.Holds[0];
        Assert.Equal(hold.Id, edited.Id);                                           // the same object, not a new hold
        Assert.Equal(corrected, edited.Points);
        Assert.Equal((hold.SurfaceLab, hold.WallLab, hold.SourceTime), (edited.SurfaceLab, edited.WallLab, edited.SourceTime));
        Assert.Equal(2, clip.HoldMarkup.Holds.Length);
        Assert.Equal((colour, keyline), (clip.HoldMarkup.ColorHex, clip.HoldMarkup.KeylineActive));   // never re-sampled
        Assert.Equal(corrected, Core.Playback.HoldOutline.Of(clip.HoldMarkup)!.Polygons[0]);        // what the Preview and the export draw

        f.UndoRedo.Undo();
        Assert.Same(before, clip.HoldMarkup);
        Assert.Equal(hold.Points, clip.HoldMarkup!.Holds[0].Points);
        f.UndoRedo.Redo();
        Assert.Equal(corrected, clip.HoldMarkup!.Holds[0].Points);
        Assert.Equal(hold.Id, clip.HoldMarkup.Holds[0].Id);
    }

    [Fact]
    public void Consecutive_contour_corrections_are_separate_steps()
    {
        var f = WithVideo(out var clip);
        var hold = Hold("#E53935");
        Assert.True(f.Service.AddHold(clip.Id, hold).Success);
        var first = hold.Points.SetItem(1, new NormalizedPoint(0.53, 0.47));
        var second = first.SetItem(2, new NormalizedPoint(0.53, 0.53));
        Assert.True(f.Service.ReplaceHoldContour(clip.Id, hold.Id, first).Success);
        Assert.True(f.Service.ReplaceHoldContour(clip.Id, hold.Id, second).Success);

        f.UndoRedo.Undo();
        Assert.Equal(first, clip.HoldMarkup!.Holds[0].Points);
        f.UndoRedo.Undo();
        Assert.Equal(hold.Points, clip.HoldMarkup!.Holds[0].Points);
        Assert.Equal("Add Hold", f.UndoRedo.NextUndo!.Description);
    }

    [Fact]
    public void A_contour_correction_is_refused_when_invalid_crossing_itself_unchanged_or_on_a_locked_track()
    {
        var f = WithVideo(out var clip);
        var hold = Hold("#E53935");
        Assert.True(f.Service.AddHold(clip.Id, hold).Success);
        var markup = clip.HoldMarkup;

        Assert.False(f.Service.ReplaceHoldContour(clip.Id, hold.Id, hold.Points.RemoveAt(0).RemoveAt(0)).Success);       // two points
        Assert.False(f.Service.ReplaceHoldContour(clip.Id, hold.Id, hold.Points.SetItem(0, new NormalizedPoint(1.2, 0.5))).Success);   // outside
        var bowtie = hold.Points.SetItem(1, new NormalizedPoint(0.49, 0.6));
        var crossing = f.Service.ReplaceHoldContour(clip.Id, hold.Id, bowtie);
        Assert.False(crossing.Success);
        Assert.Equal(Core.Holds.ContourEditing.CrossingMessage, crossing.Message);
        Assert.False(f.Service.ReplaceHoldContour(clip.Id, Guid.NewGuid(), hold.Points).Success);                       // no such hold
        Assert.True(f.Service.ReplaceHoldContour(clip.Id, hold.Id, hold.Points.ToArray().ToImmutableArray()).NoChange);  // same contour: no step
        Assert.Equal("Add Hold", f.UndoRedo.NextUndo!.Description);

        Assert.True(f.Service.SetTrackLocked(f.V1.Id, true).Success);
        var locked = f.Service.ReplaceHoldContour(clip.Id, hold.Id, hold.Points.SetItem(1, new NormalizedPoint(0.53, 0.47)));
        Assert.False(locked.Success);
        Assert.Contains("locked", locked.Message);
        Assert.Same(markup, clip.HoldMarkup);
    }

    [Fact]
    public void Split_copies_the_corrected_contour()
    {
        var f = WithVideo(out var clip);
        var hold = Hold("#E53935");
        Assert.True(f.Service.AddHold(clip.Id, hold).Success);
        var corrected = hold.Points.SetItem(1, new NormalizedPoint(0.53, 0.47));
        Assert.True(f.Service.ReplaceHoldContour(clip.Id, hold.Id, corrected).Success);
        Assert.True(f.Service.Split(MediaTime.FromSeconds(4)).Success);
        Assert.All(f.V1.Clips.OfType<VideoClip>(), c => Assert.Equal(corrected.ToArray(), c.HoldMarkup!.Holds[0].Points.ToArray()));
    }
}
