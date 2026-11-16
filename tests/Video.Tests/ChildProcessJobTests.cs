using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>D060 §5: every ffmpeg the app starts is in the app's job object, which ends it with the app's process however
/// that ends — no ffmpeg left behind.</summary>
public class ChildProcessJobTests
{
    [FfmpegFact]
    public async Task An_ffmpeg_the_app_starts_is_in_the_app_s_job()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(ChildProcessJob.IsAvailable);

        var process = FfmpegProcess.Start(FfmpegTools.Ffmpeg!,
            new[] { "-hide_banner", "-f", "lavfi", "-i", "anullsrc=r=48000", "-t", "30", "-f", "null", "-" },
            _ => true, _ => { }, NullLogger.Instance);
        await using (process)
            Assert.True(ChildProcessJob.Contains(process.Process));
    }
}
