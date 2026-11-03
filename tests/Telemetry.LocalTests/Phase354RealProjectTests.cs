using System.Reflection;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Media;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Telemetry.LocalTests;

/// <summary>
/// Step 35.4's manual rows that need the product owner's real session of 2026-09-27, run on it read-only: the FIT (copied
/// to a temporary folder where a row saves a project) and <c>video_20260927_103026.mp4</c> probed in place, with the owner's
/// own frozen reference marks of climb 5 in that video (the study of 35.3R2: the start at 33.4 s — the last foot leaves the
/// floor —, the end at 83.3 s — the last touch of the wall). C8 / C8b / C8c through the Inspector, C1 through a save and an
/// open with the real FIT, C12 with the process's time zone replaced. Nothing beside the FIT or the videos is written.
/// </summary>
[Collection(nameof(Phase354RealProjectTests))]
[CollectionDefinition(nameof(Phase354RealProjectTests), DisableParallelization = true)]
public sealed class Phase354RealProjectTests : IDisposable
{
    private const string VideoName = "video_20260927_103026.mp4";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "aive-35-4-real", Guid.NewGuid().ToString("N"));

    public Phase354RealProjectTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The owner's marks of climb 5 in the video, from the frozen study (read-only): start and end source ticks.</summary>
    private static (long StartTicks, long EndTicks) OwnersMarksOfClimb5()
    {
        var study = Path.ChangeExtension(RealSet.FitPath, ".syncstudy.json");
        using var json = JsonDocument.Parse(File.ReadAllText(study));
        var draft = json.RootElement.GetProperty("draft");
        var videoKey = draft.GetProperty("videos").EnumerateArray()
            .Single(v => v.GetProperty("originalFileName").GetString() == VideoName).GetProperty("videoKey").GetString();
        var mark = draft.GetProperty("marks").EnumerateArray().Single(m => m.GetProperty("videoKey").GetString() == videoKey);
        var markId = mark.GetProperty("markId").GetString();
        var decision = draft.GetProperty("decisions").EnumerateArray().Single(d => d.GetProperty("markId").GetString() == markId);
        Assert.Equal(5, decision.GetProperty("climb").GetProperty("climbNumber").GetInt32());   // confirmed as climb 5
        return (mark.GetProperty("startSourceTicks").GetInt64(), mark.GetProperty("endSourceTicks").GetInt64());
    }

    private sealed class Session
    {
        public required UndoRedoService Undo { get; init; }
        public required ProjectService Projects { get; init; }
        public required TimelineEditService Edit { get; init; }
        public required TrainingSessionService Training { get; init; }
        public required InspectorViewModel Inspector { get; init; }
        public required MediaAsset Asset { get; init; }
        public required VideoClip Clip { get; init; }
        public required FrameRate Rate { get; init; }
        public MediaTime Playhead { get; set; }
        public long FrameOf(long sourceTicks) => new MediaTime(sourceTicks).ToFrameFloor(Rate);
        public void At(long frame) { Playhead = MediaTime.FromFrame(frame, Rate); Inspector.OnPlayheadMoved(); }
    }

    /// <summary>A new project at the video's own rate with the video's clip from its start and the real training attached
    /// (the FIT read from <paramref name="fitPath"/>).</summary>
    private static async Task<Session> NewSessionAsync(string fitPath)
    {
        var asset = (await RealSet.ProbeAsync(new[] { Path.Combine(Path.GetDirectoryName(RealSet.FitPath)!, VideoName) })).Single();
        asset.AnalysisStatus = MediaAnalysisStatus.Completed;
        asset.FileSizeBytes = new FileInfo(asset.FilePath).Length;
        var rate = asset.Metadata!.FrameRate!.Value;
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        projects.Current.Settings.FrameRate = rate;
        projects.Current.Settings.IsFrameRateLocked = true;
        projects.AddMediaAssets(new[] { asset });
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var add = edit.AddClip(asset.Id, null, MediaTime.Zero);               // as «На таймлайн» adds it
        Assert.True(add.Success, add.Message);
        var clip = (VideoClip)projects.Current.Timeline.VideoTracks[0].Clips.Single(c => c.Id == add.ClipIds![0]);
        var import = new TrainingImportService(NullLogger<TrainingImportService>.Instance);
        var training = new TrainingSessionService(projects, import);
        training.Attach(await import.LoadAsync(fitPath), fitPath);
        Session? session = null;
        var inspector = new InspectorViewModel(edit, new StatusService()) { Training = training, Playhead = () => session!.Playhead };
        session = new Session
        {
            Undo = undo, Projects = projects, Edit = edit, Training = training, Inspector = inspector, Asset = asset, Clip = clip, Rate = rate
        };
        var selection = new TimelineClipSelection(clip, VideoName, asset, rate);
        inspector.ShowClip(selection);
        // What the main window does after every edit: the timeline shows the selected clip again in the Inspector.
        projects.TimelineChanged += (_, _) => inspector.ShowClip(selection);
        return session;
    }

    private static string Clock(DateTimeOffset w, ClimbingWorkout workout) => TrainingText.WatchClock(w, workout.UtcOffset);

    // --- C8, C8b, C8c ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task C8_C8b_C8c_matching_climb_5_by_its_end_and_by_its_start_on_the_owners_frames()
    {
        var s = await NewSessionAsync(RealSet.FitPath);
        var workout = s.Training.Workout!;
        var climb5 = workout.Climbs.Single(c => c.ClimbNumber == 5);
        var (startTicks, endTicks) = OwnersMarksOfClimb5();
        var endFrame = s.FrameOf(endTicks);
        var startFrame = s.FrameOf(startTicks);

        // The list and the default point.
        var item = string.Format(Strings.Inspector_ClimbItem, 5, "10:31:21", "10:31:53");
        Assert.Contains(item, s.Inspector.TrainingClimbs);
        Assert.Equal(ClimbAnchor.End, s.Inspector.AlignAnchor);
        Assert.Equal(Strings.Inspector_RecordingStartUnknown, s.Inspector.RecordingStartText);

        // C8: by the end on the frame of the last touch of the wall.
        s.Inspector.SelectedClimbIndex = s.Inspector.TrainingClimbs.IndexOf(item);
        s.At(endFrame);
        var history = s.Undo.CurrentPosition;
        s.Inspector.AlignToClimbCommand.Execute(null);
        var endReadout = TrainingAlignment.ReadoutAt(s.Projects.Current.Training, s.Clip, s.Asset, endFrame, s.Rate);
        Assert.Equal(climb5.End - TimeSpan.FromSeconds(4.6), endReadout.WatchTime!.Value, TimeSpan.FromTicks(MediaTime.FromFrame(1, s.Rate).Ticks));
        Assert.Equal("10:31:48", Clock(endReadout.WatchTime.Value, workout));
        Assert.Equal(string.Format(Strings.Readout_Value, "10:31:48", endReadout.Bpm), s.Inspector.TrainingReadout);
        Assert.Equal(Strings.Inspector_RecordingStartSet, s.Inspector.RecordingStartText);
        Assert.Equal(MediaTime.Zero, s.Projects.Current.Training!.ClockCorrection);
        var zeroByEnd = s.Asset.RecordingZeroOverrideUtc;
        s.Undo.Undo();
        Assert.Same(history, s.Undo.CurrentPosition);                         // one Undo step
        Assert.Null(s.Asset.RecordingZeroOverrideUtc);
        s.Undo.Redo();
        Assert.Equal(zeroByEnd, s.Asset.RecordingZeroOverrideUtc);

        // C8b: by the start on the frame where the last foot leaves the floor.
        s.Inspector.AlignAnchorIndex = 1;
        Assert.Equal(ClimbAnchor.Start, s.Inspector.AlignAnchor);
        s.At(startFrame);
        s.Inspector.AlignToClimbCommand.Execute(null);
        var startReadout = TrainingAlignment.ReadoutAt(s.Projects.Current.Training, s.Clip, s.Asset, startFrame, s.Rate);
        Assert.Equal("10:31:21", Clock(startReadout.WatchTime!.Value, workout));
        Assert.Equal(string.Format(Strings.Readout_Value, "10:31:21", startReadout.Bpm), s.Inspector.TrainingReadout);
        Assert.NotEqual(zeroByEnd, s.Asset.RecordingZeroOverrideUtc);          // the zero replaced
        var zeroByStart = s.Asset.RecordingZeroOverrideUtc;
        s.Undo.Undo();                                                        // one step back: the match by the end
        Assert.Equal(zeroByEnd, s.Asset.RecordingZeroOverrideUtc);
        s.Undo.Redo();
        Assert.Equal(zeroByStart, s.Asset.RecordingZeroOverrideUtc);

        // C8c: switching the point changes nothing; matching by the end again gives exactly C8's readout.
        var positionBefore = s.Undo.CurrentPosition;
        var readoutBefore = s.Inspector.TrainingReadout;
        s.Inspector.AlignAnchorIndex = 0;
        s.Inspector.AlignAnchorIndex = 1;
        s.Inspector.AlignAnchorIndex = 0;
        Assert.Same(positionBefore, s.Undo.CurrentPosition);
        Assert.Equal(zeroByStart, s.Asset.RecordingZeroOverrideUtc);
        Assert.Equal(readoutBefore, s.Inspector.TrainingReadout);
        Assert.Equal(Strings.Inspector_RecordingStartSet, s.Inspector.RecordingStartText);
        s.At(endFrame);
        s.Inspector.AlignToClimbCommand.Execute(null);
        Assert.Equal(zeroByEnd, s.Asset.RecordingZeroOverrideUtc);              // the 4.6 s never accumulate
        Assert.Equal(endReadout, TrainingAlignment.ReadoutAt(s.Projects.Current.Training, s.Clip, s.Asset, endFrame, s.Rate));
        s.Undo.Undo();
        Assert.Equal(zeroByStart, s.Asset.RecordingZeroOverrideUtc);
        s.Undo.Undo();
        Assert.Equal(zeroByEnd, s.Asset.RecordingZeroOverrideUtc);
        s.Undo.Undo();
        Assert.Null(s.Asset.RecordingZeroOverrideUtc);
    }

    // --- C1 with the real FIT ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task C1_the_real_training_survives_save_and_open_and_gives_the_same_heart_rate()
    {
        var fit = Path.Combine(_temp, Path.GetFileName(RealSet.FitPath));
        File.Copy(RealSet.FitPath, fit);
        var s = await NewSessionAsync(fit);
        var (_, endTicks) = OwnersMarksOfClimb5();
        s.Inspector.SelectedClimbIndex = s.Inspector.TrainingClimbs.IndexOf(string.Format(Strings.Inspector_ClimbItem, 5, "10:31:21", "10:31:53"));
        s.At(s.FrameOf(endTicks));
        s.Inspector.AlignToClimbCommand.Execute(null);
        var frames = new long[] { 0, s.FrameOf(endTicks), s.FrameOf(endTicks) + 600, s.Clip.TimelineEnd.ToFrameFloor(s.Rate) - 1 };
        var before = frames.Select(f => TrainingAlignment.ReadoutAt(s.Projects.Current.Training, s.Clip, s.Asset, f, s.Rate)).ToList();
        Assert.All(before, r => Assert.NotNull(r.Bpm));

        var folder = Path.Combine(_temp, "project");
        await s.Projects.SaveAsAsync(folder);
        await s.Projects.OpenAsync(folder);
        await s.Training.ResolveAsync();

        var project = s.Projects.Current;
        Assert.Equal(TrainingAvailability.Available, project.Training!.Availability);
        var asset = project.MediaAssets.Single();
        var clip = project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single();
        Assert.Equal(s.Asset.RecordingZeroOverrideUtc, asset.RecordingZeroOverrideUtc);
        Assert.Equal(before, frames.Select(f => TrainingAlignment.ReadoutAt(project.Training, clip, asset, f, s.Rate)).ToList());
    }

    // --- C12: another time zone -------------------------------------------------------------------------------------------

    /// <summary>Replaces the process's local time zone (what changing the Windows setting and restarting gives the app),
    /// runs <paramref name="body"/> and restores it. The time-zone cache is internal to .NET; the field is checked.</summary>
    private static T InTimeZone<T>(string id, Func<T> body)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        TimeZoneInfo.ClearCachedData();                                       // a new cache: take it after clearing
        var cache = typeof(TimeZoneInfo).GetField("s_cachedData", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        cache.GetType().GetField("_localTimeZone", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cache, zone);
        try
        {
            Assert.Equal(id, TimeZoneInfo.Local.Id);
            return body();
        }
        finally
        {
            TimeZoneInfo.ClearCachedData();
        }
    }

    [Fact]
    public async Task C12_another_time_zone_gives_the_identical_mapping_and_heart_rate()
    {
        var s = await NewSessionAsync(RealSet.FitPath);
        var (_, endTicks) = OwnersMarksOfClimb5();
        s.Inspector.SelectedClimbIndex = s.Inspector.TrainingClimbs.IndexOf(string.Format(Strings.Inspector_ClimbItem, 5, "10:31:21", "10:31:53"));
        s.At(s.FrameOf(endTicks));
        s.Inspector.AlignToClimbCommand.Execute(null);
        var folder = Path.Combine(_temp, "project");
        await s.Projects.SaveAsAsync(folder);
        var json = File.ReadAllText(ProjectFileStore.ProjectFilePath(folder));
        var frames = Enumerable.Range(0, 12).Select(i => i * 500L).ToList();

        // Everything the user sees of the mapping, from the saved project, as a fresh start in a time zone would give it.
        List<string> Seen()
        {
            var project = ProjectSerializer.Deserialize(json, folder, _ => true);
            project.Training!.Workout = s.Training.Workout;
            project.Training.Availability = TrainingAvailability.Available;
            var asset = project.MediaAssets.Single();
            var clip = project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single();
            return frames.Select(f => TrainingAlignment.ReadoutAt(project.Training, clip, asset, f, s.Rate))
                .Select(r => $"{r.WatchTime?.UtcTicks}|{(r.WatchTime is { } w ? Clock(w, s.Training.Workout!) : "")}|{r.Bpm}|{r.Reason}")
                .Prepend(string.Join(";", s.Training.Workout!.Climbs.Select(c => $"{Clock(c.StartUtc, s.Training.Workout)}–{Clock(c.End, s.Training.Workout)}")))
                .ToList();
        }

        // The control: the replaced zone does change what local time is in the process.
        var instant = new DateTimeOffset(2026, 9, 27, 7, 0, 0, TimeSpan.Zero);
        Assert.NotEqual(InTimeZone("Russian Standard Time", () => instant.ToLocalTime().Hour),
            InTimeZone("Pacific Standard Time", () => instant.ToLocalTime().Hour));

        var moscow = InTimeZone("Russian Standard Time", Seen);
        var pacific = InTimeZone("Pacific Standard Time", Seen);
        var tokyo = InTimeZone("Tokyo Standard Time", Seen);
        Assert.Equal(moscow, pacific);
        Assert.Equal(moscow, tokyo);
        Assert.Contains(moscow, line => line.Contains("10:31:48"));
    }
}
