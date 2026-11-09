using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Media;
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
/// Phase 35.5 through the view models: «Шаблон ▾ → Пульс» adds the overlay; the Inspector shows its own section — the look,
/// the corner and the heart's colour, one click and one undo step each, the buttons showing the model — with a hint that
/// names the project's reason for no value (no training, a FIT not available) or says where the value comes from; the
/// Preview's snapshot follows the training (read, replaced, removed) and the recording zero, so the value shown is current.
/// </summary>
public sealed class HeartRateOverlayUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly TrainingSessionService _trainingService;
    private readonly RecordingPlayback _playback = new();
    private readonly PreviewViewModel _preview;
    private readonly InspectorViewModel _inspector;
    private readonly TimelineViewModel _timeline;
    private readonly MediaAsset _asset;

    public HeartRateOverlayUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _projects.Current.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V2", Order = 1 });
        _asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-hr", Guid.NewGuid().ToString("N"), "climb.mp4"),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(120), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080,
                DisplayWidth = 1920, DisplayHeight = 1080,
                // D055 §1: the file's time — its tag read as the end — puts its first frame 10 s into the training.
                RecordingTimeProbe = RecordingTimeProbe.Found(T0.AddSeconds(130).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
                    RecordingTimeTagOrigin.Format)
            }
        };
        _projects.Current.MediaAssets.Add(_asset);
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(new VideoClip
        {
            MediaAssetId = _asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromSeconds(60), SourceOut = MediaTime.FromSeconds(60)
        });
        _projects.SetTraining(Training());
        _trainingService = new TrainingSessionService(_projects, new TrainingImportService(NullLogger<TrainingImportService>.Instance));
        _preview = new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance, training: _trainingService);
        _inspector = new InspectorViewModel(_edit, _status) { Training = _trainingService };
        _timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _preview.ReleasePlaybackAsync();

    private static TrainingReference Training(TrainingAvailability availability = TrainingAvailability.Available)
    {
        var workout = new ClimbingWorkout
        {
            DeviceName = "COROS APEX 4 42mm", Manufacturer = 294, Product = 814, StartUtc = T0, EndUtc = T0.AddMinutes(10),
            UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(600), TimerTime = MediaTime.FromSeconds(600),
            Sport = 31, SubSport = 69, AverageHeartRate = 130, MaxHeartRate = 169,
            HeartRate = Enumerable.Range(0, 600).Select(i => new HeartRateSample(T0.AddSeconds(i), 100 + i % 60)).ToList(),
            Pauses = Array.Empty<WorkoutInterval>(), Segments = Array.Empty<WorkoutSegment>(), Warnings = Array.Empty<TrainingFileWarning>()
        };
        return new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 10, Sha256 = new string('a', 64), Activity = TrainingActivity.Of(workout),
            Workout = workout, Availability = availability
        };
    }

    private TemplateClip AddOverlay()
    {
        _projects.Current.Timeline.PlayheadPosition = MediaTime.Zero;
        _timeline.AddTemplateCommand.Execute(nameof(TemplateKind.HeartRate));
        var overlay = _projects.Current.Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().Single();
        _inspector.ShowClip(new TimelineClipSelection(overlay, "Пульс", null, Rate));
        return overlay;
    }

    /// <summary>The value the Preview's current snapshot gives the overlay on <paramref name="second"/>.</summary>
    private int? PreviewValue(int second) =>
        _playback.Last!.LayersAt(MediaTime.FromSeconds(second)).OfType<TemplateLayer>().Single().HeartRate;

    [Fact]
    public void The_overlay_is_added_from_the_template_menu_over_the_video_below()
    {
        var overlay = AddOverlay();
        Assert.Equal(MediaTime.FromSeconds(60), overlay.Duration);
        Assert.Equal(Strings.TemplateType_HeartRate, TimelineViewModel.TemplateLabel(overlay));
        Assert.Equal(100 + 15, PreviewValue(5));                              // the video starts 10 s into the training
    }

    [Fact]
    public void The_inspector_shows_the_overlay_s_own_section_and_its_choices()
    {
        AddOverlay();
        Assert.True(_inspector.ShowsTemplateHeartRate);
        Assert.False(_inspector.ShowsTemplateBoulder);
        Assert.Equal(HeartRateLook.Plate, _inspector.HeartRateLooks.Single(o => o.IsSelected).Value);
        Assert.Equal(OverlayCorner.BottomRight, _inspector.HeartRateCorners.Single(o => o.IsSelected).Value);
        Assert.Equal(TemplateRules.HeartRateAccentHex, _inspector.TemplateAccentHex);
        Assert.Equal(Strings.HeartRate_Hint, _inspector.HeartRateHint);
    }

    [Fact]
    public void The_look_the_corner_and_the_colour_are_one_click_and_one_undo_step_each()
    {
        var overlay = AddOverlay();

        _inspector.SetHeartRateLookCommand.Execute(HeartRateLook.Plain);
        _inspector.SetHeartRateCornerCommand.Execute(OverlayCorner.TopLeft);
        _inspector.SetTemplateAccentCommand.Execute("#00E676");

        Assert.Equal((HeartRateLook.Plain, OverlayCorner.TopLeft, "#00E676"), (overlay.Look, overlay.Corner, overlay.AccentHex));
        _inspector.ShowClip(new TimelineClipSelection(overlay, "Пульс", null, Rate));   // the shell shows the edited clip again
        Assert.Equal(HeartRateLook.Plain, _inspector.HeartRateLooks.Single(o => o.IsSelected).Value);
        Assert.Equal(OverlayCorner.TopLeft, _inspector.HeartRateCorners.Single(o => o.IsSelected).Value);
        Assert.Equal("Change Heart Colour", (_undo.CurrentPosition as IUndoableCommand)?.Description);

        _undo.Undo();
        _undo.Undo();
        Assert.Equal((HeartRateLook.Plain, OverlayCorner.BottomRight), (overlay.Look, overlay.Corner));
        _undo.Undo();
        Assert.Equal(HeartRateLook.Plate, overlay.Look);
    }

    [Fact]
    public void The_hint_names_the_project_s_reason_for_no_value()
    {
        AddOverlay();

        _projects.Current.Training!.Availability = TrainingAvailability.Missing;
        _trainingService.NotifyChanged();
        Assert.Equal(Strings.HeartRate_TrainingUnavailable, _inspector.HeartRateHint);

        _trainingService.Remove();
        Assert.Equal(Strings.HeartRate_NoTraining, _inspector.HeartRateHint);
    }

    [Fact]
    public void The_preview_follows_the_training_and_never_the_recording_zero()
    {
        AddOverlay();
        Assert.Equal(115, PreviewValue(5));

        var updates = _playback.Updates;
        _projects.Current.Training!.Availability = TrainingAvailability.Missing;   // the FIT read again and found missing
        _trainingService.NotifyChanged();
        Assert.True(_playback.Updates > updates);
        Assert.Null(PreviewValue(5));

        _projects.Current.Training!.Availability = TrainingAvailability.Available;
        _trainingService.NotifyChanged();
        Assert.Equal(115, PreviewValue(5));

        // D055 §2: a new recording zero refines the route bounds only — the overlay's value stays on the file's time.
        Assert.True(_edit.SetRecordingZeroOverride(_asset.Id, T0.AddSeconds(40)).Success);
        Assert.Equal(115, PreviewValue(5));
        _undo.Undo();
        Assert.Equal(115, PreviewValue(5));
        Assert.True(_edit.SetTrainingClockCorrection(MediaTime.FromSeconds(30)).Success);   // the clock correction moves it
        Assert.Equal(145, PreviewValue(5));
        _undo.Undo();
        Assert.Equal(115, PreviewValue(5));

        _trainingService.Remove();
        Assert.Null(PreviewValue(5));
    }

    [Fact]
    public async Task A_released_preview_no_longer_follows_the_training()
    {
        AddOverlay();
        await _preview.ReleasePlaybackAsync();
        var updates = _playback.Updates;
        _trainingService.NotifyChanged();
        Assert.Equal(updates, _playback.Updates);
    }

    private sealed class RecordingPlayback : IPlaybackService
    {
        public PlaybackSnapshot? Last { get; private set; }
        public int Updates { get; private set; }
        public PlaybackState State => PlaybackState.Paused;
        public bool IsBuffering => false;
        public bool IsAvailable => true;
        public bool IsAudioAvailable => true;
        public MediaTime Position => MediaTime.Zero;
        public MediaTime Duration => MediaTime.FromSeconds(60);
        public MediaTime PlaybackEnd => Duration;
        public event EventHandler? StateChanged { add { } remove { } }
        public void UpdateSnapshot(PlaybackSnapshot snapshot) { Last = snapshot; Updates++; }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void SetPlaybackRange(PlaybackRange? range) { }
        public Task<bool> SeekAsync(MediaTime position, CancellationToken ct = default) => Task.FromResult(true);
        public PlaybackFrame Update() => new(Position, 0, State, false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
