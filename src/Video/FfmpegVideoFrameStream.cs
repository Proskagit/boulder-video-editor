using System.Diagnostics;
using System.Threading.Channels;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Video;

/// <summary>
/// One running <c>ffmpeg</c> process writing raw BGRA frames to stdout while
/// <c>showinfo</c> reports each frame's PTS, time base and size on stderr. Frame
/// <c>i</c> on stdout is paired with the <c>i</c>-th showinfo frame line: showinfo is the
/// last filter and <c>-fps_mode passthrough</c> makes ffmpeg write every filtered frame
/// exactly once, in order.
/// </summary>
internal sealed class FfmpegVideoFrameStream : IVideoFrameStream
{
    private const int StderrTailLines = 20;

    private readonly Process _process;
    private readonly Stream _stdout;
    private readonly Channel<ShowInfoFrame> _frames = Channel.CreateUnbounded<ShowInfoFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Queue<string> _stderrTail = new();
    private readonly Task _stderrTask;
    private readonly TimeSpan _frameTimeout;
    private readonly ILogger _logger;

    private static int _liveProcesses;

    private DecodedFrame? _pushedBack;
    private long _delivered;
    private bool _disposed;

    private FfmpegVideoFrameStream(Process process, TimeSpan frameTimeout, ILogger logger)
    {
        _process = process;
        _stdout = process.StandardOutput.BaseStream;
        _frameTimeout = frameTimeout;
        _logger = logger;
        _stderrTask = Task.Run(ReadStderrAsync);
    }

    /// <summary>ffmpeg processes started by this class and not yet disposed (diagnostics/tests).</summary>
    internal static int LiveProcesses => Volatile.Read(ref _liveProcesses);

    /// <summary>Number of decode attempts (seek + preroll tries) it took to open this stream.</summary>
    public int Attempts { get; internal set; }

    /// <summary>The ffmpeg arguments of the successful attempt, for diagnostics.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    public static FfmpegVideoFrameStream Start(string ffmpegPath, IReadOnlyList<string> arguments, TimeSpan frameTimeout, ILogger logger)
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
        catch (Exception ex)
        {
            process.Dispose();
            throw new VideoDecodeException(VideoDecodeError.DecoderUnavailable, "ffmpeg could not be started.", ex);
        }

        Interlocked.Increment(ref _liveProcesses);
        return new FfmpegVideoFrameStream(process, frameTimeout, logger) { Arguments = arguments };
    }

    /// <summary>Makes <paramref name="frame"/> the next frame returned (used after the
    /// decoder inspected the first frame to validate the preroll).</summary>
    public void PushBack(DecodedFrame frame) => _pushedBack = frame;

    public async ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pushedBack is { } pending)
        {
            _pushedBack = null;
            return pending;
        }

        using var timeout = new CancellationTokenSource(_frameTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            while (true)
            {
                if (!await _frames.Reader.WaitToReadAsync(linked.Token))
                    return await CompleteAsync(linked.Token);

                var info = await _frames.Reader.ReadAsync(linked.Token);
                var stride = info.Width * DecodedFrame.BytesPerPixel;
                var buffer = new byte[(long)stride * info.Height];
                try
                {
                    await _stdout.ReadExactlyAsync(buffer, linked.Token);
                }
                catch (EndOfStreamException ex)
                {
                    throw new VideoDecodeException(VideoDecodeError.DecoderFailed,
                        $"ffmpeg output ended in the middle of frame {info.Index}. {StderrTail()}", ex);
                }

                if (info.Pts is not { } pts)
                {
                    _logger.LogWarning("Skipping decoded frame {Index} without a timestamp.", info.Index);
                    continue;
                }

                _delivered++;
                return new DecodedFrame(info.Width, info.Height, stride, buffer, new SourceTimestamp(pts, info.TimeBase));
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new VideoDecodeException(VideoDecodeError.Timeout,
                $"ffmpeg produced no frame within {_frameTimeout.TotalSeconds:0.#} s. {StderrTail()}");
        }
        catch (Exception ex) when (ex is FormatException || ex is ChannelClosedException { InnerException: FormatException })
        {
            var cause = ex as FormatException ?? (FormatException)ex.InnerException!;
            throw new VideoDecodeException(VideoDecodeError.DecoderFailed, $"Unexpected ffmpeg output: {cause.Message}", ex);
        }
    }

    private async Task<DecodedFrame?> CompleteAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct);
        if (_process.ExitCode != 0 && _delivered == 0)
        {
            throw new VideoDecodeException(VideoDecodeError.DecoderFailed,
                $"ffmpeg exited with code {_process.ExitCode}. {StderrTail()}");
        }
        return null;
    }

    private async Task ReadStderrAsync()
    {
        var parser = new ShowInfoParser();
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                if (parser.Parse(line) is { } frame)
                {
                    _frames.Writer.TryWrite(frame);
                    continue;
                }

                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line);
                    if (_stderrTail.Count > StderrTailLines) _stderrTail.Dequeue();
                }
            }
            _frames.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _frames.Writer.TryComplete(ex);
        }
    }

    private string StderrTail()
    {
        lock (_stderrTail)
            return _stderrTail.Count == 0 ? string.Empty : "ffmpeg: " + string.Join(" | ", _stderrTail.TakeLast(5));
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
