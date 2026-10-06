using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// The PATH probe's limit (Phase 14 Step 14.4, D029): the app keeps 5 s — a tool slower than that counts as not
/// found — and the probe really gives up at the limit it is given. The tests whose subject is not the limit use
/// <see cref="FfmpegTools.ProbeTimeout"/> instead. In the media collection: the slow tool is a script that waits with
/// ping, like the other scripted tools.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class ExecutableProbeTimeoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-probe-timeout", Guid.NewGuid().ToString("N"));

    public ExecutableProbeTimeoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A tool that answers "-version" successfully after about <paramref name="seconds"/> s.</summary>
    private string SlowTool(int seconds)
    {
        var path = Path.Combine(_dir, $"slow-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, $"@echo off\r\nping -n {seconds + 1} 127.0.0.1 >nul\r\necho slow tool version 1\r\nexit /b 0\r\n");
        return path;
    }

    [Fact]
    public void The_app_counts_a_tool_slower_than_five_seconds_as_missing()
    {
        var options = Options.Create(new FfmpegOptions());

        Assert.Equal(TimeSpan.FromSeconds(5), ExecutableLocator.DefaultProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance).ProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance).ProbeTimeout);
    }

    [Fact]
    public void The_test_seam_only_changes_the_limit_of_the_locator_it_builds()
    {
        var options = Options.Create(new FfmpegOptions());

        Assert.Equal(FfmpegTools.ProbeTimeout, FfmpegTools.FfmpegLocator.ProbeTimeout);
        Assert.Equal(FfmpegTools.ProbeTimeout, FfmpegTools.FfprobeLocator.ProbeTimeout);
        Assert.Equal(ExecutableLocator.DefaultProbeTimeout, new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance).ProbeTimeout);
    }

    [Fact]
    public void The_app_keeps_its_analysis_and_decoding_limits_and_the_test_helpers_use_theirs()
    {
        var app = new FfprobeMediaAnalysisService(FfmpegTools.FfprobeLocator,
            NullLogger<FfprobeMediaAnalysisService>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(20), app.ProcessTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), new FfmpegAudioDecoderSettings().FirstFrameTimeout);
        Assert.Equal(FfmpegTools.ProcessTimeout, FfmpegTools.Analysis().ProcessTimeout);
        Assert.Equal(FfmpegTools.ProcessTimeout, FfmpegTools.AudioDecoderSettings.FirstFrameTimeout);
    }

    [Fact]
    public async Task A_probe_slower_than_its_limit_counts_as_missing()
    {
        var tool = SlowTool(3);

        Assert.False(await ExecutableLocator.IsExecutableAvailableAsync(tool, TimeSpan.FromMilliseconds(500), CancellationToken.None));
    }

    [Fact]
    public async Task The_same_tool_within_its_limit_is_available()
    {
        var tool = SlowTool(1);

        Assert.True(await ExecutableLocator.IsExecutableAvailableAsync(tool, FfmpegTools.ProbeTimeout, CancellationToken.None));
    }

    [Fact]
    public async Task A_locator_probes_with_its_own_limit()
    {
        var tool = SlowTool(3);
        var strict = new ExecutableLocator("tool", () => null, NullLogger.Instance, TimeSpan.FromMilliseconds(500));
        var generous = new ExecutableLocator("tool", () => null, NullLogger.Instance, FfmpegTools.ProbeTimeout);

        Assert.False(await strict.ProbeAsync(tool, CancellationToken.None));
        Assert.True(await generous.ProbeAsync(tool, CancellationToken.None));
    }
}
