using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Tests.Telemetry;
using AiVideoEditor.Media;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D054 through the real import workflow and the Inspector, with the synthetic COROS export of 2026-09-27 (climb 1 FIT
/// [0, 12.3] s, climb 2 FIT [40, 55.5] s after its start, the watch at UTC+3): a video named by its local time is bound by
/// the import itself — no Z, no choice of a climb; a clip added after the import is bound by «Распознать прохождения из
/// FIT» on it.
/// </summary>
public sealed class FitAutoBindingUiTests : IDisposable
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-d054", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly ScriptedPicker _picker = new();
    private readonly TrainingImportService _import = new(NullLogger<TrainingImportService>.Instance);
    private readonly TrainingSessionService _session;
    private readonly TrainingImportWorkflow _workflow;

    public FitAutoBindingUiTests()
    {
        Directory.CreateDirectory(_folder);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _session = new TrainingSessionService(_projects, _import);
        _workflow = new TrainingImportWorkflow(_picker, _import, _projects, _session, _status, NullLogger<TrainingImportWorkflow>.Instance,
            () => Task.CompletedTask, fitMarks: _edit);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A 120 s video named by the phone's local time (the training's start + <paramref name="offsetSeconds"/>),
    /// its clip at <paramref name="atSeconds"/> on V1.</summary>
    private VideoClip Video(int offsetSeconds, double atSeconds)
    {
        var local = CorosFixture.Start.AddHours(3).AddSeconds(offsetSeconds);
        var path = Path.Combine(_folder, $"video_{local:yyyyMMdd_HHmmss}.mp4");
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 24 });
        var asset = new MediaAsset
        {
            FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(120), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36 }
        };
        _projects.Current.MediaAssets.Add(asset);
        Assert.True(_edit.AddClip(asset.Id, null, MediaTime.FromSeconds(atSeconds)).Success);
        return _projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single(c => c.MediaAssetId == asset.Id);
    }

    private string Fit()
    {
        var path = Path.Combine(_folder, "a.fit");
        File.WriteAllBytes(path, CorosFixture.Writer().Build());
        return path;
    }

    private TemplateClip[] Areas() => _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>()
        .Where(t => t.FitClimb is not null).ToArray();

    [Fact]
    public async Task The_fit_import_binds_the_clips_on_the_timeline_by_itself()
    {
        var clip = Video(0, 0);                                    // the recording starts with the training

        _picker.Files.Enqueue(Fit());
        await _workflow.RunAsync();

        // Climb 2, estimated [28, 50.9] s: T = 28 s (700) → [525, 1398); climb 1 shows 7.7 s of its 19.7 s — no area.
        var area = Assert.Single(Areas());
        Assert.Equal(clip.Id, area.AnchorClipId);
        Assert.Equal((525L, 1398L), (area.TimelineStart.ToNearestFrame(Rate), area.TimelineEnd.ToNearestFrame(Rate)));
        Assert.Contains(string.Format(Messages.FitRoutes_Created, 1), _status.Message);
        Assert.Contains(Messages.FitRoutes_Approximate, _status.Message);
        Assert.Null(_projects.Current.MediaAssets.Single().RecordingZeroOverrideUtc);
        Assert.True(_undo.CanUndo);                                // the areas' own step (the import is none)
        _undo.Undo();
        Assert.Empty(Areas());
    }

    [Fact]
    public async Task A_clip_added_after_the_import_is_bound_by_the_inspectors_command()
    {
        _picker.Files.Enqueue(Fit());
        await _workflow.RunAsync();                                // nothing on the timeline yet
        Assert.Empty(Areas());
        var clip = Video(20, 0);                                   // starts 20 s into the training: climb 2 at 8 s

        var inspector = new InspectorViewModel(_edit, _status) { Training = _session, Playhead = () => MediaTime.Zero };
        var asset = _projects.Current.MediaAssets.Single();
        inspector.ShowClip(new TimelineClipSelection(clip, Path.GetFileName(asset.FilePath), asset, Rate));
        Assert.True(inspector.RecognizeFitClimbsCommand.CanExecute(null));

        inspector.RecognizeFitClimbsCommand.Execute(null);

        var area = Assert.Single(Areas());
        Assert.Equal(clip.Id, area.AnchorClipId);
        Assert.Equal((25L, 898L), (area.TimelineStart.ToNearestFrame(Rate), area.TimelineEnd.ToNearestFrame(Rate)));
        Assert.Contains(string.Format(Messages.FitRoutes_Created, 1), _status.Message);

        inspector.RecognizeFitClimbsCommand.Execute(null);         // again: nothing doubled
        Assert.Single(Areas());
        Assert.Equal(Messages.FitRoutes_ClipAllMade, _status.Message);
    }
}
