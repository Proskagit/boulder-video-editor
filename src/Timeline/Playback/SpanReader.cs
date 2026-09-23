using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline.Playback;

/// <summary>Result of asking a <see cref="SpanReader"/> for a timeline frame.</summary>
internal readonly record struct ReaderResult(DecodedFrame? Frame, VideoDecodeException? Error)
{
    public static readonly ReaderResult NotReady = default;
    public bool IsReady => Frame is not null || Error is not null;
}

/// <summary>
/// Decodes one <see cref="PictureSpan"/> in the background. Video: a bounded buffer of frames
/// ahead of the playhead; the frame for a timeline frame is chosen with
/// <see cref="SourceFrameSelector"/> (D009) and is only returned once it is certain (a later
/// frame is buffered or the stream ended). Still image: a single decoded frame, cached and
/// held for the whole span. The background task only writes into this reader's own buffer
/// and stops publishing once cancelled.
/// </summary>
internal sealed class SpanReader : IAsyncDisposable
{
    private readonly IVideoDecoder _decoder;
    private readonly PlaybackAsset _asset;
    private readonly FrameRate _rate;
    private readonly PlaybackSettings _settings;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _space;
    private readonly object _lock = new();
    private readonly List<DecodedFrame> _buffer = new();
    private readonly Task _task;

    private DecodedFrame? _still;
    private DecodedFrame? _lastSelected;
    private SourceTimestamp? _lastBuffered;
    private bool _ended;
    private VideoDecodeException? _error;
    private long _lastRequestedFrame;

    public SpanReader(PictureSpan span, PlaybackAsset asset, FrameRate rate, long firstFrame,
        IVideoDecoder decoder, PlaybackSettings settings, ILogger logger)
    {
        Span = span;
        _asset = asset;
        _rate = rate;
        _decoder = decoder;
        _settings = settings;
        _logger = logger;
        _space = new SemaphoreSlim(Math.Max(2, settings.BufferFrames));
        _lastRequestedFrame = firstFrame;
        _task = Task.Run(() => RunAsync(firstFrame));
    }

    public PictureSpan Span { get; }

    /// <summary>Raised on a background thread whenever new data (frame, end, error) arrived.</summary>
    public event Action? Updated;

    private bool IsStill => Span.Status == SpanStatus.StillImage;

    public SourceSamplePoint SamplePoint(long timelineFrame) =>
        SourceFrameSelector.SamplePoint(Span.TimelineStart, Span.SourceIn, timelineFrame, _rate, _asset.NominalFrameRate);

    /// <summary>True once <see cref="TryGet"/> would return a certain answer for the frame, without consuming anything.</summary>
    public bool IsReady(long timelineFrame)
    {
        lock (_lock)
        {
            if (_error is not null || (IsStill && (_still is not null || _ended))) return true;
            if (_buffer.Count == 0) return _ended;
            var index = SourceFrameSelector.Select(Timestamps(), _asset.StartTime, SamplePoint(timelineFrame));
            return index < _buffer.Count - 1 || _ended;
        }
    }

    /// <summary>The frame for <paramref name="timelineFrame"/> if certain. Frames before it are
    /// released so decoding can continue.</summary>
    public ReaderResult TryGet(long timelineFrame)
    {
        Interlocked.Exchange(ref _lastRequestedFrame, timelineFrame);
        lock (_lock)
        {
            if (_error is not null) return new ReaderResult(null, _error);

            if (IsStill)
            {
                if (_still is not null) return new ReaderResult(_still, null);
                return _ended ? new ReaderResult(null, NoFrame()) : ReaderResult.NotReady;
            }

            if (_buffer.Count == 0)
            {
                if (!_ended) return ReaderResult.NotReady;
                return _lastSelected is not null ? new ReaderResult(_lastSelected, null) : new ReaderResult(null, NoFrame());
            }

            var index = SourceFrameSelector.Select(Timestamps(), _asset.StartTime, SamplePoint(timelineFrame));
            if (index > 0)
            {
                _buffer.RemoveRange(0, index);
                _space.Release(index);
            }

            if (_buffer.Count == 1 && !_ended)
                return ReaderResult.NotReady; // the next frame might still be at or before the point

            _lastSelected = _buffer[0];
            return new ReaderResult(_lastSelected, null);
        }
    }

