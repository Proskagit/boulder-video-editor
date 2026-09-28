using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// The peak envelope of a media file's audio (D024 Step 9.5), in source time: peak <c>i</c> is the largest
/// <c>max(|L|, |R|)</c> of source samples <c>[i · SamplesPerPeak, (i + 1) · SamplesPerPeak)</c> — 48 kHz samples
/// counted from the file's start time, as the audio decoder delivers them (<c>AudioFormat</c>) — on a linear scale,
/// 0 = silence, <see cref="FullScale"/> = 1.0 or more. <see cref="SampleCount"/> is where the audio ends: nothing is
/// known after it (silence). Display only — playback and export never read it.
/// </summary>
public sealed class Waveform
{
    /// <summary>The peak value of an amplitude of 1.0 (0 dBFS) or more.</summary>
    public const byte FullScale = 255;

    public Waveform(int samplesPerPeak, long sampleCount, ReadOnlyMemory<byte> peaks)
    {
        if (samplesPerPeak <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerPeak));
        if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (peaks.Length != PeakCountFor(sampleCount, samplesPerPeak))
            throw new ArgumentException("There must be exactly one peak per started group of samples.", nameof(peaks));
        SamplesPerPeak = samplesPerPeak;
        SampleCount = sampleCount;
        Peaks = peaks;
    }

    public int SamplesPerPeak { get; }

    /// <summary>Source samples the peaks cover: <c>[0, SampleCount)</c>.</summary>
    public long SampleCount { get; }

    public ReadOnlyMemory<byte> Peaks { get; }

    /// <summary><c>⌈sampleCount / samplesPerPeak⌉</c>.</summary>
    public static long PeakCountFor(long sampleCount, int samplesPerPeak) =>
        sampleCount / samplesPerPeak + (sampleCount % samplesPerPeak == 0 ? 0 : 1);

    /// <summary>The peak value of a sample's amplitude: <c>⌈|a| · 255⌉</c>, capped at <see cref="FullScale"/> — rounded
    /// up, so any sound stays visible.</summary>
    public static byte ToPeak(float amplitude)
    {
        var magnitude = Math.Abs(amplitude);
        if (float.IsNaN(magnitude)) return 0;
        return magnitude >= 1f ? FullScale : (byte)Math.Ceiling(magnitude * FullScale);
    }

    /// <summary>The largest peak of the groups that overlap source samples <c>[fromSample, toSample)</c>; 0 where
    /// nothing is known (before 0, after <see cref="SampleCount"/>, an empty range).</summary>
    public byte MaxPeak(long fromSample, long toSample)
    {
        var from = Math.Max(fromSample, 0);
        var to = Math.Min(toSample, SampleCount);
        if (from >= to) return 0;

        var first = (int)(from / SamplesPerPeak);
        var last = (int)((to - 1) / SamplesPerPeak);
        var span = Peaks.Span;
        byte max = 0;
        for (var i = first; i <= last; i++)
            if (span[i] > max) max = span[i];
        return max;
    }
}

/// <summary>
/// Waveforms of media with sound, kept in a cache folder the caller chooses (D024 Step 9.5). A cached waveform is
/// current while the source file has the same size and last-write time and the waveform rules are unchanged — the
/// thumbnail cache's key (Step 9.4). Playback and export never use waveforms.
/// </summary>
public interface IWaveformService
{
    /// <summary>
    /// A cached waveform, without decoding anything: for media whose file is there, only one that matches the file as
    /// it is now; for offline media (marked missing, or the file is gone) the last one cached, without looking at the
    /// source. Null when there is none (or it is unreadable).
    /// </summary>
    Waveform? TryGetCached(MediaAsset asset, string cacheFolder);

    /// <summary>
    /// The cached waveform, or — for analysed, online media with sound — the one made now by decoding the file's audio
    /// (and cached). Null for media without sound, not (successfully) analysed, offline without a cache, or whose
    /// audio could not be decoded. Cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<Waveform?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default);
}
