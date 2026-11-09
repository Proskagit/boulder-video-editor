using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
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
/// D055 through the edit service and the Inspector: after the FIT's binding (D054) every green route area shows its clip's
/// heart rate by the file's time — no Z, no choice; its own settings (shown, look, corner, heart colour) are the overlay's
/// controls, each one undo step, kept by a split; the hint names why an area shows none.
/// </summary>
public sealed class HeartRateAreaUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly TrainingSessionService _session;

    public HeartRateAreaUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _session = new TrainingSessionService(_projects, new TrainingImportService(NullLogger<TrainingImportService>.Instance));
    }

    /// <summary>A 120 s video whose file name puts its first frame at W0 (13:00:00 at UTC+3), or with no time at all.</summary>
    private VideoClip Video(string name = "video_20261103_130000.mp4")
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-d055", Guid.NewGuid().ToString("N"), name), Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(120), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080 }
        };
        _projects.Current.MediaAssets.Add(asset);
        Assert.True(_edit.AddClip(asset.Id).Success);
        return _projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Last();
    }

    /// <summary>A climb FIT [W0 + 30, W0 + 50] and a sample every second, BPM 100 + the second.</summary>
    private void Training()
    {
        var climbs = new[] { new WorkoutSegment(0, WorkoutSegmentKind.Climb, 1, W0.AddSeconds(30), W0.AddSeconds(50), MediaTime.FromSeconds(20), null, null) };
        var workout = new ClimbingWorkout
        {
            DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = W0, EndUtc = W0.AddMinutes(10), UtcOffset = TimeSpan.FromHours(3),
            ElapsedTime = MediaTime.FromSeconds(600), TimerTime = MediaTime.FromSeconds(600), Sport = 31, SubSport = 69, AverageHeartRate = null,
            MaxHeartRate = null, HeartRate = Enumerable.Range(0, 600).Select(i => new HeartRateSample(W0.AddSeconds(i), 100 + i % 100)).ToList(),
            Pauses = [], Segments = climbs, Warnings = []
        };
        _projects.SetTraining(new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 1, Sha256 = new string('a', 64), Activity = TrainingActivity.Of(workout),
            Workout = workout, Availability = TrainingAvailability.Available
        });
    }

    private TemplateClip AreaOf(VideoClip clip) => _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>()
        .Single(t => t.AnchorClipId == clip.Id);

    private int? ValueAt(double seconds) => PlaybackSnapshotBuilder.Build(_projects.Current, 1)
        .LayersAt(MediaTime.FromSeconds(seconds)).OfType<TemplateLayer>().Single(l => l.Kind == TemplateKind.Boulder).HeartRate;

    private InspectorViewModel Inspector(TemplateClip area)
    {
        var inspector = new InspectorViewModel(_edit, _status) { Training = _session, Playhead = () => MediaTime.Zero };
        inspector.ShowClip(new TimelineClipSelection(area, "Боулдер", null, Rate));
        return inspector;
    }

    [Fact]
    public void After_the_fits_binding_the_area_shows_its_clips_heart_rate_without_a_recording_zero()
    {
        var clip = Video();
        Training();
        Assert.True(_edit.CreateRoutesFromFit().Success);            // D054: the green area, no Z
        var area = AreaOf(clip);
        Assert.Null(_projects.Current.MediaAssets.Single().RecordingZeroOverrideUtc);

        Assert.Equal(100 + 25, ValueAt(25));                         // the clip's picture at 25 s: w = W0 + 25 s
        Assert.True(_edit.SetRecordingZeroOverride(clip.MediaAssetId, W0.AddSeconds(-40)).Success);   // the user's Z
        Assert.Equal(100 + 25, ValueAt(25));                         // D055 §2: never moved by Z

        Assert.True(_edit.TrimClip(area.Id, ClipEdge.Start, MediaTime.FromSeconds(10)).Success);      // stretched by hand
        Assert.Equal(100 + 12, ValueAt(12));
        Assert.Equal(100 + 25, ValueAt(25));
    }

    [Fact]
    public void The_areas_heart_rate_settings_are_the_overlays_controls_one_undo_step_each_and_kept_by_a_split()
    {
        var clip = Video();
        Training();
        Assert.True(_edit.CreateRoutesFromFit().Success);
        var area = AreaOf(clip);
        var inspector = Inspector(area);
        Assert.True(inspector.ShowsTemplateBoulder);
        Assert.True(inspector.TemplateShowHeartRate);
        Assert.True(inspector.ShowsHeartRateSettings);
        Assert.Equal(Strings.HeartRate_AreaHint, inspector.HeartRateHint);
        Assert.Equal(TemplateRules.HeartRateAccentHex, inspector.TemplateHeartHex);

        inspector.SetHeartColourCommand.Execute("#00E676");
        inspector.SetHeartRateCornerCommand.Execute(OverlayCorner.TopLeft);
        inspector.TemplateShowHeartRate = false;
        Assert.Equal(("#00E676", OverlayCorner.TopLeft, false), (area.HeartHex, area.Corner, area.ShowHeartRate));
        Assert.Equal(TemplateRules.DefaultAccentHex, area.AccentHex);               // the route's colour untouched
        Assert.False(inspector.ShowsHeartRateSettings);
        Assert.Equal("Hide Heart Rate", (_undo.CurrentPosition as IUndoableCommand)?.Description);
        Assert.Null(ValueAt(35));

        var middle = new MediaTime((area.TimelineStart.Ticks + area.TimelineEnd.Ticks) / 2).ToNearestFrame(Rate) is var m ? MediaTime.FromFrame(m, Rate) : default;
        Assert.True(_edit.Split(middle, new[] { clip.Id, area.Id }).Success);
        Assert.All(_projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>(),
            t => Assert.Equal((false, "#00E676", OverlayCorner.TopLeft), (t.ShowHeartRate, t.HeartHex, t.Corner)));
        _undo.Undo();                                                               // the split
        _undo.Undo();
        Assert.True(area.ShowHeartRate);
        _undo.Undo();
        _undo.Undo();
        Assert.Equal((TemplateRules.HeartRateAccentHex, OverlayCorner.BottomRight), (area.HeartHex, area.Corner));
    }

    [Fact]
    public void The_hint_names_why_an_area_shows_no_heart_rate()
    {
        var clip = Video("clip.mp4");                                   // no time tag, no time in the name
        var area = new TemplateClip
        {
            Kind = TemplateKind.Boulder, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromSeconds(10), AnchorClipId = clip.Id,
            AccentHex = TemplateRules.DefaultAccentHex
        };
        var v2 = new Track { Type = TrackType.Video, Name = "V9", Order = 9 };
        v2.Clips.Add(area);
        _projects.Current.Timeline.VideoTracks.Add(v2);

        Assert.Equal(Strings.HeartRate_AreaNoTraining, Inspector(area).HeartRateHint);
        Training();
        Assert.Equal(Strings.HeartRate_AreaNoFileTime, Inspector(area).HeartRateHint);
        _projects.Current.Training!.Availability = TrainingAvailability.Missing;
        Assert.Equal(Strings.HeartRate_TrainingUnavailable, Inspector(area).HeartRateHint);
    }
}
