using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline.Playback;

/// <summary>
/// Audio for one (snapshot version, seek generation) pair. Keeps an <see cref="AudioSpanReader"/>
/// for every playable <see cref="AudioSpan"/> inside the look-ahead window and publishes them
/// to the <see cref="AudioMixer"/>. Runs on the service's (UI) thread; readers decode in the
/// background. On a snapshot update the previous pipeline's readers for unchanged spans are
/// taken over, so edits during playback do not interrupt audio that did not change.
/// </summary>
internal sealed class AudioPipeline : IAsyncDisposable
{
    private PlaybackSnapshot _snapshot;
    private readonly AudioMixer _mixer;
    private readonly IAudioDecoder _decoder;
    private readonly PlaybackSettings _settings;
    private readonly ILogger _logger;
    private readonly Dictionary<ReaderKey, AudioSpanReader> _readers = new();
    private readonly List<Task> _retired = new(); // readers being disposed after leaving the window
    private bool _disposed;

    /// <summary>What must match for a reader to be reused (gain and mute are applied by the mixer).</summary>
    private readonly record struct ReaderKey(Guid ClipId, Guid AssetId, MediaTime Start, MediaTime End, MediaTime SourceIn,
        string FilePath, MediaTime StartTime);

    public AudioPipeline(PlaybackSnapshot snapshot, long seekGeneration, long startSample, AudioMixer mixer,
        IAudioDecoder decoder, PlaybackSettings settings, ILogger logger, AudioPipeline? previous = null)
    {
        _snapshot = snapshot;
        SeekGeneration = seekGeneration;
        _mixer = mixer;
        _decoder = decoder;
        _settings = settings;
        _logger = logger;

        if (previous is not null)
        {
            var wanted = snapshot.AudioSpans.Where(IsPlayable).Select(Key).ToHashSet();
            foreach (var (key, reader) in previous._readers.ToList())
            {
                if (wanted.Contains(key) && reader.CanServeFrom(startSample))
                {
                    previous._readers.Remove(key);
                    _readers[key] = reader;
                }
            }
        }

        Maintain(startSample);
    }

    public long SnapshotVersion => _snapshot.SnapshotVersion;
    public long SeekGeneration { get; }

    /// <summary>Readers currently open (diagnostics/tests).</summary>
    internal int ReaderCount => _readers.Count;

    /// <summary>True when every open reader has decoded [from, until) (diagnostics/tests).</summary>
    internal bool HasData(long from, long until) => _readers.Values.All(r => r.HasData(from, until));

    /// <summary>Takes over <paramref name="snapshot"/>, which differs from the current one only in
    /// presentation (<see cref="PlaybackSnapshot.DiffersOnlyInPresentation"/>): every reader stays open and is
    /// republished with its new gain. Same pipeline, same seek generation.</summary>
    public void UpdateMix(PlaybackSnapshot snapshot, long position)
    {
        if (_disposed) return;
        _snapshot = snapshot;
        Maintain(position);
    }

    /// <summary>Opens readers for spans entering the look-ahead window from
    /// <paramref name="position"/>, closes finished ones and republishes the mix.</summary>
    public void Maintain(long position)
    {
        if (_disposed) return;

        var windowEnd = position + (long)(_settings.AudioLookahead.TotalSeconds * AudioFormat.SampleRate);
        var active = new Dictionary<ReaderKey, AudioSpan>();
        foreach (var span in _snapshot.AudioSpans)
        {
            if (!IsPlayable(span)) continue;
            if (AudioTiming.CeilingSample(span.TimelineEnd) <= position) continue;
            if (AudioTiming.CeilingSample(span.TimelineStart) >= windowEnd) continue;
            active[Key(span)] = span;
        }

        foreach (var (key, reader) in _readers.ToList())
        {
            if (active.ContainsKey(key)) continue;
            _readers.Remove(key);
            _retired.RemoveAll(t => t.IsCompleted);
            _retired.Add(reader.DisposeAsync().AsTask());
        }

        var entries = new List<MixEntry>(active.Count);
        foreach (var (key, span) in active)
        {
            if (!_readers.TryGetValue(key, out var reader))
            {
                reader = new AudioSpanReader(span, _snapshot.Assets[span.AssetId], position,
                    (int)(_settings.AudioBuffer.TotalSeconds * AudioFormat.SampleRate), _decoder, _logger);
                _readers[key] = reader;
            }
            entries.Add(new MixEntry(reader, (float)span.EffectiveGain));
        }
        _mixer.SetEntries(entries.ToArray());
    }

    private static bool IsPlayable(AudioSpan span) => span.Status == SpanStatus.Audio;

    private ReaderKey Key(AudioSpan span)
    {
        var asset = _snapshot.Assets[span.AssetId];
        return new ReaderKey(span.ClipId, span.AssetId, span.TimelineStart, span.TimelineEnd, span.SourceIn, asset.FilePath, asset.StartTime);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var readers = _readers.Values.ToList();
        _readers.Clear();
        foreach (var reader in readers)
            await reader.DisposeAsync();
        await Task.WhenAll(_retired);
    }
}
