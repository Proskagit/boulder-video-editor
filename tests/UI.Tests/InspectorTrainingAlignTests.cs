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
/// D050 amendment of 2026-11-02 to B2: the Inspector matches the playhead's frame to the end of a climb by default
/// (<c>Z = W_end − 4.6 s − ClockCorrection − s(n)</c>) or to its start (<c>Z = W_start − ClockCorrection − s(n)</c>). Choosing
/// the point changes nothing; each match replaces the video's zero — the lag never accumulates — and is one Undo step.
/// </summary>
public sealed class InspectorTrainingAlignTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);
    private const long Lag = 46_000_000L;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly TrainingReference _training;
    private readonly MediaAsset _asset;
    private readonly InspectorViewModel _inspector;
    private MediaTime _playhead = MediaTime.FromFrame(250, Rate);          // 10 s into the clip

    public InspectorTrainingAlignTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);

        _asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-align", Guid.NewGuid().ToString("N"), "climb.mp4"),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(200), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080 }
        };
        _projects.Current.MediaAssets.Add(_asset);
        var clip = new VideoClip
        {
            MediaAssetId = _asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromSeconds(200),
            SourceIn = MediaTime.FromSeconds(30), SourceOut = MediaTime.FromSeconds(230)
        };
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);

        var workout = Workout();
        _training = new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 10, Sha256 = new string('a', 64), Activity = TrainingActivity.Of(workout),
            Workout = workout, Availability = TrainingAvailability.Available
        };
        _projects.SetTraining(_training);

        _inspector = new InspectorViewModel(_edit, _status)
        {
            Training = new TrainingSessionService(_projects, new TrainingImportService(NullLogger<TrainingImportService>.Instance)),
            Playhead = () => _playhead
        };
        _inspector.ShowClip(new TimelineClipSelection(clip, "climb.mp4", _asset, Rate));
        _inspector.SelectedClimbIndex = 1;                                     // climb 2
    }

    /// <summary>Two climbs; climb 2 starts at T0 + 60 s and lasts 32.5 s (its end by the boundary rule: T0 + 92.5 s).</summary>
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
            new WorkoutSegment(2, WorkoutSegmentKind.Climb, 2, T0.AddSeconds(60), T0.AddSeconds(93), MediaTime.FromSeconds(32.5), 140, 160),
        }
    };

    /// <summary>The source time at the playhead: the clip starts at timeline 0 with source in 30 s.</summary>
    private MediaTime Source => MediaTime.FromSeconds(30) + _playhead;

    private DateTimeOffset ByEnd(MediaTime correction) =>
        new(T0.AddSeconds(92.5).UtcTicks - Lag - correction.Ticks - Source.Ticks, TimeSpan.Zero);

    private DateTimeOffset ByStart(MediaTime correction) =>
        new(T0.AddSeconds(60).UtcTicks - correction.Ticks - Source.Ticks, TimeSpan.Zero);

    private void Match() => _inspector.AlignToClimbCommand.Execute(null);

    [Fact]
    public void The_end_is_the_default_point_and_the_climbs_show_start_and_end()
    {
        Assert.Equal(0, _inspector.AlignAnchorIndex);
        Assert.Equal(ClimbAnchor.End, _inspector.AlignAnchor);
        Assert.Equal(Strings.Inspector_AlignToClimbEnd, _inspector.AlignToClimbText);
        Assert.Equal(new[] { Strings.Inspector_AnchorEnd, Strings.Inspector_AnchorStart }, _inspector.AlignAnchors);
        Assert.Equal(2, _inspector.TrainingClimbs.Count);
        Assert.Equal(string.Format(Strings.Inspector_ClimbItem, 2, "10:03:39", "10:04:11"), _inspector.TrainingClimbs[1]);

        _inspector.AlignAnchorIndex = 1;
        Assert.Equal(ClimbAnchor.Start, _inspector.AlignAnchor);
        Assert.Equal(Strings.Inspector_AlignToClimb, _inspector.AlignToClimbText);
        Assert.Equal(Strings.Inspector_AlignToClimbTip, _inspector.AlignToClimbTip);
    }

    [Fact]
    public void Matching_the_end_subtracts_the_lag_once_and_the_frame_reads_the_end_less_the_lag()
    {
        Match();

        Assert.Equal(ByEnd(MediaTime.Zero), _asset.RecordingZeroOverrideUtc);
        var frame = _playhead.ToFrameFloor(Rate);
        var clip = (VideoClip)_projects.Current.Timeline.VideoTracks[0].Clips[0];
        Assert.Equal(T0.AddSeconds(92.5).AddTicks(-Lag), TrainingAlignment.ReadoutAt(_training, clip, _asset, frame, Rate).WatchTime);
        Assert.Equal(string.Format(Strings.Inspector_AlignedToClimbEnd, 2), _status.Message);
    }

    [Fact]
    public void With_a_clock_correction_the_end_still_reads_the_end_less_the_lag_and_the_correction_is_untouched()
    {
        var correction = MediaTime.FromSeconds(2);
        _training.ClockCorrection = correction;

        Match();

        Assert.Equal(ByEnd(correction), _asset.RecordingZeroOverrideUtc);
        Assert.Equal(T0.AddSeconds(92.5).AddTicks(-Lag),
            TrainingAlignment.WatchTime(_asset.RecordingZeroOverrideUtc!.Value, Source, correction));
        Assert.Equal(correction, _training.ClockCorrection);
    }

    [Fact]
    public void Switching_the_point_changes_nothing_each_match_replaces_the_zero_and_the_lag_never_accumulates()
    {
        Match();                                                               // 1. the end
        var byEnd = _asset.RecordingZeroOverrideUtc;
        Assert.Equal(ByEnd(MediaTime.Zero), byEnd);
        var history = _undo.CurrentPosition;

        _inspector.AlignAnchorIndex = 1;                                       // switching alone: Z and the history as they were
        Assert.Equal(byEnd, _asset.RecordingZeroOverrideUtc);
        Assert.Same(history, _undo.CurrentPosition);
        _inspector.AlignAnchorIndex = 0;
        _inspector.AlignAnchorIndex = 1;
        Assert.Equal(byEnd, _asset.RecordingZeroOverrideUtc);
        Assert.Same(history, _undo.CurrentPosition);

        Match();                                                               // 2. the start: the formula of B2 as before
        Assert.Equal(ByStart(MediaTime.Zero), _asset.RecordingZeroOverrideUtc);
        Assert.Equal(string.Format(Strings.Inspector_AlignedToClimb, 2), _status.Message);

        _inspector.AlignAnchorIndex = 0;
        Assert.Equal(ByStart(MediaTime.Zero), _asset.RecordingZeroOverrideUtc);

        Match();                                                               // 3. the end again: the same zero, not 9.2 s off
        Assert.Equal(ByEnd(MediaTime.Zero), _asset.RecordingZeroOverrideUtc);
        Match();                                                               // and once more
        Assert.Equal(ByEnd(MediaTime.Zero), _asset.RecordingZeroOverrideUtc);
    }

    [Fact]
    public void Each_match_is_one_undo_step_and_switching_the_point_is_none()
    {
        Match();                                                               // end
        _inspector.AlignAnchorIndex = 1;
        Match();                                                               // start
        _inspector.AlignAnchorIndex = 0;
        Match();                                                               // end

        _undo.Undo();
        Assert.Equal(ByStart(MediaTime.Zero), _asset.RecordingZeroOverrideUtc);
        _undo.Undo();
        Assert.Equal(ByEnd(MediaTime.Zero), _asset.RecordingZeroOverrideUtc);
        _undo.Undo();
        Assert.Null(_asset.RecordingZeroOverrideUtc);
        Assert.False(_undo.CanUndo);                                           // three matches, three steps, nothing else

        _undo.Redo();
        Assert.Equal(ByEnd(MediaTime.Zero), _asset.RecordingZeroOverrideUtc);
        _undo.Redo();
        Assert.Equal(ByStart(MediaTime.Zero), _asset.RecordingZeroOverrideUtc);
        _undo.Redo();
        Assert.Equal(ByEnd(MediaTime.Zero), _asset.RecordingZeroOverrideUtc);
        Assert.False(_undo.CanRedo);
    }

    [Fact]
    public void The_same_frame_matched_by_end_and_by_start_gives_zeros_the_duration_less_the_lag_apart()
    {
        Match();
        var byEnd = _asset.RecordingZeroOverrideUtc!.Value;
        _inspector.AlignAnchorIndex = 1;
        Match();
        var byStart = _asset.RecordingZeroOverrideUtc!.Value;

        Assert.Equal(MediaTime.FromSeconds(32.5).Ticks - Lag, byEnd.UtcTicks - byStart.UtcTicks);   // 27.9 s
    }
}
