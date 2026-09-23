using System.Diagnostics;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiVideoEditor.Infrastructure;

/// <summary>
/// Resolves the ffprobe executable once per app run and caches the result (or the
/// "not found" outcome) — every subsequent call is free, so callers can ask on
/// every analysis without repeatedly spawning a "-version" probe process.
/// </summary>
public sealed class FfprobeLocator : IFfprobeLocator
{
    private readonly IOptions<FfmpegOptions> _options;
    private readonly ILogger<FfprobeLocator> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private bool _resolved;
    private string? _cachedPath;

    public FfprobeLocator(IOptions<FfmpegOptions> options, ILogger<FfprobeLocator> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<string?> GetFfprobePathAsync(CancellationToken ct = default)
    {
        if (_resolved)
            return _cachedPath;

        await _lock.WaitAsync(ct);
        try
        {
            if (_resolved)
                return _cachedPath;

            _cachedPath = await ResolveAsync(ct);
            _resolved = true;
            return _cachedPath;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string?> ResolveAsync(CancellationToken ct)
    {
        var configured = _options.Value.FfprobePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                _logger.LogInformation("Using configured ffprobe path: {Path}", configured);
                return configured;
            }

            _logger.LogWarning("Configured ffprobe path '{Path}' does not exist; falling back to PATH lookup.", configured);
        }

        var candidate = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        if (await IsExecutableAvailableAsync(candidate, ct))
        {
            _logger.LogInformation("Found ffprobe on PATH as '{Candidate}'.", candidate);
            return candidate;
        }

        _logger.LogWarning("ffprobe could not be located (not configured, not found on PATH). Metadata analysis will be unavailable until FFmpeg is installed or configured.");
        return null;
    }

    private static async Task<bool> IsExecutableAvailableAsync(string fileName, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
                return false;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await process.WaitForExitAsync(linked.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            // Missing executable, no permission, etc. — all mean "not available",
            // never a crash (see Phase 3 rule: missing ffprobe must be handled gracefully).
            return false;
        }
    }
}
