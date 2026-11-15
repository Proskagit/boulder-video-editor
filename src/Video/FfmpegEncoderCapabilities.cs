using System.Diagnostics;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Video;

/// <summary>
/// <see cref="IExportEncoderCapabilities"/> over the ffmpeg CLI (D059 §3). NVENC counts as available only when both hold:
/// <list type="number">
/// <item>the ffmpeg found has the <c>h264_nvenc</c> encoder (<c>ffmpeg -encoders</c>; the project's minimal build S1 needs
/// <c>--enable-ffnvcodec --enable-nvenc</c> for it, D059 §6);</item>
/// <item>a trial encoding of a few frames with the options of a real export («Сбалансированный») succeeds — it fails without
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
            if (code != 0 || !output.Split('\n').Any(l => l.Contains(" h264_nvenc ", StringComparison.Ordinal)))
            {
                _logger.LogInformation("NVENC unavailable: this ffmpeg has no h264_nvenc encoder.");
                return new ExportEncoderAvailability(false, Messages.Nvenc_NotInBuild);
            }

            var plan = ExportVideoPlan.For(ExportEncoding.Default with { Encoder = ExportVideoEncoder.Nvenc }, new FrameSize(640, 360),
                new ExportEncoderAvailability(true));
            var trial = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30",
                "-frames:v", "10", "-vf", "format=yuv420p" };
            trial.AddRange(FfmpegExportEncoder.CodecArguments(plan));
            trial.AddRange(new[] { "-f", "null", "-" });
            (code, output) = await RunAsync(ffmpeg, trial);
            if (code != 0)
            {
                var reason = output.Trim().Split('\n').LastOrDefault()?.Trim();
                _logger.LogInformation("NVENC unavailable: the trial encoding failed (exit code {Code}): {Output}", code, output.Trim());
                return new ExportEncoderAvailability(false, string.Format(Messages.Nvenc_TrialFailed, string.IsNullOrEmpty(reason) ? $"exit code {code}" : reason));
            }
            _logger.LogInformation("NVENC available (h264_nvenc trial encoding succeeded).");
            return new ExportEncoderAvailability(true);
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
