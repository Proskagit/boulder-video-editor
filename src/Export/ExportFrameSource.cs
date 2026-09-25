using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Export;

/// <summary>Everything to compose for output frame <see cref="Index"/>: the layers bottom to top, each with the
/// decoded source frame chosen for this output frame (none for text).</summary>
public sealed record ExportFrame(long Index, MediaTime Time, FrameSize Canvas, ImmutableArray<ResolvedLayer> Layers)
{
    /// <summary>The shared composition plan (D018/D023) at the canvas size — what the rasterizer draws.</summary>
    public CompositionDrawPlan DrawPlan() => CompositionDrawPlan.Build(Canvas, Affine2D.Identity, Layers);
}

/// <summary>
/// The pictures of an export, frame by frame (D023, Phase 8 Step 2): for output frame n the layers are
/// <see cref="PlaybackSnapshot.LayersAt"/> at <c>MediaTime.FromFrame(n)</c> — the Preview's layer set,
/// order and culling — and every picture layer gets the source frame its <see cref="ExportPictureReader"/>
/// selects. A reader exists for exactly the picture layers of the current frame (like the Preview's
/// pipeline): one that leaves the composition is closed, one that (re)appears opens at that frame.
/// Frames must be requested in ascending order within <c>[0, ExportOutput.FrameCount)</c>.
/// <para>
/// Nothing realtime here: no late frames, no pending layers, no placeholders. Offline or unsupported
/// clips can't reach this point (the preflight blocks them) and are an error if they do, as is any decode
/// failure; a failed layer never stops occluding (there is no "mayOcclude" veto) because the export stops.
/// </para>
/// </summary>
public sealed class ExportFrameSource : IAsyncDisposable
{
    private readonly PlaybackSnapshot _snapshot;
    private readonly IVideoDecoder _decoder;
    private readonly ExportDecodeSettings _settings;
    private readonly Dictionary<Guid, ExportPictureReader> _readers = new();
    private long _lastIndex = -1;
    private bool _disposed;

    public ExportFrameSource(PlaybackSnapshot snapshot, IVideoDecoder decoder, ExportDecodeSettings? settings = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _settings = settings ?? new ExportDecodeSettings();
        FrameCount = ExportOutput.For(snapshot).FrameCount;
    }

    public long FrameCount { get; }

    /// <summary>Open source readers (diagnostics/tests).</summary>
    internal IReadOnlyCollection<Guid> ReaderClipIds => _readers.Keys.ToList();

    public async Task<ExportFrame> GetFrameAsync(long index, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= FrameCount)
            throw new ArgumentOutOfRangeException(nameof(index), $"Frame {index} is outside [0, {FrameCount}).");
        if (index <= _lastIndex)
            throw new InvalidOperationException($"Frames must be requested in ascending order ({index} after {_lastIndex}).");
        _lastIndex = index;

        var time = MediaTime.FromFrame(index, _snapshot.FrameRate);
        var layers = _snapshot.LayersAt(time);

        var needed = layers.OfType<PictureLayer>().Select(l => l.Span.ClipId).ToHashSet();
        foreach (var (clipId, reader) in _readers.ToList())
        {
            if (needed.Contains(clipId)) continue;
            _readers.Remove(clipId);
            await reader.DisposeAsync();
        }

        var pictures = ImmutableArray.CreateBuilder<ResolvedLayer>(layers.Length);
        foreach (var layer in layers) // bottom to top
        {
            ct.ThrowIfCancellationRequested();
            if (layer is not PictureLayer picture)
            {
                pictures.Add(new ResolvedLayer(layer, null));
                continue;
            }
            pictures.Add(new ResolvedLayer(layer, await Reader(picture.Span).GetAsync(index, ct)));
        }
        return new ExportFrame(index, time, _snapshot.Canvas, pictures.MoveToImmutable());
    }

    private ExportPictureReader Reader(PictureSpan span)
    {
        if (_readers.TryGetValue(span.ClipId, out var reader)) return reader;
        if (span.Status is not (SpanStatus.Video or SpanStatus.StillImage) || !_snapshot.Assets.TryGetValue(span.AssetId, out var asset))
            throw new ExportException(ExportFailure.DecodeFailed,
                $"A clip at {span.TimelineStart} can't be exported ({span.Status}{(span.Reason is null ? "" : ": " + span.Reason)}).");
        reader = new ExportPictureReader(span, asset, _snapshot.FrameRate, _decoder, _settings);
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
