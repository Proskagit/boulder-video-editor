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
/// of an analysis, <c>-show_frames</c>) hangs in a child process until the ffprobe timeout ends it. So each analysis
/// holds its slot for one timeout, and what runs at the same time can be counted as processes. Shows that no more
/// ffprobe processes than the limit run — the orientation probe included —, that the timeout of a queued analysis
/// starts only when it gets its slot, and that every analysis completes with the unchanged metadata rules.
/// In the media collection, like the other tests that count ping processes.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class AnalysisConcurrencyIntegrationTests : IDisposable
{
    private const int Limit = MediaAnalysisCoordinator.MaxConcurrentAnalyses;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-analysis-queue", Guid.NewGuid().ToString("N"));

    public AnalysisConcurrencyIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class ScriptedFfprobe(string script) : IFfprobeLocator
    {
        public DateTime Created { get; } = DateTime.Now;
        public string Script { get; } = script;
        public Task<string?> GetFfprobePathAsync(CancellationToken ct = default) => Task.FromResult<string?>(Script);
    }

    private ScriptedFfprobe Ffprobe()
    {
        var json = Path.Combine(_dir, "probe.json");
        File.WriteAllText(json, """
            {"streams":[{"codec_type":"video","codec_name":"h264","width":640,"height":360,"r_frame_rate":"25/1","avg_frame_rate":"25/1"}],
             "format":{"duration":"5.000000","start_time":"0.000000","bit_rate":"1000000"}}
            """);
        var script = Path.Combine(_dir, "ffprobe.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "echo %* | findstr /C:\"-show_frames\" >nul\r\n" +
            "if %errorlevel%==0 (\r\n  ping -n 31 127.0.0.1 >nul\r\n  exit 0\r\n)\r\n" +
            $"type \"{json}\"\r\nexit 0\r\n");
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
        public IReadOnlyList<MediaAsset> DetectMissingMedia() => Array.Empty<MediaAsset>();
        public MediaAddResult AddMediaAssets(IEnumerable<MediaAsset> assets) => throw new NotSupportedException();
        public void NotifyMediaAssetsChanged() { }
        public void NotifyTimelineChanged() { }
    }

    private static int RunningPings(ScriptedFfprobe ffprobe) => Process.GetProcessesByName("PING")
        .Count(p => { try { return !p.HasExited && p.StartTime >= ffprobe.Created.AddSeconds(-1); } catch { return false; } });

    [Fact]
    public async Task At_most_the_limit_of_ffprobe_runs_at_once_and_a_queued_analysis_times_out_only_after_it_starts()
    {
        var ffprobe = Ffprobe();
        var service = new FfprobeMediaAnalysisService(ffprobe, NullLogger<FfprobeMediaAnalysisService>.Instance) { ProcessTimeout = Timeout };
        var coordinator = new MediaAnalysisCoordinator(service, new OneProject(), NullLogger<MediaAnalysisCoordinator>.Instance);
        var assets = Enumerable.Range(0, Limit + 2).Select(i =>
        {
            var path = Path.Combine(_dir, $"clip{i}.mp4");
            File.WriteAllText(path, "x");
            return new MediaAsset { FilePath = path, Kind = MediaKind.Video };
        }).ToList();

        var watch = Stopwatch.StartNew();
        coordinator.QueueAnalysis(assets);

        var done = new Dictionary<MediaAsset, TimeSpan>();
        var maxPings = 0;
        while (done.Count < assets.Count)
        {
            maxPings = Math.Max(maxPings, RunningPings(ffprobe));
            foreach (var a in assets.Where(a => a.AnalysisStatus != MediaAnalysisStatus.Analyzing && !done.ContainsKey(a)))
                done[a] = watch.Elapsed;
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException($"{done.Count} of {assets.Count} analyses ended");
            await Task.Delay(10);
        }
        await coordinator.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Limit, maxPings); // the hanging orientation probes: exactly the limit at once, never more
        var times = assets.Select(a => done[a]).Order().ToList();
        var first = times.Take(Limit).ToList();
        var queued = times.Skip(Limit).ToList();
        // The first `Limit` start at once and end at their timeout; the other two waited for a slot and end at least one
        // timeout after them — a timeout counted from queueing would have ended them together with the first ones.
        // Relative, not absolute: every analysis also starts processes (the stream probe, the script), which takes far
        // longer on a CI runner than on a workstation (seen: the first ones ending at 4.8 s with a 2 s timeout).
        Assert.All(first, t => Assert.True(t >= Timeout * 0.9, $"ended at {t}, before its timeout"));
        Assert.True(queued.Min() - first.Max() >= Timeout * 0.9,
            $"queued analyses ended {queued.Min() - first.Max()} after the first ones, less than one timeout ({string.Join(", ", times)})");
        Assert.All(assets, a =>
        {
            Assert.Equal(MediaAnalysisStatus.Completed, a.AnalysisStatus); // a failed orientation probe never fails the analysis
            Assert.Equal((640, 360, 0), (a.Metadata!.Width!.Value, a.Metadata.Height!.Value, a.Metadata.DisplayRotation!.Value));
            Assert.Equal(MediaTime.FromSeconds(5), a.Metadata.Duration);
            Assert.Equal(FrameRate.Fps25, a.Metadata.FrameRate);
        });
        Assert.Equal(0, RunningPings(ffprobe));
    }
}
