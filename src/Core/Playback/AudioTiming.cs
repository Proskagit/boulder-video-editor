using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// Exact sample arithmetic for playback audio at <see cref="AudioFormat.SampleRate"/>.
/// Timeline sample <c>k</c> is the instant <c>k / 48000</c> s. One sample is 208⅓ ticks, so
/// tick positions are converted once, with explicit rounding, and never accumulated.
/// </summary>
public static class AudioTiming
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>First timeline sample at or after <paramref name="time"/>: <c>ceil(t · 48000 / 10⁷)</c>.
    /// A clip <c>[S, E)</c> owns samples <c>[Ceiling(S), Ceiling(E))</c>, so adjacent clips
    /// neither overlap nor leave a gap.</summary>
    public static long CeilingSample(MediaTime time) => CeilDiv((Int128)time.Ticks * AudioFormat.SampleRate, TicksPerSecond);

    /// <summary>Timeline sample containing <paramref name="time"/>: <c>floor(t · 48000 / 10⁷)</c>.</summary>
    public static long FloorSample(MediaTime time) => FloorDiv((Int128)time.Ticks * AudioFormat.SampleRate, TicksPerSecond);

    /// <summary>Sample index nearest to <paramref name="time"/> (ties up): converts a source time
    /// or offset to samples with at most ½ sample (≈10.4 µs) of rounding.</summary>
    public static long NearestSample(MediaTime time) =>
        FloorDiv(2 * (Int128)time.Ticks * AudioFormat.SampleRate + TicksPerSecond, 2 * (Int128)TicksPerSecond);

    /// <summary>
    /// Offset <c>d</c> such that timeline sample <c>k</c> of a clip plays source sample <c>k + d</c>
    /// (source samples counted from the file's start time). From
    /// <c>srcTime = SourceIn + (k/48000 − S)</c>; rounded once per clip, so the source stays
    /// contiguous across the whole clip with ≤ ½ sample of error.
    /// </summary>
    public static long SourceOffset(MediaTime clipStart, MediaTime sourceIn) => NearestSample(sourceIn - clipStart);

    /// <summary>Start of sample <paramref name="sample"/> in ticks, rounded down.</summary>
    public static long SampleToTicksFloor(long sample) => FloorDiv((Int128)sample * TicksPerSecond, AudioFormat.SampleRate);

    /// <summary>Stereo frames → interleaved floats.</summary>
    public static int Floats(int frames) => frames * AudioFormat.Channels;

    private static long FloorDiv(Int128 a, Int128 b)
    {
        var q = a / b;
        return (long)((a % b != 0) && ((a < 0) != (b < 0)) ? q - 1 : q);
    }

    private static long CeilDiv(Int128 a, Int128 b) => -FloorDiv(-a, b);
}
