using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Infrastructure;

/// <summary>
/// Resolves an FFmpeg-suite executable once per app run and caches the result (or the
/// "not found" outcome) — every subsequent call is free, so callers can ask on every
/// use without repeatedly spawning a "-version" probe process. Order: configured path
/// (if the file exists), then the plain executable name via PATH.
/// A probe cancelled by the caller's token is not an outcome: nothing is cached and the
/// <see cref="OperationCanceledException"/> propagates, so the next caller probes again.
/// </summary>
internal sealed class ExecutableLocator
{
    private readonly string _toolName;
    private readonly Func<string?> _configuredPath;
    private readonly ILogger _logger;
    private readonly Func<string, CancellationToken, Task<bool>> _probe;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private bool _resolved;
    private string? _cachedPath;

    public ExecutableLocator(string toolName, Func<string?> configuredPath, ILogger logger)
        : this(toolName, configuredPath, logger, IsExecutableAvailableAsync)
    {
    }

    /// <summary>Test seam: <paramref name="probe"/> replaces the "-version" process probe.</summary>
    internal ExecutableLocator(string toolName, Func<string?> configuredPath, ILogger logger,
        Func<string, CancellationToken, Task<bool>> probe)
    {
        _toolName = toolName;
        _configuredPath = configuredPath;
        _logger = logger;
        _probe = probe;
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

            // If the caller cancels mid-probe, ResolveAsync throws and _resolved stays false.
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
        if (await _probe(candidate, ct))
        {
            _logger.LogInformation("Found {Tool} on PATH as '{Candidate}'.", _toolName, candidate);
            return candidate;
        }

        _logger.LogWarning("{Tool} could not be located (not configured, not found on PATH). Features that need it are unavailable until FFmpeg is installed or configured.", _toolName);
        return null;
    }

    /// <summary>
    /// Runs "<paramref name="fileName"/> -version". Returns false when the executable is
    /// missing, fails, or exceeds the 5 s timeout; throws <see cref="OperationCanceledException"/>
    /// only when <paramref name="ct"/> itself is cancelled (that says nothing about the tool).
    /// </summary>
    internal static async Task<bool> IsExecutableAvailableAsync(string fileName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Declared outside the try so the catch blocks can still kill it (disposal happens last).
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

        try
        {
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller gave up — not evidence the tool is missing. Don't leave the probe running.
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            // Missing executable, no permission, etc. — all mean "not available",
            // never a crash (see Phase 3 rule: missing FFmpeg must be handled gracefully).
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Never started or already exited — nothing to clean up.
        }
    }
}
