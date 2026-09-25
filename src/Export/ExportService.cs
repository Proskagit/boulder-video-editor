using System.Diagnostics;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Export;

/// <summary>
/// <see cref="IExportService"/> (D023, Phase 8 Step 6): orchestration only. It connects the parts of the export
/// and owns none of their rules:
/// <list type="number">
/// <item>Preparing: one <see cref="ICompositionRasterizer"/> for the job (from the factory the app registers —
/// Export never references a rendering backend) and <see cref="IExportEncoder.StartAsync"/>.</item>
/// <item>Audio: <see cref="ExportAudioSource"/> → <see cref="IExportEncoding.WriteAudioAsync"/> until the source
/// ends (it delivers exactly <see cref="ExportOutput.AudioSampleCount"/> frames; the encoder checks the count).</item>
/// <item>Video: for every output frame n, <see cref="ExportFrameSource"/> → <see cref="ExportFrame.DrawPlan"/> (the
/// Core composition plan) → the rasterizer → BGRA canvas → <see cref="IExportEncoding.WriteFrameAsync"/>.</item>
/// <item>Finalizing: <see cref="IExportEncoding.CompleteAsync"/>, which moves the result into place.</item>
/// </list>
/// The job is taken as the preflight produced it (<see cref="ExportPreflight"/>); its checks are not repeated here.
/// The encoder rejects an output it can't encode (no frames, odd size, relative path) before anything is written.
/// Every failure of a part propagates unchanged (<see cref="ExportException"/> with its category,
/// <see cref="OperationCanceledException"/>); leaving the method in any way other than success disposes the
/// sources, the rasterizer and the unfinished encoding, which ends ffmpeg, deletes its temporary files and leaves
/// the destination as it was (Step 5). The service never touches the destination itself.
/// </summary>
public sealed class ExportService : IExportService
{
    /// <summary>Audio frames per read/write (0.5 s at 48 kHz): the audio stage reports progress per chunk.</summary>
    internal const int AudioChunkFrames = AudioFormat.SampleRate / 2;

    private readonly IFfmpegLocator _locator;
    private readonly IVideoDecoder _videoDecoder;
    private readonly IAudioDecoder _audioDecoder;
    private readonly IExportEncoder _encoder;
    private readonly Func<ICompositionRasterizer> _rasterizerFactory;
    private readonly ILogger<ExportService> _logger;

    public ExportService(IFfmpegLocator locator, IVideoDecoder videoDecoder, IAudioDecoder audioDecoder, IExportEncoder encoder,
        Func<ICompositionRasterizer> rasterizerFactory, ILogger<ExportService> logger)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _videoDecoder = videoDecoder ?? throw new ArgumentNullException(nameof(videoDecoder));
        _audioDecoder = audioDecoder ?? throw new ArgumentNullException(nameof(audioDecoder));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _rasterizerFactory = rasterizerFactory ?? throw new ArgumentNullException(nameof(rasterizerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        await _locator.GetFfmpegPathAsync(ct) is not null;

    public Task ExportAsync(ExportJob job, IProgress<ExportProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Task.Run(() => RunAsync(job, progress, ct), ct);
    }

    private async Task RunAsync(ExportJob job, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var output = job.Output;
        var watch = Stopwatch.StartNew();
        _logger.LogInformation("Export started: {Path} ({Width}×{Height}, {Rate} fps, {Frames} frames, {Samples} audio samples)",
            job.OutputPath, output.Size.Width, output.Size.Height, output.FrameRate, output.FrameCount, output.AudioSampleCount);
        try
        {
            progress?.Report(new ExportProgress(ExportStage.Preparing, 0, 1));
            using var rasterizer = _rasterizerFactory() ?? throw new InvalidOperationException("The rasterizer factory returned null.");
            await using var encoding = await _encoder.StartAsync(output, job.OutputPath, ct);

            await WriteAudioAsync(job, encoding, progress, ct);
            await WriteVideoAsync(job, rasterizer, encoding, progress, ct);

            progress?.Report(new ExportProgress(ExportStage.Finalizing, 0, 1));
            await encoding.CompleteAsync(ct);
            progress?.Report(new ExportProgress(ExportStage.Finalizing, 1, 1));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Export cancelled after {Elapsed}: {Path}", watch.Elapsed, job.OutputPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed after {Elapsed}: {Path}", watch.Elapsed, job.OutputPath);
            throw;
        }
        _logger.LogInformation("Export finished in {Elapsed}: {Path}", watch.Elapsed, job.OutputPath);
    }

    private async Task WriteAudioAsync(ExportJob job, IExportEncoding encoding, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var total = job.Output.AudioSampleCount;
        progress?.Report(new ExportProgress(ExportStage.Audio, 0, total));
        await using var audio = new ExportAudioSource(job.Snapshot, _audioDecoder);
        var buffer = new float[AudioChunkFrames * AudioFormat.Channels];
        long written = 0;
        int read;
        while ((read = await audio.ReadAsync(buffer, ct)) > 0)
        {
            await encoding.WriteAudioAsync(buffer.AsMemory(0, read), ct);
            written += read / AudioFormat.Channels;
            progress?.Report(new ExportProgress(ExportStage.Audio, written, total));
        }
    }

    private async Task WriteVideoAsync(ExportJob job, ICompositionRasterizer rasterizer, IExportEncoding encoding,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var total = job.Output.FrameCount;
        var size = job.Output.Size;
        var stride = size.Width * 4;
        var canvas = new byte[(long)stride * size.Height];
        progress?.Report(new ExportProgress(ExportStage.Video, 0, total));
        await using var frames = new ExportFrameSource(job.Snapshot, _videoDecoder);
        for (long n = 0; n < total; n++)
        {
            ct.ThrowIfCancellationRequested();
            var frame = await frames.GetFrameAsync(n, ct);
            rasterizer.Render(frame.DrawPlan(), canvas, stride);
            await encoding.WriteFrameAsync(canvas, stride, ct);
            progress?.Report(new ExportProgress(ExportStage.Video, n + 1, total));
        }
    }
}
