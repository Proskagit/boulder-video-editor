using System.Diagnostics;
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
/// D024 Step 9.3: at most <see cref="MediaAnalysisCoordinator.MaxConcurrentAnalyses"/> analyses run at once. The rest
/// wait (shown as Analyzing) and all of them end in a terminal state with their own result; waiting analyses of a
/// replaced project end with it and leave every slot to the next one. The fake probe counts how many calls are inside
/// it at the same moment, so the bound is measured, not inferred.
/// </summary>
public sealed class AnalysisConcurrencyTests : IDisposable
{
    private const int Limit = MediaAnalysisCoordinator.MaxConcurrentAnalyses;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);
    private readonly CountingAnalysis _analysis = new();
    private readonly MediaAnalysisCoordinator _coordinator;
    private int _fileNumber;

    public AnalysisConcurrencyTests()
    {
        Directory.CreateDirectory(_root);
        _coordinator = new MediaAnalysisCoordinator(_analysis, _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Every call waits at the current gate (honouring cancellation, like ffprobe); the result's duration is
    /// the number in the file's name, so each asset can be checked for its own result.</summary>
    private sealed class CountingAnalysis : IMediaAnalysisService
    {
        private int _active;
        private int _maxActive;
        private int _calls;
        private volatile TaskCompletionSource _gate = Open();

        public int Active => Volatile.Read(ref _active);
        public int MaxActive => Volatile.Read(ref _maxActive);
        public int Calls => Volatile.Read(ref _calls);
        public bool HonourCancellation { get; set; } = true;

        private static TaskCompletionSource Open()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.SetResult();
            return gate;
        }

        /// <summary>Calls from now on wait at a new gate (calls already waiting keep theirs); returns it.</summary>
        public TaskCompletionSource Close() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _gate.TrySetResult();
        public void OpenGate() => _gate = Open();

        public async Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _active);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, now, seen) != seen) { }
            try
            {
                var gate = _gate.Task;
                if (HonourCancellation)
                {
                    try { await gate.WaitAsync(ct); }
                    catch (OperationCanceledException) { return MediaAnalysisResult.Failure(MediaAnalysisOutcome.Cancelled, "Analysis was cancelled."); }
                }
                else
                {
                    await gate;
                }
                await Task.Yield(); // overlap with the next calls
                var number = int.Parse(Path.GetFileNameWithoutExtension(filePath).Split('-')[1]);
                return MediaAnalysisResult.Success(new MediaMetadata
                {
                    Duration = MediaTime.FromSeconds(number), Width = 640, Height = 360, DisplayRotation = 0,
                    DisplayWidth = 640, DisplayHeight = 360, VideoCodec = "h264"
                });
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private string MediaFile()
    {
        var path = Path.Combine(_root, "media", $"clip-{++_fileNumber}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private List<MediaAsset> Import(int count)
    {
        var assets = Enumerable.Range(0, count).Select(_ => new MediaAsset { FilePath = MediaFile(), Kind = MediaKind.Video }).ToList();
        _coordinator.QueueAnalysis(_projects.AddMediaAssets(assets).Added);
        return assets;
    }

    private static int NumberOf(MediaAsset asset) => int.Parse(Path.GetFileNameWithoutExtension(asset.FilePath).Split('-')[1]);

    private static void AssertOwnResult(MediaAsset asset)
    {
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.Equal(MediaTime.FromSeconds(NumberOf(asset)), asset.Metadata!.Duration);
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(5);
        }
    }

    private Task Settle() => _coordinator.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Many_queued_analyses_run_at_most_the_limit_at_once_and_all_complete_with_their_own_result()
    {
        _analysis.Close();
        var assets = Import(25);

        await Eventually(() => _analysis.Active == Limit, "the first analyses did not start");
        await Task.Delay(100);                                            // nothing more starts while the slots are taken
        Assert.Equal((Limit, Limit), (_analysis.Active, _analysis.Calls));
        Assert.All(assets, a => Assert.Equal(MediaAnalysisStatus.Analyzing, a.AnalysisStatus)); // queued = Analyzing

        _analysis.Release();
        await Settle();

        Assert.Equal(Limit, _analysis.MaxActive);
        Assert.Equal(25, _analysis.Calls);
        Assert.All(assets, AssertOwnResult);
    }

    [Fact]
    public async Task Analyses_released_one_by_one_keep_the_slots_full_and_never_exceed_the_limit()
    {
        var assets = Import(0);
        for (var round = 0; round < 3; round++)
        {
            _analysis.Close();
            assets.AddRange(Import(Limit + 3));
            await Eventually(() => _analysis.Active == Limit, $"round {round}: slots not filled");
            _analysis.Release();
            await Settle();
        }

        Assert.Equal(Limit, _analysis.MaxActive);
        Assert.All(assets, AssertOwnResult);
    }

    [Theory]
    [InlineData(true)]   // the running probes notice the cancellation (ffprobe is killed) and free their slots at once
    [InlineData(false)]  // they only finish when released: the next project waits for those slots, then runs
    public async Task A_new_project_is_analysed_after_the_old_queue_is_cancelled_and_no_slot_is_lost(bool honoursCancellation)
    {
        _analysis.HonourCancellation = honoursCancellation;
        var oldGate = _analysis.Close();
        var old = Import(20);
        await Eventually(() => _analysis.Active == Limit, "old analyses did not start");

        _projects.CreateNew("Other");                // cancels the old generation: 4 running, 16 waiting for a slot
        _analysis.OpenGate();
        var fresh = Import(Limit * 2);
        if (!honoursCancellation)
        {
            // The 16 old analyses that only waited for a slot ended with the cancellation, although the 4 running
            // ones still hold every slot; left are those 4 and the new project's queue.
            await Eventually(() => _coordinator.RunningCount == Limit + fresh.Count, "cancelled waits did not end");
            await Task.Delay(100);
            Assert.All(fresh, a => Assert.Equal(MediaAnalysisStatus.Analyzing, a.AnalysisStatus)); // slots still held
            Assert.Equal(Limit, _analysis.Calls);
            oldGate.SetResult();                     // the old probes end now and are dropped
        }
        await Eventually(() => fresh.All(a => a.AnalysisStatus == MediaAnalysisStatus.Completed), "the new project was not analysed");
        await Settle();

        Assert.All(fresh, AssertOwnResult);
        Assert.Equal(Limit + fresh.Count, _analysis.Calls);                        // no waiting old analysis ever probed
        Assert.DoesNotContain(old, a => a.AnalysisStatus == MediaAnalysisStatus.Completed); // old results dropped
        Assert.Equal(Limit, _analysis.MaxActive);

        // Every slot is still there: a new batch fills exactly the limit again.
        _analysis.Close();
        var more = Import(Limit + 2);
        await Eventually(() => _analysis.Active == Limit, "slots were lost");
        await Task.Delay(50);
        Assert.Equal(Limit, _analysis.Active);
        _analysis.Release();
        await Settle();
        Assert.All(more, AssertOwnResult);
        oldGate.TrySetResult();
    }

    [Fact]
    public async Task Orientation_refreshes_after_open_share_the_same_limit()
    {
        var folder = Path.Combine(_root, "Phones");
        Directory.CreateDirectory(folder);
        var project = new Core.Entities.Project { Name = "Phones" };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        for (var i = 0; i < 10; i++) // metadata saved before Phase 7: coded size only → refreshed after Open
            project.MediaAssets.Add(new MediaAsset
            {
                FilePath = MediaFile(), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
                Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(99), Width = 1920, Height = 1080, VideoCodec = "h264" }
            });
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder));
        _analysis.Close();

        var opened = await _projects.OpenAsync(folder);
        Assert.Equal(10, _coordinator.QueueWhereNeeded(opened.MediaAssets));
        await Eventually(() => _analysis.Active == Limit, "refreshes did not start");
        await Task.Delay(50);
        Assert.Equal(Limit, _analysis.Calls);

        _analysis.Release();
        await Settle();
        Assert.Equal(Limit, _analysis.MaxActive);
        Assert.All(opened.MediaAssets, a => Assert.Equal(MediaTime.FromSeconds(NumberOf(a)), a.Metadata!.Duration)); // each its own refresh
        Assert.All(opened.MediaAssets, a => Assert.Equal(MediaAnalysisStatus.Completed, a.AnalysisStatus));
    }
}