using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 18 Step 18.4 (D033 T3): video metadata saved before Phase 18 (no pixel format) is probed again in the
/// background after Open — the asset stays Completed, the project stays clean, the asset is marked
/// <see cref="MediaAsset.IsMetadataRefreshing"/> only while the probe is queued or running (so the export waits for it),
/// a failed probe keeps the metadata and the asset counts as SDR. Images and audio are not probed for colour. The
/// Inspector shows the colour.
/// </summary>
public sealed class MediaColorRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly GatedAnalysis _analysis = new();
    private readonly ProjectFileWorkflow _workflow;
    private readonly MediaAnalysisCoordinator _coordinator;

    public MediaColorRefreshTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _coordinator = new MediaAnalysisCoordinator(_analysis, _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        _workflow = new ProjectFileWorkflow(_projects, _coordinator, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(),
            new StatusService(), NullLogger<ProjectFileWorkflow>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>An analysis that waits until the test releases it, so the "probe running" state can be seen.</summary>
    private sealed class GatedAnalysis : IMediaAnalysisService
    {
        public List<string> Calls { get; } = new();
        public TaskCompletionSource<MediaAnalysisResult> Gate { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add(filePath);
            return Gate.Task;
        }
    }

    /// <summary>The Vivo clip as probed by Phase 18.</summary>
    private static MediaMetadata ProbedHlg() => new()
    {
        Duration = MediaTime.FromSeconds(10), Width = 3840, Height = 2160, DisplayRotation = 0, DisplayWidth = 3840, DisplayHeight = 2160,
        VideoCodec = "hevc", PixelFormat = "yuv420p10le", BitDepth = 10, ColorTransfer = "arib-std-b67", ColorPrimaries = "bt2020",
        ColorMatrix = "bt2020nc", ColorRange = "tv", DolbyVisionProfile = 8, DolbyVisionCompatibility = 4
    };

    /// <summary>The same clip as saved by Phase 17: display size, no colour.</summary>
    private static MediaAsset SavedByPhase17(string path, MediaKind kind = MediaKind.Video) => new()
    {
        FilePath = path, Kind = kind, AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(10), Width = 3840, Height = 2160, DisplayRotation = 0, DisplayWidth = 3840, DisplayHeight = 2160,
            VideoCodec = "hevc"
        }
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
    public async Task A_phase_17_video_is_probed_for_its_colour_once_marked_refreshing_meanwhile_and_the_project_stays_clean()
    {
        var path = File_("vivo.mp4");
        var folder = WriteProject(SavedByPhase17(path));
        var saved = File.ReadAllText(ProjectFileStore.ProjectFilePath(folder));

        Assert.True(await _workflow.OpenAsync(folder));
        var asset = Assert.Single(_projects.Current.MediaAssets);
        Assert.Equal(new[] { path }, _analysis.Calls);
        Assert.True(asset.IsMetadataRefreshing);                               // the export waits (D033 T3)
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);     // still usable meanwhile

        _analysis.Gate.SetResult(MediaAnalysisResult.Success(ProbedHlg()));
        await _coordinator.IdleAsync();

        Assert.False(asset.IsMetadataRefreshing);
        Assert.Equal(("arib-std-b67", 10), (asset.Metadata!.ColorTransfer, asset.Metadata.BitDepth!.Value));
        Assert.False(asset.Metadata.NeedsColorProbe);
        Assert.False(_projects.Current.IsDirty);
        Assert.Equal(saved, File.ReadAllText(ProjectFileStore.ProjectFilePath(folder)));   // nothing rewritten
        Assert.Single(_analysis.Calls);
    }

    [Fact]
    public async Task A_failed_colour_probe_keeps_the_metadata_and_ends_the_refreshing_state()
    {
        var folder = WriteProject(SavedByPhase17(File_("broken.mp4")));

        Assert.True(await _workflow.OpenAsync(folder));
        _analysis.Gate.SetResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "no ffprobe"));
        await _coordinator.IdleAsync();

        var asset = Assert.Single(_projects.Current.MediaAssets);
        Assert.False(asset.IsMetadataRefreshing);                              // never waited for again: SDR as in Phase 17
        Assert.True(asset.Metadata!.NeedsColorProbe);
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.False(_projects.Current.IsDirty);
    }

    [Fact]
    public async Task Images_and_audio_are_not_probed_for_colour()
    {
        var image = SavedByPhase17(File_("still.png"), MediaKind.Image);
        var audio = new MediaAsset
        {
            FilePath = File_("tone.wav"), Kind = MediaKind.Audio, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(4), AudioCodec = "pcm_s16le" }
        };
        var folder = WriteProject(image, audio);

        Assert.True(await _workflow.OpenAsync(folder));

        Assert.Empty(_analysis.Calls);
        Assert.All(_projects.Current.MediaAssets, a => Assert.False(a.IsMetadataRefreshing));
    }

    [Fact]
    public void The_Inspector_shows_the_colour_of_a_video()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var inspector = new InspectorViewModel(new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance), new StatusService());

        inspector.ShowMedia(new MediaAsset
        {
            FilePath = Path.Combine(_root, "vivo.mp4"), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = ProbedHlg()
        });

        var row = Assert.Single(inspector.TechnicalRows, r => r.Label == "Color");
        Assert.Equal("HDR · HLG · BT.2020 · 10-bit · Dolby Vision 8.4 (not applied)", row.Value);

        inspector.ShowMedia(SavedByPhase17(Path.Combine(_root, "old.mp4")));   // colour not probed yet: no row
        Assert.DoesNotContain(inspector.TechnicalRows, r => r.Label == "Color");
    }
}
