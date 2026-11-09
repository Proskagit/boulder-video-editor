using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D053 through the edit service, at 25 fps on a 60 s clip (Ve = 1500): several independent routes on one clip (their own
/// holds, animation, «Боулдер»; edits and deletes never cross), the route a playhead is in, the orange kept inside its
/// green (§4), Top / Attempt placed where the status moment gave no lilac (problem 2), the default green of 35 s without a
/// FIT (§7 A) and the green route areas the FIT makes (§7 B).
/// </summary>
public sealed class RouteOverlayTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly TimelineFixture _f = new();

    private static MediaTime Frame(long n) => MediaTime.FromFrame(n, Rate);
    private static long F(MediaTime t) => t.ToNearestFrame(Rate);

    private static ImmutableArray<NormalizedPoint> Square(double x, double y, double r = 0.02) => ImmutableArray.Create(
        new NormalizedPoint(x - r, y - r), new NormalizedPoint(x + r, y - r), new NormalizedPoint(x + r, y + r), new NormalizedPoint(x - r, y + r));

    private static Hold NewHold(double x, double y) =>
        new(Guid.NewGuid(), Square(x, y), RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero);

    private VideoClip Clip60()
    {
        Assert.True(_f.Service.AddClip(_f.Video(60, Rate).Id).Success);
        return (VideoClip)_f.V1.Clips[0];
    }

    private TemplateClip[] Templates(VideoClip clip) => _f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>()
        .Where(t => t.AnchorClipId == clip.Id).OrderBy(t => t.TimelineStart).ToArray();

    private TemplateClip TemplateOf(VideoClip clip, Guid route) => Templates(clip).Single(t => t.AnchorRouteId == route);

    private static (long Start, long End)? Orange(VideoClip clip, Guid route)
    {
        if (RouteOverlays.Find(clip, route)?.Animation is not { Mode: HoldAnimationMode.Duration } a) return null;
        var start = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, a.StartSourceTime, Rate);
        return (start, start + HoldAnimationRule.DurationFrames(a, Rate));
    }

    /// <summary>Two routes on the clip, each marked and laid out: A at T = 250, B at T = 1000.</summary>
    private (VideoClip Clip, Guid A, Guid B) TwoRoutes()
    {
        var clip = Clip60();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Assert.True(_f.Service.AddHolds(clip.Id, new[] { NewHold(0.3, 0.8), NewHold(0.4, 0.5) }, a).Success);
        Assert.True(_f.Service.LayOutIntroAndFinal(clip.Id, Frame(250), a).Success);
        Assert.True(_f.Service.AddHolds(clip.Id, new[] { NewHold(0.6, 0.7), NewHold(0.7, 0.4), NewHold(0.8, 0.2) }, b).Success);
        Assert.True(_f.Service.LayOutIntroAndFinal(clip.Id, Frame(1000), b).Success);
        return (clip, a, b);
    }

    // ---- several routes on one clip -------------------------------------------------------------------------------------

    [Fact]
    public void Two_routes_on_one_clip_keep_their_own_holds_animation_and_template()
    {
        var (clip, a, b) = TwoRoutes();
        Assert.Equal(new[] { a, b }, clip.Routes.Select(r => r.Id));
        Assert.Equal(2, RouteOverlays.Find(clip, a)!.Holds.Length);
        Assert.Equal(3, RouteOverlays.Find(clip, b)!.Holds.Length);
        Assert.Equal((125L, 250L), Orange(clip, a));
        Assert.Equal((875L, 1000L), Orange(clip, b));
        Assert.Equal(2, Templates(clip).Length);
        Assert.Equal((75L, 950L), (F(TemplateOf(clip, a).TimelineStart), F(TemplateOf(clip, a).TimelineEnd)));   // Ps + 35 s
        Assert.Equal((825L, 1500L), (F(TemplateOf(clip, b).TimelineStart), F(TemplateOf(clip, b).TimelineEnd)));  // cut to Ve
        _f.AssertValid();
    }

    [Fact]
    public void Adding_editing_and_deleting_one_route_never_touches_the_other()
    {
        var (clip, a, b) = TwoRoutes();
        var beforeA = RouteOverlays.Find(clip, a)!;
        var plateA = (F(TemplateOf(clip, a).TimelineStart), F(TemplateOf(clip, a).TimelineEnd));

        Assert.True(_f.Service.AddHold(clip.Id, NewHold(0.9, 0.9), b).Success);
        Assert.True(_f.Service.EditOverlayStrip(clip.Id, OverlayStripHandle.OrangeStart, 25, b).Success);
        Assert.True(_f.Service.SetHoldStyle(clip.Id, HoldStyle.Of(RouteOverlays.Find(clip, b)!) with { StrokeWidth1080 = 9 }, b).Success);
        Assert.Same(beforeA, RouteOverlays.Find(clip, a));
        Assert.Equal(plateA, (F(TemplateOf(clip, a).TimelineStart), F(TemplateOf(clip, a).TimelineEnd)));
        Assert.Equal(4, RouteOverlays.Find(clip, b)!.Holds.Length);

        Assert.True(_f.Service.DeleteHoldMarkup(clip.Id, b).Success);                          // Delete of B's orange
        Assert.Null(RouteOverlays.Find(clip, b));
        Assert.Same(beforeA, RouteOverlays.Find(clip, a));
        Assert.Equal(2, Templates(clip).Length);                                                // B keeps its green
        _f.UndoRedo.Undo();
        Assert.Equal(4, RouteOverlays.Find(clip, b)!.Holds.Length);
    }

    [Fact]
    public void Without_a_route_named_an_edit_of_a_clip_with_several_routes_is_refused()
    {
        var (clip, _, _) = TwoRoutes();
        Assert.Equal(Messages.Route_Ambiguous, _f.Service.AddHold(clip.Id, NewHold(0.5, 0.5)).Message);
        Assert.Equal(Messages.Route_Ambiguous, _f.Service.DeleteHoldMarkup(clip.Id).Message);
        Assert.Null(_f.Service.OverlayStripOf(clip.Id));
        Assert.NotNull(_f.Service.OverlayStripOf(clip.Id, clip.Routes[0].Id));
    }

    [Fact]
    public void The_route_at_a_frame_is_the_one_whose_green_holds_it_else_one_without_green_else_a_new_one()
    {
        var (clip, a, b) = TwoRoutes();
        var anchored = Templates(clip);
        Assert.Equal(a, RouteOverlays.RouteAt(clip, anchored, 300, Rate));
        Assert.Equal(b, RouteOverlays.RouteAt(clip, anchored, 1200, Rate));
        Assert.Equal(b, RouteOverlays.RouteAt(clip, anchored, 900, Rate));      // both greens: the latest started
        Assert.Null(RouteOverlays.RouteAt(clip, anchored, 20, Rate));           // before every green: a new route

        var c = Guid.NewGuid();
        Assert.True(_f.Service.AddHold(clip.Id, NewHold(0.5, 0.5), c).Success);   // marked, not laid out
        Assert.Equal(c, RouteOverlays.RouteAt(clip, Templates(clip), 20, Rate));
    }

    [Fact]
    public void A_split_and_a_speed_change_keep_every_route_by_its_own_rules()
    {
        var (clip, a, b) = TwoRoutes();
        Assert.True(_f.Service.Split(Frame(600), TimelineFixture.Ids(clip)).Success);   // between A's orange and B's
        var right = _f.V1.Clips.OfType<VideoClip>().Single(c => c.Id != clip.Id);
        Assert.Equal(new[] { a }, clip.Routes.Select(r => r.Id));       // A's animation starts on the left
        Assert.Equal(new[] { b }, right.Routes.Select(r => r.Id));      // B's on the right
        Assert.Equal(b, TemplateOf(right, b).AnchorRouteId);
        _f.AssertValid();
        _f.UndoRedo.Undo();

        Assert.True(_f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(40)).Success);   // 2×
        Assert.Equal(125, Orange(clip, a)!.Value.End);                 // T scaled with the clip (§3.8), per route
        Assert.Equal(500, Orange(clip, b)!.Value.End);
        foreach (var route in new[] { a, b })
        {
            var t = TemplateOf(clip, route);
            Assert.InRange(Orange(clip, route)!.Value.Start, F(t.TimelineStart), F(t.TimelineEnd));
        }
    }

    // ---- the orange inside its green (§4) ------------------------------------------------------------------------------

    [Fact]
    public void The_greens_edges_cut_the_orange_and_a_green_past_it_deletes_the_markup()
    {
        var (clip, a, _) = TwoRoutes();                                 // A: green [75, 950), orange [125, 250)
        var plate = TemplateOf(clip, a);
        Assert.True(_f.Service.TrimClip(plate.Id, ClipEdge.Start, Frame(200)).Success);       // left edge past As
        Assert.Equal((200L, 250L), Orange(clip, a));
        _f.UndoRedo.Undo();

        Assert.True(_f.Service.SetClipProperties(plate.Id, new ClipPropertyChange
        {
            Template = TemplateProperties.Of(plate)!.Value with { Status = BoulderStatus.None }
        }).Success);
        Assert.True(_f.Service.TrimClip(plate.Id, ClipEdge.End, Frame(200)).Success);         // right edge past Ae
        Assert.Equal((125L, 200L), Orange(clip, a));
        _f.UndoRedo.Undo();

        Assert.True(_f.Service.TrimClip(plate.Id, ClipEdge.Start, Frame(300)).Success);       // past the whole orange
        Assert.Null(RouteOverlays.Find(clip, a));
        _f.UndoRedo.Undo();
        Assert.Equal((125L, 250L), Orange(clip, a));
    }

    [Fact]
    public void Moving_the_plate_carries_an_orange_it_would_leave_and_the_inspector_keeps_the_orange_inside()
    {
        var (clip, a, _) = TwoRoutes();
        var plate = TemplateOf(clip, a);
        Assert.True(_f.Service.MoveClips(TimelineFixture.Ids(plate), 100).Success);          // green [175, 1050): orange went along
        Assert.Equal((225L, 350L), Orange(clip, a));
        _f.UndoRedo.Undo();

        var animation = RouteOverlays.Find(clip, a)!.Animation!;
        var before = _f.Service.SetHoldAnimation(clip.Id, animation with { StartSourceTime = Frame(25) }, a);   // before Ps = 75
        Assert.Equal(Messages.Overlay_OrangeOutsideGreen, before.Message);
        Assert.Equal((125L, 250L), Orange(clip, a));
        Assert.True(_f.Service.SetHoldAnimation(clip.Id, animation with { StartSourceTime = Frame(75) }, a).Success);   // touching
    }

    // ---- problem 2: Top / Attempt where the moment gave no lilac -------------------------------------------------------

    [Fact]
    public void Top_chosen_with_the_status_moment_outside_the_plate_is_placed_like_the_layout_puts_it()
    {
        var (clip, a, _) = TwoRoutes();
        var plate = TemplateOf(clip, a);                                // [75, 950), orange [125, 250)
        plate.StatusAt = MediaTime.FromSeconds(200);                    // a split left it far past the plate's end
        var fields = TemplateProperties.Of(plate)!.Value;
        var result = _f.Service.SetClipProperties(plate.Id, new ClipPropertyChange { Template = fields with { Status = BoulderStatus.Top } });
        Assert.True(result.Success, result.Message);
        Assert.Equal(BoulderStatus.Top, plate.Status);
        Assert.Equal(950 - 125, F(plate.StatusAt) - F(plate.CounterStart) + 75);            // S = Pe − 5 s = 825
        Assert.Equal(new FrameSpan(825, 950), _f.Service.OverlayStripOf(clip.Id, a)!.Lilac);
        _f.UndoRedo.Undo();
        Assert.Equal(BoulderStatus.None, plate.Status);
    }

    // ---- §7: the green with and without a FIT --------------------------------------------------------------------------

    [Fact]
    public void Without_a_fit_the_layout_ends_the_green_35_s_after_its_start_and_keeps_an_end_set_by_hand()
    {
        var (clip, a, _) = TwoRoutes();
        var plate = TemplateOf(clip, a);
        Assert.Equal(75 + 875, F(plate.TimelineEnd));
        Assert.True(_f.Service.TrimClip(plate.Id, ClipEdge.End, Frame(700)).Success);          // moved by hand
        Assert.True(_f.Service.LayOutIntroAndFinal(clip.Id, Frame(260), a).Success);           // laid out again
        Assert.Equal(700, F(TemplateOf(clip, a).TimelineEnd));
    }

    private static WorkoutSegment Climb(int index, double startSeconds, double seconds) =>
        new(index, WorkoutSegmentKind.Climb, index / 2 + 1, W0.AddSeconds(startSeconds), W0.AddSeconds(startSeconds + seconds),
            MediaTime.FromSeconds(seconds), null, null);

    private TrainingReference Training(params WorkoutSegment[] climbs)
    {
        var training = new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 1, Sha256 = new string('a', 64),
            Activity = new TrainingActivity(W0.AddMinutes(-5), W0.AddHours(1), 294, 814),
            Workout = new ClimbingWorkout
            {
                DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = W0.AddMinutes(-5), EndUtc = W0.AddHours(1), UtcOffset = null,
                ElapsedTime = MediaTime.FromSeconds(3900), TimerTime = MediaTime.FromSeconds(3900), Sport = 31, SubSport = 69,
                AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Segments = climbs, Warnings = []
            },
            Availability = TrainingAvailability.Available
        };
        _f.Projects.SetTraining(training);
        return training;
    }

    [Fact]
    public void A_fit_makes_one_green_route_area_per_climb_from_the_layouts_start_to_its_end_plus_5_s()
    {
        var clip = Clip60();
        var asset = _f.Project.MediaAssets.Single();
        var training = Training(Climb(0, 20, 10), Climb(2, 45, 8));
        // The video's recording zero is the training's W0 (no clock correction): the marks are proposed (W_auto = start −
        // 12 s) and, in a step of their own, the green route areas.
        Assert.True(_f.Service.SetRecordingZeroOverride(asset.Id, W0).Success);
        Assert.Equal(2, training.Marks.Count);

        var templates = Templates(clip);
        Assert.Equal(2, templates.Length);
        // A: T = 8 s (200) → [T − 7 s, actual end 30 − 4.6 s + 5 s) = [25, 760); B: T = 33 s (825) → [650, 1335).
        Assert.Equal((25L, 760L), (F(templates[0].TimelineStart), F(templates[0].TimelineEnd)));
        Assert.Equal((650L, 1335L), (F(templates[1].TimelineStart), F(templates[1].TimelineEnd)));
        Assert.All(templates, t => Assert.Equal(BoulderStatus.None, t.Status));          // no lilac
        Assert.Equal(training.Marks.Select(m => m.Climb).ToHashSet(), templates.Select(t => t.FitClimb!.Value).ToHashSet());
        Assert.NotEqual(templates[0].AnchorRouteId, templates[1].AnchorRouteId);           // independent routes
        Assert.Empty(clip.Routes);                                                         // no holds, no orange

        // A new import of the same FIT never adds a second area for a climb.
        var again = _f.Service.CreateRoutesFromFit();
        Assert.True(again.NoChange);
        Assert.Equal(Messages.FitRoutes_None, again.Message);

        // The areas are routes like any other: their bounds are moved by hand, holds marked into them.
        var routeA = templates[0].AnchorRouteId!.Value;
        Assert.True(_f.Service.TrimClip(templates[0].Id, ClipEdge.End, Frame(700)).Success);
        Assert.Equal(700, F(templates[0].TimelineEnd));
        Assert.Equal((650L, 1335L), (F(templates[1].TimelineStart), F(templates[1].TimelineEnd)));
        Assert.True(_f.Service.AddHold(clip.Id, NewHold(0.5, 0.5), routeA).Success);
        Assert.Equal(routeA, Assert.Single(clip.Routes).Id);

        // One Undo step removes the areas (the marks stay, their own step).
        while (Templates(clip).Length > 0 && _f.UndoRedo.CanUndo) _f.UndoRedo.Undo();
        Assert.Equal(2, training.Marks.Count);
    }

    [Fact]
    public void A_climb_whose_start_no_clip_shows_gets_no_area()
    {
        var clip = Clip60();
        Assert.True(_f.Service.TrimClip(clip.Id, ClipEdge.End, Frame(500)).Success);      // the clip shows 0–20 s only
        var asset = _f.Project.MediaAssets.Single();
        var training = Training(Climb(0, 20, 10), Climb(2, 45, 8));                       // B's start (33 s) is cut away
        Assert.True(_f.Service.SetRecordingZeroOverride(asset.Id, W0).Success);
        Assert.Equal(2, training.Marks.Count);
        var template = Assert.Single(Templates(clip));
        Assert.Equal((25L, 500L), (F(template.TimelineStart), F(template.TimelineEnd)));  // A's end cut to the clip
    }
}
