using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Localization;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Video;

/// <summary>
/// <see cref="IExportEncoder"/> over the ffmpeg CLI (D023, Phase 8 Step 5), in two passes:
/// <list type="number">
/// <item>audio: the PCM on stdin → AAC-LC 48 kHz stereo at the job's bitrate → a temporary <c>.m4a</c>;</item>
/// <item>video: the BGRA canvases on stdin → BT.709 limited-range 4:2:0 → libx264 at the job's CRF and preset (D028
/// Step 13.7; <see cref="ExportEncoding.Default"/> gives exactly the Phase 8–12 command lines — CRF 18, medium,
/// 192 kbps), muxed with the
/// temporary audio (stream copy) into a temporary <c>.mp4</c> with <c>+faststart</c>, then moved to the destination.</item>
/// </list>
/// Measured with FFmpeg 9.0.1 (progress.md, Step 5): the AAC encoder's 1024 priming samples are recorded in the MP4
/// edit list, so a decoder gets exactly the samples written, starting at 0 — the same after the stream copy as when
/// encoding directly; no compensation of our own. Without explicit colour handling ffmpeg converts BGRA with the BT.601
/// matrix and leaves the stream untagged, so the video pass converts with BT.709 and tags matrix, primaries, transfer
/// and range. Rational frame rates give exact timestamps (<c>pts = n · den/num</c>).
/// </summary>
public sealed class FfmpegExportEncoder : IExportEncoder
{
    private readonly IFfmpegLocator _locator;
    private readonly ILogger<FfmpegExportEncoder> _logger;
    private readonly IExportEncoderCapabilities? _capabilities;

    /// <param name="capabilities">Which encoders work here (D059 §3); null = libx264 only (NVENC is then never used).</param>
    public FfmpegExportEncoder(IFfmpegLocator locator, ILogger<FfmpegExportEncoder> logger, IExportEncoderCapabilities? capabilities = null)
    {
        _locator = locator;
        _logger = logger;
        _capabilities = capabilities;
    }

