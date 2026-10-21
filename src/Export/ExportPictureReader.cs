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
/// <para>
/// The frames of a Dissolve's or a Push's zone outside the clip (D025 §3) are never held (D025 §4 as changed in Phase 28):
/// when such a frame's answer would be a held one — the stream's first frame lies after its sample point, the stream
/// ended before it (the point is at or after the last frame's start plus one nominal source frame), or the decoder has no
/// frame at all there — the reader fails with <see cref="ExportFailure.TransitionSourceMissing"/> naming the transition.
/// The clip's own frames keep D009's hold-first / hold-last.
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
    private readonly long _ownFirst;   // the clip's own frames [_ownFirst, _ownEnd): outside them only a zone's
    private readonly long _ownEnd;
    private readonly ZonesOf? _zones;

    private IVideoFrameStream? _stream;
    private DecodedFrame? _current;    // the frame chosen for the last request (never after its sample point unless it is the first)
    private DecodedFrame? _next;       // read ahead: the first frame after _current
    private bool _ended;
    private bool _faulted;
    private long _lastRequested = long.MinValue;

    public ExportPictureReader(PictureSpan span, PlaybackAsset asset, FrameRate rate, IVideoDecoder decoder, ExportDecodeSettings settings,
        ZonesOf? zones = null)
    {
        if (span.Status is not (SpanStatus.Video or SpanStatus.StillImage))
            throw new ArgumentException($"A {span.Status} span has no picture to decode.", nameof(span));
        Span = span;
        _asset = asset;
        _rate = rate;
        _decoder = decoder;
        _settings = settings;
        // Shown range: the clip, extended into its dissolve zones (D025 §3) — the frames there come from its handles.
        _firstFrame = span.ShownStart.ToFrameCeiling(rate);
        _endFrame = span.ShownEnd.ToFrameCeiling(rate);
        _ownFirst = span.TimelineStart.ToFrameCeiling(rate);
        _ownEnd = span.TimelineEnd.ToFrameCeiling(rate);
        _zones = zones;
    }

    public PictureSpan Span { get; }

    private bool IsStill => Span.Status == SpanStatus.StillImage;

    /// <summary>The source sample point of <paramref name="timelineFrame"/> — the Preview's formula, from Core.</summary>
    public SourceSamplePoint SamplePoint(long timelineFrame) =>
        SourceFrameSelector.SamplePoint(Span.TimelineStart, Span.SourceIn, Span.Speed, timelineFrame, _rate, _asset.NominalFrameRate);

    /// <summary>The frame for <paramref name="timelineFrame"/>, which must lie inside the span's shown range and after
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

        var extended = !IsStill && (timelineFrame < _ownFirst || timelineFrame >= _ownEnd);
        try
        {
            var point = SamplePoint(timelineFrame);
            if (_current is null)
            {
                _stream = await _decoder.OpenAsync(new VideoDecodeRequest
                {
                    FilePath = _asset.FilePath,
                    StartTime = _asset.StartTime,
                    FirstSamplePoint = IsStill ? SourceFrameSelector.StillImage : point,   // a still: from its file's start
                    NominalFrameRate = _asset.NominalFrameRate,
                    MaxWidth = ExportDecodeSettings.FullResolutionBound,
                    MaxHeight = ExportDecodeSettings.FullResolutionBound,
                    Hardware = _settings.Hardware,
                    Hdr = _asset.Hdr,   // D033: the same conversion as the Preview
                    StrictEnd = true   // a decoder failing after some frames is an export error, never a held frame
                }, ct);
                // The decoder's first frame is at or before the point, or the stream's very first frame
                // (hold-first) — the same contract the Preview's reader relies on.
                _current = await _stream.ReadFrameAsync(ct);
                if (_current is null)
                {
                    if (extended) throw Missing(timelineFrame, "the decoder has no frame there");
                    throw new VideoDecodeException(VideoDecodeError.NoVideo, $"'{_asset.FilePath}' produced no frame.");
                }
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
            if (extended)
            {
                // A held frame is no answer for a zone's frame beyond the clip: the source must really have one there.
                if (!SourceFrameSelector.IsAtOrBefore(_current!.Timestamp, _asset.StartTime, point))
                    throw Missing(timelineFrame, "the source's first frame comes later");
                if (_next is null && _ended &&
                    !SourceFrameSelector.EndsAfter(_current.Timestamp, _asset.StartTime, point, _asset.NominalFrameRate is { IsValid: true } r ? r : _rate))
                    throw Missing(timelineFrame, "the source ends before it");
            }
            return _current!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _faulted = true;
            throw;
        }
        catch (ExportException)
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

    /// <summary>The failure of a zone's frame <paramref name="timelineFrame"/> the source has no frame for.</summary>
    private ExportException Missing(long timelineFrame, string why)
    {
        var zone = timelineFrame < _ownFirst ? _zones?.Before : _zones?.After;
        var source = zone is null ? null
            : new MissingTransitionSource(zone.TypeId, _zones!.TrackId, zone.LeftClipId, zone.RightClipId, zone.Cut, BeforeRendering: false);
        return new ExportException(ExportFailure.TransitionSourceMissing,
            $"'{Path.GetFileName(_asset.FilePath)}' has no source frame for timeline frame {timelineFrame} of a {zone?.TypeId ?? "transition"} ({why}).")
        {
            MissingSource = source
        };
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

/// <summary>The zones a picture span is extended into (D025 §3): the one where it is B (<see cref="Before"/>, its frames before
/// its start) and the one where it is A (<see cref="After"/>), on the track <see cref="TrackId"/>.</summary>
internal sealed record ZonesOf(Guid TrackId, DissolveZone? Before, DissolveZone? After)
{
    /// <summary>The zones of <paramref name="span"/> on <paramref name="layer"/> that extend pictures (Dissolve, Push).</summary>
    public static ZonesOf For(VideoLayer layer, PictureSpan span) => new(layer.TrackId,
        layer.Dissolves.FirstOrDefault(z => z.ExtendsPictures && z.RightClipId == span.ClipId),
        layer.Dissolves.FirstOrDefault(z => z.ExtendsPictures && z.LeftClipId == span.ClipId));
}
