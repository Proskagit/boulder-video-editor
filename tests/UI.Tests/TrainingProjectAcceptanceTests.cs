using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Tests.Telemetry;
using AiVideoEditor.Media;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Step 35.4, the manual plan's part C as automated acceptance through the real services and view models (a synthetic
/// COROS export shaped like the session of 2026-09-27; the real-data rows run in <c>tests/Telemetry.LocalTests</c>):
/// C1 save / open, C2 recovery, C3 a missing FIT, C4 its return on window activation, C5 the new identity saved, C6 replace
/// without an Undo step, C7 / C7a the clock correction through the toolbar's field, C8a the readout following the playhead
/// through the main window's wiring, C9 the edits that keep a picture's watch time, C11 remove without an Undo step.
/// </summary>
public sealed class TrainingProjectAcceptanceTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset Start = CorosFixture.Start;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-35-4", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly ScriptedPicker _picker = new();
    private readonly QueuedDialogs _dialogs = new();
    private readonly TrainingImportService _import = new(NullLogger<TrainingImportService>.Instance);
    private readonly TrainingSessionService _session;
    private readonly TrainingImportWorkflow _workflow;

    public TrainingProjectAcceptanceTests()
    {
        Directory.CreateDirectory(_folder);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _projects.Current.Settings.IsFrameRateLocked = true;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _session = new TrainingSessionService(_projects, _import);
        _workflow = new TrainingImportWorkflow(_picker, _import, _projects, _session, _status, NullLogger<TrainingImportWorkflow>.Instance,
            () => Task.CompletedTask, _dialogs);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private sealed class QueuedDialogs : IDialogService
    {
        private readonly Queue<int?> _answers = new();
        public List<DialogRequest> Asked { get; } = new();
        public void Enqueue(int? answer) => _answers.Enqueue(answer);
        public Task<int?> AskAsync(DialogRequest request)
        {
            Asked.Add(request);
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
        }
    }

    // --- the material -------------------------------------------------------------------------------------------------

    /// <summary>The synthetic export: 1 s samples, BPM = 100 + the second since <see cref="Start"/>, the timer paused from
    /// 20 s to 30 s, the recording to 60 s.</summary>
    private string Fit(string name, Action<FitTestWriter>? extra = null)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, CorosFixture.Writer(extraRecords: extra).Build());
        return path;
    }

    private static int Bpm(double secondsSinceStart) => 100 + (int)Math.Floor(secondsSinceStart);

    /// <summary>A probed video file (a real, tiny file so the project sees it online) and its clip on V1.</summary>
    /// <param name="fileStart">D055 §1: the time of the file's first frame by its recording-time tag (read as the end of
    /// its 120 s); null — the file gives none.</param>
    private (MediaAsset Asset, VideoClip Clip) Video(string name, double startSeconds, double seconds, DateTimeOffset? fileStart = null)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 24 });
        var asset = new MediaAsset
        {
            FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(120), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36,
                DisplayWidth = 64, DisplayHeight = 36, DisplayRotation = 0, VideoCodec = "h264", StartTime = MediaTime.Zero,
                RecordingTimeProbe = fileStart is { } first
                    ? RecordingTimeProbe.Found(first.AddSeconds(120).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"), RecordingTimeTagOrigin.Format)
                    : null
            }
        };
        _projects.Current.MediaAssets.Add(asset);
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.FromSeconds(startSeconds), Duration = MediaTime.FromSeconds(seconds),
            SourceOut = MediaTime.FromSeconds(seconds)
        };
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        _projects.NotifyTimelineChanged();
        return (asset, clip);
    }

    /// <summary>Video A (timeline 0–40 s, its file's time and its zero at <see cref="Start"/> + 5 s) and B (40–60 s, + 32 s),
    /// both overridden through the edit service (the overrides are part of the rows; since D055 the heart rate follows the
    /// files' time, never the overrides), and the training imported through the workflow.</summary>
    private async Task<(MediaAsset A, MediaAsset B)> SessionAsync(string fit = "a.fit")
    {
        var (a, _) = Video("a.mp4", 0, 40, Start.AddSeconds(5));
        var (b, _) = Video("b.mp4", 40, 20, Start.AddSeconds(32));
        Assert.True(_edit.SetRecordingZeroOverride(a.Id, Start.AddSeconds(5)).Success);
        Assert.True(_edit.SetRecordingZeroOverride(b.Id, Start.AddSeconds(32)).Success);
        _picker.Files.Enqueue(Fit(fit));
        await _workflow.RunAsync();
        Assert.NotNull(_session.Workout);
        return (a, b);
    }

    private VideoClip ClipOf(MediaAsset asset) =>
        _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().First(c => c.MediaAssetId == asset.Id);

    private MediaAsset AssetOf(Guid id) => _projects.Current.MediaAssets.Single(a => a.Id == id);

    private TrainingReadout Readout(VideoClip clip, double timelineSeconds) =>
        TrainingAlignment.ReadoutAt(_projects.Current.Training, clip, AssetOf(clip.MediaAssetId),
            MediaTime.FromSeconds(timelineSeconds).ToFrameFloor(Rate), Rate);

    private string ProjectFolder => Path.Combine(_folder, "project");

    private ToolbarViewModel Toolbar()
    {
        var analysis = new MediaAnalysisCoordinator(new HoldMarkupUiTests.NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(_picker, new HoldMarkupUiTests.NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), _picker, _status,
            NullLogger<ProjectFileWorkflow>.Instance);
        return new ToolbarViewModel(_undo, files, import, _status, trainingImport: _workflow, trainingSession: _session, edit: _edit);
    }

    // --- C1, C3, C4: save, open; the FIT missing and back ----------------------------------------------------------------

    [Fact]
    public async Task C1_the_training_and_the_overrides_survive_save_and_open_and_the_heart_rate_is_there()
    {
        var (a, b) = await SessionAsync();
        Assert.True(_edit.SetTrainingClockCorrection(new MediaTime(7)).Success);
        await _projects.SaveAsAsync(ProjectFolder);

        await _projects.OpenAsync(ProjectFolder);
        await _session.ResolveAsync();

        var training = _projects.Current.Training!;
        Assert.Equal(TrainingAvailability.Available, training.Availability);
        Assert.Equal(new MediaTime(7), training.ClockCorrection);
        Assert.Equal(Start.AddSeconds(5), AssetOf(a.Id).RecordingZeroOverrideUtc);
        Assert.Equal(Start.AddSeconds(32), AssetOf(b.Id).RecordingZeroOverrideUtc);
        Assert.Equal(Bpm(5 + 1), Readout(ClipOf(a), 1).Bpm);          // A at 1 s: w = start + 6 s (+ 7 ticks)
        Assert.Equal(Bpm(32 + 3), Readout(ClipOf(b), 43).Bpm);         // B at 3 s into it: w = start + 35 s
        var tip = Toolbar().TrainingToolTip;
        Assert.Contains(TrainingText.Summary(_session.Workout!), tip);
        Assert.Contains(TrainingText.State(TrainingAvailability.Available), tip);
        Assert.False(_projects.Current.IsDirty);
    }

    [Fact]
    public async Task C3_C4_a_missing_fit_blocks_nothing_and_is_used_again_once_back_without_reopening()
    {
        var (a, _) = await SessionAsync();
        await _projects.SaveAsAsync(ProjectFolder);
        var fit = _projects.Current.Training!.FilePath;
        File.Move(fit, fit + ".hidden");

        await _projects.OpenAsync(ProjectFolder);
        await _session.ResolveAsync();
        var project = _projects.Current;
        Assert.Equal(TrainingAvailability.Missing, project.Training!.Availability);
        Assert.Contains(TrainingText.State(TrainingAvailability.Missing), Toolbar().TrainingToolTip);
        Assert.Equal(TrainingReadoutReason.TrainingNotAvailable, Readout(ClipOf(a), 1).Reason);
        Assert.False(project.IsDirty);

        // Editing and an export without an HR overlay work as usual.
        Assert.True(_edit.Split(MediaTime.FromSeconds(10), new[] { ClipOf(a).Id }).Success);
        var output = Path.Combine(_folder, "out.mp4");
        var check = ExportPreflight.Check(project, output, new ExportPreflightEnvironment(true, _ => true, File.Exists, Directory.Exists));
        Assert.True(check.CanExport, string.Join(" | ", check.Issues.Select(i => i.Message)));
        Assert.DoesNotContain(check.Issues, i => i.Kind == ExportIssueKind.HeartRateUnavailable);

        // C4: the file back, the window activated: available again, the same project, no reopening.
        File.Move(fit + ".hidden", fit);
        var monitor = new MediaAvailabilityMonitor(_projects, _status, NullLogger<MediaAvailabilityMonitor>.Instance, _session);
        monitor.OnWindowActivated();
        await monitor.LastCheck;
        Assert.Same(project, _projects.Current);
        Assert.Equal(TrainingAvailability.Available, project.Training.Availability);
        Assert.Equal(Bpm(6), Readout(project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().First(), 1).Bpm);
    }

    // --- C2: recovery ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task C2_an_autosaved_session_recovers_the_training_the_clock_correction_and_the_overrides()
    {
        var (a, b) = await SessionAsync();
        Assert.True(_edit.SetTrainingClockCorrection(MediaTime.FromSeconds(2)).Success);
        var recovery = Path.Combine(_folder, "recovery");
        var autosave = new AutosaveService(_projects, new RecoveryStore(recovery), NullLogger<AutosaveService>.Instance);
        Assert.True(await autosave.AutosaveNowAsync());
        var file = Assert.Single(Directory.GetFiles(recovery));

        // A new session after the app was ended: the recovery restored, the FIT resolved again.
        var projects = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        var session = new TrainingSessionService(projects, _import);
        await projects.RestoreRecoveryAsync(file);
        await session.ResolveAsync();

        var restored = projects.Current;
        Assert.Equal(TrainingAvailability.Available, restored.Training!.Availability);
        Assert.Equal(MediaTime.FromSeconds(2), restored.Training.ClockCorrection);
        Assert.Equal(Start.AddSeconds(5), restored.MediaAssets.Single(x => x.Id == a.Id).RecordingZeroOverrideUtc);
        Assert.Equal(Start.AddSeconds(32), restored.MediaAssets.Single(x => x.Id == b.Id).RecordingZeroOverrideUtc);
        var clip = restored.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single(c => c.MediaAssetId == a.Id);
        var readout = TrainingAlignment.ReadoutAt(restored.Training, clip, restored.MediaAssets.Single(x => x.Id == a.Id),
            MediaTime.FromSeconds(1).ToFrameFloor(Rate), Rate);
        Assert.Equal(Bpm(5 + 1 + 2), readout.Bpm);
    }

    // --- C5, C6, C11 ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task C5_a_new_export_of_the_same_activity_is_changed_used_and_its_identity_is_saved_with_the_next_save()
    {
        await SessionAsync();
        await _projects.SaveAsAsync(ProjectFolder);
        var fit = _projects.Current.Training!.FilePath;
        File.WriteAllBytes(fit, CorosFixture.Writer(extraRecords: w => w
            .Define(7, 20, (253, 4, FitTestWriter.UInt32), (3, 1, FitTestWriter.UInt8))
            .Data(7, CorosFixture.T0 + 61, 120L)).Build());              // the same session, other bytes

        await _projects.OpenAsync(ProjectFolder);
        await _session.ResolveAsync();
        Assert.Equal(TrainingAvailability.Changed, _projects.Current.Training!.Availability);
        Assert.NotNull(_session.Workout);
        Assert.Contains(TrainingText.State(TrainingAvailability.Changed), Toolbar().TrainingToolTip);

        await _projects.SaveAsync();
        var saved = ProjectSerializer.Deserialize(File.ReadAllText(ProjectFileStore.ProjectFilePath(ProjectFolder)), ProjectFolder, _ => true);
        var file = await _import.LoadAsync(fit);
        Assert.Equal(file.FileSizeBytes, saved.Training!.FileSizeBytes);
        Assert.Equal(file.Sha256, saved.Training.Sha256);
    }

    [Fact]
    public async Task C6_replacing_asks_resets_the_correction_keeps_the_overrides_and_makes_no_undo_step()
    {
        var (a, b) = await SessionAsync();
        Assert.True(_edit.SetTrainingClockCorrection(new MediaTime(99)).Success);
        var history = _undo.CurrentPosition;

        _dialogs.Enqueue(0);                                               // «Заменить»
        _picker.Files.Enqueue(Fit("c.fit"));
        await _workflow.RunAsync();

        var request = Assert.Single(_dialogs.Asked);
        Assert.Equal(Strings.Training_ReplaceTitle, request.Title);
        Assert.EndsWith("c.fit", _projects.Current.Training!.FilePath);
        Assert.Equal(MediaTime.Zero, _projects.Current.Training.ClockCorrection);
        Assert.Equal(Start.AddSeconds(5), a.RecordingZeroOverrideUtc);
        Assert.Equal(Start.AddSeconds(32), b.RecordingZeroOverrideUtc);
        Assert.Same(history, _undo.CurrentPosition);                         // no Undo step
    }

    [Fact]
    public async Task C11_removing_drops_the_training_and_its_correction_keeps_the_overrides_and_makes_no_undo_step()
    {
        var (a, b) = await SessionAsync();
        Assert.True(_edit.SetTrainingClockCorrection(new MediaTime(5)).Success);
        var history = _undo.CurrentPosition;

        _workflow.Remove();

        Assert.Null(_projects.Current.Training);
        Assert.Equal(Start.AddSeconds(5), a.RecordingZeroOverrideUtc);
        Assert.Equal(Start.AddSeconds(32), b.RecordingZeroOverrideUtc);
        Assert.Same(history, _undo.CurrentPosition);
        Assert.Equal(TrainingReadoutReason.NoTraining, Readout(ClipOf(a), 1).Reason);
    }

    // --- C7, C7a: the clock correction through the toolbar's field --------------------------------------------------------

    [Fact]
    public async Task C7_one_frame_of_correction_moves_every_aligned_video_by_one_frame_in_one_undo_step()
    {
        var (a, b) = await SessionAsync();
        var toolbar = Toolbar();
        var points = new[] { (ClipOf(a), 1.0), (ClipOf(a), 12.36), (ClipOf(b), 41.0), (ClipOf(b), 55.48) };
        var before = points.Select(p => Readout(p.Item1, p.Item2).WatchTime!.Value).ToList();
        var frame = MediaTime.FromFrame(1, Rate).Ticks;

        foreach (var (frames, sign) in new[] { (1m, 1), (-1m, -1) })
        {
            var history = _undo.CurrentPosition;
            toolbar.TrainingClockCorrectionFrames = frames;
            Assert.Equal(frames, toolbar.TrainingClockCorrectionFrames);
            for (var i = 0; i < points.Length; i++)
                Assert.Equal(before[i].UtcTicks + sign * frame, Readout(points[i].Item1, points[i].Item2).WatchTime!.Value.UtcTicks);

            _undo.Undo();                                                   // one step
            Assert.Same(history, _undo.CurrentPosition);
            for (var i = 0; i < points.Length; i++)
                Assert.Equal(before[i], Readout(points[i].Item1, points[i].Item2).WatchTime);
            _undo.Redo();
            Assert.Equal(before[0].UtcTicks + sign * frame, Readout(points[0].Item1, points[0].Item2).WatchTime!.Value.UtcTicks);
            _undo.Undo();
        }
    }

    [Fact]
    public async Task C7a_a_correction_of_plus_two_seconds_shows_the_sample_two_seconds_later_on_the_watch()
    {
        var (a, _) = await SessionAsync();
        var toolbar = Toolbar();
        // A's frame at 0 s was filmed at C = start + 5 s.
        Assert.Equal(Bpm(5), Readout(ClipOf(a), 0).Bpm);

        toolbar.TrainingClockCorrectionFrames = 50;                         // + 2 s at 25 fps: W − C = + 2 s

        Assert.Equal(MediaTime.FromSeconds(2), _projects.Current.Training!.ClockCorrection);
        Assert.Equal(Start.AddSeconds(7), Readout(ClipOf(a), 0).WatchTime);
        Assert.Equal(Bpm(7), Readout(ClipOf(a), 0).Bpm);
    }

    // --- C8a: the readout follows the playhead through the main window's wiring --------------------------------------------

    [Fact]
    public async Task C8a_the_readout_follows_the_playhead_with_the_value_or_the_reason()
    {
        var (a, b) = await SessionAsync();
        var (c, _) = Video("c.mp4", 60, 10);                                 // no recording zero
        var playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4 });
        try
        {
            var analysis = new MediaAnalysisCoordinator(new HoldMarkupUiTests.NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
            var mediaImport = new MediaImportWorkflow(_picker, new HoldMarkupUiTests.NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
            var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), _picker, _status,
                NullLogger<ProjectFileWorkflow>.Instance);
            var vm = new MainWindowViewModel(
                new ToolbarViewModel(_undo, files, mediaImport, _status),
                new MediaBrowserViewModel(_projects, mediaImport, NullLogger<MediaBrowserViewModel>.Instance),
                new PreviewViewModel(_status, playback, _projects, NullLogger<PreviewViewModel>.Instance),
                new InspectorViewModel(_edit, _status),
                new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance),
                _status, files, _projects, NullLogger<MainWindowViewModel>.Instance, training: _session);
            var workout = _session.Workout!;
            string Value(double secondsSinceStart) => string.Format(Strings.Readout_Value,
                TrainingText.WatchClock(Start.AddSeconds(secondsSinceStart), workout.UtcOffset), Bpm(secondsSinceStart));
            string Reason(string reason) => string.Format(Strings.Readout_NoValue, reason);
            void At(double seconds) => vm.Timeline.SetPlayhead(MediaTime.FromSeconds(seconds));

            vm.Timeline.SelectClipFromPreview(ClipOf(a).Id);
            At(1);
            Assert.Equal(Value(6), vm.Inspector.TrainingReadout);
            At(3);
            Assert.Equal(Value(8), vm.Inspector.TrainingReadout);                // follows the playhead
            At(17);                                                               // w = start + 22 s: the timer's pause
            Assert.Equal(Reason(string.Format(Strings.Readout_NoSample, TrainingText.WatchClock(Start.AddSeconds(22), workout.UtcOffset))),
                vm.Inspector.TrainingReadout);
            At(50);                                                               // outside the selected clip
            Assert.Equal(Reason(Strings.Readout_OutsideClip), vm.Inspector.TrainingReadout);

            vm.Timeline.SelectClipFromPreview(ClipOf(b).Id);
            At(59);                                                               // w = start + 51 s
            Assert.Equal(Value(51), vm.Inspector.TrainingReadout);
            At(59.5);                                                             // w = start + 51.5 s
            var byFileTime = vm.Inspector.TrainingReadout;
            // D055 §2: a new recording zero refines the route bounds only — the readout stays on the file's time.
            Assert.True(_edit.SetRecordingZeroOverride(b.Id, Start.AddSeconds(50)).Success);
            At(59.5);
            Assert.Equal(byFileTime, vm.Inspector.TrainingReadout);
            Assert.True(_edit.SetTrainingClockCorrection(MediaTime.FromSeconds(18)).Success);
            At(59.5);                                                             // w = start + 69.5 s: after the recording
            Assert.Equal(Reason(string.Format(Strings.Readout_OutsideRecording, TrainingText.WatchClock(Start.AddSeconds(69.5), workout.UtcOffset))),
                vm.Inspector.TrainingReadout);
            Assert.True(_edit.SetTrainingClockCorrection(MediaTime.Zero).Success);

            vm.Timeline.SelectClipFromPreview(ClipOf(c).Id);                     // the unaligned clip
            At(65);
            Assert.Equal(Reason(Strings.Readout_NoFileTime), vm.Inspector.TrainingReadout);

            vm.Timeline.SelectClipFromPreview(ClipOf(a).Id);
            _projects.Current.Training!.Availability = TrainingAvailability.Missing;
            _session.NotifyChanged();
            At(2);
            Assert.Equal(Reason(Strings.Readout_TrainingNotAvailable), vm.Inspector.TrainingReadout);
            _workflow.Remove();
            At(3);
            Assert.Equal(Reason(Strings.Readout_NoTraining), vm.Inspector.TrainingReadout);
        }
        finally
        {
            await playback.DisposeAsync();
        }
    }

    // --- C9: edits keep a picture's watch time --------------------------------------------------------------------------

    /// <summary>The timeline frame of <paramref name="clip"/> that shows source time <paramref name="source"/> (the clip's
    /// exact rule), or null.</summary>
    private static long? FrameShowing(VideoClip clip, MediaTime source)
    {
        var first = clip.TimelineStart.ToFrameFloor(Rate);
        var end = clip.TimelineEnd.ToFrameFloor(Rate);
        for (var n = first; n < end; n++)
            if (TrainingAlignment.SourceTimeAt(clip, n, Rate) == source) return n;
        return null;
    }

    [Fact]
    public async Task C9_split_trim_move_slip_speed_and_duplicate_keep_the_watch_time_and_the_heart_rate_of_a_picture()
    {
        var (a, _) = await SessionAsync();
        // The picture of source frame 201 (8.04 s) of video A: shown at 1× and, after the slip to 7.00 s, at 2× (every other
        // source frame).
        var source = MediaTime.FromFrame(201, Rate);
        var asset = AssetOf(a.Id);
        TrainingReadout Picture(VideoClip clip) =>
            TrainingAlignment.ReadoutAt(_projects.Current.Training, clip, asset, FrameShowing(clip, source)!.Value, Rate);
        var original = Picture(ClipOf(a));
        Assert.Equal(Start.AddSeconds(5) + source.ToTimeSpan(), original.WatchTime);
        Assert.Equal(Bpm(13.04), original.Bpm);

        void Same(string step)
        {
            var clips = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>()
                .Where(c => c.MediaAssetId == a.Id && FrameShowing(c, source) is not null).ToList();
            Assert.True(clips.Count > 0, $"{step}: no clip shows the picture");
            foreach (var clip in clips) Assert.True(original == Picture(clip), $"{step}: {Picture(clip)} instead of {original}");
        }

        Assert.True(_edit.Split(MediaTime.FromSeconds(5), new[] { ClipOf(a).Id }).Success);
        Same("split");
        var right = _projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>()
            .Where(c => c.MediaAssetId == a.Id).OrderBy(c => c.TimelineStart).Last();
        Assert.True(_edit.TrimClip(right.Id, ClipEdge.Start, MediaTime.FromSeconds(6)).Success);
        Same("trim");
        Assert.True(_edit.MoveClips(new[] { right.Id }, 25 * 3).Success);
        Same("move");
        Assert.True(_edit.SlipClip(right.Id, 25).Success);
        Same("slip");
        Assert.True(_edit.SetClipSpeed(right.Id, ClipSpeed.FromSteps(40)).Success);   // 2×
        Same("speed 2×");
        Assert.True(_edit.DuplicateClips(new[] { right.Id }).Success);
        var copies = _projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Count(c => c.MediaAssetId == a.Id);
        Assert.Equal(3, copies);
        Same("duplicate");
    }
}
