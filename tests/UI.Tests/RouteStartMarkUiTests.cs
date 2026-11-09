using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D056 §4–§5 on the timeline: a route area — also one made from the FIT, with no holds — shows a thin mark at its computed
/// start <c>T</c>, apart from the orange and the status mark; a free «Боулдер» is green like a route's area but keeps no
/// strip and no mark; the other templates keep their colour. A 120 s clip at 25 fps starting at W0 by its tag; climb 1 FIT
/// [30, 50] s → <c>T</c> = frame 450, the area [275, 1260).
/// </summary>
public sealed class RouteStartMarkUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();

    public RouteStartMarkUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-t-mark", Guid.NewGuid().ToString("N"), "climb.mp4"), Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(120), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080,
                RecordingTimeProbe = RecordingTimeProbe.Found(W0.AddSeconds(120).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"), RecordingTimeTagOrigin.Format)
            }
        };
        _projects.Current.MediaAssets.Add(asset);
        Assert.True(_edit.AddClip(asset.Id).Success);
        _projects.SetTraining(new TrainingReference
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
        Assert.True(_edit.CreateRoutesFromFit().Success);
    }

    private TimelineViewModel Timeline()
    {
        var timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance);
        _projects.NotifyTimelineChanged();
        return timeline;
    }

    private static TimelineClipViewModel Vm(TimelineViewModel timeline, Clip clip) => timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);

    private TemplateClip FitArea => _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Single(t => t.FitClimb is not null);

    [Fact]
    public void A_fit_area_without_holds_shows_its_T_mark()
    {
        var timeline = Timeline();
        var area = Vm(timeline, FitArea);

        Assert.True(area.HasRouteStartMark);
        Assert.Equal(TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(450 - 275, Rate), timeline.PixelsPerSecond), area.RouteStartMarkLeft, 6);
        Assert.Contains(Strings.Timeline_RouteStartMark.Split('{')[0], area.RouteStartMarkTip);
        Assert.False(area.HasStatusMark);                              // not the status mark
        Assert.DoesNotContain(area.TemplateSegments, s => s.Kind == StripSegmentKind.Orange);   // not the orange
    }

    [Fact]
    public void The_mark_follows_a_new_Z_and_hides_when_T_is_outside_the_area()
    {
        var timeline = Timeline();
        var area = Vm(timeline, FitArea);
        Assert.True(_edit.SetRecordingZeroOverride(_projects.Current.MediaAssets[0].Id, W0.AddSeconds(-10)).Success);
        Assert.Equal(TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(700 - 525, Rate), timeline.PixelsPerSecond), area.RouteStartMarkLeft, 6);

        Assert.True(_edit.TrimClip(FitArea.Id, ClipEdge.Start, MediaTime.FromFrame(800, Rate)).Success);   // T before the area now
        Assert.False(area.HasRouteStartMark);
    }

    [Fact]
    public void A_free_boulder_is_green_without_a_strip_or_a_mark_and_other_templates_keep_their_colour()
    {
        Assert.True(_edit.AddTemplateClip(TemplateKind.Boulder, MediaTime.FromSeconds(200)).Success);
        Assert.True(_edit.AddTemplateClip(TemplateKind.Rec, MediaTime.FromSeconds(220)).Success);
        var templates = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().ToList();
        var timeline = Timeline();

        var free = Vm(timeline, templates.Single(t => t.Kind == TemplateKind.Boulder && t.AnchorClipId is null));
        Assert.Equal(TimelineViewModel.GreenHex, free.ColorHex);
        Assert.False(free.HasRouteStartMark);
        Assert.False(free.HasTemplateSegments);
        Assert.Equal("#783A5C", Vm(timeline, templates.Single(t => t.Kind == TemplateKind.Rec)).ColorHex);

        var anchored = Vm(timeline, FitArea);                          // the route area: its strip and its mark
        Assert.True(anchored.HasTemplateSegments);
        Assert.True(anchored.HasRouteStartMark);
    }
}
