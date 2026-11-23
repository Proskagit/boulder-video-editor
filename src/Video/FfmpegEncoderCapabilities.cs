using System.Diagnostics;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Video;

/// <summary>
/// <see cref="IExportEncoderCapabilities"/> over the ffmpeg CLI (D059 §3, D076 per codec: H.264, HEVC, AV1; and whether the
/// software libx265 / SVT-AV1 are in this FFmpeg). NVENC of a codec counts as available only when both hold:
/// <list type="number">
/// <item>the ffmpeg found has the <c>h264_nvenc</c> encoder (<c>ffmpeg -encoders</c>; the project's minimal build S1 needs
/// <c>--enable-ffnvcodec --enable-nvenc</c> for it, D059 §6);</item>
/// <item>a trial encoding of a few frames with the options of a real export (medium speed and level) succeeds — it fails without
/// an NVIDIA GPU, with a driver too old for the headers ffmpeg was built with, or when the GPU has no free session.</item>
/// </list>
/// Checked once (the first call; the app starts it in the background at startup) and cached; a failure of the check only
/// makes NVENC unavailable, with the reason (never an exception).
/// </summary>
public sealed class FfmpegEncoderCapabilities : IExportEncoderCapabilities
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly IFfmpegLocator _locator;
    private readonly ILogger<FfmpegEncoderCapabilities> _logger;
    private readonly object _gate = new();
    private Task<ExportEncoderAvailability>? _result;

    public FfmpegEncoderCapabilities(IFfmpegLocator locator, ILogger<FfmpegEncoderCapabilities> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    /// <summary>The cached result if the check has finished (the UI's first look), else null.</summary>
    public ExportEncoderAvailability? Known
    {
        get
        {
            lock (_gate) return _result is { IsCompletedSuccessfully: true } done ? done.Result : null;
        }
    }

    public Task<ExportEncoderAvailability> GetAsync(CancellationToken ct = default)
    {
        Task<ExportEncoderAvailability> result;
        lock (_gate) result = _result ??= Task.Run(CheckAsync, CancellationToken.None);
        return result.WaitAsync(ct);
    }

    private async Task<ExportEncoderAvailability> CheckAsync()
    {
        try
        {
            var ffmpeg = await _locator.GetFfmpegPathAsync();
            if (ffmpeg is null) return new ExportEncoderAvailability(false, Messages.Nvenc_NoFfmpeg);

            var (code, output) = await RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" });
            bool Listed(string encoder) => code == 0 && output.Split('\n').Any(l => l.Contains($" {encoder} ", StringComparison.Ordinal));
            // D076: the software HEVC / AV1 encoders this FFmpeg has (the delivered minimal build has both).
            var software = new ExportEncoderAvailability(false) { SoftwareHevc = Listed("libx265"), SoftwareAv1 = Listed("libsvtav1") };
            if (!software.SoftwareHevc || !software.SoftwareAv1)
                _logger.LogInformation("This ffmpeg lacks {Missing}: that codec is encoded only with NVENC.",
                    string.Join(", ", new[] { software.SoftwareHevc ? null : "libx265", software.SoftwareAv1 ? null : "libsvtav1" }.Where(m => m is not null)));
            if (!Listed("h264_nvenc"))
            {
                _logger.LogInformation("NVENC unavailable: this ffmpeg has no h264_nvenc encoder.");
                return software with { NvencUnavailableReason = Messages.Nvenc_NotInBuild };
            }

            // A trial per codec with the options of a real export (the medium speed and level), on a small picture.
            async Task<string?> TrialAsync(ExportVideoCodec codec)
            {
                var plan = ExportVideoPlan.For(new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Medium, 192) { Encoder = ExportVideoEncoder.Nvenc, Codec = codec },
                    new FrameSize(640, 360), new ExportEncoderAvailability(true) { NvencHevc = true, NvencAv1 = true });
                var trial = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30",
                    "-frames:v", "10", "-vf", "format=yuv420p" };
                trial.AddRange(FfmpegExportEncoder.CodecArguments(plan));
                trial.AddRange(new[] { "-f", "null", "-" });
                var (c, o) = await RunAsync(ffmpeg, trial);
                if (c == 0) return null;
                _logger.LogInformation("NVENC {Codec} unavailable: the trial encoding failed (exit code {Code}): {Output}", codec, c, o.Trim());
                var last = o.Trim().Split('\n').LastOrDefault()?.Trim();
                return string.IsNullOrEmpty(last) ? $"exit code {c}" : last;
            }

            var h264 = await TrialAsync(ExportVideoCodec.H264);
            var hevc = Listed("hevc_nvenc") && await TrialAsync(ExportVideoCodec.Hevc) is null;
            var av1 = Listed("av1_nvenc") && await TrialAsync(ExportVideoCodec.Av1) is null;
            _logger.LogInformation("NVENC: H.264 {H264}, HEVC {Hevc}, AV1 {Av1}.", h264 is null, hevc, av1);
            return software with
            {
                Nvenc = h264 is null, NvencHevc = hevc, NvencAv1 = av1,
                NvencUnavailableReason = h264 is null ? null : string.Format(Messages.Nvenc_TrialFailed, h264)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The NVENC check failed; NVENC is treated as unavailable.");
            return new ExportEncoderAvailability(false, string.Format(Messages.Nvenc_CheckFailed, ex.Message));
        }
    }

    /// <summary>Runs ffmpeg to its end (at most <see cref="Timeout"/>); stdout and stderr together.</summary>
    private static async Task<(int Code, string Output)> RunAsync(string ffmpeg, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, "ffmpeg did not finish within " + Timeout.TotalSeconds + " s.");
        }
        return (process.ExitCode, await stdout + await stderr);
    }
}
