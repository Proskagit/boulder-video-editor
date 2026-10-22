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

    // A relative configured path belongs to the application folder, not the current directory (D046 §1.2).

    [Fact]
    public async Task RelativeConfiguredPath_ResolvedAgainstTheApplicationFolder_WithSpacesAndCyrillic()
    {
        using var app = new TempFolder("Мои программы\\Boulder Editor");
        var tool = app.CreateFile("ffmpeg\\bin\\" + Candidate);
        var probeCalls = 0;
        var locator = new ExecutableLocator("ffmpeg", () => "ffmpeg\\bin\\" + Candidate, NullLogger.Instance, (_, _) =>
        {
            Interlocked.Increment(ref probeCalls);
            return Task.FromResult(true);
        }, app.Path);

        Assert.NotEqual(app.Path, Path.GetFullPath(Environment.CurrentDirectory));
        Assert.Equal(tool, await locator.GetPathAsync(CancellationToken.None));
        Assert.Equal(0, probeCalls); // a configured file that exists is used without the PATH probe
    }

    [Fact]
    public async Task RelativeConfiguredPath_PresentOnlyUnderTheCurrentDirectory_IsNotUsed()
    {
        // The file exists relative to the current directory (as the old lookup would have found it), not to the app
        // folder: the locator falls back to PATH.
        var relative = "locator-cwd-" + Guid.NewGuid().ToString("N") + "\\" + Candidate;
        var underCwd = Path.GetFullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(underCwd)!);
        File.WriteAllText(underCwd, "");
        try
        {
            using var app = new TempFolder("app folder");
            string? probed = null;
            var locator = new ExecutableLocator("ffmpeg", () => relative, NullLogger.Instance, (file, _) =>
            {
                probed = file;
                return Task.FromResult(false);
            }, app.Path);

            Assert.Null(await locator.GetPathAsync(CancellationToken.None));
            Assert.Equal(Candidate, probed);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(underCwd)!, recursive: true);
        }
    }

    [Fact]
    public async Task AbsoluteConfiguredPath_IsUsedAsItIs()
    {
        using var other = new TempFolder("Другая папка\\tools");
        var tool = other.CreateFile(Candidate);
        using var app = new TempFolder("app");
        var locator = new ExecutableLocator("ffmpeg", () => tool, NullLogger.Instance,
            (_, _) => Task.FromResult(false), app.Path);

        Assert.Equal(tool, await locator.GetPathAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MissingConfiguredFile_FallsBackToPath()
    {
        using var app = new TempFolder("app");
        string? probed = null;
        var locator = new ExecutableLocator("ffmpeg", () => "ffmpeg\\bin\\" + Candidate, NullLogger.Instance, (file, _) =>
        {
            probed = file;
            return Task.FromResult(true);
        }, app.Path);

        Assert.Equal(Candidate, await locator.GetPathAsync(CancellationToken.None));
        Assert.Equal(Candidate, probed);
    }

    [Theory]
    [InlineData("ffmpeg\\bin\\ffmpeg.exe", "ffmpeg\\bin\\ffmpeg.exe")]
    [InlineData(".\\ffmpeg.exe", "ffmpeg.exe")]
    [InlineData("..\\Общие инструменты\\ffmpeg.exe", "..\\Общие инструменты\\ffmpeg.exe")]
    public void ResolveConfiguredPath_Relative_IsUnderTheBaseDirectory(string configured, string expectedRelative)
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "Мои программы", "Boulder Editor");

        Assert.Equal(Path.GetFullPath(Path.Combine(baseDirectory, expectedRelative)),
            ExecutableLocator.ResolveConfiguredPath(configured, baseDirectory));
    }

    [Fact]
    public void ResolveConfiguredPath_Rooted_IsUnchanged()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "С пробелом", "ffmpeg.exe");

        Assert.Equal(rooted, ExecutableLocator.ResolveConfiguredPath(rooted, Path.Combine(Path.GetTempPath(), "app")));
    }

    [Fact]
    public async Task ProductionLocator_UsesTheApplicationFolder()
    {
        // The app's constructor resolves against AppContext.BaseDirectory: a configured file next to the test assembly is
        // found whatever the current directory is.
        var name = "locator-base-" + Guid.NewGuid().ToString("N") + ".exe";
        var file = Path.Combine(AppContext.BaseDirectory, name);
        File.WriteAllText(file, "");
        try
        {
            var locator = new FfmpegLocator(Options.Create(new FfmpegOptions { FfmpegPath = name }),
                NullLogger<FfmpegLocator>.Instance);

            Assert.Equal(file, await locator.GetFfmpegPathAsync());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [FfmpegFact]
    public async Task RealLocator_FirstCallCancelledMidProbe_SecondCallStillFindsFfmpeg()
    {
        // The real probe, with the test limit: the subject is the cancelled first call, not the 5 s limit.
        var locator = new FfmpegLocator(Options.Create(new FfmpegOptions()), NullLogger<FfmpegLocator>.Instance, FfmpegTools.ProbeTimeout);

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

    private sealed class TempFolder : IDisposable
    {
        private readonly string _root;

        public TempFolder(string relative)
        {
            _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aive-locator-" + Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(_root, relative);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string relative)
        {
            var file = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "");
            return file;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
