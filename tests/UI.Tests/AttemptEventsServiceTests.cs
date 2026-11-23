using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Project;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D078 §3–§4: the attempt events' service asks for the windows of the FIT's route areas only while a setting is on and the
/// training is usable; an area whose window is analysed is looked at once (its events handed to the refinement) and asked for
/// no more; the lift-offs' magnet is empty while its setting is off.
/// </summary>
public sealed class AttemptEventsServiceTests : IDisposable
{
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TestUiThread _ui = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-aes-" + Guid.NewGuid().ToString("N"));
    private readonly VideoClip _clip;
    private readonly TemplateClip _area;

    public AttemptEventsServiceTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "a.mp4");
        File.WriteAllBytes(path, new byte[100]);
        var asset = new MediaAsset
        {
            FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(120), Width = 64, Height = 36, DisplayWidth = 64, DisplayHeight = 36,
                FrameRate = FrameRate.Fps30, StartTime = MediaTime.Zero
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        _clip = new VideoClip { MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(120) };
        _clip.SourceOut = _clip.Duration;
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(_clip);
        // A FIT area: green [53, 95) s, T at 60 s.
        _area = new TemplateClip
        {
            Kind = TemplateKind.Boulder, AnchorClipId = _clip.Id, AnchorRouteId = Guid.NewGuid(), FitClimb = new ClimbIdentity(1, 1, DateTimeOffset.UnixEpoch),
            FitLayout = new FitAreaLayout(MediaTime.FromSeconds(53), MediaTime.FromSeconds(95), MediaTime.FromSeconds(37)),
            RouteStart = MediaTime.FromSeconds(60), TimelineStart = MediaTime.FromSeconds(53), Duration = MediaTime.FromSeconds(42),
            Status = BoulderStatus.Attempt
        };
        _projects.SetTraining(new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 1, Sha256 = new string('a', 64),
            Activity = new TrainingActivity(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), 294, 814),
            Workout = new ClimbingWorkout
            {
                DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = DateTimeOffset.UnixEpoch, EndUtc = DateTimeOffset.UnixEpoch.AddHours(1),
                UtcOffset = TimeSpan.Zero, ElapsedTime = MediaTime.FromSeconds(3600), TimerTime = MediaTime.FromSeconds(3600), Sport = 31, SubSport = 69,
                AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Warnings = [], Segments = []
            },
            Availability = TrainingAvailability.Available
        });
    }

    /// <summary>The FIT's area made now (the import) — an area already in the project when the service starts is never
    /// refined (amendment 5).</summary>
    private void AddArea()
    {
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(_area);
        _projects.NotifyTimelineChanged();
    }

    public void Dispose()
    {
        _ui.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_window_of_an_area_is_T_minus_35_s_to_its_end_plus_20_s()
    {
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(_area);
        var window = Assert.Single(FitAreaEventWindows.Of(_projects.Current));
        Assert.Equal((_area.Id, _clip.MediaAssetId), (window.TemplateId, window.AssetId));
        Assert.Equal((MediaTime.FromSeconds(25), MediaTime.FromSeconds(115)), (window.From, window.To));
    }

    [Fact]
    public Task Both_settings_off_ask_for_nothing_and_the_magnet_is_empty() => _ui.Run(async () =>
    {
        var settings = new InMemoryUserSettings(new UserSettings { RefineFallFromVideo = false, SnapRouteStartToLiftOff = false });
        var decoder = new Decoder();
        using var people = new PeopleAnalysisCoordinator(_projects, decoder, new Vision());
        var edit = new Edit();
        _ = new AttemptEventsService(_projects, people, edit, settings, new StatusService());
        AddArea();
        await people.IdleAsync();
        await Settle();
        Assert.Empty(decoder.Starts);
        Assert.Empty(edit.Calls);
    });

    [Fact]
    public Task An_analysed_window_is_handed_on_once_and_asked_for_no_more() => _ui.Run(async () =>
    {
        var settings = new InMemoryUserSettings();   // the defaults: the fall on, the lift-off off
        Assert.True(settings.Current.RefineFallFromVideo);
        Assert.False(settings.Current.SnapRouteStartToLiftOff);
        var decoder = new Decoder();
        using var people = new PeopleAnalysisCoordinator(_projects, decoder, new Vision());
        var edit = new Edit();
        var service = new AttemptEventsService(_projects, people, edit, settings, new StatusService());
        AddArea();
        await people.IdleAsync();
        await Settle();

        Assert.Equal(new[] { MediaTime.FromSeconds(24) }, decoder.Starts);         // the window [25, 115) from its chunk at 24 s
        var call = Assert.Single(edit.Calls);
        Assert.Equal((true, false), (call.Fall, call.LiftOff));
        Assert.Equal(_area.Id, Assert.Single(call.Events.Keys));
        Assert.True(service.LiftOffsAround(_clip, MediaTime.FromSeconds(60)).IsEmpty);                             // the lift-off's setting is off

        settings.Update(s => s with { SnapRouteStartToLiftOff = true });            // the area was looked at already
        await people.IdleAsync();
        await Settle();
        Assert.Single(edit.Calls);
    });

    [Fact]
    public Task An_area_whose_window_is_analysed_already_is_looked_at_on_the_timelines_change() => _ui.Run(async () =>
    {
        var decoder = new Decoder();
        using var people = new PeopleAnalysisCoordinator(_projects, decoder, new Vision());
        var edit = new Edit();
        _ = new AttemptEventsService(_projects, people, edit, new InMemoryUserSettings(), new StatusService());
        AddArea();
        await people.IdleAsync();
        await Settle();
        Assert.Single(edit.Calls);

        // A second area over the same window (made again from the FIT, say): no new analysis, still looked at.
        var again = new TemplateClip
        {
            Kind = TemplateKind.Boulder, AnchorClipId = _clip.Id, AnchorRouteId = Guid.NewGuid(), FitClimb = new ClimbIdentity(2, 2, DateTimeOffset.UnixEpoch),
            FitLayout = _area.FitLayout, RouteStart = _area.RouteStart, TimelineStart = _area.TimelineStart, Duration = _area.Duration,
            Status = BoulderStatus.Attempt
        };
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(again);
        _projects.NotifyTimelineChanged();
        await people.IdleAsync();
        await Settle();
        Assert.Equal(2, edit.Calls.Count);
        Assert.Equal(again.Id, Assert.Single(edit.Calls[1].Events.Keys));
        Assert.Single(decoder.Starts);
    });

    [Fact]
    public Task An_export_holds_the_refinement_until_editing_is_allowed_again() => _ui.Run(async () =>
    {
        var editing = new EditingLock();
        var scope = editing.Acquire();
        using var people = new PeopleAnalysisCoordinator(_projects, new Decoder(), new Vision());
        var edit = new Edit();
        _ = new AttemptEventsService(_projects, people, edit, new InMemoryUserSettings(), new StatusService(), editing);
        AddArea();
        await people.IdleAsync();
        await Settle();
        Assert.Empty(edit.Calls);                                                   // analysed, but the export locks editing
        scope.Dispose();
        await Settle();
        Assert.Single(edit.Calls);
    });

    /// <summary>Lets the refinement posted to the UI context run (it follows the current work item).</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 3; i++) await Task.Yield();
    }

    [Fact]
    public Task Areas_made_again_over_analysed_windows_are_refined_in_a_step_of_their_own_after_their_creation() => _ui.Run(async () =>
    {
        // The real edit service and undo stack: the FIT's areas are created by a command whose Execute notifies the timeline's
        // change — the refinement must not run inside it (it would be recorded before the creation).
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var w0 = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(_folder, "b.mp4");
        File.WriteAllBytes(path, new byte[100]);
        var asset = new MediaAsset
        {
            FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(120), Width = 64, Height = 36, DisplayWidth = 64, DisplayHeight = 36,
                FrameRate = FrameRate.Fps30, StartTime = MediaTime.Zero,
                RecordingTimeProbe = RecordingTimeProbe.Found(w0.AddSeconds(120).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"), RecordingTimeTagOrigin.Format)
            }
        };
        projects.AddMediaAssets(new[] { asset });
        var service = new AiVideoEditor.Timeline.TimelineEditService(projects, undo, NullLogger<AiVideoEditor.Timeline.TimelineEditService>.Instance);
        Assert.True(service.AddClip(asset.Id).Success);
        projects.SetTraining(new TrainingReference
        {
            FilePath = @"C:\fit\b.fit", FileSizeBytes = 1, Sha256 = new string('b', 64),
            Activity = new TrainingActivity(w0.AddMinutes(-5), w0.AddHours(1), 294, 814),
            Workout = new ClimbingWorkout
            {
                DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = w0.AddMinutes(-5), EndUtc = w0.AddHours(1),
                UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(3900), TimerTime = MediaTime.FromSeconds(3900),
                Sport = 31, SubSport = 69, AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Warnings = [],
                Segments = new[] { new WorkoutSegment(0, WorkoutSegmentKind.Climb, 1, w0.AddSeconds(30), w0.AddSeconds(50), MediaTime.FromSeconds(20), null, null) }
            },
            Availability = TrainingAvailability.Available
        });
        using var people = new PeopleAnalysisCoordinator(projects, new Decoder(), new Vision());
        var edit = new StepEdit(service, undo);
        // The FIT's areas have no status here: the lift-off's setting makes every area's window wanted.
        _ = new AttemptEventsService(projects, people, edit, new InMemoryUserSettings(new UserSettings { SnapRouteStartToLiftOff = true }), new StatusService());

        Assert.True(service.CreateRoutesFromFit().Success);
        await people.IdleAsync();
        await Settle();
        Assert.Single(edit.Calls);
        Assert.Same(edit.Steps[0], undo.NextUndo);                                    // the refinement after the creation

        // Back to no area, then the areas made again: their windows are analysed already.
        undo.Undo();
        undo.Undo();
        Assert.DoesNotContain(projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips), c => c is TemplateClip);
        var before = undo.NextUndo;
        Assert.True(service.CreateRoutesFromFit().Success);
        var creation = undo.NextUndo;
        Assert.NotSame(before, creation);
        await people.IdleAsync();
        await Settle();
        Assert.Equal(2, edit.Calls.Count);
        Assert.Same(edit.Steps[1], undo.NextUndo);                                    // its own step, above the creation
        undo.Undo();
        Assert.Same(creation, undo.NextUndo);
        undo.Undo();
        Assert.Same(before, undo.NextUndo);
        Assert.DoesNotContain(projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips), c => c is TemplateClip);
    });

    /// <summary>The real edit service, whose refinement records a step of its own on the undo stack (a marker command).</summary>
    private sealed class StepEdit(AiVideoEditor.Timeline.TimelineEditService inner, UndoRedoService undo) : IFitMarkEditService
    {
        public List<IReadOnlyDictionary<Guid, AttemptEventSet>> Calls { get; } = new();
        public List<IUndoableCommand> Steps { get; } = new();

        public TimelineEditResult RefineFitAreasFromVideo(IReadOnlyDictionary<Guid, AttemptEventSet> events, bool fall, bool liftOff)
        {
            Calls.Add(events);
            var step = new Marker();
            undo.Execute(step);
            Steps.Add(step);
            return new TimelineEditResult { Success = true, ClipIds = events.Keys.ToList() };
        }

        public TimelineEditResult MoveFitMark(ClimbIdentity climb, MediaTime sourceTime) => inner.MoveFitMark(climb, sourceTime);
        public TimelineEditResult DeclineClimb(ClimbIdentity climb) => inner.DeclineClimb(climb);
        public TimelineEditResult RestoreClimb(ClimbIdentity climb) => inner.RestoreClimb(climb);
        public TimelineEditResult LinkFitClimb(ClimbIdentity climb, Guid clipId, Guid? routeId = null) => inner.LinkFitClimb(climb, clipId, routeId);
        public TimelineEditResult UnlinkFitClimb(ClimbIdentity climb) => inner.UnlinkFitClimb(climb);
        public TimelineEditResult LayOutRouteFromMark(ClimbIdentity climb, Guid clipId, Guid? routeId = null) => inner.LayOutRouteFromMark(climb, clipId, routeId);
        public TimelineEditResult CreateRoutesFromFit(Guid? clipId = null) => inner.CreateRoutesFromFit(clipId);

        private sealed class Marker : IUndoableCommand
        {
            public string Description => "Refine Areas from Video";
            public void Execute() { }
            public void Undo() { }
        }
    }

    [Fact]
    public Task With_the_lift_off_off_only_an_attempts_window_is_analysed() => _ui.Run(async () =>
    {
        _area.Status = BoulderStatus.Top;                                           // a top: the fall is never used
        var decoder = new Decoder();
        using var people = new PeopleAnalysisCoordinator(_projects, decoder, new Vision());
        var edit = new Edit();
        var settings = new InMemoryUserSettings();
        _ = new AttemptEventsService(_projects, people, edit, settings, new StatusService());
        AddArea();
        await people.IdleAsync();
        await Settle();
        Assert.Empty(decoder.Starts);
        Assert.Empty(edit.Calls);

        settings.Update(s => s with { SnapRouteStartToLiftOff = true });            // the lift-off needs every area's window
        await people.IdleAsync();
        await Settle();
        Assert.Single(decoder.Starts);
        Assert.Single(edit.Calls);
    });

    [Fact]
    public Task An_area_already_in_the_project_is_never_refined() => _ui.Run(async () =>
    {
        // Amendment 5 (the owner): opening a project with its FIT areas changes nothing — only areas made in the session are.
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(_area);
        var decoder = new Decoder();
        using var people = new PeopleAnalysisCoordinator(_projects, decoder, new Vision());
        var edit = new Edit();
        _ = new AttemptEventsService(_projects, people, edit, new InMemoryUserSettings(), new StatusService());
        _projects.NotifyTimelineChanged();
        await people.IdleAsync();
        await Settle();
        Assert.Empty(decoder.Starts);
        Assert.Empty(edit.Calls);
    });

    private sealed class Edit : IFitMarkEditService
    {
        public List<(IReadOnlyDictionary<Guid, AttemptEventSet> Events, bool Fall, bool LiftOff)> Calls { get; } = new();

        public TimelineEditResult RefineFitAreasFromVideo(IReadOnlyDictionary<Guid, AttemptEventSet> events, bool fall, bool liftOff)
        {
            Calls.Add((events, fall, liftOff));
            return new TimelineEditResult { Success = true, NoChange = true };
        }

        public TimelineEditResult MoveFitMark(ClimbIdentity climb, MediaTime sourceTime) => throw new NotSupportedException();
        public TimelineEditResult DeclineClimb(ClimbIdentity climb) => throw new NotSupportedException();
        public TimelineEditResult RestoreClimb(ClimbIdentity climb) => throw new NotSupportedException();
        public TimelineEditResult LinkFitClimb(ClimbIdentity climb, Guid clipId, Guid? routeId = null) => throw new NotSupportedException();
        public TimelineEditResult UnlinkFitClimb(ClimbIdentity climb) => throw new NotSupportedException();
        public TimelineEditResult LayOutRouteFromMark(ClimbIdentity climb, Guid clipId, Guid? routeId = null) => throw new NotSupportedException();
        public TimelineEditResult CreateRoutesFromFit(Guid? clipId = null) => throw new NotSupportedException();
    }

    private sealed class Vision : IPeopleVision
    {
        public string ModelId => "fake";
        public InferenceDevice DetectorDevice => InferenceDevice.Gpu;
        public InferenceDevice PrepareDetector(CancellationToken cancellationToken) => InferenceDevice.Gpu;
        public IReadOnlyList<PersonDetection> Detect(DecodedFrame frame, CancellationToken cancellationToken) => Array.Empty<PersonDetection>();
        public IPersonSegmenter CreateSegmenter(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Decoder : IVideoDecoder
    {
        public List<MediaTime> Starts { get; } = new();

        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            var start = new MediaTime((long)(request.FirstSamplePoint.TicksNumerator / request.FirstSamplePoint.TicksDenominator));
            Starts.Add(start);
            return Task.FromResult<IVideoFrameStream>(new Stream((int)(start.Ticks * 30 / TimeSpan.TicksPerSecond), 120 * 30));
        }

        private sealed class Stream(int first, int count) : IVideoFrameStream
        {
            private int _next = first;

            public ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default)
            {
                if (_next >= count) return ValueTask.FromResult<DecodedFrame?>(null);
                var frame = new DecodedFrame(64, 36, 64 * 4, new byte[64 * 36 * 4], new SourceTimestamp(_next++, new TimeBase(1, 30)));
                return ValueTask.FromResult<DecodedFrame?>(frame);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
