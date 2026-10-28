using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D048 stage 3 (§4.2, §3.9) in the timeline view model with the real edit service, on a 20 s, 25 fps clip laid out at
/// <c>T</c> = 250 with Top (green [75, 375), orange [125, 250), lilac [375, 500)): which handle a press on the strip takes,
/// every handle's drag — the preview from the service (nothing applied while dragging), whole frames, the limits, the
/// refused overlap shown and released with nothing changed, one Undo step and its Redo, a return to the start or Esc with
/// no step, a release far outside the segment, no merge with an earlier Inspector edit, the snap; the orange's selection
/// and Delete; the ordinary move and trim unchanged.
/// </summary>
public sealed class OverlayStripGestureUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private int _steps;

    public OverlayStripGestureUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private (TimelineViewModel Timeline, VideoClip Clip, TemplateClip Template) LaidOut()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "strip-ui", Guid.NewGuid().ToString("N"), "climb.mp4"), Kind = MediaKind.Video,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(20), FrameRate = Rate, Width = 1920, Height = 1080 },
            AnalysisStatus = MediaAnalysisStatus.Completed
        };
        _projects.Current.MediaAssets.Add(asset);
        Assert.True(_edit.AddClip(asset.Id).Success);
        var clip = (VideoClip)_projects.Current.Timeline.VideoTracks[0].Clips[0];
        foreach (var y in new[] { 0.3, 0.6, 0.8 })
            Assert.True(_edit.AddHold(clip.Id, new Hold(Guid.NewGuid(), Square(0.5, y), RouteColor.HexToOkLab("#E53935"),
                RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero)).Success);
        Assert.True(_edit.LayOutIntroAndFinal(clip.Id, F(250)).Success);
        var template = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Single();
        Assert.True(_edit.SetClipProperties(template.Id,
            new ClipPropertyChange { Template = TemplateProperties.Of(template)!.Value with { Status = BoulderStatus.Top } }).Success);
        // The lane (the Debug alternative of D048 variant B): these tests drive the lane's handles; variant B below.
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance) { SnappingEnabled = false, SegmentsInTemplate = false };
        _undo.StateChanged += (_, _) => _steps++;
        _steps = 0;
        return (timeline, clip, template);
    }

    private static ImmutableArray<NormalizedPoint> Square(double x, double y, double r = 0.02) => ImmutableArray.Create(
        new NormalizedPoint(x - r, y - r), new NormalizedPoint(x + r, y - r), new NormalizedPoint(x + r, y + r), new NormalizedPoint(x - r, y + r));

    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static double X(TimelineViewModel t, double frames) => TimelineCoordinateMapper.TimeToX(MediaTime.FromSeconds(frames / 25.0), t.PixelsPerSecond);
    private static TimelineClipViewModel Vm(TimelineViewModel t, Clip c) => t.Tracks.SelectMany(x => x.Clips).Single(v => v.Id == c.Id);
    private static TimelineTrackViewModel RowOf(TimelineViewModel t, TimelineClipViewModel vm) => t.Tracks.Single(r => r.Clips.Contains(vm));

    /// <summary>The model as the composition reads it: Ps, S, Pe of the template, As, Ae of the animation.</summary>
    private static (long Ps, long S, long Pe, long As, long Ae) Model(VideoClip clip, TemplateClip t)
    {
        long ps = t.TimelineStart.ToNearestFrame(Rate);
        var a = clip.HoldMarkup!.Animation!;
        var start = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, a.StartSourceTime, Rate);
        return (ps, ps + t.StatusAt.ToNearestFrame(Rate) - t.CounterStart.ToNearestFrame(Rate), t.TimelineEnd.ToNearestFrame(Rate),
            start, start + HoldAnimationRule.DurationFrames(a, Rate));
    }

    private static readonly (long, long, long, long, long) Start = (75, 375, 500, 125, 250);

    /// <summary>A drag of the handle at frame <paramref name="atFrame"/> by <paramref name="byFrames"/> (pixels), released.</summary>
    private static void Drag(TimelineViewModel t, VideoClip clip, OverlayStripHandle handle, double atFrame, double byFrames)
    {
        var vm = Vm(t, clip);
        Assert.Equal(handle, t.StripHandleAt(vm, X(t, atFrame)));
        Assert.True(t.BeginStripDrag(vm, handle, X(t, atFrame)));
        t.UpdateGesture(X(t, atFrame + byFrames), null);
        t.EndGesture();
    }

    [Fact]
    public void A_press_takes_the_nearest_edge_the_green_start_first_and_the_orange_or_lilac_body_elsewhere()
    {
        var (t, clip, _) = LaidOut();
        var vm = Vm(t, clip);
        Assert.Equal(new[] { StripSegmentKind.Green, StripSegmentKind.Orange, StripSegmentKind.Lilac }, vm.StripSegments.Select(s => s.Kind));
        Assert.Equal(OverlayStripHandle.GreenStart, t.StripHandleAt(vm, X(t, 75)));
        Assert.Equal(OverlayStripHandle.OrangeStart, t.StripHandleAt(vm, X(t, 125)));
        Assert.Equal(OverlayStripHandle.OrangeEnd, t.StripHandleAt(vm, X(t, 250)));
        Assert.Equal(OverlayStripHandle.OrangeBody, t.StripHandleAt(vm, X(t, 190)));
        Assert.Equal(OverlayStripHandle.Status, t.StripHandleAt(vm, X(t, 375)));
        Assert.Equal(OverlayStripHandle.LilacBody, t.StripHandleAt(vm, X(t, 440)));
        Assert.Equal(OverlayStripHandle.LilacEnd, t.StripHandleAt(vm, X(t, 500)));
        Assert.Null(t.StripHandleAt(vm, X(t, 320)));                                     // the green's body: not movable

        // A shared left boundary (after the orange's start is dragged onto Ps): the green's start is taken first.
        Drag(t, clip, OverlayStripHandle.OrangeStart, 125, -50);
        Assert.Equal(OverlayStripHandle.GreenStart, Assert.IsType<OverlayStripHandle>(t.StripHandleAt(Vm(t, clip), X(t, 75))));
    }

    [Theory]
    [InlineData(OverlayStripHandle.GreenStart, 75, -40, 35, 375, 500, 125, 250)]
    [InlineData(OverlayStripHandle.Status, 375, 30, 75, 405, 500, 125, 250)]
    [InlineData(OverlayStripHandle.LilacEnd, 500, -60, 75, 375, 440, 125, 250)]
    [InlineData(OverlayStripHandle.LilacBody, 440, -20, 75, 355, 480, 125, 250)]
    [InlineData(OverlayStripHandle.OrangeStart, 125, -25, 75, 375, 500, 100, 250)]
    [InlineData(OverlayStripHandle.OrangeEnd, 250, 50, 75, 375, 500, 125, 300)]
    [InlineData(OverlayStripHandle.OrangeBody, 190, 60, 75, 375, 500, 185, 310)]
    public void Every_handle_drags_previews_without_applying_and_releases_in_one_undo_step(OverlayStripHandle handle, double at,
        double by, long ps, long s, long pe, long @as, long ae)
    {
        var (t, clip, template) = LaidOut();
        var vm = Vm(t, clip);
        Assert.True(t.BeginStripDrag(vm, handle, X(t, at)));
        t.UpdateGesture(X(t, at + by), null);
        Assert.True(t.IsGestureActive);
        Assert.Equal(Start, Model(clip, template));                                     // the preview applies nothing
        Assert.Equal(0, _steps);
        Assert.NotNull(vm.StripText);
        Assert.False(vm.IsStripInvalid);

        t.EndGesture();
        Assert.Equal((ps, s, pe, @as, ae), Model(clip, template));
        Assert.Equal(1, _steps);
        Assert.Null(vm.StripText);
        _undo.Undo();
        Assert.Equal(Start, Model(clip, template));
        _undo.Redo();
        Assert.Equal((ps, s, pe, @as, ae), Model(clip, template));
        Assert.Equal((0L, 500L), (clip.TimelineStart.ToNearestFrame(Rate), clip.TimelineEnd.ToNearestFrame(Rate)));
    }

    [Fact]
    public void The_drag_moves_in_whole_frames_and_the_strip_follows_it()
    {
        var (t, clip, template) = LaidOut();
        var vm = Vm(t, clip);
        Assert.True(t.BeginStripDrag(vm, OverlayStripHandle.OrangeEnd, X(t, 250)));
        t.UpdateGesture(X(t, 250 + 10.4), null);                                        // 10.4 frames → 10
        var orange = vm.StripSegments.Single(s => s.Kind == StripSegmentKind.Orange);
        Assert.Equal((125L, 260L), (orange.Start, orange.End));
        Assert.Equal(X(t, 135), orange.Width, 6);
        t.UpdateGesture(X(t, 250 + 10.6), null);                                        // 10.6 frames → 11
        t.EndGesture();
        Assert.Equal((75L, 375L, 500L, 125L, 261L), Model(clip, template));
    }

    [Fact]
    public void The_limits_stop_the_value_and_a_release_far_outside_the_segment_still_applies_once()
    {
        var (t, clip, template) = LaidOut();
        Drag(t, clip, OverlayStripHandle.GreenStart, 75, -5000);                       // far left of the clip: stops at Vs
        Assert.Equal((0L, 375L, 500L, 125L, 250L), Model(clip, template));
        Drag(t, clip, OverlayStripHandle.LilacEnd, 500, -5000);                        // stops at S + 2 (§1.25)
        Assert.Equal((0L, 375L, 377L, 125L, 250L), Model(clip, template));
        Assert.Equal(2, _steps);
    }

    [Fact]
    public void An_overlap_of_the_lilac_and_the_orange_is_shown_invalid_and_its_release_changes_nothing()
    {
        var (t, clip, template) = LaidOut();
        var vm = Vm(t, clip);
        Assert.True(t.BeginStripDrag(vm, OverlayStripHandle.Status, X(t, 375)));
        t.UpdateGesture(X(t, 200), null);                                               // S into the orange
        Assert.True(vm.IsStripInvalid);
        Assert.Contains(Messages.Overlay_LilacOverlap, vm.StripText);
        t.EndGesture();
        Assert.Equal(Start, Model(clip, template));
        Assert.Equal(0, _steps);
        Assert.Equal(Messages.Overlay_LilacOverlap, _status.Message);
        Assert.False(Vm(t, clip).IsStripInvalid);
        Assert.Equal(375, Vm(t, clip).StripSegments.Single(s => s.Kind == StripSegmentKind.Lilac).Start);   // the strip back
    }

    [Fact]
    public void Back_to_the_start_Esc_and_a_press_without_a_drag_leave_no_step()
    {
        var (t, clip, template) = LaidOut();
        var vm = Vm(t, clip);
        Assert.True(t.BeginStripDrag(vm, OverlayStripHandle.OrangeBody, X(t, 190)));
        t.UpdateGesture(X(t, 230), null);
        t.UpdateGesture(X(t, 190), null);                                               // back where it started
        t.EndGesture();

        Assert.True(t.BeginStripDrag(vm, OverlayStripHandle.LilacBody, X(t, 440)));
        t.UpdateGesture(X(t, 470), null);
        t.CancelGesture();                                                              // Esc / a lost pointer
        Assert.Equal(375, Vm(t, clip).StripSegments.Single(s => s.Kind == StripSegmentKind.Lilac).Start);

        Assert.True(t.BeginStripDrag(vm, OverlayStripHandle.Status, X(t, 375)));
        t.UpdateGesture(X(t, 375) + 1, RowOf(t, vm));                                   // under the drag threshold
        Assert.False(t.IsGestureActive);
        t.EndGesture();

        Assert.Equal(Start, Model(clip, template));
        Assert.Equal(0, _steps);
    }

    [Fact]
    public void A_status_drag_after_an_Inspector_edit_of_the_template_is_its_own_step()
    {
        var (t, clip, template) = LaidOut();
        Assert.True(_edit.SetClipProperties(template.Id,
            new ClipPropertyChange { Template = TemplateProperties.Of(template)!.Value with { Status = BoulderStatus.Attempt } }).Success);
        Drag(t, clip, OverlayStripHandle.Status, 375, 25);
        Assert.Equal(400, Model(clip, template).S);

        _undo.Undo();                                                                   // the drag only
        Assert.Equal(375, Model(clip, template).S);
        Assert.Equal(BoulderStatus.Attempt, template.Status);
        _undo.Redo();
        Assert.Equal(400, Model(clip, template).S);
    }

    [Fact]
    public void The_boundary_snaps_to_the_timelines_targets_when_snapping_is_on()
    {
        var (t, clip, template) = LaidOut();
        t.SnappingEnabled = true;
        Drag(t, clip, OverlayStripHandle.OrangeStart, 125, -123);                      // to frame 2: snaps to the clip's start
        Assert.Equal(0, Model(clip, template).As);
    }

    [Fact]
    public void A_click_on_the_orange_selects_it_and_Delete_removes_the_markup_in_one_undo_step()
    {
        var (t, clip, template) = LaidOut();
        var vm = Vm(t, clip);
        t.OnStripClicked(vm, OverlayStripHandle.OrangeBody);
        Assert.Equal(clip.Id, t.SelectedOrangeClipId);
        Assert.True(Vm(t, clip).StripSegments.Single(s => s.Kind == StripSegmentKind.Orange).IsSelected);
        Assert.True(t.DeleteSelectedCommand.CanExecute(null));

        t.DeleteSelectedCommand.Execute(null);
        Assert.Null(clip.HoldMarkup);                                                  // the holds and the animation
        Assert.Contains(clip, _projects.Current.Timeline.VideoTracks[0].Clips);       // never the clip
        Assert.Contains(template, _projects.Current.Timeline.VideoTracks.SelectMany(tr => tr.Clips)); // nor the template
        Assert.Null(t.SelectedOrangeClipId);
        Assert.DoesNotContain(Vm(t, clip).StripSegments, s => s.Kind == StripSegmentKind.Orange);
        Assert.Equal(1, _steps);

        _undo.Undo();
        Assert.Equal(3, clip.HoldMarkup!.Holds.Length);
        Assert.Equal(Start, Model(clip, template));

        t.OnStripClicked(Vm(t, clip), null);                                           // a click on the green: no selection
        Assert.Null(t.SelectedOrangeClipId);
    }

    // --- Variant B: the segments inside the «Боулдер» (Ps = 75, Pe = 500: orange [50, 175), lilac [300, 425) on the plate) --------

    private (TimelineViewModel T, VideoClip Clip, TemplateClip Template) VariantB(BoulderStatus status = BoulderStatus.Top)
    {
        var (t, clip, template) = LaidOut();
        if (status != BoulderStatus.Top)
            Assert.True(_edit.SetClipProperties(template.Id,
                new ClipPropertyChange { Template = TemplateProperties.Of(template)!.Value with { Status = status } }).Success);
        t.SegmentsInTemplate = true;
        _steps = 0;
        return (t, clip, template);
    }

    [Fact]
    public void Variant_B_draws_the_same_strip_inside_the_Boulder_and_switches_back_to_the_lane()
    {
        Assert.True(new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance).SegmentsInTemplate);  // variant B by default
        var (t, clip, template) = LaidOut();
        Assert.True(Vm(t, clip).ShowsStripLane);
        Assert.Empty(Vm(t, template).TemplateSegments);                                 // the lane when switched off

        t.SegmentsInTemplate = true;
        Assert.False(Vm(t, clip).ShowsStripLane);
        var plate = Vm(t, template);
        Assert.Same(Vm(t, clip), plate.StripOwner);
        Assert.Equal(new[] { StripSegmentKind.Green, StripSegmentKind.Orange, StripSegmentKind.Lilac }, plate.TemplateSegments.Select(s => s.Kind));
        Assert.Equal(new[] { X(t, 0), X(t, 50), X(t, 300) }, plate.TemplateSegments.Select(s => s.Left));   // from Ps = 75
        Assert.Equal(new[] { X(t, 300), X(t, 125), X(t, 125) }, plate.TemplateSegments.Select(s => s.Width));
        Assert.Empty(plate.TemplateOverflow);

        t.SegmentsInTemplate = false;
        Assert.Empty(Vm(t, template).TemplateSegments);
        Assert.True(Vm(t, clip).ShowsStripLane);
    }

    [Fact]
    public void Variant_B_hit_testing_gives_the_plates_edges_the_inner_boundaries_and_the_orange_to_the_strip_the_rest_to_the_template()
    {
        var (t, clip, template) = VariantB();
        var plate = Vm(t, template);
        var owner = Vm(t, clip);
        OverlayStripHandle? At(double frames, bool alt = false) => t.TemplatePressAt(plate, X(t, frames), alt)?.Handle;

        Assert.Equal(OverlayStripHandle.GreenStart, At(0));                             // the plate's left edge: Ps, S kept (§4.2)
        Assert.Equal(OverlayStripHandle.LilacEnd, At(425));                             // its right edge: Pe
        Assert.Equal(OverlayStripHandle.OrangeStart, At(50));
        Assert.Equal(OverlayStripHandle.OrangeEnd, At(174.5));                          // just inside the orange
        Assert.Equal(OverlayStripHandle.Status, At(300));
        Assert.Equal(OverlayStripHandle.OrangeBody, At(110));                           // the orange: never the template
        Assert.Null(At(20));                                                            // the green's body: the template's move
        Assert.Null(At(360));                                                           // the lilac's body: the template's move…
        Assert.Equal(OverlayStripHandle.LilacBody, At(360, alt: true));                 // …with Alt the lilac's (§4.2)
        Assert.Same(owner, t.TemplatePressAt(plate, X(t, 110))!.Value.Owner);

        var (u, _, other) = VariantB(BoulderStatus.None);                               // no status: the right edge is the template's trim
        Assert.Null(u.TemplatePressAt(Vm(u, other), X(u, 425)));
    }

    [Fact]
    public void Variant_B_the_plates_edges_trim_the_template_by_the_strips_rules_in_one_step()
    {
        var (t, clip, template) = VariantB();
        var plate = Vm(t, template);
        Assert.True(t.BeginStripDrag(Vm(t, clip), OverlayStripHandle.GreenStart, X(t, 75), pressedOn: plate));
        t.UpdateGesture(X(t, 35), RowOf(t, plate));
        Assert.NotNull(Vm(t, template).StripText);                                      // the interval on the plate
        Assert.Null(Vm(t, clip).StripText);
        t.EndGesture();
        Assert.Equal((35L, 375L, 500L, 125L, 250L), Model(clip, template));              // Ps only; S and Pe kept
        Assert.Equal(1, _steps);

        Assert.True(t.BeginStripDrag(Vm(t, clip), OverlayStripHandle.LilacEnd, X(t, 500), pressedOn: Vm(t, template)));
        t.UpdateGesture(X(t, 0), RowOf(t, Vm(t, template)));                            // far left: stops at S + 2 (§1.25)
        t.EndGesture();
        Assert.Equal((35L, 375L, 377L, 125L, 250L), Model(clip, template));
        _undo.Undo();
        _undo.Undo();
        Assert.Equal(Start, Model(clip, template));
    }

    [Fact]
    public void Variant_B_a_click_on_the_orange_selects_it_alone_and_Delete_removes_the_markup_not_the_template()
    {
        var (t, clip, template) = VariantB();
        t.OnClipPressed(Vm(t, template), toggle: false);                                // the template selected first
        Assert.True(t.HasSelection);

        t.OnTemplateStripClicked(Vm(t, template), Vm(t, clip), OverlayStripHandle.OrangeBody);
        Assert.False(t.HasSelection);                                                   // the clips' selection cleared
        Assert.Equal(clip.Id, t.SelectedOrangeClipId);
        Assert.True(Vm(t, template).TemplateSegments.Single(s => s.Kind == StripSegmentKind.Orange).IsSelected);

        t.DeleteSelectedCommand.Execute(null);
        Assert.Null(clip.HoldMarkup);
        Assert.Contains(template, _projects.Current.Timeline.VideoTracks.SelectMany(tr => tr.Clips));
        Assert.DoesNotContain(Vm(t, template).TemplateSegments, s => s.Kind == StripSegmentKind.Orange);
        Assert.Equal(1, _steps);
        _undo.Undo();
        Assert.Equal(Start, Model(clip, template));

        t.OnTemplateStripClicked(Vm(t, template), Vm(t, clip), OverlayStripHandle.Status);   // a click on an edge: the template
        Assert.Null(t.SelectedOrangeClipId);
        Assert.True(t.HasSelection);
    }

    [Fact]
    public void Variant_B_dragging_the_orange_moves_the_animation_never_the_template_and_its_part_before_the_plate_stays_visible()
    {
        var (t, clip, template) = VariantB();
        var plate = Vm(t, template);
        Assert.True(t.BeginStripDrag(Vm(t, clip), OverlayStripHandle.OrangeBody, X(t, 185), pressedOn: plate));
        t.UpdateGesture(X(t, 125), RowOf(t, plate));
        t.EndGesture();
        Assert.Equal((75L, 375L, 500L, 65L, 190L), Model(clip, template));               // the template where it was
        Assert.Equal(1, _steps);

        var overflow = Assert.Single(Vm(t, template).TemplateOverflow);                 // [65, 75) beside the plate
        Assert.Equal(StripSegmentKind.Orange, overflow.Kind);
        Assert.Equal((X(t, -10), X(t, 10)), (overflow.Left, overflow.Width));
        Assert.Equal(0, Vm(t, template).TemplateSegments.Single(s => s.Kind == StripSegmentKind.Orange).Left);
        Assert.Equal(OverlayStripHandle.OrangeStart, t.TemplatePressAt(Vm(t, template), X(t, -10))?.Handle);   // its start, beside the plate
        Assert.Equal(OverlayStripHandle.OrangeBody, t.TemplatePressAt(Vm(t, template), X(t, 40))?.Handle);
    }

    [Fact]
    public void Variant_B_the_plates_body_still_moves_and_the_right_edge_without_a_status_still_trims_the_template()
    {
        var (t, clip, template) = VariantB();
        var plate = Vm(t, template);
        Assert.Null(t.TemplatePressAt(plate, X(t, 20)));
        Assert.True(t.OnClipPressed(plate, toggle: false));
        t.BeginMove(X(t, 95));
        t.UpdateGesture(X(t, 85), RowOf(t, plate));
        t.EndGesture();
        Assert.Equal((65L, 365L, 490L, 125L, 250L), Model(clip, template));              // §3.5: the whole template, inside V

        var (u, other, boulder) = VariantB(BoulderStatus.None);
        var otherPlate = Vm(u, boulder);
        u.BeginTrim(otherPlate, ClipEdge.End, X(u, 500));
        u.UpdateGesture(X(u, 450), RowOf(u, otherPlate));
        u.EndGesture();
        Assert.Equal(450, boulder.TimelineEnd.ToNearestFrame(Rate));
        Assert.Equal(250, other.HoldMarkup!.Animation is { } a
            ? HoldAnimationRule.StartFrame(other.TimelineStart, other.SourceIn, other.Speed, a.StartSourceTime, Rate) + HoldAnimationRule.DurationFrames(a, Rate)
            : -1);
    }

    [Fact]
    public void Variant_B_a_refused_overlap_is_shown_on_the_plate_and_changes_nothing()
    {
        var (t, clip, template) = VariantB();
        var plate = Vm(t, template);
        Assert.True(t.BeginStripDrag(Vm(t, clip), OverlayStripHandle.Status, X(t, 375), pressedOn: plate));
        t.UpdateGesture(X(t, 200), RowOf(t, plate));
        Assert.True(Vm(t, template).IsStripInvalid);
        Assert.Contains(Messages.Overlay_LilacOverlap, Vm(t, template).StripText);
        t.EndGesture();
        Assert.Equal(Start, Model(clip, template));
        Assert.Equal(0, _steps);
        Assert.Null(Vm(t, template).StripText);
    }
    [Fact]
    public void The_ordinary_move_and_trim_of_the_clip_still_work_and_carry_the_strip()
    {
        var (t, clip, template) = LaidOut();
        var vm = Vm(t, clip);
        Assert.True(t.OnClipPressed(vm, toggle: false));
        t.BeginMove(X(t, 300));
        t.UpdateGesture(X(t, 350), RowOf(t, vm));
        t.EndGesture();
        Assert.Equal(50, clip.TimelineStart.ToNearestFrame(Rate));
        Assert.Equal((125L, 425L, 550L, 175L, 300L), Model(clip, template));            // the overlays followed (§3.5)
        Assert.Equal(X(t, 125), Vm(t, clip).StripSegments.Single(s => s.Kind == StripSegmentKind.Orange).Left, 6);

        t.BeginTrim(Vm(t, clip), ClipEdge.End, X(t, 550));
        t.UpdateGesture(X(t, 540), null);
        t.EndGesture();
        Assert.Equal(540, clip.TimelineEnd.ToNearestFrame(Rate));
    }
}