    /// <summary>Exact comparison of two timestamps (time bases may differ).</summary>
    private static bool IsAfter(SourceTimestamp a, SourceTimestamp b) =>
        (Int128)a.Pts * a.TimeBase.Numerator * b.TimeBase.Denominator >
        (Int128)b.Pts * b.TimeBase.Numerator * a.TimeBase.Denominator;

    private List<SourceTimestamp> Timestamps() => _buffer.Select(f => f.Timestamp).ToList();

    private VideoDecodeException NoFrame() =>
        new(VideoDecodeError.NoVideo, $"'{_asset.FilePath}' produced no frame.");

    private async Task RunAsync(long firstFrame)
    {
        var ct = _cts.Token;
        var hardware = _settings.Hardware;
        var from = firstFrame;
        while (true)
        {
            var delivered = 0;
            try
            {
                var request = new VideoDecodeRequest
                {
                    FilePath = _asset.FilePath,
                    StartTime = _asset.StartTime,
                    FirstSamplePoint = SamplePoint(from),
                    NominalFrameRate = _asset.NominalFrameRate,
                    MaxWidth = _settings.MaxWidth,
                    MaxHeight = _settings.MaxHeight,
                    Hardware = hardware
                };

                await using var stream = await _decoder.OpenAsync(request, ct);
                if (IsStill)
                {
                    var frame = await stream.ReadFrameAsync(ct);
                    Publish(() => { _still = frame; _ended = true; });
                    return;
                }

                while (true)
                {
                    await _space.WaitAsync(ct);
                    DecodedFrame? frame;
                    try
                    {
                        frame = await stream.ReadFrameAsync(ct);
                    }
                    catch
                    {
                        _space.Release(); // no frame arrived for this slot
                        throw;
                    }
                    if (frame is null)
                    {
                        Publish(() => _ended = true);
                        return;
                    }
                    if (_lastBuffered is { } last && !IsAfter(frame.Timestamp, last))
                    {
                        _space.Release(); // already buffered before a fallback reopen
                        continue;
                    }
                    delivered++;
                    Publish(() =>
                    {
                        _buffer.Add(frame);
                        _lastBuffered = frame.Timestamp;
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (VideoDecodeException ex) when (hardware == HardwareDecoding.Auto && delivered > 0 && !ct.IsCancellationRequested)
            {
                // Hardware failure after decoding started: reopen the same span in software at
                // the frame playback last asked for. Frames already buffered stay valid; the new
                // stream's frames up to the last buffered PTS are skipped, so nothing is shown
                // twice, dropped or re-sought from the start of the clip.
                _logger.LogWarning(ex, "Decoding '{Path}' failed mid-stream; reopening without hardware decoding.", _asset.FilePath);
                hardware = HardwareDecoding.Disabled;
                from = Math.Max(firstFrame, Interlocked.Read(ref _lastRequestedFrame));
            }
            catch (VideoDecodeException ex)
            {
                _logger.LogWarning(ex, "Decoding '{Path}' failed.", _asset.FilePath);
                Publish(() => _error = ex);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure decoding '{Path}'.", _asset.FilePath);
                Publish(() => _error = new VideoDecodeException(VideoDecodeError.DecoderFailed, ex.Message, ex));
                return;
            }
        }
    }

    /// <summary>Applies a change to the shared state unless this reader was cancelled.</summary>
    private void Publish(Action change)
    {
        lock (_lock)
        {
            if (_cts.IsCancellationRequested) return;
            change();
        }
        if (!_cts.IsCancellationRequested)
            Updated?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_cts.IsCancellationRequested) return;
            _cts.Cancel();
        }
        try
        {
            await _task;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Span reader ended with an exception.");
        }
        _cts.Dispose();
        _space.Dispose();
    }
}
