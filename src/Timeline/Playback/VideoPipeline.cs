using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline.Playback;

/// <summary>What the pipeline has for one timeline frame: every visible layer bottom to top, and
/// the compatibility picture (topmost picture layer) for the single-picture Preview.</summary>
internal readonly record struct VideoFrameResult(ImmutableArray<LayerPicture> Layers, PreviewPicture? Picture);

/// <summary>
/// Decoding for one seek generation, starting at a timeline frame (D010/D011, multi-layer since
/// Phase 7 Step 6). Keeps a <see cref="SpanReader"/> for every decodable layer that
/// <see cref="PlaybackSnapshot.LayersAt"/> returns — layers hidden under an opaque full-canvas video
/// get none — plus, within the prefetch window, the layers that appear at the next clip edge.
/// Readers of layers no longer needed are disposed.
/// <para>
/// A snapshot that differs only in presentation (<see cref="PlaybackSnapshot.DiffersOnlyInPresentation"/>)
/// is taken over by <see cref="UpdatePresentation"/>: same pipeline, same seek generation, existing
/// readers kept. If it changes which layers are visible (opacity 1 → 0.9 uncovers a layer), the
/// newly needed reader opens and that layer is <see cref="LayerPictureState.Pending"/> until its
/// first frame — no buffering. Readers are keyed by clip: within one pipeline a clip's decoding
/// (timing, source, media) never changes, only its presentation.
/// </para>
/// <para>
/// Late frames (D012) are per layer: a layer whose decoder is behind keeps showing its last frame,
/// flagged not current. A layer whose decoding fails at run time becomes a placeholder and never
/// hides the layers below it. Pictures are requested from the service's thread; readers decode in
/// the background.
/// </para>
/// </summary>
internal sealed class VideoPipeline : IAsyncDisposable
{
    private PlaybackSnapshot _snapshot;
    private readonly IVideoDecoder _decoder;
    private readonly PlaybackSettings _settings;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, SpanReader> _readers = new();
    private readonly Dictionary<Guid, DecodedFrame> _lastFrame = new();            // per layer, for late frames
    private readonly Dictionary<Guid, VideoDecodeException> _failed = new();        // run-time failures → placeholders
    private readonly List<Task> _retired = new(); // readers being disposed after leaving the composition
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ImmutableArray<SpanReader> _startReaders;
    private readonly long _startFrame;
    private bool _disposed;

    public VideoPipeline(PlaybackSnapshot snapshot, long seekGeneration, long startFrame,
        IVideoDecoder decoder, PlaybackSettings settings, ILogger logger)
    {
        _snapshot = snapshot;
        SeekGeneration = seekGeneration;
        _decoder = decoder;
        _settings = settings;
        _logger = logger;
        _startFrame = startFrame;

        // Ready once every layer visible at the start frame has its picture (or its error).
        _startReaders = DecodableLayers(Time(startFrame))
            .Select(layer => Open(layer.Span, startFrame))
            .ToImmutableArray();
        foreach (var reader in _startReaders)
            reader.Updated += CheckReady;
        CheckReady();
    }

    /// <summary>Version of the snapshot the pipeline currently works with (changes with
    /// <see cref="UpdatePresentation"/>, never the decoding).</summary>
    public long SnapshotVersion => _snapshot.SnapshotVersion;

    public long SeekGeneration { get; }

    /// <summary>Completes with true when every layer visible at the start frame is known, false if
    /// the pipeline was disposed first.</summary>
    public Task<bool> Ready => _ready.Task;

    public bool IsReady => _ready.Task.IsCompletedSuccessfully && _ready.Task.Result;

    /// <summary>Set once any reader reported that no decoder backend is available.</summary>
    public bool DecoderUnavailable { get; private set; }

    /// <summary>Open readers (diagnostics/tests).</summary>
    internal int ReaderCount => _readers.Count;

    /// <summary>Clips that currently have a reader (diagnostics/tests).</summary>
    internal IReadOnlyCollection<Guid> ReaderClipIds => _readers.Keys.ToList();

    /// <summary>Takes over a snapshot that differs from the current one only in presentation. The
    /// caller guarantees that (<see cref="PlaybackSnapshot.DiffersOnlyInPresentation"/>); readers,
    /// buffered frames and the seek generation stay, the next <see cref="GetFrame"/> opens or closes
    /// readers for layers that became visible or hidden.</summary>
    public void UpdatePresentation(PlaybackSnapshot snapshot)
    {
        if (_disposed) return;
        _snapshot = snapshot;
    }

    /// <summary>The layers for <paramref name="timelineFrame"/> and the compatibility picture (null
    /// while the topmost picture layer has nothing certain for this frame yet).</summary>
    public VideoFrameResult GetFrame(long timelineFrame)
    {
        if (_disposed) return new VideoFrameResult(ImmutableArray<LayerPicture>.Empty, null);

        var time = Time(timelineFrame);
        // A layer found failing while the pictures are collected stops occluding; collect again so
        // the layers it hid appear in the same frame (at most once per layer).
        for (var attempt = 0; ; attempt++)
        {
            var failuresBefore = _failed.Count;
            var layers = _snapshot.LayersAt(time, MayOcclude);
            var prefetch = PrefetchLayers(time);
            Retain(layers.OfType<PictureLayer>().Where(IsDecodable).Select(l => l.Span.ClipId)
                .Concat(prefetch.Select(p => p.Span.ClipId)).ToHashSet());
            foreach (var (span, frame) in prefetch)
                if (!_readers.ContainsKey(span.ClipId)) Open(span, frame);

            var pictures = layers.Select(layer => Picture(layer, timelineFrame)).ToImmutableArray();
            if (_failed.Count == failuresBefore || attempt >= layers.Length)
                return new VideoFrameResult(pictures, Compatibility(pictures));
        }
    }

