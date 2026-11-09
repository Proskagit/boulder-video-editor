using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D056 §3 (Q-B): outside every green near the FIT's routes the «Зацепки» section offers the route to mark the holds on — the
/// nearest FIT route within 30 s by default, another one or «Новая трасса» by choice; nothing is merged without it; inside a
/// green and on a clip without FIT routes D053 is unchanged. A 120 s clip at 25 fps starting at W0 by its tag; climb 1
/// FIT [30, 50] s → its area [275, 1260); climb 2 FIT [80, 95] s → [1525, 2385).
/// </summary>
public sealed class HoldRouteChoiceUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly VideoClip _clip;
    private readonly HoldMarkupViewModel _markup;
    private MediaTime _playhead;

    public HoldRouteChoiceUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-route-choice", Guid.NewGuid().ToString("N"), "climb.mp4"), Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(120), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080,
                RecordingTimeProbe = RecordingTimeProbe.Found(W0.AddSeconds(120).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"), RecordingTimeTagOrigin.Format)
            }
        };
        _projects.Current.MediaAssets.Add(asset);
        Assert.True(_edit.AddClip(asset.Id).Success);
        _clip = _projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single();
        _markup = new HoldMarkupViewModel(_edit, _projects, _status) { Playhead = () => _playhead };
    }

    private void Training(params (double Start, double Seconds)[] climbs)
    {
        var segments = climbs.Select((c, i) => new WorkoutSegment(2 * i, WorkoutSegmentKind.Climb, i + 1, W0.AddSeconds(c.Start),
            W0.AddSeconds(c.Start + c.Seconds), MediaTime.FromSeconds(c.Seconds), null, null)).ToArray();
        _projects.SetTraining(new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 1, Sha256 = new string('a', 64),
            Activity = new TrainingActivity(W0.AddMinutes(-5), W0.AddHours(1), 294, 814),
            Workout = new ClimbingWorkout
            {
                DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = W0.AddMinutes(-5), EndUtc = W0.AddHours(1),
                UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(3900), TimerTime = MediaTime.FromSeconds(3900),
                Sport = 31, SubSport = 69, AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Warnings = [], Segments = segments
            },
            Availability = TrainingAvailability.Available
        });
        Assert.True(_edit.CreateRoutesFromFit().Success);
    }

    private Guid FitRoute(int number) => _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>()
        .Single(t => t.FitClimb?.ClimbNumber == number).AnchorRouteId!.Value;

    private void At(long frame)
    {
        _playhead = MediaTime.FromFrame(frame, Rate);
        _markup.OnPlayheadMoved();
    }

    private void Show(long frame)
    {
        _playhead = MediaTime.FromFrame(frame, Rate);
        _markup.SetClip(_clip);
    }

    [Fact]
    public void Next_to_a_fit_green_its_route_is_offered_by_default_and_an_edit_goes_to_it()
    {
        Training((30, 20));
        Show(1300);                                                   // 1.6 s after the green

        Assert.True(_markup.ShowsRouteChoice);
        Assert.Equal(FitRoute(1), _markup.CurrentRouteId);
        Assert.Equal(new[] { string.Format(Strings.Holds_RouteFit, 1, "2", 0), Strings.Holds_RouteNew }, _markup.RouteChoices);
        Assert.Equal(0, _markup.SelectedRouteChoiceIndex);
        Assert.Equal("", _markup.RouteChoiceHint);

        _markup.StrokeWidth = 12;                                     // an edit: the FIT's route gets the markup
        Assert.Equal(12, RouteOverlays.Find(_clip, FitRoute(1))!.StrokeWidth1080);
        Assert.Single(_clip.Routes);
        At(1310);
        Assert.Equal(FitRoute(1), _markup.CurrentRouteId);
    }

    [Fact]
    public void Far_from_every_fit_green_a_new_route_is_the_default_and_the_hint_says_why()
    {
        Training((30, 20));
        Show(2900);                                                   // 65 s after the green

        Assert.True(_markup.ShowsRouteChoice);
        Assert.Null(_markup.CurrentRouteId);
        Assert.Equal(1, _markup.SelectedRouteChoiceIndex);            // «Новая трасса»
        Assert.Equal(string.Format(Strings.Holds_RouteNoFitNear, 30), _markup.RouteChoiceHint);

        _markup.SelectedRouteChoiceIndex = 0;                         // the user chooses the FIT's route
        Assert.Equal(FitRoute(1), _markup.CurrentRouteId);
        At(2880);                                                     // the same default: the choice stays
        Assert.Equal(FitRoute(1), _markup.CurrentRouteId);
    }

    [Fact]
    public void A_new_route_chosen_next_to_a_fit_green_never_touches_the_fit_route()
    {
        Training((30, 20));
        Show(1300);
        _markup.SelectedRouteChoiceIndex = 1;                         // «Новая трасса»
        Assert.Null(_markup.CurrentRouteId);

        _markup.StrokeWidth = 12;
        var route = Assert.IsType<Guid>(_markup.CurrentRouteId);
        Assert.NotEqual(FitRoute(1), route);
        Assert.Null(RouteOverlays.Find(_clip, FitRoute(1)));
        At(1305);                                                     // still the new route, shown in the list
        Assert.Equal(route, _markup.CurrentRouteId);
        Assert.Equal(3, _markup.RouteChoices.Count);
        Assert.Equal(string.Format(Strings.Holds_RouteMarkedOnly, 0), _markup.RouteChoices[_markup.SelectedRouteChoiceIndex]);
    }

    [Fact]
    public void Of_two_fit_routes_the_nearest_is_the_default_and_the_other_can_be_chosen()
    {
        Training((30, 20), (80, 15));
        Show(1400);                                                   // 140 frames after climb 1's green, 125 before climb 2's

        Assert.Equal(FitRoute(2), _markup.CurrentRouteId);
        Assert.Equal(3, _markup.RouteChoices.Count);
        _markup.SelectedRouteChoiceIndex = 1;
        Assert.Equal(FitRoute(1), _markup.CurrentRouteId);
    }

    [Fact]
    public void Inside_a_green_and_without_fit_routes_there_is_no_choice()
    {
        Training((30, 20));
        Show(500);
        Assert.False(_markup.ShowsRouteChoice);
        Assert.Equal(FitRoute(1), _markup.CurrentRouteId);
        Assert.Empty(_markup.RouteChoices);
    }

    [Fact]
    public void A_clip_without_fit_routes_keeps_D053s_rule()
    {
        Show(1300);
        Assert.False(_markup.ShowsRouteChoice);
        Assert.Null(_markup.CurrentRouteId);
    }
}
