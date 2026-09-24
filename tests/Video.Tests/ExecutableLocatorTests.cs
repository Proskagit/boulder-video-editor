using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// A probe cancelled by its caller (e.g. a span reader retired by an immediate seek) must not
/// be cached as "ffmpeg not found" for the rest of the run; a genuinely missing tool still is.
/// </summary>
public sealed class ExecutableLocatorTests
{
    private static readonly string Candidate = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    [Fact]
    public async Task CancelledProbe_IsNotCached_NextCallerProbesAgain()
    {
        var probeCalls = 0;
        var firstProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var locator = new ExecutableLocator("ffmpeg", () => null, NullLogger.Instance, async (_, ct) =>
        {
            if (Interlocked.Increment(ref probeCalls) == 1)
            {
                firstProbeStarted.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return true;
        });

        using var cts = new CancellationTokenSource();
        var first = locator.GetPathAsync(cts.Token);
        await firstProbeStarted.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(Candidate, await locator.GetPathAsync(CancellationToken.None));
        Assert.Equal(2, probeCalls);
    }

    [Fact]
    public async Task MissingTool_IsCachedAsNull_WithoutReprobing()
    {
        var probeCalls = 0;
        var locator = new ExecutableLocator("ffmpeg", () => null, NullLogger.Instance, (_, _) =>
        {
            Interlocked.Increment(ref probeCalls);
            return Task.FromResult(false);
        });

        Assert.Null(await locator.GetPathAsync(CancellationToken.None));
        Assert.Null(await locator.GetPathAsync(CancellationToken.None));
        Assert.Equal(1, probeCalls);
    }

    [Fact]
    public async Task ProcessProbe_CancelledByCaller_ThrowsInsteadOfReportingMissing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ExecutableLocator.IsExecutableAvailableAsync(Candidate, cts.Token));
    }

    [Fact]
    public async Task ProcessProbe_NonexistentExecutable_ReturnsFalse()
    {
        Assert.False(await ExecutableLocator.IsExecutableAvailableAsync(
            "definitely-not-a-real-tool-" + Guid.NewGuid().ToString("N"), CancellationToken.None));
    }

    [FfmpegFact]
    public async Task RealLocator_FirstCallCancelledMidProbe_SecondCallStillFindsFfmpeg()
    {
        var locator = new FfmpegLocator(Options.Create(new FfmpegOptions()), NullLogger<FfmpegLocator>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        try
        {
            await locator.GetFfmpegPathAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when the cancel lands during the probe — the bug was caching null here.
        }

        Assert.NotNull(await locator.GetFfmpegPathAsync(CancellationToken.None));
    }
}
