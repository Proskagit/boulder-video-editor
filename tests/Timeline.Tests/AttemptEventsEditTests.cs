using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D078 §3: the FIT's route areas refined by the attempt's events, and a manual «Боулдер» anchored to the video clip under it
/// (§2 Q4). At 25 fps, W0 = 10:00:00 UTC: video A (120 s) starts at W0 by its tag; climb 1 is FIT [W0 + 30, W0 + 50] — its area
/// is [275, 1260) with <c>T</c> on frame 450 (18 s) and the status moment on 1135 (FitAreaRelayoutTests).
/// </summary>
public sealed class AttemptEventsEditTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly TimelineFixture _f = new();
    private readonly MediaAsset _asset;
    private readonly VideoClip _clip;

    public AttemptEventsEditTests()
    {
        _asset = _f.Video(120, Rate, "a.mp4");
        _asset.Metadata!.RecordingTimeProbe = RecordingTimeProbe.Found(W0.AddSeconds(120).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            RecordingTimeTagOrigin.Format);
        Assert.True(_f.Service.AddClip(_asset.Id).Success);
        _clip = _f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single();
        _f.Projects.SetTraining(new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 1, Sha256 = new string('a', 64),
            Activity = new TrainingActivity(W0.AddMinutes(-5), W0.AddHours(1), 294, 814),
            Workout = new ClimbingWorkout
            {
                DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = W0.AddMinutes(-5), EndUtc = W0.AddHours(1),
                UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(3900), TimerTime = MediaTime.FromSeconds(3900),
                Sport = 31, SubSport = 69, AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Warnings = [],
                Segments = new[] { new WorkoutSegment(0, WorkoutSegmentKind.Climb, 1, W0.AddSeconds(30), W0.AddSeconds(50), MediaTime.FromSeconds(20), null, null) }
            },
            Availability = TrainingAvailability.Available
        });
        Assert.True(_f.Service.CreateRoutesFromFit().Success);
    }

    private static long F(MediaTime t) => t.ToNearestFrame(Rate);

    private TemplateClip Area => Assert.Single(_f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>(), t => t.FitClimb is not null);

    private static AttemptEventSet Events(double[] lifts, double[] falls) => new(
        lifts.Select(MediaTime.FromSeconds).ToImmutableArray(), falls.Select(MediaTime.FromSeconds).ToImmutableArray());

    private Dictionary<Guid, AttemptEventSet> For(AttemptEventSet events) => new() { [Area.Id] = events };

    [Fact]
    public void The_fall_moves_the_lilac_of_an_attempt_and_grows_the_green_in_one_undo_step()
    {
        var area = Area;
        area.Status = BoulderStatus.Attempt;
        Assert.Equal((275L, 1260L, 1135L), (F(area.TimelineStart), F(area.TimelineEnd), F(area.TimelineStart + area.StatusAt)));
        var steps = _f.UndoRedo.NextUndo;

        // The watch's end 50 s − 3 s = 47 s; the fall at 46.4 s (frame 1160) — within 8 s.
        var result = _f.Service.RefineFitAreasFromVideo(For(Events([], [30.0, 46.4])), fall: true, liftOff: false);

        Assert.True(result.Success && !result.NoChange, result.Message);
        Assert.Equal((275L, 1285L, 1160L), (F(area.TimelineStart), F(area.TimelineEnd), F(area.TimelineStart + area.StatusAt)));
        Assert.Equal(area.StatusAt, area.FitLayout!.StatusAt);                       // the area stays «untouched» for a new Z
        Assert.Equal(MediaTime.FromFrame(1285, Rate), area.FitLayout.SourceEnd);
        Assert.NotSame(steps, _f.UndoRedo.NextUndo);
        _f.UndoRedo.Undo();
        Assert.Equal((275L, 1260L, 1135L), (F(area.TimelineStart), F(area.TimelineEnd), F(area.TimelineStart + area.StatusAt)));
    }

    [Fact]
    public void A_fall_is_not_used_on_a_top_too_far_or_with_the_setting_off()
    {
        var area = Area;
        area.Status = BoulderStatus.Top;
        Assert.True(_f.Service.RefineFitAreasFromVideo(For(Events([], [46.4])), fall: true, liftOff: false).NoChange);   // a top
        area.Status = BoulderStatus.Attempt;
        Assert.True(_f.Service.RefineFitAreasFromVideo(For(Events([], [56.0])), fall: true, liftOff: false).NoChange);   // 9 s away
        Assert.True(_f.Service.RefineFitAreasFromVideo(For(Events([], [46.4])), fall: false, liftOff: false).NoChange);  // setting off
        Assert.Equal(1135L, F(area.TimelineStart + area.StatusAt));
    }

    [Fact]
    public void The_lift_off_within_3_s_moves_T_and_the_green_start_with_it()
    {
        var area = Area;
        Assert.Equal(450L, F(area.RouteStart!.Value));
        Assert.True(_f.Service.RefineFitAreasFromVideo(For(Events([12.0, 22.0], [])), fall: false, liftOff: true).NoChange);   // 6 / 4 s away

        var result = _f.Service.RefineFitAreasFromVideo(For(Events([12.0, 16.4], [])), fall: false, liftOff: true);
        Assert.True(result.Success && !result.NoChange, result.Message);
        Assert.Equal(410L, F(area.RouteStart!.Value));
        Assert.Equal((235L, 1260L, 1135L), (F(area.TimelineStart), F(area.TimelineEnd), F(area.TimelineStart + area.StatusAt)));
        Assert.Equal(MediaTime.FromFrame(235, Rate), area.FitLayout!.SourceStart);
        // Again with the same events: nothing more moves.
        Assert.True(_f.Service.RefineFitAreasFromVideo(For(Events([12.0, 16.4], [])), fall: false, liftOff: true).NoChange);
    }

    [Fact]
    public void An_area_without_a_status_gets_its_lilac_from_the_fall_as_an_attempt()
    {
        // D078 amendment 3 (the owner): an area with no lilac gets one where the climber falls.
        var area = Area;
        Assert.Equal(BoulderStatus.None, area.Status);
        var result = _f.Service.RefineFitAreasFromVideo(For(Events([], [46.4])), fall: true, liftOff: false);
        Assert.True(result.Success && !result.NoChange, result.Message);
        Assert.Equal(BoulderStatus.Attempt, area.Status);
        Assert.Equal((275L, 1285L, 1160L), (F(area.TimelineStart), F(area.TimelineEnd), F(area.TimelineStart + area.StatusAt)));
        _f.UndoRedo.Undo();
        Assert.Equal(BoulderStatus.None, area.Status);
    }

    [Fact]
    public void A_manual_boulder_has_no_window_and_is_never_refined()
    {
        // D078 amendment 4 (the owner): the fall is searched for the FIT's areas only.
        var added = _f.Service.AddTemplateClip(TemplateKind.Boulder, MediaTime.FromSeconds(60));
        Assert.True(added.Success, added.Message);
        Assert.DoesNotContain(FitAreaEventWindows.Of(_f.Project), w => w.TemplateId == added.ClipIds[0]);
        var result = _f.Service.RefineFitAreasFromVideo(new Dictionary<Guid, AttemptEventSet> { [added.ClipIds[0]] = Events([], [62.0, 65.0]) }, fall: true, liftOff: true);
        Assert.True(result.NoChange);
    }

    [Fact]
    public void An_area_changed_by_hand_is_left_as_it_is()
    {
        var area = Area;
        area.Status = BoulderStatus.Attempt;
        Assert.True(_f.Service.TrimClip(area.Id, ClipEdge.End, MediaTime.FromFrame(1200, Rate)).Success);
        Assert.True(_f.Service.RefineFitAreasFromVideo(For(Events([16.4], [46.4])), fall: true, liftOff: true).NoChange);
        Assert.Equal(450L, F(area.RouteStart!.Value));
    }

    [Fact]
    public void An_area_on_a_locked_track_is_left_as_it_is()
    {
        var area = Area;
        area.Status = BoulderStatus.Attempt;
        _f.Project.Timeline.VideoTracks.Single(t => t.Clips.Contains(area)).IsLocked = true;
        Assert.True(_f.Service.RefineFitAreasFromVideo(For(Events([16.4], [46.4])), fall: true, liftOff: true).NoChange);
        Assert.Equal((275L, 1260L, 450L), (F(area.TimelineStart), F(area.TimelineEnd), F(area.RouteStart!.Value)));
    }

    [Fact]
    public void A_moved_status_moment_keeps_its_lilac_and_a_fall_before_T_is_not_taken()
    {
        var area = Area;
        area.Status = BoulderStatus.Attempt;
        area.StatusAt -= MediaTime.FromSeconds(1);                                   // the lilac moved by hand
        Assert.True(_f.Service.RefineFitAreasFromVideo(For(Events([], [46.4])), fall: true, liftOff: false).NoChange);
        area.StatusAt += MediaTime.FromSeconds(1);
        Assert.True(_f.Service.RefineFitAreasFromVideo(For(Events([], [15.0])), fall: true, liftOff: false).NoChange);   // before T (18 s)
    }

    [Fact]
    public void T_with_an_orange_is_the_users_and_is_not_snapped()
    {
        var area = Area;
        var hold = new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.6, 0.4), new NormalizedPoint(0.5, 0.6)),
            default, default, MediaTime.Zero);
        _clip.Routes = ImmutableArray.Create(new HoldMarkup
        {
            Id = area.AnchorRouteId!.Value, Holds = ImmutableArray.Create(hold),
            Animation = new HoldAnimation { Mode = HoldAnimationMode.Duration, StartSourceTime = MediaTime.FromSeconds(15), Duration = MediaTime.FromSeconds(3) }
        });
        Assert.True(_f.Service.RefineFitAreasFromVideo(For(Events([16.4], [])), fall: false, liftOff: true).NoChange);
        Assert.Equal(450L, F(area.RouteStart!.Value));
    }

    [Fact]
    public void The_lift_off_refinement_is_undone_in_one_step()
    {
        var area = Area;
        Assert.True(_f.Service.RefineFitAreasFromVideo(For(Events([16.4], [])), fall: false, liftOff: true).Success);
        Assert.Equal(410L, F(area.RouteStart!.Value));
        _f.UndoRedo.Undo();
        Assert.Equal((275L, 450L), (F(area.TimelineStart), F(area.RouteStart!.Value)));
        Assert.Equal(MediaTime.FromFrame(275, Rate), area.FitLayout!.SourceStart);
    }

    [Fact]
    public void Two_climbs_seconds_apart_get_areas_that_end_where_the_next_begins_on_one_track()
    {
        // D078 amendment 4 (the owner, 27.09's 110133): climbs 10 and 11 were 12 s apart and their areas overlapped by the
        // layout's margins — the second went to a new track.
        var f = new TimelineFixture();
        var asset = f.Video(120, Rate, "b.mp4");
        asset.Metadata!.RecordingTimeProbe = RecordingTimeProbe.Found(W0.AddSeconds(120).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            RecordingTimeTagOrigin.Format);
        Assert.True(f.Service.AddClip(asset.Id).Success);
        f.Projects.SetTraining(new TrainingReference
        {
            FilePath = @"C:it.fit", FileSizeBytes = 1, Sha256 = new string('b', 64),
            Activity = new TrainingActivity(W0.AddMinutes(-5), W0.AddHours(1), 294, 814),
            Workout = new ClimbingWorkout
            {
                DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = W0.AddMinutes(-5), EndUtc = W0.AddHours(1),
                UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(3900), TimerTime = MediaTime.FromSeconds(3900),
                Sport = 31, SubSport = 69, AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Warnings = [],
                Segments = new[]
                {
                    new WorkoutSegment(0, WorkoutSegmentKind.Climb, 1, W0.AddSeconds(30), W0.AddSeconds(48), MediaTime.FromSeconds(18), null, null),
                    new WorkoutSegment(1, WorkoutSegmentKind.Climb, 2, W0.AddSeconds(60), W0.AddSeconds(67), MediaTime.FromSeconds(7), null, null)
                }
            },
            Availability = TrainingAvailability.Available
        });
        Assert.True(f.Service.CreateRoutesFromFit().Success);

        var areas = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().OrderBy(t => t.TimelineStart).ToList();
        Assert.Equal(2, areas.Count);
        Assert.Equal(F(areas[0].TimelineEnd), F(areas[1].TimelineStart));            // the first ends where the second begins
        Assert.Equal(2, f.Project.Timeline.VideoTracks.Count);                        // both on one track above the clip
        Assert.True(areas[0].StatusAt < areas[0].Duration);
        Assert.Equal(areas[0].FitLayout!.SourceEnd, MediaTime.FromFrame(F(areas[0].TimelineEnd), Rate));
    }

    [Fact]
    public void The_first_4K_video_gives_an_empty_project_its_frame_size_in_the_adds_step()
    {
        var f = new TimelineFixture();
        var asset = f.Video(10, Rate, "uhd.mp4");
        asset.Metadata!.Width = 3840;
        asset.Metadata.Height = 2160;
        asset.Metadata.DisplayWidth = 3840;
        asset.Metadata.DisplayHeight = 2160;
        var result = f.Service.AddClip(asset.Id);
        Assert.True(result.Success, result.Message);
        Assert.Equal((3840, 2160), (f.Project.Settings.FrameWidth, f.Project.Settings.FrameHeight));
        f.UndoRedo.Undo();
        Assert.Equal((1920, 1080), (f.Project.Settings.FrameWidth, f.Project.Settings.FrameHeight));
    }

    [Fact]
    public void A_manual_boulder_over_a_video_clip_is_its_route_area()
    {
        var result = _f.Service.AddTemplateClip(TemplateKind.Boulder, MediaTime.FromSeconds(100));
        Assert.True(result.Success, result.Message);
        var template = Assert.Single(_f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>(), t => t.Id == result.ClipIds[0]);
        Assert.Equal(_clip.Id, template.AnchorClipId);
        Assert.NotNull(template.AnchorRouteId);
        Assert.True(F(template.TimelineEnd) <= F(_clip.TimelineEnd));                   // cut to the clip
        Assert.Equal(F(template.Duration) - 125, F(template.StatusAt));                 // the status moment: the last 5 s
        Assert.NotNull(RouteOverlays.RouteIdOf(template, _clip));
    }

    [Fact]
    public void A_manual_boulder_over_no_video_clip_stays_free()
    {
        var result = _f.Service.AddTemplateClip(TemplateKind.Boulder, MediaTime.FromSeconds(130));   // after the 120 s clip
        Assert.True(result.Success, result.Message);
        var template = Assert.Single(_f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>(), t => t.Id == result.ClipIds[0]);
        Assert.Null(template.AnchorClipId);
    }
}
