using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D056 §4: every route area keeps its computed start <c>T</c> as a source time of its clip — a FIT area the <c>T</c> it was
/// laid out with (again by a new <c>Z</c>), a layout by hand its <c>T</c>; never moved by a trim of the area; an older area
/// shows its route's Duration animation's end. At 25 fps a 120 s clip starting at W0 by its tag; climb 1 FIT [30, 50] s:
/// <c>T</c> = 18 s (frame 450), its area [275, 1260).
/// </summary>
public sealed class RouteStartTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly TimelineFixture _f = new();
    private readonly MediaAsset _asset;
    private readonly VideoClip _clip;

    public RouteStartTests()
    {
        _asset = _f.Video(120, Rate, "a.mp4");
        _asset.Metadata!.RecordingTimeProbe = RecordingTimeProbe.Found(W0.AddSeconds(120).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            RecordingTimeTagOrigin.Format);
        Assert.True(_f.Service.AddClip(_asset.Id).Success);
        _clip = _f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single();
    }

    private void Training() => _f.Projects.SetTraining(new TrainingReference
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

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private TemplateClip[] Greens() => _f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().ToArray();

    private static Hold NewHold() => new(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0.3, 0.3), new NormalizedPoint(0.35, 0.3),
        new NormalizedPoint(0.32, 0.35)), RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero);

    [Fact]
    public void A_fit_area_keeps_its_T_a_new_Z_moves_it_with_the_area_and_Undo_restores_it()
    {
        Training();
        Assert.True(_f.Service.CreateRoutesFromFit().Success);
        var area = Assert.Single(Greens());
        Assert.Equal(F(450), area.RouteStart);
        Assert.Equal(F(450), RouteOverlays.RouteStartOf(area, _clip, Rate));

        Assert.True(_f.Service.SetRecordingZeroOverride(_asset.Id, W0.AddSeconds(-10)).Success);
        Assert.Equal(F(700), area.RouteStart);
        _f.UndoRedo.Undo();
        Assert.Equal(F(450), area.RouteStart);
    }

    [Fact]
    public void A_trim_of_the_area_never_moves_T()
    {
        Training();
        Assert.True(_f.Service.CreateRoutesFromFit().Success);
        var area = Assert.Single(Greens());
        Assert.True(_f.Service.TrimClip(area.Id, ClipEdge.Start, F(400)).Success);
        Assert.Equal(F(450), area.RouteStart);
    }

    [Fact]
    public void A_layout_by_hand_stores_its_T_on_the_green_in_its_undo_step()
    {
        var route = Guid.NewGuid();
        Assert.True(_f.Service.AddHolds(_clip.Id, new[] { NewHold() }, route).Success);
        Assert.True(_f.Service.LayOutIntroAndFinal(_clip.Id, F(1000), route).Success);
        var green = Assert.Single(Greens());
        Assert.Equal(F(1000), green.RouteStart);

        Assert.True(_f.Service.LayOutIntroAndFinal(_clip.Id, F(1100), route).Success);   // laid out again: the same green
        Assert.Equal(F(1100), green.RouteStart);
        _f.UndoRedo.Undo();
        Assert.Equal(F(1000), green.RouteStart);
    }

    [Fact]
    public void A_layout_on_a_fit_route_moves_its_T_to_the_layouts()
    {
        Training();
        Assert.True(_f.Service.CreateRoutesFromFit().Success);
        var area = Assert.Single(Greens());
        Assert.True(_f.Service.AddHolds(_clip.Id, new[] { NewHold() }, area.AnchorRouteId).Success);
        Assert.True(_f.Service.LayOutIntroAndFinal(_clip.Id, F(500), area.AnchorRouteId).Success);
        Assert.Equal(F(500), area.RouteStart);
    }

    [Fact]
    public void An_older_area_without_T_shows_the_end_of_its_routes_animation()
    {
        var route = Guid.NewGuid();
        Assert.True(_f.Service.AddHolds(_clip.Id, new[] { NewHold() }, route).Success);
        Assert.True(_f.Service.LayOutIntroAndFinal(_clip.Id, F(1000), route).Success);
        var green = Assert.Single(Greens());
        green.RouteStart = null;                                  // as read from a v17 file

        Assert.Equal(F(1000), RouteOverlays.RouteStartOf(green, _clip, Rate));
    }

    [Fact]
    public void A_free_template_has_no_T()
    {
        Assert.True(_f.Service.AddTemplateClip(TemplateKind.Boulder, MediaTime.FromSeconds(130)).Success);
        var free = Greens().Single();
        Assert.Null(free.AnchorClipId);
        Assert.Null(free.RouteStart);
    }
}