    public async Task<IExportEncoding> StartAsync(ExportOutput output, ExportEncoding encoding, string destinationPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(encoding);
        if (encoding.Validate() is { } invalid) throw new ArgumentException(invalid, nameof(encoding));
        if (!output.Size.IsValid || output.Size.Width % 2 != 0 || output.Size.Height % 2 != 0)
            throw new ArgumentException($"The frame size {output.Size.Width} × {output.Size.Height} can't be encoded (even sizes only).", nameof(output));
        if (output.FrameCount <= 0 || output.AudioSampleCount <= 0 || !output.FrameRate.IsValid)
            throw new ArgumentException("An export needs at least one frame, audio samples and a valid frame rate.", nameof(output));
        if (!Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("The destination must be a full path.", nameof(destinationPath));

        var ffmpeg = await _locator.GetFfmpegPathAsync(ct)
            ?? throw new ExportException(ExportFailure.EncoderUnavailable, "ffmpeg could not be found.");
        var folder = Path.GetDirectoryName(destinationPath)!;
        if (!Directory.Exists(folder))
            throw new ExportException(ExportFailure.OutputFailed, $"The folder '{folder}' does not exist.");

        // D059 §3: the encoder actually used — NVENC only where it was confirmed; otherwise libx264 of the same profile.
        // D076: the codec's software encoder must be in this FFmpeg too (libx265 / SVT-AV1) — asked for every codec but H.264.
        var available = _capabilities is not null && (encoding.Encoder == ExportVideoEncoder.Nvenc || encoding.Codec != ExportVideoCodec.H264)
            ? await _capabilities.GetAsync(ct) : ExportEncoderAvailability.SoftwareOnly;
        var plan = ExportVideoPlan.For(encoding, output.Size, available);
        if (plan.Unavailable)
            throw new ExportException(ExportFailure.EncoderUnavailable, string.Format(Messages.Export_CodecUnavailable, CodecName(plan.Codec),
                plan.Codec == ExportVideoCodec.Av1 ? "libsvtav1" : "libx265"));
        string? fallback = null;
        if (plan.FellBack)
        {
            fallback = available.NvencFor(encoding.Codec)
                ? string.Format(Messages.Nvenc_TooLarge, ExportEncoderAvailability.NvencMaxSideOf(encoding.Codec), output.Size.Width, output.Size.Height)
                : available.NvencUnavailableReason ?? Messages.Nvenc_NotAvailable;
            _logger.LogWarning("NVENC was chosen but can't be used here ({Reason}); encoding with {Plan}.", fallback, plan.Description);
        }

        var baseName = $".{Path.GetFileNameWithoutExtension(destinationPath)}.{Guid.NewGuid():N}";
        var audioTemp = Path.Combine(folder, baseName + ".audio.m4a");
        var videoTemp = Path.Combine(folder, baseName + ".partial" + ExportFormat.ExtensionOf(encoding.Container));
        var running = new FfmpegExportEncoding(ffmpeg, output, encoding, plan, destinationPath, audioTemp, videoTemp, _logger)
        {
            FallbackReason = fallback
        };
        try
        {
            running.StartAudio();
        }
        catch
        {
            await running.DisposeAsync();
            throw;
        }
        return running;
    }

    /// <summary>"H.264", "HEVC", "AV1".</summary>
    public static string CodecName(ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.Hevc => "HEVC",
        ExportVideoCodec.Av1 => "AV1",
        _ => "H.264"
    };

    private static readonly string[] Common = { "-hide_banner", "-nostats", "-loglevel", "error", "-y" };

    /// <summary>Pass 1: 48 kHz stereo float PCM on stdin → AAC-LC at <paramref name="encoding"/>'s bitrate (bits per
    /// second) in an MP4 (m4a) file.</summary>
    internal static List<string> AudioArguments(ExportEncoding encoding, string audioPath) => new(Common)
    {
        "-f", "f32le", "-ar", Invariant(ExportFormat.AudioSampleRate), "-ac", Invariant(ExportFormat.AudioChannels), "-i", "pipe:0",
        "-c:a", "aac", "-profile:a", "aac_low", "-b:a", Invariant(checked(encoding.AudioBitrateKbps * 1000)),
        "-ar", Invariant(ExportFormat.AudioSampleRate), "-ac", Invariant(ExportFormat.AudioChannels),
        "-f", "mp4", audioPath
    };

    /// <summary>Pass 2 for <paramref name="encoding"/> with libx264 (no NVENC): see the plan overload.</summary>
    internal static List<string> VideoArguments(ExportOutput output, ExportEncoding encoding, string audioPath, string videoPath) =>
        VideoArguments(output, ExportVideoPlan.For(encoding, output.Size, ExportEncoderAvailability.SoftwareOnly), audioPath, videoPath, encoding.Container);

    /// <summary>Pass 2: BGRA canvases on stdin (exact rational rate) + the encoded audio → the MP4, encoded by
    /// <paramref name="plan"/> (D059 §3). The input, the colour conversion (on the CPU and the same for both encoders: NVENC
    /// receives the converted yuv420p frames, never BGRA — the research §4), the tags, the audio copy and the container are
    /// the same for every plan; libx264 at constant quality is exactly the D028 command line.</summary>
    internal static List<string> VideoArguments(ExportOutput output, ExportVideoPlan plan, string audioPath, string videoPath,
        ExportContainer container = ExportContainer.Mp4)
    {
        var args = new List<string>(Common)
        {
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{Invariant(output.Size.Width)}x{Invariant(output.Size.Height)}",
            "-framerate", $"{Invariant(output.FrameRate.Numerator)}/{Invariant(output.FrameRate.Denominator)}", "-i", "pipe:0",
            "-i", audioPath,
            "-map", "0:v:0", "-map", "1:a:0",
            // BGRA → YUV with the BT.709 matrix, limited range, 4:2:0; the frames are tagged so the encoder writes every tag.
            "-vf", "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p," +
                   "setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
            "-fps_mode", "passthrough"
        };
        args.AddRange(CodecArguments(plan));
        // D076: HEVC in MP4 / MOV tagged hvc1 — what Apple's and Windows' players read (Matroska has no such tag).
        if (plan.Codec == ExportVideoCodec.Hevc && container != ExportContainer.Mkv) args.AddRange(new[] { "-tag:v", "hvc1" });
        args.AddRange(new[]
        {
            "-pix_fmt", "yuv420p",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-c:a", "copy"
        });
        // D076: the index at the front — MP4 / MOV with +faststart (the D023 command line), Matroska with its cues first.
        args.AddRange(container == ExportContainer.Mkv ? new[] { "-cues_to_front", "1" } : new[] { "-movflags", "+faststart" });
        args.AddRange(new[] { "-f", ExportFormat.MuxerOf(container), videoPath });
        return args;
    }

    /// <summary>The encoder's own options of <paramref name="plan"/> (also those of the NVENC trial encoding).</summary>
    internal static List<string> CodecArguments(ExportVideoPlan plan)
    {
        if (plan.Encoder == ExportVideoEncoder.Software && plan.Codec == ExportVideoCodec.Hevc)
        {
            // D076: libx265, 8-bit Main, its log quiet (tagged hvc1 by VideoArguments).
            var x265 = new List<string> { "-c:v", "libx265", "-preset", plan.Preset, "-profile:v", "main" };
            if (plan.RateControl == ExportRateControl.ConstantQuality) x265.AddRange(new[] { "-crf", Invariant(plan.Quality) });
            else x265.AddRange(BitrateArguments(plan.VideoBitrateKbps));
            x265.AddRange(new[] { "-x265-params", "log-level=error" });
            return x265;
        }
        if (plan.Encoder == ExportVideoEncoder.Software && plan.Codec == ExportVideoCodec.Av1)
        {
            // D076: SVT-AV1, a key frame at least every 250 frames as the others.
            var svt = new List<string> { "-c:v", "libsvtav1", "-preset", plan.Preset, "-g", "250" };
            if (plan.RateControl == ExportRateControl.ConstantQuality) svt.AddRange(new[] { "-crf", Invariant(plan.Quality) });
            else svt.AddRange(new[] { "-b:v", Invariant(plan.VideoBitrateKbps) + "k" });
            return svt;
        }
        if (plan.Encoder == ExportVideoEncoder.Software)
        {
            var x264 = new List<string> { "-c:v", "libx264", "-preset", plan.Preset };
            if (plan.RateControl == ExportRateControl.ConstantQuality) x264.AddRange(new[] { "-crf", Invariant(plan.Quality) });
            else x264.AddRange(BitrateArguments(plan.VideoBitrateKbps));
            return x264;
        }

        // NVENC H.264 (D059 §3, §5): High profile, the "hq" tuning, spatial adaptive quantization, a look-ahead, three
        // B-frames, a key frame at least every 250 frames like x264's default; VBR towards the calibrated CQ with no bitrate
        // cap (-b:v 0), or an average bitrate.
        // D076: HEVC (Main) and AV1 (Main) with the same options; each codec's CQ from its own calibration.
        var nvenc = plan.Codec switch
        {
            ExportVideoCodec.Hevc => new List<string> { "-c:v", "hevc_nvenc", "-preset", plan.Preset, "-tune", "hq", "-profile:v", "main", "-rc", "vbr" },
            ExportVideoCodec.Av1 => new List<string> { "-c:v", "av1_nvenc", "-preset", plan.Preset, "-tune", "hq", "-rc", "vbr" },
            _ => new List<string> { "-c:v", "h264_nvenc", "-preset", plan.Preset, "-tune", "hq", "-profile:v", "high", "-rc", "vbr" }
        };
        if (plan.RateControl == ExportRateControl.ConstantQuality) nvenc.AddRange(new[] { "-cq", Invariant(plan.Quality), "-b:v", "0" });
        else nvenc.AddRange(BitrateArguments(plan.VideoBitrateKbps));
        if (plan.SpatialAq) nvenc.AddRange(new[] { "-spatial-aq", "1" });
        nvenc.AddRange(new[] { "-rc-lookahead", "20", "-bf", "3", "-g", "250" });
        if (plan.Multipass) nvenc.AddRange(new[] { "-multipass", "qres" });
        return nvenc;
    }

    /// <summary>The encoder options of <paramref name="plan"/> exactly as an export passes them (the benchmark's calibration).</summary>
    public static IReadOnlyList<string> CodecArgumentsFor(ExportVideoPlan plan) => CodecArguments(plan);

    /// <summary>An average bitrate with a peak of 1.5× and a 2× buffer (both encoders).</summary>
    private static IEnumerable<string> BitrateArguments(int kbps) => new[]
    {
        "-b:v", Invariant(kbps) + "k", "-maxrate", Invariant(kbps * 3 / 2) + "k", "-bufsize", Invariant(kbps * 2) + "k"
    };

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One two-pass encoding (see <see cref="FfmpegExportEncoder"/>). Not thread-safe.</summary>
internal sealed class FfmpegExportEncoding : IExportEncoding
{
    private readonly string _ffmpeg;
    private readonly ExportOutput _output;
    private readonly ExportEncoding _encoding;
    private readonly ExportVideoPlan _plan;
    private readonly string _destination;
    private readonly ILogger _logger;
    private readonly int _frameBytes;

    private FfmpegProcess? _audio;
    private FfmpegProcess? _video;
    private long _audioFrames;
    private long _videoFrames;
    private bool _audioDone;
    private bool _completed;
    private bool _faulted;
    private bool _disposed;

    public FfmpegExportEncoding(string ffmpeg, ExportOutput output, ExportEncoding encoding, ExportVideoPlan plan, string destination,
        string audioTemp, string videoTemp, ILogger logger)
    {
        _ffmpeg = ffmpeg;
        _output = output;
        _encoding = encoding;
        _plan = plan;
        _destination = destination;
        AudioTemp = audioTemp;
        VideoTemp = videoTemp;
        _logger = logger;
        _frameBytes = checked(output.Size.Width * output.Size.Height * 4);
    }

    /// <summary>The encoding actually used (D059 §3).</summary>
    public ExportVideoPlan Plan => _plan;

    public string EncoderDescription => _plan.Description;

    public bool IsHardware => _plan.Encoder == ExportVideoEncoder.Nvenc;

    public string? FallbackReason { get; init; }

    internal string AudioTemp { get; }
    internal string VideoTemp { get; }

    internal void StartAudio() => _audio = Start(FfmpegExportEncoder.AudioArguments(_encoding, AudioTemp), "audio");

    public async ValueTask WriteAudioAsync(ReadOnlyMemory<float> interleaved, CancellationToken ct = default)
    {
        EnsureUsable();
        if (_audioDone) throw new InvalidOperationException("The audio has ended: video frames were written already.");
        if (interleaved.Length % AudioFormat.Channels != 0)
            throw new ArgumentException("Audio is written in whole stereo frames.", nameof(interleaved));
        var frames = interleaved.Length / AudioFormat.Channels;
        if (_audioFrames + frames > _output.AudioSampleCount)
            throw new InvalidOperationException($"More audio than the export's {_output.AudioSampleCount} samples.");

        var bytes = MemoryMarshal.AsBytes(interleaved.Span).Length;
        var buffer = ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            MemoryMarshal.AsBytes(interleaved.Span).CopyTo(buffer);
            await WriteAsync(_audio!, "audio", buffer.AsMemory(0, bytes), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        _audioFrames += frames;
    }

    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> bgra, int stride, CancellationToken ct = default)
    {
        EnsureUsable();
        var rowBytes = _output.Size.Width * 4;
        if (stride < rowBytes || bgra.Length < (long)stride * (_output.Size.Height - 1) + rowBytes)
            throw new ArgumentException($"A frame is {_output.Size.Width} × {_output.Size.Height} BGRA pixels.", nameof(bgra));
        if (_videoFrames >= _output.FrameCount)
            throw new InvalidOperationException($"More frames than the export's {_output.FrameCount}.");

        if (!_audioDone) await FinishAudioAsync(ct);
        _video ??= Start(FfmpegExportEncoder.VideoArguments(_output, _plan, AudioTemp, VideoTemp, _encoding.Container), "video");

        if (stride == rowBytes)
        {
            await WriteAsync(_video, "video", bgra[.._frameBytes], ct);
        }
        else
        {
            for (var y = 0; y < _output.Size.Height; y++)
                await WriteAsync(_video, "video", bgra.Slice(y * stride, rowBytes), ct);
        }
        _videoFrames++;
    }

    public async Task CompleteAsync(CancellationToken ct = default)
    {
        EnsureUsable();
        if (_videoFrames != _output.FrameCount)
            throw new InvalidOperationException($"{_videoFrames} of the export's {_output.FrameCount} frames were written.");

        await EndAsync(_video!, "video", ct);
        if (!File.Exists(VideoTemp) || new FileInfo(VideoTemp).Length == 0)
            throw Fail(ExportFailure.EncodeFailed, "The video encoder produced no file.");
        try
        {
            File.Move(VideoTemp, _destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Fail(ExportFailure.OutputFailed, $"The export could not be written to '{_destination}': {ex.Message}", ex);
        }
        _completed = true;
        await CleanUpAsync();
    }

    /// <summary>Ends pass 1: the audio must be complete; the encoder must finish successfully.</summary>
    private async Task FinishAudioAsync(CancellationToken ct)
    {
        if (_audioFrames != _output.AudioSampleCount)
            throw new InvalidOperationException($"{_audioFrames} of the export's {_output.AudioSampleCount} audio samples were written before the first frame.");
        await EndAsync(_audio!, "audio", ct);
        await _audio!.DisposeAsync();
        _audio = null;
        _audioDone = true;
    }

    private FfmpegProcess Start(List<string> arguments, string pass)
    {
        _logger.LogDebug("Export {Pass} encoder starting.", pass); // command line: FfmpegProcess
        try
        {
            return FfmpegProcess.Start(_ffmpeg, arguments, _ => false, _ => { }, _logger, redirectStdin: true);
        }
        catch (Exception ex)
        {
            throw Fail(ExportFailure.EncoderUnavailable, $"ffmpeg could not be started: {ex.Message}", ex);
        }
    }

    private async Task WriteAsync(FfmpegProcess process, string pass, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            // Also a write blocked by a full pipe (the encoder not reading) ends on cancellation: .NET cancels the
            // pending synchronous pipe write (tested).
            await process.Stdin.WriteAsync(data, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _faulted = true;
            throw;
        }
        catch (IOException ex)
        {
            // ffmpeg stopped reading: it failed. Its exit code and stderr say why.
            var reason = await ExitReasonAsync(process);
            throw Fail(ExportFailure.EncodeFailed, $"The {pass} encoder stopped accepting input. {reason}", ex);
        }
    }

    /// <summary>Closes the input and waits for a successful exit.</summary>
    private async Task EndAsync(FfmpegProcess process, string pass, CancellationToken ct)
    {
        try
        {
            process.CloseStdin();
            if (await process.AbnormalExitAsync(ct) is { } failure)
                throw Fail(ExportFailure.EncodeFailed, $"The {pass} encoding failed: {failure}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _faulted = true;
            throw;
        }
        catch (IOException ex)
        {
            var reason = await ExitReasonAsync(process);
            throw Fail(ExportFailure.EncodeFailed, $"The {pass} encoder failed while its input was closed. {reason}", ex);
        }
    }

    private static async Task<string> ExitReasonAsync(FfmpegProcess process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await process.AbnormalExitAsync(timeout.Token) ?? "ffmpeg exited normally.";
        }
        catch (OperationCanceledException)
        {
            return process.StderrTail();
        }
    }

    private ExportException Fail(ExportFailure failure, string message, Exception? inner = null)
    {
        _faulted = true;
        return new ExportException(failure, message, inner);
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("The encoding is complete.");
        if (_faulted) throw new InvalidOperationException("The encoding failed or was cancelled.");
    }

    private async Task CleanUpAsync()
    {
        if (_audio is not null) { await _audio.DisposeAsync(); _audio = null; }
        if (_video is not null) { await _video.DisposeAsync(); _video = null; }
        Delete(AudioTemp);
        if (!_completed) Delete(VideoTemp);
    }

    private void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Temporary export file '{Path}' could not be deleted.", path);
        }
    }

    /// <summary>Aborts an unfinished encoding (processes killed, temporary files deleted, destination untouched).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CleanUpAsync();
    }
}
