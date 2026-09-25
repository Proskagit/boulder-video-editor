using System.Collections.Concurrent;
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
/// D024 Step 9.3: media analyses belong to the project they were started for. New, Open and Recover cancel the
/// running ones (their ffprobe is told to stop); a result of a replaced project — also one that arrives after the
/// switch without noticing the cancellation — changes no asset and raises no event; the new project's analyses all
/// complete, including a project reopened while its own analysis was still running (same asset ids, new objects).
/// </summary>
public sealed class AnalysisGenerationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly GatedAnalysis _analysis = new();
    private readonly MediaAnalysisCoordinator _coordinator;
    private readonly RecoveryOffer _autosave = new();
    private readonly ProjectFileWorkflow _workflow;
    private int _mediaEvents;

    public AnalysisGenerationTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _coordinator = new MediaAnalysisCoordinator(_analysis, _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        _workflow = new ProjectFileWorkflow(_projects, _coordinator, _autosave, new ScriptedDialogs(0 /* Recover */), new ScriptedPicker(),
            new StatusService(), NullLogger<ProjectFileWorkflow>.Instance);
        _projects.MediaAssetsChanged += (_, _) => Interlocked.Increment(ref _mediaEvents);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // --- fakes and helpers ---------------------------------------------------------------------------------

    /// <summary>Returns a result per file (its duration in seconds = the file's number of calls so far, so an old and
    /// a new result can be told apart). A call can be held at a gate; a held call either honours cancellation (like
    /// ffprobe: a Cancelled result) or ignores it and answers Success once released (a result racing the switch).</summary>
    private sealed class GatedAnalysis : IMediaAnalysisService
    {
        private readonly ConcurrentDictionary<string, Queue<TaskCompletionSource>> _gates = new();
        public ConcurrentQueue<(string Path, CancellationToken Token)> Calls { get; } = new();
        public bool HonourCancellation { get; set; } = true;

        /// <summary>Holds the next call for <paramref name="path"/> until the returned source is completed.</summary>
        public TaskCompletionSource HoldNext(string path)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates.GetOrAdd(path, _ => new Queue<TaskCompletionSource>()).Enqueue(gate);
            return gate;
        }

        public int CallsFor(string path) => Calls.Count(c => c.Path == path);

        public async Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            Calls.Enqueue((filePath, ct));
            var number = CallsFor(filePath);
            if (_gates.TryGetValue(filePath, out var queue) && queue.TryDequeue(out var gate))
            {
                if (HonourCancellation)
                {
                    try { await gate.Task.WaitAsync(ct); }
                    catch (OperationCanceledException) { return MediaAnalysisResult.Failure(MediaAnalysisOutcome.Cancelled, "Analysis was cancelled."); }
                }
                else
                {
                    await gate.Task;
                }
            }
            return MediaAnalysisResult.Success(Metadata(number));
        }

        public static MediaMetadata Metadata(int seconds) => new()
        {
            Duration = MediaTime.FromSeconds(seconds), Width = 640, Height = 360, DisplayRotation = 0,
            DisplayWidth = 640, DisplayHeight = 360, VideoCodec = "h264", FrameRate = FrameRate.Fps25
        };
    }

    /// <summary>Autosave that offers one recovery file at startup.</summary>
    private sealed class RecoveryOffer : IAutosaveService
    {
        public RecoveryCandidate? Candidate { get; set; }
        public event EventHandler<string>? AutosaveCompleted { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public Task<bool> AutosaveNowAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<RecoveryScanResult> FindRecoveryAsync(CancellationToken ct = default) =>
            Task.FromResult(Candidate is null ? RecoveryScanResult.None : new RecoveryScanResult(Candidate, 1, 0));
        public Task DiscardRecoveryAsync(RecoveryCandidate candidate) => Task.CompletedTask;
        public Task DiscardRecoveryAsync(Guid projectId) => Task.CompletedTask;
        public Task ShutdownAsync(bool keepUnsavedChanges = true) => Task.CompletedTask;
    }

    private string MediaFile(string name)
    {
        var path = Path.Combine(_root, "media", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>Imports a file into the current project and starts its analysis, as the import workflow does.</summary>
    private MediaAsset Import(string path)
    {
        var asset = new MediaAsset { FilePath = path, Kind = MediaKind.Video };
        var added = _projects.AddMediaAssets(new[] { asset }).Added;
        _coordinator.QueueAnalysis(added);
        return asset;
    }

    private static Core.Entities.Project ProjectWith(string name, params MediaAsset[] assets)
    {
        var project = new Core.Entities.Project { Name = name };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        project.MediaAssets.AddRange(assets);
        return project;
    }

    /// <summary>A saved project whose media still needs analysis (no saved metadata).</summary>
    private string SavedProject(string name, params string[] mediaPaths)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        var project = ProjectWith(name, mediaPaths.Select(p => new MediaAsset { FilePath = p, Kind = MediaKind.Video }).ToArray());
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder));
        return folder;
    }

    /// <summary>Waits until every analysis started so far has finished or been dropped.</summary>
    private async Task Settle()
    {
        await _coordinator.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(20); // nothing may follow once idle; give a wrong late event a chance to show up
    }

    /// <summary>Waits for <paramref name="condition"/> while an old analysis may still be held (so not idle yet).</summary>
    private static async Task Eventually(Func<bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(5);
        }
    }

    private bool AllCurrentCompleted() => _projects.Current.MediaAssets.All(a => a.AnalysisStatus == MediaAnalysisStatus.Completed);

    private static void AssertCompleted(MediaAsset asset, int seconds)
    {
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.Equal(MediaTime.FromSeconds(seconds), asset.Metadata!.Duration);
    }

    // --- New / Open / Recover -------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]   // the probe notices the cancellation (ffprobe)
    [InlineData(false)]  // its result arrives after the switch anyway
    public async Task New_during_analysis_drops_the_old_result_and_the_new_project_analyses_normally(bool honoursCancellation)
    {
        _analysis.HonourCancellation = honoursCancellation;
        var oldPath = MediaFile("old.mp4");
        var hold = _analysis.HoldNext(oldPath);
        var old = Import(oldPath);
        Assert.Equal(MediaAnalysisStatus.Analyzing, old.AnalysisStatus);

        _projects.CreateNew("Other");
        Assert.True(_analysis.Calls.Single().Token.IsCancellationRequested); // the old probe was told to stop
        var eventsAfterSwitch = _mediaEvents;
        hold.SetResult();
        await Settle();

        Assert.Equal(eventsAfterSwitch, _mediaEvents);                     // no event for the replaced project
        Assert.Equal(MediaAnalysisStatus.Analyzing, old.AnalysisStatus);  // its asset untouched
        Assert.Null(old.Metadata);

        var a = Import(MediaFile("a.mp4"));
        var b = Import(MediaFile("b.mp4"));
        await Settle();
        AssertCompleted(a, 1);
        AssertCompleted(b, 1);
        Assert.DoesNotContain(_analysis.Calls.Skip(1), c => c.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Open_during_analysis_drops_the_old_result_and_analyses_the_opened_project(bool honoursCancellation)
    {
        _analysis.HonourCancellation = honoursCancellation;
        var oldPath = MediaFile("old.mp4");
        var hold = _analysis.HoldNext(oldPath);
        var old = Import(oldPath);
        var folder = SavedProject("Film", MediaFile("film1.mp4"), MediaFile("film2.mp4"));

        Assert.True(await _workflow.OpenAsync(folder));
        await Eventually(AllCurrentCompleted, "opened media not analysed");
        Assert.All(_projects.Current.MediaAssets, asset => AssertCompleted(asset, 1));
        var eventsAfterOpen = _mediaEvents;

        hold.SetResult();
        await Settle();
        Assert.Equal(eventsAfterOpen, _mediaEvents);
        Assert.Null(old.Metadata);
        Assert.True(_analysis.Calls.First().Token.IsCancellationRequested);
        Assert.All(_projects.Current.MediaAssets, asset => AssertCompleted(asset, 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recover_during_analysis_drops_the_old_result_and_analyses_the_recovered_project(bool honoursCancellation)
    {
        _analysis.HonourCancellation = honoursCancellation;
        var oldPath = MediaFile("old.mp4");
        var hold = _analysis.HoldNext(oldPath);
        var old = Import(oldPath);

        var recovered = ProjectWith("Recovered", new MediaAsset { FilePath = MediaFile("rec.mp4"), Kind = MediaKind.Video });
        var file = Path.Combine(_root, "recovery.json");
        File.WriteAllText(file, ProjectSerializer.SerializeRecovery(recovered, new RecoveryInfo(null, DateTimeOffset.Now, 0, null)));
        _autosave.Candidate = new RecoveryCandidate(file, recovered.Id, recovered.Name, null, DateTimeOffset.Now);

        await _workflow.StartSessionAsync(); // offers the recovery file; the dialog answers Recover
        Assert.Equal("Recovered", _projects.Current.Name);
        await Eventually(AllCurrentCompleted, "recovered media not analysed");
        AssertCompleted(Assert.Single(_projects.Current.MediaAssets), 1);
        var eventsAfterRecover = _mediaEvents;

        hold.SetResult();
        await Settle();
        Assert.Equal(eventsAfterRecover, _mediaEvents);
        Assert.Null(old.Metadata);
        Assert.True(_analysis.Calls.First().Token.IsCancellationRequested);
    }

    // --- the same project opened again while its analysis runs ---------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reopening_the_same_project_during_its_analysis_analyses_the_reopened_media(bool honoursCancellation)
    {
        // Import → Save → Open the same project while the import's analysis still runs: the reopened asset has the
        // same id as the one being analysed. It used to be skipped as a "duplicate" and stayed Pending for good.
        _analysis.HonourCancellation = honoursCancellation;
        var path = MediaFile("clip.mp4");
        var hold = _analysis.HoldNext(path);
        var imported = Import(path);
        var folder = Path.Combine(_root, "Same");
        await _projects.SaveAsAsync(folder); // saved while analysing: no metadata in the file

        Assert.True(await _workflow.OpenAsync(folder));
        var reopened = Assert.Single(_projects.Current.MediaAssets);
        Assert.Equal(imported.Id, reopened.Id);
        Assert.NotSame(imported, reopened);
        await Eventually(AllCurrentCompleted, "reopened media not analysed (stuck Pending)");

        Assert.Equal(2, _analysis.CallsFor(path));
        AssertCompleted(reopened, 2);                  // the reopened asset got its own (second) result

        var eventsAfter = _mediaEvents;
        hold.SetResult();                              // the first analysis ends only now
        await Settle();
        AssertCompleted(reopened, 2);                  // not overwritten by the old result
        Assert.Null(imported.Metadata);
        Assert.Equal(eventsAfter, _mediaEvents);
    }

    // --- orientation refresh (RefreshDisplaySize) ------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_orientation_refresh_of_a_replaced_project_changes_nothing(bool honoursCancellation)
    {
        _analysis.HonourCancellation = honoursCancellation;
        var path = MediaFile("phone.mp4");
        var folder = Path.Combine(_root, "Phone");
        Directory.CreateDirectory(folder);
        var stale = new MediaMetadata { Duration = MediaTime.FromSeconds(10), Width = 1920, Height = 1080, VideoCodec = "h264" }; // saved before Phase 7
        var project = ProjectWith("Phone", new MediaAsset { FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed, Metadata = stale });
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder));
        var hold = _analysis.HoldNext(path);

        Assert.True(await _workflow.OpenAsync(folder));
        var asset = Assert.Single(_projects.Current.MediaAssets);
        Assert.True(asset.Metadata!.NeedsDisplaySizeProbe);    // refresh running
        _projects.CreateNew("Other");
        Assert.True(_analysis.Calls.Single().Token.IsCancellationRequested);
        var eventsAfterSwitch = _mediaEvents;

        hold.SetResult();
        await Settle();
        Assert.Equal(eventsAfterSwitch, _mediaEvents);
        Assert.True(asset.Metadata!.NeedsDisplaySizeProbe);    // the refreshed metadata was not applied
        Assert.Equal(MediaTime.FromSeconds(10), asset.Metadata.Duration);
    }

    [Fact]
    public async Task An_orientation_refresh_of_the_current_project_still_applies()
    {
        var path = MediaFile("phone.mp4");
        var folder = Path.Combine(_root, "Phone");
        Directory.CreateDirectory(folder);
        var stale = new MediaMetadata { Duration = MediaTime.FromSeconds(10), Width = 1920, Height = 1080, VideoCodec = "h264" };
        var project = ProjectWith("Phone", new MediaAsset { FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed, Metadata = stale });
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder));

        Assert.True(await _workflow.OpenAsync(folder));
        await Settle();

        var asset = Assert.Single(_projects.Current.MediaAssets);
        Assert.False(asset.Metadata!.NeedsDisplaySizeProbe);
        Assert.Equal(MediaTime.FromSeconds(1), asset.Metadata.Duration);
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
    }
}
