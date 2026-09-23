using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Video;

/// <summary>
/// One running ffmpeg process with stdout left to the caller and stderr drained continuously
/// on its own task (so a full stderr pipe can never block ffmpeg). Each stderr line is offered
/// to a handler first; lines it doesn't consume are kept as a short tail for error messages.
/// Disposal kills the process tree.
/// </summary>
internal sealed class FfmpegProcess : IAsyncDisposable
{
    private const int StderrTailLines = 20;

    private static int _liveProcesses;

    private readonly Process _process;
    private readonly Func<string, bool> _onLine;
    private readonly Action<Exception?> _onStderrEnd;
    private readonly ILogger _logger;
    private readonly Queue<string> _stderrTail = new();
    private readonly Task _stderrTask;
    private bool _disposed;

    private FfmpegProcess(Process process, Func<string, bool> onLine, Action<Exception?> onStderrEnd, ILogger logger)
    {
        _process = process;
        _onLine = onLine;
        _onStderrEnd = onStderrEnd;
        _logger = logger;
        _stderrTask = Task.Run(ReadStderrAsync);
    }

    /// <summary>ffmpeg processes started and not yet disposed (diagnostics/tests).</summary>
    public static int LiveProcesses => Volatile.Read(ref _liveProcesses);

    public Stream Stdout => _process.StandardOutput.BaseStream;

    public int ExitCode => _process.ExitCode;

    /// <summary>Starts ffmpeg. <paramref name="onLine"/> returns true for lines it consumed;
    /// <paramref name="onStderrEnd"/> runs once when stderr closes (with the failure, if any).
    /// Throws the underlying exception if the process cannot be started.</summary>
    public static FfmpegProcess Start(string ffmpegPath, IReadOnlyList<string> arguments,
        Func<string, bool> onLine, Action<Exception?> onStderrEnd, ILogger logger)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        Interlocked.Increment(ref _liveProcesses);
        return new FfmpegProcess(process, onLine, onStderrEnd, logger);
    }

    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    /// <summary>The last few unconsumed stderr lines, for error messages.</summary>
    public string StderrTail()
    {
        lock (_stderrTail)
            return _stderrTail.Count == 0 ? string.Empty : "ffmpeg: " + string.Join(" | ", _stderrTail.TakeLast(5));
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                if (_onLine(line)) continue;
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line);
                    if (_stderrTail.Count > StderrTailLines) _stderrTail.Dequeue();
                }
            }
            _onStderrEnd(null);
        }
        catch (Exception ex)
        {
            _onStderrEnd(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Decrement(ref _liveProcesses);

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited between the check and the kill.
        }

        try
        {
            await _stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffmpeg stderr reader did not finish cleanly.");
        }

        _process.Dispose();
    }
}
