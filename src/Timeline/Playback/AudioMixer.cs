using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Timeline.Playback;

/// <summary>A reader, the gain it is mixed with and its clip's fade (D025).</summary>
internal readonly record struct MixEntry(AudioSpanReader Reader, float Gain, AudioFadeEnvelope Fade = default);

/// <summary>
/// Sums the active <see cref="AudioSpanReader"/>s into the output (48 kHz stereo float) with the
/// shared <see cref="AudioMix"/> rule (the export mixes the same way, D023): samples × gain × the clip's fade (D025), summed,
/// clamped to [-1, 1]. <see cref="Read"/> runs on the device thread: it only reads the currently
/// published entry array (replaced atomically by the UI thread), never waits and never allocates.
/// Samples nobody covers are silence.
/// </summary>
internal sealed class AudioMixer : IAudioSampleSource
{
    private MixEntry[] _entries = Array.Empty<MixEntry>();
    private long _writeSample;
    private long _endSample = long.MaxValue;

    /// <summary>Timeline sample of the next frame the device will pull.</summary>
    public long WritePosition => Interlocked.Read(ref _writeSample);

    /// <summary>Sets the next sample to produce. Only while the output is stopped.</summary>
    public void Reset(long sample) => Interlocked.Exchange(ref _writeSample, sample);

    /// <summary>Publishes the readers to mix from now on (UI thread).</summary>
    public void SetEntries(MixEntry[] entries) => Volatile.Write(ref _entries, entries);

    /// <summary>Samples at or after <paramref name="sample"/> are silence — the end of a loop range (D030 §8), so the
    /// buffered sound never runs past Out; <see cref="long.MaxValue"/>: no end (UI thread).</summary>
    public void SetEnd(long sample) => Interlocked.Exchange(ref _endSample, sample);

    public void Read(Span<float> interleaved)
    {
        interleaved.Clear();
        var frames = interleaved.Length / AudioFormat.Channels;
        var from = Interlocked.Read(ref _writeSample);
        var until = from + frames;

        foreach (var entry in Volatile.Read(ref _entries))
        {
            if (entry.Reader.EndSample > from && entry.Reader.FirstSample < until)
                entry.Reader.MixInto(from, interleaved, entry.Gain, entry.Fade);
        }

        AudioMix.Clamp(interleaved);

        var end = Interlocked.Read(ref _endSample);
        if (end < until)
            interleaved[(int)(Math.Max(0, end - from) * AudioFormat.Channels)..].Clear();

        Interlocked.Add(ref _writeSample, frames);
    }
}
