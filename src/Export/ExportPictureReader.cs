using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Export;

/// <summary>How the export decodes its sources.</summary>
public sealed record ExportDecodeSettings
{
    /// <summary>Upper bound passed as the decoder's maximum frame size: large enough that sources are
    /// decoded at their own (display) size — never scaled down as for the Preview (D010: ≤ 1280 × 720).</summary>
    public const int FullResolutionBound = 16_384;

    /// <summary>Software decoding by default: the export is deterministic and a hardware failure in the
    /// middle of a stream can't happen (the Preview's mid-stream fallback, D012, is realtime behaviour).
    /// Frame selection doesn't depend on it (D009: identical PTS either way).</summary>
    public HardwareDecoding Hardware { get; init; } = HardwareDecoding.Disabled;
}

/// <summary>
/// Offline, blocking counterpart of the Preview's span reader for one <see cref="PictureSpan"/>
/// (D023). Frames are requested in ascending timeline order; each request returns exactly the frame
/// <see cref="SourceFrameSelector"/> picks for the timeline frame's sample point (D009/D022):
/// the last decoded frame whose source time is at or before the point; before the stream's first frame
/// that first frame (hold-first); after the stream's end its last frame (hold-last). A still image is
/// decoded once and held.
/// <para>
/// Unlike realtime playback nothing is ever substituted: a request waits until the answer is certain
/// (the next frame is known to lie after the point, or the stream ended) — no late frame, no previous
/// picture — and any decoder failure is an <see cref="ExportException"/>.
/// </para>
/// </summary>
internal sealed class ExportPictureReader : IAsyncDisposable
{
    private readonly PlaybackAsset _asset;
    private readonly FrameRate _rate;
    private readonly IVideoDecoder _decoder;
    private readonly ExportDecodeSettings _settings;
    private readonly long _firstFrame;
    private readonly long _endFrame;   // first timeline frame after the span

    private IVideoFrameStream? _stream;
    private DecodedFrame? _current;    // the frame chosen for the last request (never after its sample point unless it is the first)
    private DecodedFrame? _next;       // read ahead: the first frame after _current
    private bool _ended;
    private bool _faulted;
    private long _lastRequested = long.MinValue;

    public ExportPictureReader(PictureSpan span, PlaybackAsset asset, FrameRate rate, IVideoDecoder decoder, ExportDecodeSettings settings)
    {
        if (span.Status is not (SpanStatus.Video or SpanStatus.StillImage))
            throw new ArgumentException($"A {span.Status} span has no picture to decode.", nameof(span));
        Span = span;
        _asset = asset;
        _rate = rate;
        _decoder = decoder;
        _settings = settings;
        _firstFrame = span.TimelineStart.ToFrameCeiling(rate);
        _endFrame = span.TimelineEnd.ToFrameCeiling(rate);
    }

    public PictureSpan Span { get; }

    private bool IsStill => Span.Status == SpanStatus.StillImage;

    /// <summary>The source sample point of <paramref name="timelineFrame"/> — the Preview's formula, from Core.</summary>
    public SourceSamplePoint SamplePoint(long timelineFrame) =>
        SourceFrameSelector.SamplePoint(Span.TimelineStart, Span.SourceIn, Span.Speed, timelineFrame, _rate, _asset.NominalFrameRate);

    /// <summary>The frame for <paramref name="timelineFrame"/>, which must lie inside the span and after
    /// every frame requested before.</summary>
    public async ValueTask<DecodedFrame> GetAsync(long timelineFrame, CancellationToken ct)
    {
        if (timelineFrame < _firstFrame || timelineFrame >= _endFrame)
            throw new ArgumentOutOfRangeException(nameof(timelineFrame), $"Frame {timelineFrame} is outside the clip [{_firstFrame}, {_endFrame}).");
        if (timelineFrame <= _lastRequested)
            throw new InvalidOperationException($"Frames must be requested in ascending order ({timelineFrame} after {_lastRequested}).");
        if (_faulted)
            throw new InvalidOperationException("The reader failed before and can't be used again.");
        _lastRequested = timelineFrame;

        try
        {
            var point = SamplePoint(timelineFrame);
            if (_current is null)
            {
                _stream = await _decoder.OpenAsync(new VideoDecodeRequest
                {
                    FilePath = _asset.FilePath,
                    StartTime = _asset.StartTime,
                    FirstSamplePoint = point,
                    NominalFrameRate = _asset.NominalFrameRate,
                    MaxWidth = ExportDecodeSettings.FullResolutionBound,
                    MaxHeight = ExportDecodeSettings.FullResolutionBound,
                    Hardware = _settings.Hardware,
                    StrictEnd = true   // a decoder failing after some frames is an export error, never a held frame
                }, ct);
                // The decoder's first frame is at or before the point, or the stream's very first frame
                // (hold-first) — the same contract the Preview's reader relies on.
                _current = await _stream.ReadFrameAsync(ct)
                    ?? throw new VideoDecodeException(VideoDecodeError.NoVideo, $"'{_asset.FilePath}' produced no frame.");
                if (IsStill) _ended = true;
            }

            // Advance while the next frame is at or before the point: the last such frame is the one
            // SourceFrameSelector.Select picks; at the end of the stream the last frame stays (hold-last).
            while (!IsStill)
            {
                if (_next is null && !_ended)
                {
                    _next = await _stream!.ReadFrameAsync(ct);
                    if (_next is null) _ended = true;
                }
                if (_next is null || !SourceFrameSelector.IsAtOrBefore(_next.Timestamp, _asset.StartTime, point))
                    break;
                _current = _next;
                _next = null;
            }
            return _current!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _faulted = true;
            throw;
        }
        catch (VideoDecodeException ex)
        {
            _faulted = true;
            throw Failure(ex.Error, ex.Message, ex);
        }
        catch (Exception ex)
        {
            _faulted = true;
            throw Failure(VideoDecodeError.DecoderFailed, ex.Message, ex);
        }
    }

    private ExportException Failure(VideoDecodeError error, string message, Exception inner) =>
        error == VideoDecodeError.DecoderUnavailable
            ? new ExportException(ExportFailure.EncoderUnavailable, "ffmpeg could not be found.", inner)
            : new ExportException(ExportFailure.DecodeFailed,
                $"'{Path.GetFileName(_asset.FilePath)}' could not be decoded ({error}): {message}", inner);

    public async ValueTask DisposeAsync()
    {
        var stream = _stream;
        _stream = null;
        _current = _next = null;
        if (stream is not null) await stream.DisposeAsync();
    }
}
