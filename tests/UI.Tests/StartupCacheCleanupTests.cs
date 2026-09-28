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
/// D024 Step 9.4b: at startup the caches of unsaved projects that can't come back are removed before the recovery
/// offer — a project that is recovered keeps its thumbnails and uses that folder again.
/// </summary>
public sealed class StartupCacheCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class OfferOnce(RecoveryCandidate candidate) : IAutosaveService
    {
        public event EventHandler<string>? AutosaveCompleted { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public Task<bool> AutosaveNowAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<RecoveryScanResult> FindRecoveryAsync(CancellationToken ct = default) => Task.FromResult(new RecoveryScanResult(candidate, 1, 0));
        public Task DiscardRecoveryAsync(RecoveryCandidate c) => Task.CompletedTask;
        public Task DiscardRecoveryAsync(Guid projectId) => Task.CompletedTask;
        public Task ShutdownAsync(bool keepUnsavedChanges = true) => Task.CompletedTask;
    }

    [Fact]
    public async Task Orphaned_unsaved_caches_are_removed_and_the_recovered_project_keeps_its_thumbnails()
    {
        var unsavedRoot = Path.Combine(_root, "cache", "unsaved");
        var recovery = new RecoveryStore(Path.Combine(_root, "recovery"));

        var lost = new Core.Entities.Project { Name = "Lost" };
        lost.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        lost.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        var file = recovery.PathFor(lost.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, ProjectSerializer.SerializeRecovery(lost, new RecoveryInfo(null, DateTimeOffset.Now, 0, null)));
        var lostThumbs = Path.Combine(unsavedRoot, lost.Id.ToString("N"), "thumbnails");
        var orphan = Path.Combine(unsavedRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lostThumbs);
        Directory.CreateDirectory(Path.Combine(orphan, "thumbnails"));
        File.WriteAllText(Path.Combine(lostThumbs, "x-1-1-v1.thumb"), "t");
        File.WriteAllText(Path.Combine(orphan, "thumbnails", "y-1-1-v1.thumb"), "t");

        var projects = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        var location = new ThumbnailCacheLocation(projects, recovery, unsavedRoot, NullLogger<ThumbnailCacheLocation>.Instance);
        var coordinator = new MediaAnalysisCoordinator(new NoAnalysis(), projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var workflow = new ProjectFileWorkflow(projects, coordinator,
            new OfferOnce(new RecoveryCandidate(file, lost.Id, lost.Name, null, DateTimeOffset.Now)),
            new ScriptedDialogs(0 /* Recover */), new ScriptedPicker(), new StatusService(), NullLogger<ProjectFileWorkflow>.Instance, location);

        await workflow.StartSessionAsync();

        Assert.False(Directory.Exists(orphan));
        Assert.Equal(lost.Id, projects.Current.Id);
        Assert.Equal(lostThumbs, location.CurrentFolder);
        Assert.True(File.Exists(Path.Combine(lostThumbs, "x-1-1-v1.thumb")));
    }

    [Fact]
    public async Task Startup_removes_both_caches_of_an_orphan_and_the_recovered_project_keeps_its_waveforms()
    {
        // D024 Step 9.5b: the workflow cleans up the waveform cache too, before the recovery offer.
        var unsavedRoot = Path.Combine(_root, "cache", "unsaved");
        var recovery = new RecoveryStore(Path.Combine(_root, "recovery"));

        var lost = new Core.Entities.Project { Name = "Lost" };
        lost.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        lost.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        var file = recovery.PathFor(lost.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, ProjectSerializer.SerializeRecovery(lost, new RecoveryInfo(null, DateTimeOffset.Now, 0, null)));
        var lostWaveforms = Path.Combine(unsavedRoot, lost.Id.ToString("N"), "waveforms");
        var orphan = Path.Combine(unsavedRoot, Guid.NewGuid().ToString("N"));
        foreach (var (folder, name) in new[] { (lostWaveforms, "x-1-1-v1.peaks"), (Path.Combine(orphan, "waveforms"), "y-1-1-v1.peaks"),
                     (Path.Combine(orphan, "thumbnails"), "y-1-1-v1.thumb") })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name), "t");
        }

        var projects = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        var thumbnails = new ThumbnailCacheLocation(projects, recovery, unsavedRoot, NullLogger<ThumbnailCacheLocation>.Instance);
        var waveforms = new WaveformCacheLocation(projects, recovery, unsavedRoot, NullLogger<WaveformCacheLocation>.Instance);
        var coordinator = new MediaAnalysisCoordinator(new NoAnalysis(), projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var workflow = new ProjectFileWorkflow(projects, coordinator,
            new OfferOnce(new RecoveryCandidate(file, lost.Id, lost.Name, null, DateTimeOffset.Now)),
            new ScriptedDialogs(0 /* Recover */), new ScriptedPicker(), new StatusService(), NullLogger<ProjectFileWorkflow>.Instance,
            thumbnails, waveforms);

        await workflow.StartSessionAsync();

        Assert.False(Directory.Exists(orphan));
        Assert.Equal(lostWaveforms, waveforms.CurrentFolder);
        Assert.True(File.Exists(Path.Combine(lostWaveforms, "x-1-1-v1.peaks")));
    }

    private sealed class NoAnalysis : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "test"));
    }
}
