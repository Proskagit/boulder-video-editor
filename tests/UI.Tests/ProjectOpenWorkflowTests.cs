using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>Opening a project from the UI side: status message, missing media, and which media
/// gets analysed (real ProjectService and files; ffprobe replaced by a recording fake).</summary>
public sealed class ProjectOpenWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly RecordingAnalysis _analysis = new();
    private readonly StatusService _status = new();
    private readonly ProjectFileWorkflow _workflow;

    public ProjectOpenWorkflowTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        var coordinator = new MediaAnalysisCoordinator(_analysis, _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        _workflow = new ProjectFileWorkflow(_projects, coordinator, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Returns fixed metadata for every file and records which files were probed.</summary>
    private sealed class RecordingAnalysis : IMediaAnalysisService
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            Calls.Enqueue(filePath);
            return Task.FromResult(MediaAnalysisResult.Success(new MediaMetadata { Duration = new MediaTime(42_000_000), AudioSampleRate = 48000, AudioChannels = 2 }));
        }
    }

    private string File_(string name)
    {
        var path = Path.Combine(_root, "media", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private string Missing(string name) => Path.Combine(_root, "media", name);

    private static MediaAsset Analysed(string path) => new()
    {
        FilePath = path,
        Kind = MediaKind.Audio,
        AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata { Duration = new MediaTime(10_000_000), AudioSampleRate = 44100, AudioChannels = 1 }
    };

    private static MediaAsset NotAnalysed(string path, MediaAnalysisStatus status = MediaAnalysisStatus.Pending) =>
        new() { FilePath = path, Kind = MediaKind.Audio, AnalysisStatus = status };

    private string WriteProject(string name, params MediaAsset[] assets)
    {
        var folder = Path.Combine(_root, name);
        var project = new Core.Entities.Project { Name = name };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        project.MediaAssets.AddRange(assets);
        Directory.CreateDirectory(folder);
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder));
        return folder;
    }

    [Fact]
    public async Task All_media_available_with_saved_metadata_opens_without_analysis()
    {
        var folder = WriteProject("Film", Analysed(File_("a.wav")), Analysed(File_("b.wav")));

        Assert.True(await _workflow.OpenAsync(folder));

        Assert.Empty(_analysis.Calls);
        Assert.Equal("Opened project \"Film\".", _status.Message);
        Assert.All(_projects.Current.MediaAssets, a => Assert.Equal(MediaAnalysisStatus.Completed, a.AnalysisStatus));
        Assert.False(_projects.Current.IsDirty);
    }

    [Fact]
    public async Task Mixed_project_analyses_only_media_without_metadata()
    {
        var withMetadata = File_("with.wav");
        var without = File_("without.wav");
        var failedBefore = File_("failed-before.wav");
        var folder = WriteProject("Mixed",
            Analysed(withMetadata),
            NotAnalysed(without),
            NotAnalysed(failedBefore, MediaAnalysisStatus.Failed));

        await _workflow.OpenAsync(folder);

        Assert.Equal(new[] { without, failedBefore }.OrderBy(p => p), _analysis.Calls.OrderBy(p => p));
        var assets = _projects.Current.MediaAssets;
        Assert.All(assets, a => Assert.Equal(MediaAnalysisStatus.Completed, a.AnalysisStatus));
        Assert.Equal(44100, assets.Single(a => a.FilePath == withMetadata).Metadata!.AudioSampleRate); // untouched
        Assert.Equal(48000, assets.Single(a => a.FilePath == without).Metadata!.AudioSampleRate);    // freshly analysed
        Assert.False(_projects.Current.IsDirty); // analysis results are not user changes
    }

    [Fact]
    public async Task One_missing_file_is_reported_and_not_analysed()
    {
        var folder = WriteProject("Film", Analysed(File_("a.wav")), NotAnalysed(Missing("gone.wav")));

        Assert.True(await _workflow.OpenAsync(folder));

        Assert.Empty(_analysis.Calls);
        Assert.Equal("Opened project \"Film\". 1 media file is missing and is shown as offline.", _status.Message);
        var gone = _projects.Current.MediaAssets.Single(a => a.IsMissing);
        Assert.Equal(MediaAnalysisStatus.Pending, gone.AnalysisStatus); // not turned into a failed probe
        Assert.False(_projects.Current.IsDirty);
    }

    [Fact]
    public async Task Several_missing_files_are_counted_and_none_are_analysed()
    {
        var present = File_("present.wav");
        var folder = WriteProject("Film",
            NotAnalysed(Missing("x.wav")), Analysed(Missing("y.wav")), NotAnalysed(present), NotAnalysed(Missing("z.wav")));

        await _workflow.OpenAsync(folder);

        Assert.Equal(new[] { present }, _analysis.Calls);
        Assert.Equal("Opened project \"Film\". 3 media files are missing and are shown as offline.", _status.Message);
        Assert.False(_projects.Current.IsDirty);
    }

    [Fact]
    public async Task Reopening_a_saved_project_reuses_the_metadata_from_the_first_analysis()
    {
        var media = File_("a.wav");
        var folder = WriteProject("Film", NotAnalysed(media));
        await _workflow.OpenAsync(folder);
        Assert.Single(_analysis.Calls);
        await _projects.SaveAsync();

        Assert.True(await _workflow.OpenAsync(folder));

        Assert.Single(_analysis.Calls); // still only the first one
        var asset = Assert.Single(_projects.Current.MediaAssets);
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.Equal(42_000_000, asset.Metadata!.Duration.Ticks);
    }

    [Fact]
    public async Task Failed_open_reports_the_error_and_changes_nothing()
    {
        var current = _projects.Current;
        var folder = Path.Combine(_root, "Broken");
        Directory.CreateDirectory(folder);
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), "{ not json");

        Assert.False(await _workflow.OpenAsync(folder));

        Assert.Same(current, _projects.Current);
        Assert.StartsWith("Couldn't open the project. The project file is damaged", _status.Message);
        Assert.Empty(_analysis.Calls);
    }

    [Fact]
    public async Task Open_of_a_folder_without_a_project_reports_it()
    {
        Assert.False(await _workflow.OpenAsync(Path.Combine(_root, "Nothing here")));

        Assert.Contains("project.json was not found", _status.Message);
    }

    [Fact]
    public async Task Media_browser_shows_missing_media_as_offline()
    {
        var folder = WriteProject("Film", Analysed(File_("a.wav")), Analysed(Missing("gone.wav")));
        await _workflow.OpenAsync(folder);

        var items = _projects.Current.MediaAssets.Select(a => new MediaBrowserItemViewModel(a)).ToList();

        Assert.Equal("Media offline", items.Single(i => i.FileName == "gone.wav").TechnicalSummary);
        Assert.NotEqual("Media offline", items.Single(i => i.FileName == "a.wav").TechnicalSummary);
    }

    [Fact]
    public void Queueing_a_missing_asset_directly_does_not_probe_it()
    {
        var coordinator = new MediaAnalysisCoordinator(_analysis, _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var asset = new MediaAsset { FilePath = Missing("gone.wav"), Kind = MediaKind.Audio, IsMissing = true };

        coordinator.QueueAnalysis(asset);
        coordinator.QueueAnalysis(asset);

        Assert.Empty(_analysis.Calls);
        Assert.Equal(MediaAnalysisStatus.Pending, asset.AnalysisStatus);
    }
}
