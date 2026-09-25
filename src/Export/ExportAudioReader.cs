using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Export;

/// <summary>
/// Offline, blocking counterpart of the Preview's audio span reader for one <see cref="AudioSpan"/> (D023,
/// Phase 8 Step 4). Placement is the shared <see cref="AudioPlacement"/>: the decoder is asked for the source
/// of the first timeline sample needed (<see cref="AudioPlacement.Request"/>) and its stream is placed by its
/// real first sample (<see cref="AudioPlacement.TimelineSampleOfStream"/>); samples before a window are
/// dropped, a stream starting later leaves silence before it, a stream that ends early leaves silence after
/// it (the source has no audio there — as in the Preview), nothing is played outside the clip.
/// <para>
/// Windows are requested in ascending, non-overlapping order. A request waits until its samples are decoded —
/// no underrun, no substitute — and any decoder failure is an <see cref="ExportException"/>.
/// </para>
/// </summary>
internal sealed class ExportAudioReader : IAsyncDisposable
{
    private const int ChunkFrames = 4096;

    private readonly PlaybackAsset _asset;
    private readonly IAudioDecoder _decoder;
    private readonly float[] _chunk = new float[ChunkFrames * 2];

    private IAudioSampleStream? _stream;
    private long _chunkStart;      // timeline sample of _chunk[0]
    private int _chunkFrames;      // frames in _chunk
    private int _consumed;         // frames of _chunk already passed
    private long _nextSample;      // timeline sample of the next frame the stream delivers
    private bool _ended;
    private bool _faulted;
    private long _windowEnd = long.MinValue;

    public ExportAudioReader(AudioSpan span, PlaybackAsset asset, IAudioDecoder decoder)
    {
        if (span.Status != SpanStatus.Audio)
            throw new ArgumentException($"A {span.Status} span has no audio to decode.", nameof(span));
        Span = span;
        Placement = AudioPlacement.Of(span);
        _asset = asset;
        _decoder = decoder;
    }

    public AudioSpan Span { get; }
    public AudioPlacement Placement { get; }

    /// <summary>Adds this clip's samples for timeline samples [<paramref name="from"/>, from + frames) of
    /// <paramref name="mix"/> (interleaved stereo) × <paramref name="gain"/> (<see cref="AudioMix.Add"/>).</summary>
    public async ValueTask MixIntoAsync(long from, Memory<float> mix, float gain, CancellationToken ct)
    {
        var frames = mix.Length / AudioFormat.Channels;
        if (from < _windowEnd)
            throw new InvalidOperationException($"Audio must be read in ascending order ({from} after {_windowEnd}).");
        if (_faulted)
            throw new InvalidOperationException("The reader failed before and can't be used again.");
        _windowEnd = from + frames;

        var begin = Math.Max(from, Placement.FirstSample);
        var end = Math.Min(from + frames, Placement.EndSample);
        if (begin >= end) return;

        try
        {
            if (_stream is null)
            {
                // Strict end: a decoder failing after some samples is an export error, never silence.
                _stream = await _decoder.OpenAsync(Placement.Request(_asset, begin) with { StrictEnd = true }, ct);
                _nextSample = Placement.TimelineSampleOfStream(_stream.FirstSampleIndex);
            }

            var at = begin;
            while (at < end)
            {
                if (_consumed == _chunkFrames)
                {
                    if (_ended) break;                                     // source ended: silence
                    var read = await _stream.ReadAsync(_chunk, ct);
                    if (read == 0) { _ended = true; break; }
                    _chunkStart = _nextSample;
                    _chunkFrames = read / AudioFormat.Channels;
                    _consumed = 0;
                    _nextSample += _chunkFrames;
                }

                var available = _chunkStart + _consumed;
                var chunkEnd = _chunkStart + _chunkFrames;
                if (chunkEnd <= at) { _consumed = _chunkFrames; continue; }     // before the window: dropped
                if (available > at) { at = Math.Min(available, end); continue; } // stream starts later: silence

                _consumed += (int)(at - available);
                var count = (int)(Math.Min(end, chunkEnd) - at);
                AudioMix.Add(_chunk.AsSpan(_consumed * AudioFormat.Channels, count * AudioFormat.Channels),
                    mix.Span.Slice((int)(at - from) * AudioFormat.Channels), gain);
                _consumed += count;
                at += count;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _faulted = true;
            throw;
        }
        catch (AudioDecodeException ex)
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
                $"The audio of '{Path.GetFileName(_asset.FilePath)}' could not be decoded ({error}): {message}", inner);

    public async ValueTask DisposeAsync()
    {
        var stream = _stream;
        _stream = null;
        if (stream is not null) await stream.DisposeAsync();
    }
}
