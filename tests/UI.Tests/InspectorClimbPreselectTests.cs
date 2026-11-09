using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Media;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D056 §1: the Inspector's list «Подъём» shows the climb at the playhead — the climb of the FIT-made area under it, else the
/// climb the video's time shows there — on a clip with several climbs; a frame matched to another climb is asked about first,
/// so a wrong choice no longer silently adds a second area (the 2026-11-09 check on <c>101034</c>).
/// </summary>
public sealed class InspectorClimbPreselectTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly MediaAsset _asset;
    private readonly VideoClip _clip;
    private readonly Track _areas = new() { Type = TrackType.Video, Name = "V2", Order = 1 };
    private readonly ScriptedDialogs _dialogs;
    private readonly InspectorViewModel _inspector;
    private readonly ClimbingWorkout _workout = Workout();
    private MediaTime _playhead = MediaTime.FromSeconds(70);

    public InspectorClimbPreselectTests() : this(new ScriptedDialogs()) { }

    private InspectorClimbPreselectTests(ScriptedDialogs dialogs, bool fileTime = true)
    {
        _dialogs = dialogs;
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-preselect", Guid.NewGuid().ToString("N"), "climb.mp4"),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(200), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080,
                // The tag read as the end of the recording: the first frame is the training's start, s = w − T0.
                RecordingTimeProbe = fileTime
                    ? RecordingTimeProbe.Found(T0.AddSeconds(200).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"), RecordingTimeTagOrigin.Format)
                    : null
            }
        };
        _projects.Current.MediaAssets.Add(_asset);
        _clip = new VideoClip
        {
            MediaAssetId = _asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromSeconds(200), SourceOut = MediaTime.FromSeconds(200)
        };
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(_clip);
        _projects.Current.Timeline.VideoTracks.Add(_areas);
        _projects.SetTraining(new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 10, Sha256 = new string('a', 64), Activity = TrainingActivity.Of(_workout),
            Workout = _workout, Availability = TrainingAvailability.Available
        });

        _inspector = new InspectorViewModel(_edit, _status, dialogs: _dialogs)
        {
            Training = new TrainingSessionService(_projects, new TrainingImportService(NullLogger<TrainingImportService>.Instance)),
            Playhead = () => _playhead
        };
    }

    /// <summary>Climb 1 at 0–20 s, climb 2 at 60–93 s, climb 3 at 150–180 s of the training (and of the video).</summary>
    private static ClimbingWorkout Workout() => new()
    {
        DeviceName = "COROS APEX 4 42mm", Manufacturer = 294, Product = 814, StartUtc = T0, EndUtc = T0.AddMinutes(10),
        UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(600), TimerTime = MediaTime.FromSeconds(600),
        Sport = 31, SubSport = 69, AverageHeartRate = 130, MaxHeartRate = 169, HeartRate = Array.Empty<HeartRateSample>(),
        Pauses = Array.Empty<WorkoutInterval>(), Warnings = Array.Empty<TrainingFileWarning>(),
        Segments = new[]
        {
            new WorkoutSegment(0, WorkoutSegmentKind.Climb, 1, T0, T0.AddSeconds(20), MediaTime.FromSeconds(20), 120, 130),
            new WorkoutSegment(1, WorkoutSegmentKind.Rest, null, T0.AddSeconds(20), T0.AddSeconds(60), MediaTime.FromSeconds(40), 110, 120),
            new WorkoutSegment(2, WorkoutSegmentKind.Climb, 2, T0.AddSeconds(60), T0.AddSeconds(93), MediaTime.FromSeconds(33), 140, 160),
            new WorkoutSegment(3, WorkoutSegmentKind.Rest, null, T0.AddSeconds(93), T0.AddSeconds(150), MediaTime.FromSeconds(57), 110, 120),
            new WorkoutSegment(4, WorkoutSegmentKind.Climb, 3, T0.AddSeconds(150), T0.AddSeconds(180), MediaTime.FromSeconds(30), 140, 160),
        }
    };

    private ClimbIdentity Climb(int number) => FitMarkRules.IdentityOf(_workout.Climbs.Single(c => c.ClimbNumber == number));

    /// <summary>A green area made from the FIT for <paramref name="number"/> on the clip, on the track above it.</summary>
    private void Area(int number, double from, double to) => _areas.Clips.Add(new TemplateClip
    {
        Kind = TemplateKind.Boulder, AnchorClipId = _clip.Id, AnchorRouteId = Guid.NewGuid(), FitClimb = Climb(number),
        TimelineStart = MediaTime.FromSeconds(from), Duration = MediaTime.FromSeconds(to - from)
    });

    private void Show() => _inspector.ShowClip(new TimelineClipSelection(_clip, "climb.mp4", _asset, Rate));

    private void MoveTo(double seconds)
    {
        _playhead = MediaTime.FromSeconds(seconds);
        _inspector.OnPlayheadMoved();
    }

    private int Selected => _inspector.SelectedClimbIndex < 0 ? 0 : _workout.Climbs.ElementAt(_inspector.SelectedClimbIndex).ClimbNumber ?? 0;

    [Fact]
    public void Each_area_of_a_clip_with_several_climbs_chooses_its_own_climb()
    {
        Area(2, 55, 100);
        Area(3, 140, 190);
        Show();

        Assert.Equal(2, Selected);
        Assert.True(_inspector.AlignToClimbCommand.CanExecute(null));
        MoveTo(160);
        Assert.Equal(3, Selected);
        MoveTo(80);
        Assert.Equal(2, Selected);
    }

    [Fact]
    public void Outside_every_area_the_climb_is_the_one_the_video_time_shows_and_none_far_from_any()
    {
        Area(3, 140, 190);
        _playhead = MediaTime.FromSeconds(10);
        Show();
        Assert.Equal(1, Selected);                     // inside climb 1 by the file's time

        MoveTo(115);                                   // 22 s after climb 2, 35 s before climb 3
        Assert.Equal(2, Selected);

        MoveTo(40);                                    // 20 s from both: the nearest wins (climb 1 is checked first)
        Assert.Equal(1, Selected);
    }

    [Fact]
    public void A_climb_chosen_by_hand_stays_while_the_playhead_stays_in_the_area_and_follows_it_to_another()
    {
        Area(2, 55, 100);
        Area(3, 140, 190);
        Show();
        _inspector.SelectedClimbIndex = 0;             // climb 1, by hand

        MoveTo(85);
        Assert.Equal(1, Selected);
        MoveTo(160);
        Assert.Equal(3, Selected);
    }

    [Fact]
    public void Of_two_areas_under_the_playhead_the_one_the_video_time_shows_wins()
    {
        Area(2, 55, 100);
        Area(1, 58, 110);                              // made by a wrong Z: begins later, but the time shows climb 2
        Show();

        Assert.Equal(2, Selected);
    }

    [Fact]
    public void Matching_the_climb_at_the_playhead_asks_nothing()
    {
        Area(2, 55, 100);
        Show();

        _inspector.AlignToClimbCommand.Execute(null);

        Assert.Empty(_dialogs.Asked);
        Assert.NotNull(_asset.RecordingZeroOverrideUtc);
    }

    [Fact]
    public async Task Matching_another_climb_than_the_area_under_the_playhead_asks_and_cancel_changes_nothing()
    {
        var test = new InspectorClimbPreselectTests(new ScriptedDialogs(1));
        test.Area(2, 55, 100);
        test.Area(3, 140, 190);
        test._playhead = MediaTime.FromSeconds(160);
        test.Show();
        test._inspector.SelectedClimbIndex = 1;        // climb 2 while the playhead is in climb 3's area

        await test._inspector.AlignToClimbCommand.ExecuteAsync(null);

        var asked = Assert.Single(test._dialogs.Asked);
        Assert.Equal(Strings.Inspector_AlignMismatchTitle, asked.Title);
        Assert.Equal(string.Format(Strings.Inspector_AlignMismatchArea, 3, 2), asked.Message);
        Assert.Equal(new[] { string.Format(Strings.Inspector_AlignMismatchConfirm, 2), Strings.Common_Cancel }, asked.Buttons);
        Assert.Null(test._asset.RecordingZeroOverrideUtc);
        Assert.False(test._undo.CanUndo);
        Assert.Equal(2, test._areas.Clips.Count);
    }

    [Fact]
    public async Task Matching_another_climb_when_confirmed_sets_the_zero_by_the_chosen_climb()
    {
        var test = new InspectorClimbPreselectTests(new ScriptedDialogs(0));
        test.Area(3, 140, 190);
        test._playhead = MediaTime.FromSeconds(160);
        test.Show();
        test._inspector.SelectedClimbIndex = 1;        // climb 2

        await test._inspector.AlignToClimbCommand.ExecuteAsync(null);

        Assert.Single(test._dialogs.Asked);
        var expected = TrainingAlignment.ZeroForClimb(test._workout.Climbs.ElementAt(1), ClimbAnchor.End, MediaTime.Zero, MediaTime.FromSeconds(160));
        Assert.Equal(expected, test._asset.RecordingZeroOverrideUtc);
    }

    [Fact]
    public async Task Without_an_area_the_video_time_names_the_climb_in_the_question()
    {
        var test = new InspectorClimbPreselectTests(new ScriptedDialogs(1));
        test._playhead = MediaTime.FromSeconds(165);
        test.Show();
        test._inspector.SelectedClimbIndex = 0;        // climb 1 while the time shows climb 3

        await test._inspector.AlignToClimbCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(Strings.Inspector_AlignMismatchTime, 3, 1), Assert.Single(test._dialogs.Asked).Message);
        Assert.Null(test._asset.RecordingZeroOverrideUtc);
    }

    [Fact]
    public void Without_a_time_and_an_area_nothing_is_chosen_and_nothing_is_asked()
    {
        var test = new InspectorClimbPreselectTests(new ScriptedDialogs(1), fileTime: false);
        test.Show();

        Assert.Equal(-1, test._inspector.SelectedClimbIndex);
        Assert.False(test._inspector.AlignToClimbCommand.CanExecute(null));
        test._inspector.SelectedClimbIndex = 2;
        test._inspector.AlignToClimbCommand.Execute(null);

        Assert.Empty(test._dialogs.Asked);
        Assert.NotNull(test._asset.RecordingZeroOverrideUtc);
    }
}
