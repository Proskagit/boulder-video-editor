using System.Diagnostics;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Export;

/// <summary>
/// <see cref="IExportService"/> (D023, Phase 8 Step 6): orchestration only. It connects the parts of the export
/// and owns none of their rules:
/// <list type="number">
/// <item>Checking: every Dissolve and Push has the source frames its zone needs beyond its clips — asked of the decoder,
/// not of the metadata (<see cref="ExportTransitionSourceCheck"/>, D025 §4); one that lacks them ends the export before
/// anything is written (<see cref="ExportFailure.TransitionSourceMissing"/>).</item>
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
    private readonly ExportPipelineSettings _pipeline;

    /// <param name="pipeline">How many frames are worked on at once (D059 E2); null = chosen per export.</param>
    public ExportService(IFfmpegLocator locator, IVideoDecoder videoDecoder, IAudioDecoder audioDecoder, IExportEncoder encoder,
        Func<ICompositionRasterizer> rasterizerFactory, ILogger<ExportService> logger, ExportPipelineSettings? pipeline = null)
    {
        _pipeline = pipeline ?? new ExportPipelineSettings();
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
        _logger.LogInformation("Export started: {Path} ({Width}×{Height}, {Rate} fps, {Frames} frames, {Samples} audio samples; " +
            "profile {Profile}, encoder {Encoder}, quality {Quality}, preset {Preset}, rate control {RateControl}, AAC {Bitrate} kbps)",
            job.OutputPath, output.Size.Width, output.Size.Height, output.FrameRate, output.FrameCount, output.AudioSampleCount,
            job.Encoding.Profile, job.Encoding.Encoder, job.Encoding.Quality, job.Encoding.Preset, job.Encoding.RateControl,
            job.Encoding.AudioBitrateKbps);
        try
        {
            progress?.Report(new ExportProgress(ExportStage.Preparing, 0, 1));
            await ExportTransitionSourceCheck.RunAsync(job.Snapshot, output, _videoDecoder, new ExportDecodeSettings(), ct);

            // D059 §3: a hardware encoder that fails (at its start or in the middle) never leaves a partial file — the
            // encoding is abandoned like any failure and the export starts again with libx264 of the same profile. A second
            // failure (libx264) propagates.
            var encoding = job.Encoding;
            string? fallback = null;
            while (true)
            {
                var attempt = new Attempt();
                try
                {
                    await RunOnceAsync(job, encoding, fallback, attempt, progress, ct);
                    break;
                }
                catch (ExportException ex) when (attempt.Hardware && !ct.IsCancellationRequested &&
                                                  ex.Failure is ExportFailure.EncodeFailed or ExportFailure.EncoderUnavailable)
                {
                    _logger.LogWarning(ex, "The hardware encoder failed ({Encoder}); the export starts again with libx264.", attempt.Description);
                    fallback = ex.Message;
                    encoding = encoding.WithLibx264();
                }
            }
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
        _logger.LogInformation("Export finished in {Elapsed} ({Fps:0.0} fps over the whole export): {Path}", watch.Elapsed,
            output.FrameCount / Math.Max(0.001, watch.Elapsed.TotalSeconds), job.OutputPath);
    }

    /// <summary>What one attempt used (read by the fallback rule).</summary>
    private sealed class Attempt
    {
        public bool Hardware;
        public string? Description;
    }

    private async Task RunOnceAsync(ExportJob job, ExportEncoding settings, string? fallback, Attempt attempt,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var output = job.Output;
        var (count, queued) = _pipeline.Resolve(output, job.Snapshot.VideoLayers.Length,
            hardwareEncoder: settings.Encoder == ExportVideoEncoder.Nvenc);
        var rasterizers = new List<ICompositionRasterizer>(count);
        try
        {
            for (var i = 0; i < count; i++)
                rasterizers.Add(_rasterizerFactory() ?? throw new InvalidOperationException("The rasterizer factory returned null."));
            await using var encoding = await _encoder.StartAsync(output, settings, job.OutputPath, ct);
            attempt.Hardware = encoding.IsHardware;
            attempt.Description = encoding.EncoderDescription;
            _logger.LogInformation("Export pipeline: {Rasterizers} rasterizers, {Queued} queued frames; video encoder {Encoder}.",
                count, queued, encoding.EncoderDescription);
            var noted = progress is null ? null
                : new EncoderNote(progress, encoding.EncoderDescription, fallback ?? encoding.FallbackReason);

            await WriteAudioAsync(job, encoding, progress, ct);
            await WriteVideoAsync(job, rasterizers, queued, encoding, noted, ct);

            noted?.Report(new ExportProgress(ExportStage.Finalizing, 0, 1));
            await encoding.CompleteAsync(ct);
            noted?.Report(new ExportProgress(ExportStage.Finalizing, 1, 1));
        }
        finally
        {
            foreach (var rasterizer in rasterizers) rasterizer.Dispose();
        }
    }

    /// <summary>Adds the encoder actually used (and a fallback's reason) to the reports of the video stage and later.</summary>
    private sealed class EncoderNote(IProgress<ExportProgress> inner, string encoder, string? fallback) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => inner.Report(value with { Encoder = encoder, Fallback = fallback });
    }

    private async Task WriteAudioAsync(ExportJob job, IExportEncoding encoding, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var total = job.Output.AudioSampleCount;
        progress?.Report(new ExportProgress(ExportStage.Audio, 0, total));
        await using var audio = new ExportAudioSource(job.Snapshot, _audioDecoder, job.Output);
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

    private async Task WriteVideoAsync(ExportJob job, IReadOnlyList<ICompositionRasterizer> rasterizers, int queued, IExportEncoding encoding,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        // D059 E2 (replaces D024 Step 9.7 "A", one frame fetched ahead): decoders → bounded queue → the rasterizers in
        // parallel → the encoder in frame order. The frame source is disposed only after every stage has ended.
        await using var frames = new ExportFrameSource(job.Snapshot, _videoDecoder, output: job.Output);
        await new ExportVideoPipeline(frames, rasterizers, encoding, job.Output, queued, _logger).RunAsync(progress, ct);
    }
}