    private LayerPicture Picture(CompositionLayer layer, long timelineFrame)
    {
        if (layer is not PictureLayer picture)
            return new LayerPicture(layer, LayerPictureState.Text);

        var span = picture.Span;
        switch (span.Status)
        {
            case SpanStatus.Offline:
                return new LayerPicture(layer, LayerPictureState.Offline, Message: span.Reason);
            case SpanStatus.Unsupported:
                return new LayerPicture(layer, LayerPictureState.Unsupported, Message: span.Reason);
        }

        if (_failed.TryGetValue(span.ClipId, out var failure))
            return Failed(layer, failure);

        var reader = _readers.TryGetValue(span.ClipId, out var existing) ? existing : Open(span, timelineFrame);
        var result = reader.TryGet(timelineFrame);
        if (result.Error is { } error)
        {
            if (error.Error == VideoDecodeError.DecoderUnavailable) DecoderUnavailable = true;
            _failed[span.ClipId] = error;
            return Failed(layer, error);
        }
        if (result.Frame is { } frame)
        {
            _lastFrame[span.ClipId] = frame;
            return new LayerPicture(layer, LayerPictureState.Frame, frame);
        }
        return _lastFrame.TryGetValue(span.ClipId, out var late)
            ? new LayerPicture(layer, LayerPictureState.Frame, late, IsCurrent: false)
            : new LayerPicture(layer, LayerPictureState.Pending, IsCurrent: false);
    }

    private static LayerPicture Failed(CompositionLayer layer, VideoDecodeException error) =>
        new(layer, error.Error == VideoDecodeError.FileNotFound ? LayerPictureState.Offline : LayerPictureState.DecodeError,
            Message: error.Message);

    /// <summary>The Phase 5 single picture: the topmost picture layer, or black when there is none.
    /// Null when that layer has nothing certain for this frame (pending or late) — the service then
    /// keeps showing its previous picture, flagged not current.</summary>
    private static PreviewPicture? Compatibility(ImmutableArray<LayerPicture> pictures)
    {
        for (var i = pictures.Length - 1; i >= 0; i--)
        {
            var p = pictures[i];
            if (p.Layer is not PictureLayer) continue;
            return p.State switch
            {
                LayerPictureState.Frame when p.IsCurrent => new PreviewPicture(PictureKind.Frame, p.Frame, p.Layer.ClipId),
                LayerPictureState.Offline => new PreviewPicture(PictureKind.Offline, null, p.Layer.ClipId, p.Message),
                LayerPictureState.Unsupported => new PreviewPicture(PictureKind.Unsupported, null, p.Layer.ClipId, p.Message),
                LayerPictureState.DecodeError => new PreviewPicture(PictureKind.DecodeError, null, p.Layer.ClipId, p.Message),
                _ => null
            };
        }
        return PreviewPicture.Black;
    }

    private bool MayOcclude(PictureLayer layer) => !_failed.ContainsKey(layer.Span.ClipId);

    private MediaTime Time(long frame) => MediaTime.FromFrame(frame, _snapshot.FrameRate);

    private IEnumerable<PictureLayer> DecodableLayers(MediaTime time) =>
        _snapshot.LayersAt(time, MayOcclude).OfType<PictureLayer>().Where(IsDecodable);

    /// <summary>Decodable layers that become visible at the next clip edge within the prefetch window.</summary>
    private List<(PictureSpan Span, long Frame)> PrefetchLayers(MediaTime time)
    {
        var change = _snapshot.NextPictureChange(time);
        if (change >= _snapshot.Duration || (change - time).Ticks > _settings.PrefetchWindow.Ticks)
            return new List<(PictureSpan, long)>();
        var frame = FrameMath.CeilingFrame(change, _snapshot.FrameRate);
        return DecodableLayers(Time(frame)).Select(l => (l.Span, frame)).ToList();
    }

    private void Retain(HashSet<Guid> needed)
    {
        foreach (var (clipId, reader) in _readers.ToList())
        {
            if (needed.Contains(clipId)) continue;
            _readers.Remove(clipId);
            _lastFrame.Remove(clipId);
            _failed.Remove(clipId);
            _retired.RemoveAll(t => t.IsCompleted);
            _retired.Add(reader.DisposeAsync().AsTask());
        }
    }

    private SpanReader Open(PictureSpan span, long firstFrame)
    {
        var asset = _snapshot.Assets[span.AssetId];
        var reader = new SpanReader(span, asset, _snapshot.FrameRate, firstFrame, _decoder, _settings, _logger);
        _readers[span.ClipId] = reader;
        return reader;
    }

    private void CheckReady()
    {
        foreach (var reader in _startReaders)
            if (!reader.IsReady(_startFrame)) return;
        _ready.TrySetResult(true);
    }

    private static bool IsDecodable(PictureLayer layer) => IsDecodable(layer.Span);

    private static bool IsDecodable(PictureSpan span) => span.Status is SpanStatus.Video or SpanStatus.StillImage;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _ready.TrySetResult(false);
        var readers = _readers.Values.ToList();
        _readers.Clear();
        foreach (var reader in readers)
            await reader.DisposeAsync();
        await Task.WhenAll(_retired);
    }
}
