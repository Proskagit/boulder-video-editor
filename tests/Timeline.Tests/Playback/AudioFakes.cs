using System.Collections.Concurrent;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>
/// Synthetic audio: sample <c>i</c> of the file (counted from its start time) is
/// (<c>i·2⁻²⁴</c>, <c>−i·2⁻²⁴</c>) — exact in float for i &lt; 2²⁴ — unless <see cref="Constant"/> is set.
/// </summary>
internal sealed record FakeAudioSource(long LengthSamples, long StreamStartSample = 0, float? Constant = null)
{
    public const float Unit = 1f / (1 << 24);

    public static long IndexOf(float left) => (long)Math.Round(left / Unit);

    public (float L, float R) Sample(long index) =>
        Constant is { } c ? (c, c) : (index * Unit, -index * Unit);
}

/// <summary>Fake <see cref="IAudioDecoder"/>: starts a configurable number of samples before
/// (preroll) or after the requested position, can be gated or fail.</summary>
internal sealed class FakeAudioDecoder : IAudioDecoder
{
    private readonly ConcurrentDictionary<string, FakeAudioSource> _sources = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
    private readonly ConcurrentDictionary<string, AudioDecodeException> _failures = new();
    private int _live;

    public ConcurrentQueue<AudioDecodeRequest> Requests { get; } = new();

    /// <summary>Stream starts this many samples before the requested one (negative = after it).</summary>
    public long PrerollSamples { get; set; } = 100;

    public int LiveStreams => Volatile.Read(ref _live);

    /// <summary>How long closing a stream takes (a slow ffmpeg shutdown).</summary>
    public TimeSpan StreamDisposeDelay { get; set; }

    public void Add(string path, FakeAudioSource source) => _sources[path] = source;
    public TaskCompletionSource Gate(string path) => _gates[path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Fail(string path) => _failures[path] = new AudioDecodeException(VideoDecodeError.DecoderFailed, "fake audio failure");
    public int OpenCount(string path) => Requests.Count(r => r.FilePath == path);

    public async Task<IAudioSampleStream> OpenAsync(AudioDecodeRequest request, CancellationToken ct = default)
    {
        Requests.Enqueue(request);
        if (_gates.TryGetValue(request.FilePath, out var gate))
            await gate.Task.WaitAsync(ct);
        if (_failures.TryGetValue(request.FilePath, out var failure))
            throw failure;
        if (!_sources.TryGetValue(request.FilePath, out var source))
            throw new AudioDecodeException(VideoDecodeError.FileNotFound, "fake: no such file");

        var requested = AudioTiming.NearestSample(request.SourcePosition);
        var first = Math.Max(source.StreamStartSample, requested - PrerollSamples);
        Interlocked.Increment(ref _live);
        return new Stream(source, first, StreamDisposeDelay, () => Interlocked.Decrement(ref _live));
    }

    private sealed class Stream : IAudioSampleStream
    {
        private readonly FakeAudioSource _source;
        private readonly TimeSpan _disposeDelay;
        private readonly Action _onDispose;
        private long _next;
        private int _disposed;

        public Stream(FakeAudioSource source, long first, TimeSpan disposeDelay, Action onDispose)
        {
            _source = source;
            _disposeDelay = disposeDelay;
            _onDispose = onDispose;
            FirstSampleIndex = first;
            _next = first;
        }

        public long FirstSampleIndex { get; }

        public ValueTask<int> ReadAsync(Memory<float> interleaved, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var span = interleaved.Span;
            var frames = (int)Math.Min(span.Length / 2, _source.StreamStartSample + _source.LengthSamples - _next);
            for (var i = 0; i < frames; i++)
            {
                var (l, r) = _source.Sample(_next++);
                span[2 * i] = l;
                span[2 * i + 1] = r;
            }
            return ValueTask.FromResult(Math.Max(0, frames) * 2);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_disposeDelay > TimeSpan.Zero) await Task.Delay(_disposeDelay);
            _onDispose();
        }
    }
}

/// <summary>
/// Fake <see cref="IAudioOutput"/>: the test "plays" frames with <see cref="Play"/>, which pulls
/// them from the mixer and advances the played-frames clock (no device latency).
/// </summary>
internal sealed class FakeAudioOutput : IAudioOutput
{
    private readonly PlayedClock _clock = new();
    private IAudioSampleSource? _source;

    public bool Available { get; set; } = true;
    public bool IsRunning => _source is not null;
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public bool HasFailed { get; private set; }
    public IReferenceClock Clock => _clock;

    public bool TryStart(IAudioSampleSource source)
    {
        if (!Available) return false;
        _source = source;
        HasFailed = false;
        StartCount++;
        return true;
    }

    public void Stop()
    {
        _source = null;
        StopCount++;
    }

    /// <summary>Device pulls and plays <paramref name="frames"/> frames; returns them interleaved.</summary>
    public float[] Play(int frames)
    {
        var buffer = new float[frames * 2];
        if (_source is null || HasFailed) return buffer;
        _source.Read(buffer);
        _clock.Played += frames;
        return buffer;
    }

    /// <summary>The device disappears: the clock stops advancing.</summary>
    public void Fail() => HasFailed = true;

    public void Dispose() { }

    private sealed class PlayedClock : IReferenceClock
    {
        public long Played;
        public ReferenceTime Now => new(Played, AudioFormat.SampleRate);
    }
}
