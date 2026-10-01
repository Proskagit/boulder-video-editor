using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D024 Step 9.3: the analysis queue over the real <see cref="FfprobeMediaAnalysisService"/>. The "ffprobe" is a
/// script: the stream probe answers at once with valid JSON of a video; the orientation probe (the second ffprobe
/// of an analysis, <c>-show_frames</c>) blocks — on a gate the test opens, or until the ffprobe timeout ends it. Every
/// probe leaves a marker file (<c>stream-</c> / <c>orient-&lt;file&gt;</c>, its write time = the probe's start), so
/// what ran and when is known without sampling processes. Shows that no more analyses than the limit probe at once —
/// the orientation probe included —, that the timeout of a queued analysis starts only when it gets its slot, and
/// that every analysis completes with the unchanged metadata rules. Nothing here depends on how fast the machine is:
/// the overlap is held by the gate, and times are measured from each probe's own start.
/// In the media collection, like the other tests that count ping processes.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class AnalysisConcurrencyIntegrationTests : IDisposable
{
    private const int Limit = MediaAnalysisCoordinator.MaxConcurrentAnalyses;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-analysis-queue", Guid.NewGuid().ToString("N"));
    private string Gate => Path.Combine(_dir, "release");

    public AnalysisConcurrencyIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { File.WriteAllText(Gate, ""); } catch (IOException) { } // never leave a blocked script behind
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class ScriptedFfprobe(string script) : IFfprobeLocator
    {
        public DateTime Created { get; } = DateTime.Now;
        public string Script { get; } = script;
        public Task<string?> GetFfprobePathAsync(CancellationToken ct = default) => Task.FromResult<string?>(Script);
    }

    /// <param name="gated">The orientation probe waits for <see cref="Gate"/>; otherwise it hangs until the timeout.</param>
    private ScriptedFfprobe Ffprobe(bool gated)
    {
        var json = Path.Combine(_dir, "probe.json");
        File.WriteAllText(json, """
            {"streams":[{"codec_type":"video","codec_name":"h264","width":640,"height":360,"r_frame_rate":"25/1","avg_frame_rate":"25/1"}],
             "format":{"duration":"5.000000","start_time":"0.000000","bit_rate":"1000000"}}
            """);
        var block = gated
            ? $":wait\r\n  if exist \"{Gate}\" exit 0\r\n  ping -n 2 127.0.0.1 >nul\r\n  goto wait\r\n"
            : "  ping -n 31 127.0.0.1 >nul\r\n  exit 0\r\n";
        var script = Path.Combine(_dir, "ffprobe.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "for %%a in (%*) do set \"media=%%~nxa\"\r\n" +
            "echo %* | findstr /C:\"-show_frames\" >nul\r\n" +
            $"if %errorlevel%==0 (\r\n  echo.>\"{_dir}\\orient-%media%\"\r\n  goto orient\r\n)\r\n" +
            $"echo.>\"{_dir}\\stream-%media%\"\r\n" +
            $"type \"{json}\"\r\nexit 0\r\n" +
            ":orient\r\n" + block);
        return new ScriptedFfprobe(script);
    }

    /// <summary>The coordinator only needs the project events; the project itself is never replaced here.</summary>
    private sealed class OneProject : IProjectService
    {
        public Core.Entities.Project Current { get; } = new();
        public event EventHandler? ProjectChanged { add { } remove { } }
        public event EventHandler? MediaAssetsChanged { add { } remove { } }
        public event EventHandler? TimelineChanged { add { } remove { } }
        public event EventHandler? SaveStateChanged { add { } remove { } }
        public event EventHandler? ProjectSaved { add { } remove { } }
        public Core.Entities.Project CreateNew(string name, ProjectSettings? settings = null) => throw new NotSupportedException();
        public Task<Core.Entities.Project> OpenAsync(string projectFolderPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Core.Entities.Project> RestoreRecoveryAsync(string recoveryFilePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecheckMediaAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<MediaAvailabilityChangedEventArgs>? MediaAvailabilityChanged { add { } remove { } }
        public MediaAddResult AddMediaAssets(IEnumerable<MediaAsset> assets) => throw new NotSupportedException();
        public void NotifyMediaAssetsChanged() { }
        public void NotifyTimelineChanged() { }
    }

    private static int RunningPings(ScriptedFfprobe ffprobe) => Process.GetProcessesByName("PING")
        .Count(p => { try { return !p.HasExited && p.StartTime >= ffprobe.Created.AddSeconds(-1); } catch { return false; } });

    private (MediaAnalysisCoordinator Coordinator, List<MediaAsset> Assets) Queue(ScriptedFfprobe ffprobe, TimeSpan timeout)
    {
        var service = new FfprobeMediaAnalysisService(ffprobe, NullLogger<FfprobeMediaAnalysisService>.Instance) { ProcessTimeout = timeout };
        var coordinator = new MediaAnalysisCoordinator(service, new OneProject(), NullLogger<MediaAnalysisCoordinator>.Instance);
        var assets = Enumerable.Range(0, Limit + 2).Select(i =>
        {
            var path = Path.Combine(_dir, $"clip{i}.mp4");
            File.WriteAllText(path, "x");
            return new MediaAsset { FilePath = path, Kind = MediaKind.Video };
        }).ToList();
        coordinator.QueueAnalysis(assets);
        return (coordinator, assets);
    }

    private string[] Markers(string kind) =>
        Directory.GetFiles(_dir, kind + "-*").Select(p => Path.GetFileName(p)[(kind.Length + 1)..]).Order().ToArray();

    private static async Task Until(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Deadline) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }

    private static void AssertAnalysed(IEnumerable<MediaAsset> assets) => Assert.All(assets, a =>
    {
        Assert.Equal(MediaAnalysisStatus.Completed, a.AnalysisStatus); // a failed orientation probe never fails the analysis
        Assert.Equal((640, 360, 0), (a.Metadata!.Width!.Value, a.Metadata.Height!.Value, a.Metadata.DisplayRotation!.Value));
        Assert.Equal(MediaTime.FromSeconds(5), a.Metadata.Duration);
        Assert.Equal(FrameRate.Fps25, a.Metadata.FrameRate);
    });

    [Fact]
    public async Task At_most_the_limit_of_analyses_probe_at_once_while_their_orientation_probes_hold_the_slots()
    {
        var ffprobe = Ffprobe(gated: true);
        var (coordinator, assets) = Queue(ffprobe, TimeSpan.FromSeconds(60)); // the gate ends the probes, not the timeout

        await Until(() => Markers("orient").Length >= Limit, $"{Limit} orientation probes did not start");
        // The first analyses now hold every slot for as long as the gate stays closed. However slow the machine, a
        // queued analysis could only start a probe by taking a slot it must not have — give it every chance to.
        await Task.Delay(1500);

        Assert.Equal(Limit, Markers("orient").Length);
        Assert.Equal(Limit, Markers("stream").Length); // the queued ones have not run any ffprobe yet
        var holding = Markers("orient").ToHashSet();
        var queued = assets.Where(a => !holding.Contains(Path.GetFileName(a.FilePath))).ToList();
        Assert.Equal(2, queued.Count);
        Assert.All(queued, a => Assert.Equal(MediaAnalysisStatus.Analyzing, a.AnalysisStatus));

        File.WriteAllText(Gate, "");
        await Until(() => assets.All(a => a.AnalysisStatus != MediaAnalysisStatus.Analyzing), "not every analysis ended");
        await coordinator.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(assets.Count, Markers("orient").Length);
        AssertAnalysed(assets);
        await Until(() => RunningPings(ffprobe) == 0, "a ping of the scripted ffprobe is still running");
    }

    [Fact]
    public async Task A_queued_analysis_times_out_only_after_it_starts()
    {
        // The timeout applies to every ffprobe run, the stream probe too, so it must outlast starting the script on a loaded
        // machine (the slowest seen on CI: ~2.8 s from queueing to the orientation probe) — the one assumption about speed
        // this test keeps: the first analyses must hold their slots for a whole timeout, or nothing tells the two rules
        // apart. A machine that needs more than the timeout to start a batch script fails here with "no orientation probe".
        var timeout = TimeSpan.FromSeconds(10);
        var ffprobe = Ffprobe(gated: false); // every orientation probe hangs until the timeout ends it
        var queuedAt = DateTime.UtcNow;
        var (coordinator, assets) = Queue(ffprobe, timeout);

        var ended = new Dictionary<MediaAsset, DateTime>();
        await Until(() =>
        {
            foreach (var a in assets.Where(a => a.AnalysisStatus != MediaAnalysisStatus.Analyzing && !ended.ContainsKey(a)))
                ended[a] = DateTime.UtcNow;
            return ended.Count == assets.Count;
        }, "not every analysis ended");
        await coordinator.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Every analysis reached its orientation probe (a stream probe that timed out would leave no marker).
        Assert.Equal(assets.Select(a => Path.GetFileName(a.FilePath)).Order(), Markers("orient"));
        DateTime StreamProbe(MediaAsset a) => File.GetLastWriteTimeUtc(Path.Combine(_dir, "stream-" + Path.GetFileName(a.FilePath)));

        // The first `Limit` analyses (their stream probes ran at once) hold their slots until their timeouts; the other
        // two had to wait for a slot, i.e. for the first of them to end. A queued analysis's timeout starts only after it
        // got a slot, so it ends at least one timeout after that first end — a timeout counted from queueing would have
        // run out while it waited and ended it at once. The reference is that end, not a clock: process start-up on a
        // loaded machine (seconds on a CI runner) only makes the queued ones later, never earlier.
        var byStart = assets.OrderBy(StreamProbe).ToList();
        var firstEnd = byStart.Take(Limit).Min(a => ended[a]);
        Assert.All(byStart.Take(Limit), a => Assert.True(ended[a] - queuedAt >= timeout * 0.9,
            $"{Path.GetFileName(a.FilePath)} ended {(ended[a] - queuedAt).TotalSeconds:0.00} s after queueing, before its timeout"));
        Assert.All(byStart.Skip(Limit), a =>
        {
            var name = Path.GetFileName(a.FilePath);
            Assert.True(StreamProbe(a) >= firstEnd - TimeSpan.FromSeconds(0.5),
                $"{name} started probing {(firstEnd - StreamProbe(a)).TotalSeconds:0.00} s before any slot was free");
            Assert.True(ended[a] - firstEnd >= timeout * 0.9,
                $"{name} ended {(ended[a] - firstEnd).TotalSeconds:0.00} s after the first slot was free, less than one timeout");
        });

        AssertAnalysed(assets);
        await Until(() => RunningPings(ffprobe) == 0, "a ping of the scripted ffprobe is still running");
    }
}
