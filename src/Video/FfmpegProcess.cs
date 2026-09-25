using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Video;

/// <summary>
/// One running ffmpeg process with stdout left to the caller and stderr drained continuously
/// on its own task (so a full stderr pipe can never block ffmpeg). Each stderr line is offered
/// to a handler first; lines it doesn't consume are kept as a short tail for error messages.
/// Disposal kills the process tree.
/// <para>
/// Diagnostics (D024 Step 9.3; the caller's logger is a Video one, so these go to <c>ffmpeg-*.log</c>): the command
/// line at Debug when the process starts, and one entry when it has ended — Debug for a normal exit or when the app
/// ended it (disposal: shutdown, seek, cancellation), Warning with the command line, exit code and the unconsumed end
/// of stderr when ffmpeg exited with a non-zero code on its own.
/// </para>
/// </summary>
internal sealed class FfmpegProcess : IAsyncDisposable
{
    private const int StderrTailLines = 20;

    private static int _liveProcesses;

    private readonly Process _process;
    private readonly string _commandLine;
    private readonly Func<string, bool> _onLine;
    private readonly Action<Exception?> _onStderrEnd;
    private readonly ILogger _logger;
    private readonly Queue<string> _stderrTail = new();
    private readonly Task _stderrTask;
    private volatile bool _endedByApp;
    private bool _disposed;

    private FfmpegProcess(Process process, string commandLine, Func<string, bool> onLine, Action<Exception?> onStderrEnd, ILogger logger)
    {
        _process = process;
        _commandLine = commandLine;
        _onLine = onLine;
        _onStderrEnd = onStderrEnd;
        _logger = logger;
        _stderrTask = Task.Run(ReadStderrAsync);
    }

    /// <summary>ffmpeg processes started and not yet disposed (diagnostics/tests).</summary>
    public static int LiveProcesses => Volatile.Read(ref _liveProcesses);

    public Stream Stdout => _process.StandardOutput.BaseStream;

    /// <summary>ffmpeg's stdin (only when started with <c>redirectStdin</c>): the encoder's input.</summary>
    public Stream Stdin => _process.StandardInput.BaseStream;

    /// <summary>Ends ffmpeg's input (end of file on stdin). Throws <see cref="IOException"/> if ffmpeg is already gone.</summary>
    public void CloseStdin() => _process.StandardInput.Close();

    public int ExitCode => _process.ExitCode;

    /// <summary>Starts ffmpeg. <paramref name="onLine"/> returns true for lines it consumed;
    /// <paramref name="onStderrEnd"/> runs once when stderr closes (with the failure, if any).
    /// Throws the underlying exception if the process cannot be started.</summary>
    public static FfmpegProcess Start(string ffmpegPath, IReadOnlyList<string> arguments,
        Func<string, bool> onLine, Action<Exception?> onStderrEnd, ILogger logger, bool redirectStdin = false)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = redirectStdin,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        var commandLine = CommandLine(ffmpegPath, arguments);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ffmpeg could not be started: {CommandLine}", commandLine);
            process.Dispose();
            throw;
        }

        logger.LogDebug("ffmpeg started (process {ProcessId}): {CommandLine}", process.Id, commandLine);
        Interlocked.Increment(ref _liveProcesses);
        return new FfmpegProcess(process, commandLine, onLine, onStderrEnd, logger);
    }

    /// <summary>The command line as a user could paste it: arguments with spaces or quotes are quoted.</summary>
    internal static string CommandLine(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(a =>
            a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\""));

    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    /// <summary>
    /// For the end of stdout: waits for ffmpeg to exit and describes the failure when it exited with a non-zero
    /// code — its output then ended because it failed, not because the input ended (normal end: null). Both
    /// decoder streams judge their end with this (always for "nothing delivered at all", and for any end when the
    /// request asked for a strict end, D023). Cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<string?> AbnormalExitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct);
        return _process.ExitCode == 0 ? null : $"ffmpeg exited with code {_process.ExitCode}. {StderrTail()}";
    }

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
        LogEnd();
    }

    /// <summary>stderr closes when ffmpeg ends (or is killed): the one place that reports how it ended.</summary>
    private void LogEnd()
    {
        try
        {
            if (!_process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                _logger.LogDebug("ffmpeg (process {ProcessId}) closed stderr but is still running: {CommandLine}", _process.Id, _commandLine);
                return;
            }
            var exitCode = _process.ExitCode;
            if (_endedByApp)
                _logger.LogDebug("ffmpeg (process {ProcessId}) was ended by the app (exit code {ExitCode}).", _process.Id, exitCode);
            else if (exitCode == 0)
                _logger.LogDebug("ffmpeg (process {ProcessId}) exited normally.", _process.Id);
            else
                _logger.LogWarning("ffmpeg (process {ProcessId}) exited with code {ExitCode}: {CommandLine}{NewLine}{Stderr}",
                    _process.Id, exitCode, _commandLine, Environment.NewLine, StderrLines());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "The end of an ffmpeg process could not be read.");
        }
    }

    private string StderrLines()
    {
        lock (_stderrTail)
            return _stderrTail.Count == 0 ? "(no stderr output)" : string.Join(Environment.NewLine, _stderrTail);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Decrement(ref _liveProcesses);

        try
        {
            if (!_process.HasExited)
            {
                _endedByApp = true; // before the kill: its exit code is the app's doing, not a failure
                _process.Kill(entireProcessTree: true);
            }
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

        try
        {
            _process.Dispose();
        }
        catch (IOException ex)
        {
            // An encoder's stdin can't be flushed into a killed process.
            _logger.LogDebug(ex, "ffmpeg process streams did not close cleanly.");
        }
    }
}
