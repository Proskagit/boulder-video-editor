using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Infrastructure;

/// <summary>
/// Resolves an FFmpeg-suite executable once per app run and caches the result (or the
/// "not found" outcome) — every subsequent call is free, so callers can ask on every
/// use without repeatedly spawning a "-version" probe process. Order: configured path
/// (if the file exists), then the plain executable name via PATH.
/// </summary>
internal sealed class ExecutableLocator
{
    private readonly string _toolName;
    private readonly Func<string?> _configuredPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private bool _resolved;
    private string? _cachedPath;

    public ExecutableLocator(string toolName, Func<string?> configuredPath, ILogger logger)
    {
        _toolName = toolName;
        _configuredPath = configuredPath;
        _logger = logger;
    }

    public async Task<string?> GetPathAsync(CancellationToken ct)
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
        var configured = _configuredPath();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                _logger.LogInformation("Using configured {Tool} path: {Path}", _toolName, configured);
                return configured;
            }

            _logger.LogWarning("Configured {Tool} path '{Path}' does not exist; falling back to PATH lookup.", _toolName, configured);
        }

        var candidate = OperatingSystem.IsWindows() ? _toolName + ".exe" : _toolName;
        if (await IsExecutableAvailableAsync(candidate, ct))
        {
            _logger.LogInformation("Found {Tool} on PATH as '{Candidate}'.", _toolName, candidate);
            return candidate;
        }

        _logger.LogWarning("{Tool} could not be located (not configured, not found on PATH). Features that need it are unavailable until FFmpeg is installed or configured.", _toolName);
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

            // Drain output so a full pipe buffer can't block "-version".
            var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode == 0;
        }
        catch
        {
            // Missing executable, no permission, etc. — all mean "not available",
            // never a crash (see Phase 3 rule: missing FFmpeg must be handled gracefully).
            return false;
        }
    }
}
