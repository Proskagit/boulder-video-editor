using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Step 10.9 manual run: the same project opened a second time (Open again, or after Save / Save As) has new clip
/// objects with the ids of the old ones. The timeline must show — and hand the Inspector — the clips of the project
/// that is open now; before the fix it kept the clip view models (keyed by id) of the first load, so speed, split,
/// fades and the dissolve changed the model while the timeline and the Inspector showed the old clips.
/// </summary>
public sealed class ReopenedProjectTimelineTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly MainWindowViewModel _vm;

    public ReopenedProjectTimelineTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance);
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status, editingLock: new EditingLock()),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, new EditingLock()),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    /// <summary>A saved project with one 20 s video split at frame 100 (two clips that meet, with handles), opened
    /// twice — as the product owner's project was.</summary>
    public async Task InitializeAsync()
    {
        var media = Path.Combine(_root, "media", "v.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(media)!);
        await File.WriteAllTextAsync(media, "x");
        var asset = new MediaAsset
        {
            FilePath = media,
            Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(20), FrameRate = Rate, Width = 1920, Height = 1080 }
        };
        _projects.AddMediaAssets(new[] { asset });
        Assert.True(_edit.AddClip(asset.Id).Success);
        Assert.True(_edit.Split(F(100)).Success);

        var folder = Path.Combine(_root, "project");
        await _projects.SaveAsAsync(folder);
        await _projects.OpenAsync(folder);
        await _projects.OpenAsync(folder);
    }

    public async Task DisposeAsync()
    {
        await _playback.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private TimelineViewModel Timeline => _vm.Timeline;
    private InspectorViewModel Inspector => _vm.Inspector;
    private Track V1 => _projects.Current.Timeline.VideoTracks[0];
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);
    private TimelineClipViewModel ClipVm(Clip clip) => Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);
    private double X(long frame) => TimelineCoordinateMapper.TimeToX(F(frame), Timeline.PixelsPerSecond);

    [Fact]
    public void The_timeline_shows_the_clips_of_the_project_open_now()
    {
        foreach (var clip in V1.Clips)
            Assert.Same(clip, ClipVm(clip).Clip);
    }

    [Fact]
    public void A_speed_change_resizes_the_clip_and_undo_restores_it()
    {
        var a = V1.Clips[0];
        Timeline.OnClipPressed(ClipVm(a), toggle: false);

        Inspector.SpeedValue = 2m;

        Assert.Equal(F(50), a.TimelineEnd);
        Assert.Equal(X(50), ClipVm(a).Width, 6);
        Assert.Equal(2m, Inspector.SpeedValue);

        _undo.Undo();
        Assert.Equal(X(100), ClipVm(a).Width, 6);
        _undo.Redo();
        Assert.Equal(X(50), ClipVm(a).Width, 6);
    }

    [Fact]
    public void A_split_gives_two_clips_side_by_side_and_undo_one_again()
    {
        var a = V1.Clips[0];
        Timeline.SetPlayhead(F(40));
        Assert.True(_edit.Split(F(40)).Success);

        var (left, right) = (V1.Clips[0], V1.Clips[1]);
        Assert.Equal((0d, X(40)), (ClipVm(left).Left, ClipVm(left).Width));
        Assert.Equal((X(40), X(60)), (ClipVm(right).Left, ClipVm(right).Width));

        _undo.Undo();
        Assert.Equal(2, Timeline.Tracks.SelectMany(t => t.Clips).Count());
        Assert.Equal(X(100), ClipVm(a).Width, 6);
    }

    [Fact]
    public void A_fade_typed_in_the_inspector_stays_and_is_drawn()
    {
        var a = V1.Clips[0];
        Timeline.OnClipPressed(ClipVm(a), toggle: false);

        Inspector.FadeInFrames = 20;

        Assert.Equal(F(20), a.FadeIn);
        Assert.Equal(20m, Inspector.FadeInFrames);
        Assert.Equal(X(20), ClipVm(a).FadeInWidth, 6);
    }

    [Fact]
    public void A_dissolve_can_be_added_on_the_cut_and_is_drawn()
    {
        var (a, b) = (V1.Clips[0], V1.Clips[1]);
        Timeline.OnClipPressed(ClipVm(a), toggle: false);
        Timeline.OnClipPressed(ClipVm(b), toggle: true);

        Timeline.AddDissolveCommand.Execute(null);

        var transition = Assert.Single(V1.Transitions);
        Assert.Equal((a.Id, b.Id), (transition.LeftClipId, transition.RightClipId));
        Assert.Equal(TransitionRules.Frames(transition.Duration, Rate), (long?)Inspector.DissolveFrames);
        Assert.Single(Timeline.Tracks.SelectMany(t => t.Transitions));
    }

    private sealed class NoAnalysis : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "test"));
    }

    private sealed class NoImport : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromResult(new MediaImportBatchResult());
    }
}
