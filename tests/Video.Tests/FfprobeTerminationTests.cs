using System.Diagnostics;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D024 Step 9.3: cancelling an analysis or hitting its timeout ends the ffprobe process — its whole tree — instead
/// of only giving up waiting for it. The "ffprobe" is a script that never answers: it starts a long-running child
/// (ping), so a kill that misses the tree leaves the child behind. In the media collection, like every test here that
/// counts ping processes, so they never see each other's.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class FfprobeTerminationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-ffprobe-end", Guid.NewGuid().ToString("N"));
    private readonly string _media;

    public FfprobeTerminationTests()
    {
        Directory.CreateDirectory(_dir);
        _media = Path.Combine(_dir, "clip.mp4");
        File.WriteAllText(_media, "not probed for real");
    }

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

    /// <summary>An ffprobe that hangs: 30 s of ping, no output.</summary>
    private ScriptedFfprobe HangingFfprobe()
    {
        var path = Path.Combine(_dir, $"ffprobe-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, "@echo off\r\nping -n 31 127.0.0.1 >nul\r\nexit 0\r\n");
        return new ScriptedFfprobe(path);
    }

    /// <summary>No ping started by the script (i.e. since it was written) is still alive.</summary>
    private static void AssertNothingRunning(ScriptedFfprobe ffprobe)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var alive = Process.GetProcessesByName("PING")
                .Where(p => { try { return !p.HasExited && p.StartTime >= ffprobe.Created.AddSeconds(-1); } catch { return false; } })
                .Select(p => $"{p.ProcessName}#{p.Id}").ToList();
            if (alive.Count == 0) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail($"still running after '{Path.GetFileName(ffprobe.Script)}': {string.Join(", ", alive)}");
            Thread.Sleep(50);
        }
    }

    private static async Task WaitForPing(ScriptedFfprobe ffprobe)
    {
        var watch = Stopwatch.StartNew();
        while (!Process.GetProcessesByName("PING").Any(p => { try { return p.StartTime >= ffprobe.Created.AddSeconds(-1); } catch { return false; } }))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("the fake ffprobe did not start");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Cancelling_the_analysis_ends_ffprobe_and_its_children()
    {
        var ffprobe = HangingFfprobe();
        var service = new FfprobeMediaAnalysisService(ffprobe, NullLogger<FfprobeMediaAnalysisService>.Instance);
        using var cts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();

        var analysis = service.AnalyzeAsync(_media, cts.Token);
        await WaitForPing(ffprobe);
        cts.Cancel();
        var result = await analysis;

        Assert.Equal(MediaAnalysisOutcome.Cancelled, result.Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}"); // not the script's 30 s
        AssertNothingRunning(ffprobe);
    }

    [Fact]
    public async Task The_timeout_ends_ffprobe_and_its_children()
    {
        var ffprobe = HangingFfprobe();
        var service = new FfprobeMediaAnalysisService(ffprobe, NullLogger<FfprobeMediaAnalysisService>.Instance)
        {
            ProcessTimeout = TimeSpan.FromSeconds(2)
        };
        var watch = Stopwatch.StartNew();

        var result = await service.AnalyzeAsync(_media);

        Assert.Equal(MediaAnalysisOutcome.ProbeProcessFailed, result.Outcome);
        Assert.Equal("FFprobe took too long to respond.", result.ErrorMessage);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
        AssertNothingRunning(ffprobe);
    }
}
