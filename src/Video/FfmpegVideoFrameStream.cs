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
    private readonly FfmpegProcess _process;
    private readonly Channel<ShowInfoFrame> _frames;
    private readonly TimeSpan _frameTimeout;
    private readonly ILogger _logger;
    private readonly bool _strictEnd;

    private DecodedFrame? _pushedBack;
    private long _delivered;
    private bool _disposed;

    private FfmpegVideoFrameStream(FfmpegProcess process, Channel<ShowInfoFrame> frames, TimeSpan frameTimeout, ILogger logger, bool strictEnd)
    {
        _strictEnd = strictEnd;
        _process = process;
        _frames = frames;
        _frameTimeout = frameTimeout;
        _logger = logger;
    }

    /// <summary>ffmpeg processes started and not yet disposed, video and audio (diagnostics/tests).</summary>
    internal static int LiveProcesses => FfmpegProcess.LiveProcesses;

    /// <summary>Number of decode attempts (seek + preroll tries) it took to open this stream.</summary>
    public int Attempts { get; internal set; }

    /// <summary>The ffmpeg arguments of the successful attempt, for diagnostics.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <param name="strictEnd"><see cref="VideoDecodeRequest.StrictEnd"/>: a failed ffmpeg is an error at any end of
    /// the stream, not only when it delivered no frame.</param>
    public static FfmpegVideoFrameStream Start(string ffmpegPath, IReadOnlyList<string> arguments, TimeSpan frameTimeout, ILogger logger,
        bool strictEnd = false)
    {
        var frames = Channel.CreateUnbounded<ShowInfoFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var parser = new ShowInfoParser();
        FfmpegProcess process;
        try
        {
            process = FfmpegProcess.Start(ffmpegPath, arguments,
                line =>
                {
                    if (parser.Parse(line) is not { } frame) return false;
                    frames.Writer.TryWrite(frame);
                    return true;
                },
                error => frames.Writer.TryComplete(error),
                logger);
        }
        catch (Exception ex)
        {
            throw new VideoDecodeException(VideoDecodeError.DecoderUnavailable, "ffmpeg could not be started.", ex);
        }

        return new FfmpegVideoFrameStream(process, frames, frameTimeout, logger, strictEnd) { Arguments = arguments };
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
                    await _process.Stdout.ReadExactlyAsync(buffer, linked.Token);
                }
                catch (EndOfStreamException ex)
                {
                    throw new VideoDecodeException(VideoDecodeError.DecoderFailed,
                        $"ffmpeg output ended in the middle of frame {info.Index}. {_process.StderrTail()}", ex);
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
                $"ffmpeg produced no frame within {_frameTimeout.TotalSeconds:0.#} s. {_process.StderrTail()}");
        }
        catch (Exception ex) when (ex is FormatException || ex is ChannelClosedException { InnerException: FormatException })
        {
            var cause = ex as FormatException ?? (FormatException)ex.InnerException!;
            throw new VideoDecodeException(VideoDecodeError.DecoderFailed, $"Unexpected ffmpeg output: {cause.Message}", ex);
        }
    }

    private async Task<DecodedFrame?> CompleteAsync(CancellationToken ct)
    {
        // Nothing delivered: a failed ffmpeg is always an error. After frames: only with a strict end (export) —
        // playback keeps its last frame (D012/D023).
        if (await _process.AbnormalExitAsync(ct) is { } failure && (_delivered == 0 || _strictEnd))
        {
            throw new VideoDecodeException(VideoDecodeError.DecoderFailed,
                _delivered == 0 ? failure : $"The video stream ended early after {_delivered} frames: {failure}");
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _process.DisposeAsync();
    }
}
