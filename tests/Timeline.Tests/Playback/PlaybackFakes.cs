using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>Advances by one tick on every read, like a real clock that keeps running between calls.</summary>
internal sealed class AutoAdvancingReferenceClock : IReferenceClock
{
    private long _value = 1_000;
    public List<long> Reads { get; } = new();
    public ReferenceTime Now
    {
        get
        {
            var value = Interlocked.Increment(ref _value);
            lock (Reads) Reads.Add(value);
            return new ReferenceTime(value, TimeSpan.TicksPerSecond);
        }
    }
}

internal sealed class FakeReferenceClock : IReferenceClock
{
    private long _value;
    public ReferenceTime Now => new(Interlocked.Read(ref _value), TimeSpan.TicksPerSecond);
    public void Advance(MediaTime by) => Interlocked.Add(ref _value, by.Ticks);
    public void Advance(double seconds) => Advance(MediaTime.FromSeconds(seconds));
}

/// <summary>A synthetic source: CFR frames whose 1×1 pixel encodes the frame number.</summary>
internal sealed record FakeSource(FrameRate Rate, int FrameCount, long StartPts = 0)
{
    public TimeBase TimeBase => new(Rate.Denominator, Rate.Numerator);

    public DecodedFrame Frame(int index)
    {
        var pixels = BitConverter.GetBytes(index);
        return new DecodedFrame(1, 1, 4, pixels, new SourceTimestamp(StartPts + index, TimeBase));
    }

    public List<SourceTimestamp> Timestamps() => Enumerable.Range(0, FrameCount).Select(i => new SourceTimestamp(StartPts + i, TimeBase)).ToList();
}

/// <summary>
/// Fake <see cref="IVideoDecoder"/> honouring the decoder contract: the stream starts at the
/// frame selected for the first sample point (D009). Per file it can delay opening until a gate
/// is released, fail on open, or fail after some frames.
/// </summary>
internal sealed class FakeVideoDecoder : IVideoDecoder
{
    private readonly ConcurrentDictionary<string, FakeSource> _sources = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _softwareGates = new();
    private readonly ConcurrentDictionary<string, VideoDecodeError> _openFailures = new();
    private readonly ConcurrentDictionary<string, int> _failAfter = new();
    private readonly ConcurrentDictionary<string, (int Frames, TaskCompletionSource Release)> _holds = new();

    public ConcurrentQueue<VideoDecodeRequest> Requests { get; } = new();

    private int _liveStreams;

    /// <summary>Streams opened and not yet disposed.</summary>
    public int LiveStreams => Volatile.Read(ref _liveStreams);

    public void Add(string path, FakeSource source) => _sources[path] = source;
    public TaskCompletionSource Gate(string path) => _gates[path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Delays only opens with <see cref="HardwareDecoding.Disabled"/> (the software fallback).</summary>
    public TaskCompletionSource GateSoftware(string path) => _softwareGates[path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    public void FailOpen(string path, VideoDecodeError error) => _openFailures[path] = error;
    public void FailAfter(string path, int frames) => _failAfter[path] = frames;

    /// <summary>Streams of <paramref name="path"/> opened from now on deliver <paramref name="frames"/>
    /// frames and then stall until the returned source is completed (a decoder falling behind).</summary>
    public TaskCompletionSource HoldAfter(string path, int frames)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _holds[path] = (frames, release);
        return release;
    }
    public int OpenCount(string path) => Requests.Count(r => r.FilePath == path);

    public async Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
    {
        Requests.Enqueue(request);
        if (_gates.TryGetValue(request.FilePath, out var gate))
            await gate.Task.WaitAsync(ct);
        if (request.Hardware == HardwareDecoding.Disabled && _softwareGates.TryGetValue(request.FilePath, out var softwareGate))
            await softwareGate.Task.WaitAsync(ct);
        if (_openFailures.TryGetValue(request.FilePath, out var error))
            throw new VideoDecodeException(error, $"fake {error}");
        if (!_sources.TryGetValue(request.FilePath, out var source))
            throw new VideoDecodeException(VideoDecodeError.FileNotFound, "fake: no such file");

        var first = SourceFrameSelector.Select(source.Timestamps(), request.StartTime, request.FirstSamplePoint);
        var failAfter = _failAfter.TryGetValue(request.FilePath, out var n) && request.Hardware == HardwareDecoding.Auto ? n : int.MaxValue;
        Interlocked.Increment(ref _liveStreams);
        var hold = _holds.TryGetValue(request.FilePath, out var h) ? h : ((int, TaskCompletionSource)?)null;
        return new Stream(source, first, failAfter, () => Interlocked.Decrement(ref _liveStreams), hold);
    }

    private sealed class Stream : IVideoFrameStream
    {
        private readonly FakeSource _source;
        private readonly Action _onDispose;
        private int _next;
        private int _remainingBeforeFailure;
        private int _disposed;
        private readonly (int Frames, TaskCompletionSource Release)? _hold;
        private int _delivered;

        public Stream(FakeSource source, int first, int failAfter, Action onDispose, (int, TaskCompletionSource)? hold = null) =>
            (_source, _next, _remainingBeforeFailure, _onDispose, _hold) = (source, first, failAfter, onDispose, hold);

        public async ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_hold is { } hold && _delivered++ >= hold.Frames)
                await hold.Release.Task.WaitAsync(ct);
            if (_remainingBeforeFailure-- <= 0)
                throw new VideoDecodeException(VideoDecodeError.DecoderFailed, "fake hardware failure");
            return _next < _source.FrameCount ? _source.Frame(_next++) : null;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _onDispose();
            return ValueTask.CompletedTask;
        }
    }

    public static int Number(DecodedFrame frame) => BitConverter.ToInt32(frame.Pixels.Span);
}
