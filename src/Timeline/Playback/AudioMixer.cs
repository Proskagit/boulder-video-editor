using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Timeline.Playback;

/// <summary>A reader and the gain it is mixed with.</summary>
internal readonly record struct MixEntry(AudioSpanReader Reader, float Gain);

/// <summary>
/// Sums the active <see cref="AudioSpanReader"/>s into the output (48 kHz stereo float) with the
/// shared <see cref="AudioMix"/> rule (the export mixes the same way, D023): samples × gain, summed,
/// clamped to [-1, 1]. <see cref="Read"/> runs on the device thread: it only reads the currently
/// published entry array (replaced atomically by the UI thread), never waits and never allocates.
/// Samples nobody covers are silence.
/// </summary>
internal sealed class AudioMixer : IAudioSampleSource
{
    private MixEntry[] _entries = Array.Empty<MixEntry>();
    private long _writeSample;

    /// <summary>Timeline sample of the next frame the device will pull.</summary>
    public long WritePosition => Interlocked.Read(ref _writeSample);

    /// <summary>Sets the next sample to produce. Only while the output is stopped.</summary>
    public void Reset(long sample) => Interlocked.Exchange(ref _writeSample, sample);

    /// <summary>Publishes the readers to mix from now on (UI thread).</summary>
    public void SetEntries(MixEntry[] entries) => Volatile.Write(ref _entries, entries);

    public void Read(Span<float> interleaved)
    {
        interleaved.Clear();
        var frames = interleaved.Length / AudioFormat.Channels;
        var from = Interlocked.Read(ref _writeSample);
        var until = from + frames;

        foreach (var entry in Volatile.Read(ref _entries))
        {
            if (entry.Reader.EndSample > from && entry.Reader.FirstSample < until)
                entry.Reader.MixInto(from, interleaved, entry.Gain);
        }

        AudioMix.Clamp(interleaved);

        Interlocked.Add(ref _writeSample, frames);
    }
}
