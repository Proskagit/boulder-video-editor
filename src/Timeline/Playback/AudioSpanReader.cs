using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline.Playback;

/// <summary>
/// Decodes one <see cref="AudioSpan"/> in the background into a bounded buffer indexed by
/// timeline sample. Timeline sample <c>k</c> of the clip plays source sample <c>k + d</c>
/// (<see cref="AudioTiming.SourceOffset"/>); decoded frames are placed by the decoder's real
/// first-sample index, frames before the requested start are dropped and a late start is
/// filled with silence. The mixer (device thread) consumes it through <see cref="MixInto"/>,
/// which never blocks: missing samples are silence (underrun) and the reader realigns to
/// whatever the mixer asks for next — time never shifts.
/// </summary>
internal sealed class AudioSpanReader : IAsyncDisposable
{
    private const int ChunkFrames = 2048;

    private readonly IAudioDecoder _decoder;
    private readonly PlaybackAsset _asset;
    private readonly ILogger _logger;
    private readonly long _sourceOffset;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _space = new(0, 1);
    private readonly object _lock = new();
    private readonly float[] _ring;       // interleaved stereo
    private readonly int _capacityFrames;
    private readonly Task _task;

    // Buffered frames cover timeline samples [_headSample, _headSample + _count).
    private long _headSample;
    private int _headIndex;               // frame index of _headSample in _ring
    private int _count;
    private bool _ended;

    public AudioSpanReader(AudioSpan span, PlaybackAsset asset, long startSample, int bufferFrames,
        IAudioDecoder decoder, ILogger logger)
    {
        Span = span;
        _asset = asset;
        _decoder = decoder;
        _logger = logger;
        FirstSample = AudioTiming.CeilingSample(span.TimelineStart);
        EndSample = AudioTiming.CeilingSample(span.TimelineEnd);
        _sourceOffset = AudioTiming.SourceOffset(span.TimelineStart, span.SourceIn);
        _capacityFrames = Math.Max(ChunkFrames * 2, bufferFrames);
        _ring = new float[AudioTiming.Floats(_capacityFrames)];
        _headSample = Math.Clamp(startSample, FirstSample, EndSample);
        _task = Task.Run(() => RunAsync(_headSample));
    }

    public AudioSpan Span { get; }

    /// <summary>Timeline samples this clip owns: [FirstSample, EndSample).</summary>
    public long FirstSample { get; }
    public long EndSample { get; }

    /// <summary>Set once decoding failed; the span stays silent.</summary>
    public AudioDecodeException? Error { get; private set; }

    /// <summary>Frames the mixer needed but that were not decoded yet (underrun), for diagnostics.</summary>
    public long UnderrunFrames { get; private set; }

    /// <summary>True if this reader can still provide samples from <paramref name="sample"/> on
    /// (it has not already discarded them) — the condition for reusing it in a new pipeline.</summary>
    public bool CanServeFrom(long sample)
    {
        lock (_lock) return Error is null && _headSample <= Math.Max(sample, FirstSample);
    }

    /// <summary>True when the samples for [<paramref name="from"/>, <paramref name="until"/>) of
    /// this clip are decoded (or will never come: error / end). Diagnostics and tests.</summary>
    internal bool HasData(long from, long until)
    {
        lock (_lock)
        {
            var begin = Math.Max(from, FirstSample);
            var end = Math.Min(until, EndSample);
            if (begin >= end || Error is not null || _ended) return true;
            return _headSample <= begin && _headSample + _count >= end;
        }
    }

    /// <summary>
    /// Device thread: adds this span's samples for timeline frames [<paramref name="from"/>,
    /// from + dest/2) into <paramref name="dest"/> × <paramref name="gain"/>, and discards
    /// everything before the end of that range. Never blocks or allocates.
    /// </summary>
    public void MixInto(long from, Span<float> dest, float gain)
    {
        var frames = dest.Length / AudioFormat.Channels;
        var until = from + frames;
        var begin = Math.Max(from, FirstSample);
        var end = Math.Min(until, EndSample);
        var freed = false;

        lock (_lock)
        {
            if (begin < end && Error is null)
            {
                Discard(begin);
                // Samples before the buffered head are not decoded (yet): silence.
                var missingBefore = Math.Min(end, _headSample) - begin;
                if (missingBefore > 0) UnderrunFrames += missingBefore;

                var available = Math.Min(end, _headSample + _count) - Math.Max(begin, _headSample);
                if (available > 0)
                {
                    var destFrame = (int)(Math.Max(begin, _headSample) - from);
                    for (var i = 0; i < available; i++)
                    {
                        var src = AudioTiming.Floats((_headIndex + i) % _capacityFrames);
                        var dst = AudioTiming.Floats(destFrame + i);
                        dest[dst] += _ring[src] * gain;
                        dest[dst + 1] += _ring[src + 1] * gain;
                    }
                }

                var missingAfter = end - Math.Max(begin, _headSample + _count);
                if (missingAfter > 0 && !_ended) UnderrunFrames += missingAfter;
            }

            freed = Discard(until);
        }

        if (freed && _space.CurrentCount == 0)
            _space.Release();
    }

