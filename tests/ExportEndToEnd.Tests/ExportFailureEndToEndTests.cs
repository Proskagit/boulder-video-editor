using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Video.Tests;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 8 Step 6, the output guarantees of <c>ExportService</c> with the real ffmpeg encoder: success replaces an
/// existing file; cancellation in every stage is an <see cref="OperationCanceledException"/>; a source that vanished
/// after the preflight, a missing output folder and a missing ffmpeg keep their <see cref="ExportFailure"/>. After every
/// failure the existing destination is byte-for-byte unchanged, the folder holds no temporary file and no ffmpeg
/// process is left.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportFailureEndToEndTests
{
    private static readonly byte[] Previous = "previous export"u8.ToArray();
    private readonly E2EMedia _media;

    public ExportFailureEndToEndTests(E2EMedia media) => _media = media;

    private (ProjectBuilder Project, string Destination) Setup(Core.Entities.MediaAsset? video = null)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), video ?? _media.Pattern(), 0, 75);
        p.Audio(p.AudioTrack(), _media.Tone(), 0, 75);
        var destination = Path.Combine(_media.OutputFolder(), "export.mp4");
        File.WriteAllBytes(destination, Previous);
        return (p, destination);
    }

    private static void AssertUntouched(string destination)
    {
        Assert.Equal(Previous, File.ReadAllBytes(destination));
        Assert.Equal(new[] { destination }, Directory.GetFiles(Path.GetDirectoryName(destination)!));
        AssertNoFfmpegLeft();
    }

    private static Task Run(ExportJob job, Func<ExportProgress, bool>? cancelAt = null, IFfmpegLocator? locator = null)
    {
        var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (cancelAt?.Invoke(p) == true) cts.Cancel(); });
        return Service(EncoderHarness.Encoder(locator), locator).ExportAsync(job, progress, cts.Token);
    }

    [FfmpegFact]
    public async Task Success_replaces_the_existing_file()
    {
        var (p, destination) = Setup();
        var run = await ExportProject(p.Project, destination);

        AssertValidMp4(run);                                                                    // only the MP4 in the folder, no process
        Assert.NotEqual(Previous, File.ReadAllBytes(destination));
    }

    public static TheoryData<string> Stages => new() { "audio", "video", "finalizing" };

    [FfmpegTheory]
    [MemberData(nameof(Stages))]
    public async Task Cancellation_in_any_stage_leaves_the_destination_untouched(string stage)
    {
        var (p, destination) = Setup();
        var job = Preflight(p.Project, destination);
        Func<ExportProgress, bool> at = stage switch
        {
            "audio" => r => r is { Stage: ExportStage.Audio, Done: > 0 },
            "video" => r => r is { Stage: ExportStage.Video, Done: 10 },
            _ => r => r is { Stage: ExportStage.Finalizing, Done: 0 }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(job, at));

        AssertUntouched(destination);
    }

    [FfmpegFact]
    public async Task A_source_that_vanished_after_the_preflight_fails_with_DecodeFailed()
    {
        var video = _media.CopyOf(_media.Pattern());
        var (p, destination) = Setup(video);
        var job = Preflight(p.Project, destination);
        File.Delete(video.FilePath);

        var error = await Assert.ThrowsAsync<ExportException>(() => Run(job));

        Assert.Equal(ExportFailure.DecodeFailed, error.Failure);
        AssertUntouched(destination);
    }

    [FfmpegFact]
    public async Task A_missing_output_folder_fails_with_OutputFailed()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Pattern(), 0, 25);
        var folder = _media.OutputFolder();
        var job = Preflight(p.Project, Path.Combine(folder, "export.mp4"));
        Directory.Delete(folder);

        var error = await Assert.ThrowsAsync<ExportException>(() => Run(job));

        Assert.Equal(ExportFailure.OutputFailed, error.Failure);
        Assert.False(Directory.Exists(folder));
        AssertNoFfmpegLeft();
    }

    [FfmpegFact]
    public async Task A_missing_ffmpeg_fails_with_EncoderUnavailable()
    {
        var (p, destination) = Setup();
        var job = Preflight(p.Project, destination);
        var missing = new NoFfmpeg();

        Assert.False(await Service(EncoderHarness.Encoder(missing), missing).IsAvailableAsync());
        var error = await Assert.ThrowsAsync<ExportException>(() => Run(job, locator: missing));

        Assert.Equal(ExportFailure.EncoderUnavailable, error.Failure);
        AssertUntouched(destination);
    }

    private sealed class NoFfmpeg : IFfmpegLocator
    {
        public Task<string?> GetFfmpegPathAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
