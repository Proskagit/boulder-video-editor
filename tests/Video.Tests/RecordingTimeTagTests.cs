using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Phase 35 Step 35.3 (D049 §2–§3): the analysis keeps a video's <c>creation_time</c> tag exactly as ffprobe prints it,
/// with its origin — the container's tag first, else the first video stream's; an audio stream's tag alone is not used.
/// Nothing interprets the text (its meaning is not established).
/// </summary>
public sealed class RecordingTimeTagTests
{
    private static Dictionary<string, string> Tags(string? creationTime) =>
        creationTime is null ? new() { ["language"] = "eng" } : new() { ["creation_time"] = creationTime, ["language"] = "eng" };

    [Fact]
    public void The_container_tag_is_kept_verbatim_and_wins_over_the_stream_tag()
    {
        var probe = FfprobeMediaAnalysisService.RecordingTimeOf(
            new FfprobeFormat { Tags = Tags("2026-09-27T07:05:18.000000Z") },
            new FfprobeStream { CodecType = "video", Tags = Tags("2026-09-27T07:05:19.000000Z") });

        Assert.Equal(RecordingTimeProbe.Found("2026-09-27T07:05:18.000000Z", RecordingTimeTagOrigin.Format), probe);
    }

    [Fact]
    public void Without_a_container_tag_the_video_streams_tag_is_used()
    {
        var probe = FfprobeMediaAnalysisService.RecordingTimeOf(
            new FfprobeFormat { Tags = Tags(null) },
            new FfprobeStream { CodecType = "video", Tags = Tags("2026-09-27T10:05:18+03:00") });

        Assert.Equal(RecordingTimeProbe.Found("2026-09-27T10:05:18+03:00", RecordingTimeTagOrigin.VideoStream), probe);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_tag_or_an_empty_one_is_absent(string? value)
    {
        var probe = FfprobeMediaAnalysisService.RecordingTimeOf(
            new FfprobeFormat { Tags = value is null ? null : Tags(value) },
            new FfprobeStream { CodecType = "video" });

        Assert.Same(RecordingTimeProbe.Absent, probe);
        Assert.False(probe.IsFound);
    }

    [Theory]
    [InlineData("2026-09-27 07:05:18")]           // no zone designator: kept as text, never read as UTC here
    [InlineData("1970-01-01T00:00:00.000000Z")]   // a container default epoch: kept as text
    [InlineData("garbage")]
    public void Any_text_is_kept_unchanged(string value)
    {
        var probe = FfprobeMediaAnalysisService.RecordingTimeOf(
            new FfprobeFormat { Tags = Tags(value) }, new FfprobeStream { CodecType = "video" });

        Assert.Equal(value, probe.Tag);
        Assert.Equal(RecordingTimeTagOrigin.Format, probe.Origin);
    }

    [FfmpegFact]
    public async Task A_real_file_gets_its_tag_from_ffprobe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aive-recording-time-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var video = Path.Combine(dir, "tagged.mp4");
            TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "testsrc2=s=320x180:r=25:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p",
                "-metadata", "creation_time=2026-09-27T07:05:18.000000Z", video });
            var untagged = Path.Combine(dir, "untagged.mp4");
            TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "testsrc2=s=320x180:r=25:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", untagged });
            var audio = Path.Combine(dir, "tone.m4a");
            TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "sine=d=1", "-metadata", "creation_time=2026-09-27T07:05:18.000000Z", audio });

            var analysis = FfmpegTools.Analysis();
            var tagged = (await analysis.AnalyzeAsync(video)).Metadata!.RecordingTimeProbe;
            Assert.NotNull(tagged);
            Assert.Equal(RecordingTimeTagOrigin.Format, tagged!.Origin);
            Assert.StartsWith("2026-09-27T07:05:18", tagged.Tag);

            Assert.Same(RecordingTimeProbe.Absent, (await analysis.AnalyzeAsync(untagged)).Metadata!.RecordingTimeProbe);
            Assert.Null((await analysis.AnalyzeAsync(audio)).Metadata!.RecordingTimeProbe);   // no picture stream
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
