using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// The internal playback audio format: 48 kHz, stereo, 32-bit float, interleaved (L, R).
/// Every decoded source is converted to it; the mixer and the output device use it as is.
/// </summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
}

/// <summary>
/// Mixed samples pulled by the output device. <see cref="Read"/> runs on the device's own
/// thread: it must fill the whole span (silence where nothing is available) and must never
/// block, wait for decoding, allocate on the hot path, or start/stop decoders.
/// </summary>
public interface IAudioSampleSource
{
    /// <summary>Fills <paramref name="interleaved"/> (a whole number of stereo frames).</summary>
    void Read(Span<float> interleaved);
}

/// <summary>
/// An audio output device playing <see cref="AudioFormat"/>. Owned and called by the playback
/// service on one thread (the thread that creates it — device APIs may be apartment-bound).
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>
    /// The last observed playback position of the device — frames it reports as played (not
    /// merely queued), in <see cref="AudioFormat.SampleRate"/> units — accumulated over every
    /// Start/Stop session so it never runs backwards. It stands still while stopped and during
    /// the device's start-up latency. How closely it tracks the audible output is a property of
    /// the backend and is verified by tests, not guaranteed by this contract.
    /// </summary>
    IReferenceClock Clock { get; }

    /// <summary>Starts pulling from <paramref name="source"/>. False if no device could be started.</summary>
    bool TryStart(IAudioSampleSource source);

    /// <summary>Stops playback and discards queued, not yet played audio.</summary>
    void Stop();

    /// <summary>Set (from any thread) when the device failed or disappeared while playing.</summary>
    bool HasFailed { get; }
}

/// <summary>What to decode: one file's first audio stream, starting at or before
/// <see cref="SourcePosition"/>.</summary>
public sealed record AudioDecodeRequest
{
    public required string FilePath { get; init; }

    /// <summary>The file's start time (<c>MediaMetadata.StartTime</c>): origin of source time (D009).</summary>
    public MediaTime StartTime { get; init; }

    /// <summary>Source time of the first sample that will be needed.</summary>
    public MediaTime SourcePosition { get; init; }

    /// <summary>Playback speed (D022). At 1× every output sample is the next source sample. At other
    /// speeds the output is tempo-changed with the pitch kept: output sample <c>j</c> stands for source
    /// sample <see cref="IAudioSampleStream.FirstSampleIndex"/> <c>+ j · speed</c> (the decoder
    /// accounts for its own filter latency in <see cref="IAudioSampleStream.FirstSampleIndex"/>).</summary>
    public ClipSpeed Speed { get; init; }
}

/// <summary>Decodes source audio into <see cref="AudioFormat"/> with an exact sample position.</summary>
public interface IAudioDecoder
{
    /// <summary>Starts decoding near the requested position; the caller aligns samples by
    /// <see cref="IAudioSampleStream.FirstSampleIndex"/> (never by the request). Failures are
    /// reported as <see cref="AudioDecodeException"/>.</summary>
    Task<IAudioSampleStream> OpenAsync(AudioDecodeRequest request, CancellationToken ct = default);
}

/// <summary>A running audio decode: contiguous interleaved stereo float samples at 48 kHz.</summary>
public interface IAudioSampleStream : IAsyncDisposable
{
    /// <summary>Source sample index (48 kHz, counted from the file's start time) of the first
    /// frame this stream returns — taken from the decoder's real timestamps, not from the seek
    /// request. May be negative or before/after the requested position.</summary>
    long FirstSampleIndex { get; }

    /// <summary>Reads interleaved samples (a whole number of frames). Returns the number of
    /// floats written; 0 at the end of the stream.</summary>
    ValueTask<int> ReadAsync(Memory<float> interleaved, CancellationToken ct = default);
}

/// <summary>A controlled audio decoder failure for one source; playback treats it as silence.</summary>
public sealed class AudioDecodeException : Exception
{
    public AudioDecodeException(VideoDecodeError error, string message, Exception? inner = null)
        : base(message, inner) => Error = error;

    /// <summary>Same failure categories as video decoding.</summary>
    public VideoDecodeError Error { get; }
}
