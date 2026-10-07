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
    private readonly long _firstFrame;
    private long _lastIndex = -1;
    private bool _disposed;

    /// <param name="output">The job's output (<see cref="ExportJob.Output"/>): output frame n is timeline frame
    /// <c>output.FirstFrame + n</c> (a range export, D030 §8); null = the whole sequence.</param>
    public ExportFrameSource(PlaybackSnapshot snapshot, IVideoDecoder decoder, ExportDecodeSettings? settings = null, ExportOutput? output = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _settings = settings ?? new ExportDecodeSettings();
        output ??= ExportOutput.For(snapshot);
        FrameCount = output.FrameCount;
        _firstFrame = output.FirstFrame;
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

        var timelineFrame = _firstFrame + index;
        var time = MediaTime.FromFrame(timelineFrame, _snapshot.FrameRate);
        var layers = _snapshot.LayersAt(time);

        var needed = layers.OfType<PictureLayer>().Select(l => l.Span.ClipId).ToHashSet();
        foreach (var (clipId, reader) in _readers.ToList())
        {
            if (needed.Contains(clipId)) continue;
            _readers.Remove(clipId);
            await reader.DisposeAsync();
        }

        // The layers' readers are independent: their frames are fetched at the same time (D024 Step 9.7, A), so the
        // decoders of a multi-layer composition work in parallel. Each reader still gets its requests in ascending
        // order and selects exactly as before; the layers keep their order.
        // Every reader exists (or its clip has failed) before any fetch starts, so a failure here leaves nothing running.
        ct.ThrowIfCancellationRequested();
        var readers = layers.Select(l => l is PictureLayer picture ? Reader(picture.Span) : null).ToArray();
        var frames = new Task<DecodedFrame>?[layers.Length];
        for (var i = 0; i < layers.Length; i++) // bottom to top
            frames[i] = readers[i]?.GetAsync(timelineFrame, ct).AsTask();
        // Every fetch ends before anything is reported, also when one fails: no reader is still reading when the
        // caller disposes the source. The first failure in layer order propagates.
        await Task.WhenAll(frames.OfType<Task<DecodedFrame>>());

        var pictures = ImmutableArray.CreateBuilder<ResolvedLayer>(layers.Length);
        for (var i = 0; i < layers.Length; i++)
            pictures.Add(new ResolvedLayer(layers[i], frames[i]?.Result));
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
