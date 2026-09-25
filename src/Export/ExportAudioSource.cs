using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Export;

/// <summary>
/// The audio of an export (D023, Phase 8 Step 4): the snapshot's <see cref="PlaybackSnapshot.AudioSpans"/> mixed
/// offline into 48 kHz stereo float PCM (<see cref="AudioFormat"/>), exactly
/// <see cref="ExportOutput.AudioSampleCount"/> frames — silence where nothing plays, also for a project without
/// any audio. Read sequentially with <see cref="ReadAsync"/>.
/// <para>
/// The mix is the Preview's (<see cref="AudioMix"/>): per window, every audible span in snapshot order adds its
/// samples × <see cref="AudioMix.Gain"/>, then the sum is clamped to [−1, 1]. Spans with gain 0 (muted clip,
/// volume 0) contribute nothing and are not decoded — the same output as the Preview, which mixes them at 0;
/// muted tracks have no spans; a video clip on a hidden track keeps its audio (the snapshot's rules, D010).
/// Readers open when their clip is reached and close after it. An audible span that can't be played (offline,
/// unsupported — the preflight blocks those) or any decode failure is an <see cref="ExportException"/>.
/// </para>
/// </summary>
public sealed class ExportAudioSource : IAsyncDisposable
{
    private readonly PlaybackSnapshot _snapshot;
    private readonly IAudioDecoder _decoder;
    private readonly List<(AudioSpan Span, AudioPlacement Placement, float Gain)> _audible;
    private readonly Dictionary<Guid, ExportAudioReader> _readers = new();
    private bool _disposed;

    public ExportAudioSource(PlaybackSnapshot snapshot, IAudioDecoder decoder)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        SampleCount = ExportOutput.For(snapshot).AudioSampleCount;
        _audible = snapshot.AudioSpans
            .Select(span => (span, AudioPlacement.Of(span), AudioMix.Gain(span)))
            .Where(a => a.Item3 != 0 && a.Item2.EndSample > a.Item2.FirstSample)
            .ToList();
    }

    /// <summary>Frames (stereo samples at 48 kHz) of the whole export.</summary>
    public long SampleCount { get; }

    /// <summary>Timeline sample of the next frame <see cref="ReadAsync"/> delivers.</summary>
    public long Position { get; private set; }

    /// <summary>Open decoders (diagnostics/tests).</summary>
    internal int ReaderCount => _readers.Count;

    /// <summary>Fills <paramref name="interleaved"/> (a whole number of stereo frames) with the next mixed frames;
    /// returns the number of floats written, 0 after <see cref="SampleCount"/> frames.</summary>
    public async ValueTask<int> ReadAsync(Memory<float> interleaved, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (interleaved.Length % AudioFormat.Channels != 0)
            throw new ArgumentException("The buffer must hold whole stereo frames.", nameof(interleaved));

        var from = Position;
        var frames = (int)Math.Min(interleaved.Length / AudioFormat.Channels, SampleCount - from);
        if (frames <= 0) return 0;
        var until = from + frames;
        var mix = interleaved[..(frames * AudioFormat.Channels)];
        mix.Span.Clear();

        foreach (var (span, placement, gain) in _audible) // snapshot order, like the Preview's mixer entries
        {
            if (placement.EndSample <= from || placement.FirstSample >= until) continue;
            ct.ThrowIfCancellationRequested();
            await Reader(span).MixIntoAsync(from, mix, gain, ct);
        }
        AudioMix.Clamp(mix.Span);

        // Clips that end within this window are finished.
        foreach (var (span, placement, _) in _audible)
        {
            if (placement.EndSample > until || !_readers.Remove(span.ClipId, out var done)) continue;
            await done.DisposeAsync();
        }

        Position = until;
        return frames * AudioFormat.Channels;
    }

    private ExportAudioReader Reader(AudioSpan span)
    {
        if (_readers.TryGetValue(span.ClipId, out var reader)) return reader;
        if (span.Status != SpanStatus.Audio || !_snapshot.Assets.TryGetValue(span.AssetId, out var asset))
            throw new ExportException(ExportFailure.DecodeFailed,
                $"The audio of a clip at {span.TimelineStart} can't be exported ({span.Status}{(span.Reason is null ? "" : ": " + span.Reason)}).");
        reader = new ExportAudioReader(span, asset, _decoder);
        _readers[span.ClipId] = reader;
        return reader;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var readers = _readers.Values.ToList();
        _readers.Clear();
        foreach (var reader in readers)
            await reader.DisposeAsync();
    }
}
