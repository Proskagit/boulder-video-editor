using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 7 Step 6: metadata saved before orientation was probed (coded size, no display size) is
/// refreshed in the background after Open — the asset stays Completed and usable, the project stays
/// clean, missing files are not probed, a failed probe keeps the saved metadata. Users see the
/// display size.
/// </summary>
public sealed class MediaOrientationRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly ScriptedAnalysis _analysis = new();
    private readonly StatusService _status = new();
    private readonly ProjectFileWorkflow _workflow;
    private readonly List<MediaAnalysisStatus> _statusesSeen = new();

    public MediaOrientationRefreshTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        var coordinator = new MediaAnalysisCoordinator(_analysis, _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        _workflow = new ProjectFileWorkflow(_projects, coordinator, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status,
            NullLogger<ProjectFileWorkflow>.Instance);
        _projects.MediaAssetsChanged += (_, _) => _statusesSeen.AddRange(_projects.Current.MediaAssets.Select(a => a.AnalysisStatus));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class ScriptedAnalysis : IMediaAnalysisService
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public Func<string, MediaAnalysisResult> Result { get; set; } = _ => MediaAnalysisResult.Success(Portrait());

        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            Calls.Enqueue(filePath);
            return Task.FromResult(Result(filePath));
        }
    }

    /// <summary>A phone video as probed now: coded landscape, displayed portrait.</summary>
    private static MediaMetadata Portrait() => new()
    {
        Duration = MediaTime.FromSeconds(10), Width = 1920, Height = 1080,
        DisplayRotation = 90, DisplayWidth = 1080, DisplayHeight = 1920, VideoCodec = "h264"
    };

    /// <summary>The same video as saved by Phase 6: coded size only.</summary>
    private static MediaAsset SavedBeforeOrientation(string path) => new()
    {
        FilePath = path,
        Kind = MediaKind.Video,
        AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), Width = 1920, Height = 1080, VideoCodec = "h264" }
    };

    private string File_(string name)
    {
        var path = Path.Combine(_root, "media", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private string WriteProject(params MediaAsset[] assets)
    {
        var folder = Path.Combine(_root, "Film");
        var project = new Core.Entities.Project { Name = "Film" };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        project.MediaAssets.AddRange(assets);
        Directory.CreateDirectory(folder);
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder));
        return folder;
    }

    [Fact]
    public async Task Stale_metadata_of_available_media_is_refreshed_silently_and_the_project_stays_clean()
    {
        var path = File_("phone.mp4");
        var folder = WriteProject(SavedBeforeOrientation(path));
        var projectFile = File.ReadAllText(ProjectFileStore.ProjectFilePath(folder));

        Assert.True(await _workflow.OpenAsync(folder));

        Assert.Equal(new[] { path }, _analysis.Calls);
        var asset = Assert.Single(_projects.Current.MediaAssets);
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.DoesNotContain(MediaAnalysisStatus.Analyzing, _statusesSeen);   // no "Analyzing…" flicker
        Assert.Equal((90, 1080, 1920), (asset.Metadata!.DisplayRotation!.Value, asset.Metadata.DisplayWidth!.Value, asset.Metadata.DisplayHeight!.Value));
        Assert.Equal((1920, 1080), (asset.Metadata.Width!.Value, asset.Metadata.Height!.Value)); // coded size unchanged
        Assert.False(_projects.Current.IsDirty);
        Assert.Equal(projectFile, File.ReadAllText(ProjectFileStore.ProjectFilePath(folder))); // nothing rewritten
    }

    [Fact]
    public async Task Missing_media_with_stale_metadata_is_not_probed_and_stays_offline()
    {
        var folder = WriteProject(SavedBeforeOrientation(Path.Combine(_root, "media", "gone.mp4")));

        Assert.True(await _workflow.OpenAsync(folder));

        Assert.Empty(_analysis.Calls);
        var asset = Assert.Single(_projects.Current.MediaAssets);
        Assert.True(asset.IsMissing);
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.Equal(MediaTime.FromSeconds(10), asset.Metadata!.Duration); // saved metadata kept
        Assert.True(asset.Metadata.NeedsDisplaySizeProbe);
        Assert.False(_projects.Current.IsDirty);
    }

    [Fact]
    public async Task Failed_refresh_keeps_the_saved_metadata()
    {
        var path = File_("broken.mp4");
        var folder = WriteProject(SavedBeforeOrientation(path));
        _analysis.Result = _ => MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeProcessFailed, "boom");

        Assert.True(await _workflow.OpenAsync(folder));

        var asset = Assert.Single(_projects.Current.MediaAssets);
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.Null(asset.AnalysisError);
        Assert.Equal((1920, 1080), (asset.Metadata!.Width!.Value, asset.Metadata.Height!.Value));
        Assert.False(_projects.Current.IsDirty);
    }

    [Fact]
    public async Task Current_metadata_is_not_probed_again()
    {
        var path = File_("new.mp4");
        var asset = SavedBeforeOrientation(path);
        asset.Metadata = Portrait();
        var folder = WriteProject(asset);

        Assert.True(await _workflow.OpenAsync(folder));

        Assert.Empty(_analysis.Calls);
    }

    // --- What the user sees -------------------------------------------------------------------

    [Fact]
    public void Resolution_is_the_display_size_with_the_coded_size_as_fallback()
    {
        Assert.Equal("1080×1920", ResolutionFormat.Display(Portrait()));
        Assert.Equal("Rotated 90°", ResolutionFormat.Orientation(Portrait()));
        Assert.Equal("1920×1080", ResolutionFormat.Display(SavedBeforeOrientation("x").Metadata!));
        Assert.Null(ResolutionFormat.Orientation(new MediaMetadata { DisplayRotation = 0 }));
        Assert.Null(ResolutionFormat.Orientation(new MediaMetadata { DisplayRotation = null, DisplayWidth = 5, DisplayHeight = 5 }));
        Assert.Null(ResolutionFormat.Display(new MediaMetadata()));
    }

    [Fact]
    public void Inspector_and_media_browser_show_the_display_size()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(_root, "phone.mp4"), Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed, Metadata = Portrait()
        };
        var inspector = new InspectorViewModel(new TimelineEditServiceStub(), _status);

        inspector.ShowMedia(asset);

        Assert.Contains(inspector.TechnicalRows, r => r.Label == "Resolution" && r.Value == "1080×1920");
        Assert.Contains(inspector.TechnicalRows, r => r.Label == "Orientation" && r.Value == "Rotated 90°");
        Assert.Contains("1080×1920", new MediaBrowserItemViewModel(asset).TechnicalSummary);
    }

    private sealed class TimelineEditServiceStub : ITimelineEditService
    {
        private static readonly TimelineEditResult No = TimelineEditResult.Fail("stub");
        public FrameRate FrameRate => FrameRate.Default;
        public string? GetAddBlockReason(MediaAsset asset) => null;
        public TimelineEditResult AddClip(Guid mediaAssetId, Guid? trackId = null, MediaTime? start = null) => No;
        public TimelineEditResult AddTextClip(MediaTime start) => No;
        public TimelineEditResult SetClipSpeed(Guid clipId, ClipSpeed speed) => No;
        public TimelineEditResult MoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null) => No;
        public string? CanMoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null) => null;
        public TimelineEditResult TrimClip(Guid clipId, ClipEdge edge, MediaTime edgeTime) => No;
        public (MediaTime Start, MediaTime End)? PreviewTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime) => null;
        public TimelineEditResult Split(MediaTime at, IReadOnlyCollection<Guid>? clipIds = null) => No;
        public TimelineEditResult DeleteClips(IReadOnlyCollection<Guid> clipIds) => No;
        public TimelineEditResult AddTrack(TrackType type) => No;
        public TimelineEditResult SetClipProperties(Guid clipId, ClipPropertyChange change) => No;
        public SnapResult Snap(IReadOnlyList<MediaTime> candidates, MediaTime tolerance, IReadOnlyCollection<Guid> excludedClipIds) => SnapResult.None;
    }
}
