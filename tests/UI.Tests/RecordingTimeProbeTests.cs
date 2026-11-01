using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 35 Step 35.3 (D049 §4, M2): a video whose metadata has no recording-time probe is refreshed in the background
/// at most once per generation; the result is runtime only (not saved before Step 35.4), so a reopened project is probed
/// again; the refresh never makes the project dirty and adds no Undo step; a failed probe is not retried in the same
/// generation, also when the file goes and comes back; an unavailable ffprobe is not an attempt; a file missing at Open
/// is probed once when it returns. The colour refresh of Phase 18 keeps its own behaviour.
/// </summary>
public sealed class RecordingTimeProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly ScriptedAnalysis _analysis = new();
    private readonly ProjectFileWorkflow _workflow;
    private readonly MediaAnalysisCoordinator _coordinator;

    public RecordingTimeProbeTests()
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

    /// <summary>Answers every probe with <see cref="Next"/> and counts the calls.</summary>
    private sealed class ScriptedAnalysis : IMediaAnalysisService
    {
        public List<string> Calls { get; } = new();
        public Func<MediaAnalysisResult> Next { get; set; } = () => MediaAnalysisResult.Success(Probed(RecordingTimeProbe.Absent));

        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add(filePath);
            return Task.FromResult(Next());
        }
    }

    private const string Tag = "2026-09-27T07:05:18.000000Z";

    private static MediaMetadata Probed(RecordingTimeProbe? probe, string? pixelFormat = "yuv420p") => new()
    {
        Duration = MediaTime.FromSeconds(10), Width = 1920, Height = 1080, DisplayRotation = 0, DisplayWidth = 1920,
        DisplayHeight = 1080, VideoCodec = "h264", PixelFormat = pixelFormat, RecordingTimeProbe = probe
    };

    /// <summary>As saved by the current format: everything but the (runtime-only) recording-time probe.</summary>
    private static MediaAsset Saved(string path, MediaKind kind = MediaKind.Video, string? pixelFormat = "yuv420p") => new()
    {
        FilePath = path, Kind = kind, AnalysisStatus = MediaAnalysisStatus.Completed, Metadata = Probed(null, pixelFormat)
    };

    private string MediaPath(string name, bool create = true)
    {
        var path = Path.Combine(_root, "media", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (create) File.WriteAllText(path, "x");
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

    private async Task OpenAsync(string folder)
    {
        Assert.True(await _workflow.OpenAsync(folder));
        await _coordinator.IdleAsync();
    }

    private async Task RecheckAsync()
    {
        await _projects.RecheckMediaAsync();
        await _coordinator.IdleAsync();
    }

    [Fact]
    public async Task An_opened_video_is_probed_once_the_project_stays_clean_and_nothing_is_written()
    {
        var path = MediaPath("climb.mp4");
        var folder = WriteProject(Saved(path));
        var saved = File.ReadAllText(ProjectFileStore.ProjectFilePath(folder));
        _analysis.Next = () => MediaAnalysisResult.Success(Probed(RecordingTimeProbe.Found(Tag, RecordingTimeTagOrigin.Format)));

        await OpenAsync(folder);
        var asset = Assert.Single(_projects.Current.MediaAssets);

        Assert.Equal(new[] { path }, _analysis.Calls);
        Assert.Equal(RecordingTimeProbe.Found(Tag, RecordingTimeTagOrigin.Format), asset.Metadata!.RecordingTimeProbe);
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.False(asset.IsMetadataRefreshing);
        Assert.False(_projects.Current.IsDirty);                                   // M2: not dirty
        Assert.False(_undo.CanUndo);                                               // M2: no Undo step
        Assert.Equal(saved, File.ReadAllText(ProjectFileStore.ProjectFilePath(folder)));   // runtime only (Step 35.3)

        await RecheckAsync();                                                      // same generation: no second probe
        Assert.Single(_analysis.Calls);
    }

    [Fact]
    public async Task A_probe_that_finds_no_tag_marks_absent_and_a_reopened_project_is_probed_again()
    {
        var folder = WriteProject(Saved(MediaPath("plain.mp4")));

        await OpenAsync(folder);
        Assert.Same(RecordingTimeProbe.Absent, Assert.Single(_projects.Current.MediaAssets).Metadata!.RecordingTimeProbe);
        Assert.Single(_analysis.Calls);

        await OpenAsync(folder);                                                   // new generation, nothing saved: again
        Assert.Equal(2, _analysis.Calls.Count);
    }

    [Fact]
    public async Task A_failed_probe_is_not_retried_in_the_generation_even_when_the_file_returns_but_is_after_reopening()
    {
        var path = MediaPath("broken.mp4");
        var folder = WriteProject(Saved(path));
        _analysis.Next = () => MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeProcessFailed, "ffprobe crashed");

        await OpenAsync(folder);
        var asset = Assert.Single(_projects.Current.MediaAssets);
        Assert.Null(asset.Metadata!.RecordingTimeProbe);
        Assert.Single(_analysis.Calls);
        Assert.False(_projects.Current.IsDirty);

        File.Delete(path);
        await RecheckAsync();                                                      // gone
        File.WriteAllText(path, "x");
        await RecheckAsync();                                                      // back: still no second attempt
        Assert.Single(_analysis.Calls);

        await OpenAsync(folder);                                                   // next generation: one more attempt
        Assert.Equal(2, _analysis.Calls.Count);
    }

    [Fact]
    public async Task An_unavailable_ffprobe_is_not_an_attempt()
    {
        var path = MediaPath("later.mp4");
        var folder = WriteProject(Saved(path));
        _analysis.Next = () => MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "no ffprobe");

        await OpenAsync(folder);
        Assert.Single(_analysis.Calls);

        _analysis.Next = () => MediaAnalysisResult.Success(Probed(RecordingTimeProbe.Absent));
        File.Delete(path);
        await RecheckAsync();
        File.WriteAllText(path, "x");
        await RecheckAsync();                                                      // back: probed now

        Assert.Equal(2, _analysis.Calls.Count);
        Assert.Same(RecordingTimeProbe.Absent, Assert.Single(_projects.Current.MediaAssets).Metadata!.RecordingTimeProbe);
    }

    [Fact]
    public async Task A_file_missing_at_open_is_probed_exactly_once_when_it_returns()
    {
        var path = MediaPath("away.mp4", create: false);
        var folder = WriteProject(Saved(path));

        await OpenAsync(folder);
        Assert.Empty(_analysis.Calls);

        File.WriteAllText(path, "x");
        await RecheckAsync();
        Assert.Single(_analysis.Calls);

        File.Delete(path);
        await RecheckAsync();
        File.WriteAllText(path, "x");
        await RecheckAsync();                                                      // back a second time: no second probe
        Assert.Single(_analysis.Calls);
    }

    [Fact]
    public async Task Images_and_audio_are_not_probed_for_a_recording_time()
    {
        var image = Saved(MediaPath("still.png"), MediaKind.Image);
        var audio = new MediaAsset
        {
            FilePath = MediaPath("music.mp3"), Kind = MediaKind.Audio, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), AudioCodec = "mp3" }
        };

        await OpenAsync(WriteProject(image, audio));

        Assert.Empty(_analysis.Calls);
    }

    [Fact]
    public async Task The_colour_refresh_keeps_its_own_retry_when_the_file_returns()
    {
        // Phase 18 behaviour unchanged: a video still needing its colour is refreshed again when its file returns after a
        // failed refresh; the recording-time limit does not stop it.
        var path = MediaPath("old.mp4");
        var folder = WriteProject(Saved(path, pixelFormat: null));
        _analysis.Next = () => MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeProcessFailed, "ffprobe crashed");

        await OpenAsync(folder);
        Assert.Single(_analysis.Calls);

        File.Delete(path);
        await RecheckAsync();
        File.WriteAllText(path, "x");
        await RecheckAsync();

        Assert.Equal(2, _analysis.Calls.Count);
        Assert.False(_projects.Current.IsDirty);
    }
}