    /// <summary>Drops buffered frames before <paramref name="sample"/>; moves the head forward
    /// even past the buffered data so late-arriving frames are dropped too.</summary>
    private bool Discard(long sample)
    {
        if (sample <= _headSample) return false;
        var drop = (int)Math.Min(_count, sample - _headSample);
        _headIndex = (_headIndex + drop) % _capacityFrames;
        _count -= drop;
        _headSample = sample;
        if (_count == 0) _headIndex = 0;
        return drop > 0;
    }

    private async Task RunAsync(long startSample)
    {
        var ct = _cts.Token;
        try
        {
            var sourceTime = new MediaTime(AudioTiming.SampleToTicksFloor(startSample + _sourceOffset));
            ct.ThrowIfCancellationRequested(); // retired before the task ran: don't start a decoder
            await using var stream = await _decoder.OpenAsync(new AudioDecodeRequest
            {
                FilePath = _asset.FilePath,
                StartTime = _asset.StartTime,
                SourcePosition = sourceTime
            }, ct);

            // Timeline sample of the next decoded frame.
            var next = stream.FirstSampleIndex - _sourceOffset;
            var chunk = new float[AudioTiming.Floats(ChunkFrames)];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, ct);
                if (read == 0) break;
                var frames = read / AudioFormat.Channels;
                if (!await AppendAsync(chunk, frames, next, ct)) break;
                next += frames;
            }
            Complete(null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (AudioDecodeException ex)
        {
            _logger.LogWarning(ex, "Audio decoding of '{Path}' failed; the clip plays as silence.", _asset.FilePath);
            Complete(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected audio decoding failure for '{Path}'.", _asset.FilePath);
            Complete(new AudioDecodeException(VideoDecodeError.DecoderFailed, ex.Message, ex));
        }
    }

    /// <summary>Stores decoded frames starting at timeline sample <paramref name="first"/>,
    /// filling any gap before them with silence. False once the clip end is reached.</summary>
    private async Task<bool> AppendAsync(float[] chunk, int frames, long first, CancellationToken ct)
    {
        var offset = 0;
        while (offset < frames)
        {
            var written = 0;
            var progressed = false;
            lock (_lock)
            {
                var tail = _headSample + _count;                 // next timeline sample to store
                if (tail >= EndSample) return false;
                var at = first + offset;
                if (at + (frames - offset) <= tail) return true;  // everything before the head: dropped
                if (at < tail) { offset += (int)(tail - at); continue; }

                var free = _capacityFrames - _count;
                if (free > 0)
                {
                    if (at > tail)
                    {
                        // The stream starts after the needed sample: silence for the gap first.
                        var silence = (int)Math.Min(Math.Min(at - tail, free), EndSample - tail);
                        for (var i = 0; i < silence; i++) Put(null, 0);
                        progressed = silence > 0;
                    }
                    else
                    {
                        written = (int)Math.Min(Math.Min(free, frames - offset), EndSample - tail);
                        for (var i = 0; i < written; i++) Put(chunk, offset + i);
                        progressed = written > 0;
                    }
                }
            }

            offset += written;
            if (!progressed)
                await _space.WaitAsync(ct); // buffer full: wait until the mixer consumes
        }
        return true;
    }

    private void Put(float[]? chunk, int frame)
    {
        var at = AudioTiming.Floats((_headIndex + _count) % _capacityFrames);
        _ring[at] = chunk is null ? 0 : chunk[AudioTiming.Floats(frame)];
        _ring[at + 1] = chunk is null ? 0 : chunk[AudioTiming.Floats(frame) + 1];
        _count++;
    }

    private void Complete(AudioDecodeException? error)
    {
        lock (_lock)
        {
            if (_cts.IsCancellationRequested) return;
            _ended = true;
            Error = error;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try
        {
            await _task;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio reader ended with an exception.");
        }
        _cts.Dispose();
        // _space is deliberately not disposed: the mixer may still hold this reader for the
        // Read in progress when the pipeline is replaced, and MixInto may release it.
    }
}
