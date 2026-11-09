using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D056 §2 (Q-A): a new <c>Z</c> lays out again, in its own Undo step, the FIT's area of the same climb on that video whose
/// bounds the user has not changed; an area changed by hand, the other routes of the clip and a climb the clip no longer
/// shows are left alone, and no second area is made. At 25 fps, W0 = 10:00:00 UTC: video A (120 s) starts at W0 by its
/// tag; climb 1 is FIT [W0 + 30, W0 + 50] — by the file's time its area is [275, 1260) (FitAutoBindingTests); a Z 10 s
/// earlier than the file's time moves it by 250 frames.
/// </summary>
public sealed class FitAreaRelayoutTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly TimelineFixture _f = new();
    private readonly MediaAsset _asset;
    private readonly VideoClip _clip;

    public FitAreaRelayoutTests()
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

    private TemplateClip[] Areas() => _f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>()
        .Where(t => t.FitClimb is not null).ToArray();

    private TemplateClip Area => Assert.Single(Areas());

    private (long, long) Bounds(TemplateClip t) => (F(t.TimelineStart), F(t.TimelineEnd));

    private TimelineEditResult SetZ(double secondsBeforeTheFileTime) =>
        _f.Service.SetRecordingZeroOverride(_asset.Id, W0.AddSeconds(-secondsBeforeTheFileTime));

    [Fact]
    public void The_area_records_how_it_was_laid_out()
    {
        Assert.Equal((275L, 1260L), Bounds(Area));
        var layout = Assert.IsType<FitAreaLayout>(Area.FitLayout);
        Assert.Equal(MediaTime.FromFrame(275, Rate), layout.SourceStart);
        Assert.Equal(MediaTime.FromFrame(1260, Rate), layout.SourceEnd);
        Assert.Equal(Area.StatusAt, layout.StatusAt);
    }

    [Fact]
    public void A_new_Z_lays_out_again_the_untouched_area_in_the_Zs_undo_step_and_makes_no_second_one()
    {
        var area = Area;
        var before = area.FitLayout;

        var result = SetZ(10);

        Assert.True(result.Success, result.Message);
        Assert.Same(area, Area);                                                   // the same area, no second one
        Assert.Equal((525L, 1510L), Bounds(area));
        Assert.Equal(new FitAreaLayout(MediaTime.FromFrame(525, Rate), MediaTime.FromFrame(1510, Rate), area.StatusAt), area.FitLayout);
        Assert.Contains(string.Format(Messages.FitRoutes_LaidOutAgain, 1), result.Message);

        _f.UndoRedo.Undo();                                                        // one step: the Z and the area together
        Assert.Null(_asset.RecordingZeroOverrideUtc);
        Assert.Equal((275L, 1260L), Bounds(area));
        Assert.Equal(before, area.FitLayout);
        _f.UndoRedo.Redo();
        Assert.Equal((525L, 1510L), Bounds(area));
        Assert.NotNull(_asset.RecordingZeroOverrideUtc);
        _f.AssertValid();
    }

    [Fact]
    public void Clearing_the_Z_lays_the_area_out_again_by_the_files_time()
    {
        SetZ(10);
        Assert.True(_f.Service.SetRecordingZeroOverride(_asset.Id, null).Success);

        Assert.Equal((275L, 1260L), Bounds(Area));
        Assert.Single(Areas());
    }

    [Fact]
    public void An_area_changed_by_hand_is_never_moved()
    {
        Assert.True(_f.Service.TrimClip(Area.Id, ClipEdge.End, MediaTime.FromFrame(1200, Rate)).Success);
        var bounds = Bounds(Area);

        var result = SetZ(10);

        Assert.Equal(bounds, Bounds(Area));
        Assert.Single(Areas());
        Assert.Contains(string.Format(Messages.FitRoutes_KeptByHand, 1), result.Message);
    }

    [Fact]
    public void The_other_routes_of_the_clip_are_not_touched()
    {
        var route = Guid.NewGuid();
        var hold = new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0.3, 0.3), new NormalizedPoint(0.35, 0.3), new NormalizedPoint(0.32, 0.35)),
            RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero);
        Assert.True(_f.Service.AddHolds(_clip.Id, new[] { hold }, route).Success);
        Assert.True(_f.Service.LayOutIntroAndFinal(_clip.Id, MediaTime.FromFrame(2500, Rate), route).Success);
        var green = _f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Single(t => t.AnchorRouteId == route);
        var greenBounds = Bounds(green);
        var markup = RouteOverlays.Find(_clip, route);

        SetZ(10);

        Assert.Equal(greenBounds, Bounds(green));
        Assert.Same(markup, RouteOverlays.Find(_clip, route));
        Assert.Equal((525L, 1510L), Bounds(Area));
    }

    [Fact]
    public void An_area_of_an_older_file_is_laid_out_again_only_when_it_has_the_rules_bounds()
    {
        Area.FitLayout = null;                                                     // as read from a v16 file
        SetZ(10);
        Assert.Equal((525L, 1510L), Bounds(Area));
        Assert.NotNull(Area.FitLayout);
    }

    [Fact]
    public void An_older_files_area_changed_by_hand_stays()
    {
        Assert.True(_f.Service.TrimClip(Area.Id, ClipEdge.Start, MediaTime.FromFrame(300, Rate)).Success);
        Area.FitLayout = null;
        var bounds = Bounds(Area);

        SetZ(10);

        Assert.Equal(bounds, Bounds(Area));
        Assert.Null(Area.FitLayout);
    }

    [Fact]
    public void An_area_whose_climb_the_clip_no_longer_shows_stays_and_none_is_added()
    {
        var bounds = Bounds(Area);

        Assert.True(SetZ(200).Success);                                            // climb 1 at 230–250 s: past the clip

        Assert.Equal(bounds, Bounds(Area));
        Assert.Single(Areas());
    }

    [Fact]
    public void A_copy_of_the_area_is_not_the_FITs_and_has_no_layout()
    {
        var clipboard = _f.Service.CopyClips(new[] { _clip.Id })!;
        var copiedArea = clipboard.Entries.Select(e => e.Clip).OfType<TemplateClip>().Single();
        Assert.Null(copiedArea.FitClimb);
        Assert.Null(copiedArea.FitLayout);
    }
}
