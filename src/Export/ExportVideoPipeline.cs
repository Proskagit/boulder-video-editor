using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Export;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Export;

/// <summary>
/// How many frames the export works on at once (D059 E2). Null values are chosen per export by <see cref="Resolve"/>;
/// a value set here (tests, the benchmark, the <c>AIVE_EXPORT_RASTERIZERS</c> variable) is taken as it is.
/// </summary>
public sealed record ExportPipelineSettings
{
    /// <summary>The variable that overrides <see cref="Rasterizers"/> (the benchmark; diagnostics).</summary>
    public const string RasterizersVariable = "AIVE_EXPORT_RASTERIZERS";

    /// <summary>The upper bound of the rasterizers of one export.</summary>
    public const int MaxRasterizers = 8;

    /// <summary>Rasterizers working in parallel (each its own thread, its own Avalonia render target and caches).</summary>
    public int? Rasterizers { get; init; }

    /// <summary>Decoded output frames waiting for a rasterizer (the bounded queue between the decoders and the pool).</summary>
    public int? QueuedFrames { get; init; }

    /// <summary>
    /// The numbers for one export: the rasterizers from the logical processors — a third of them beside libx264 (it encodes
    /// on the same processors), half beside a hardware encoder — at most <see cref="MaxRasterizers"/>, and never more than
    /// a quarter of the memory available to the process allows (each rasterizer holds its canvas-size render target, its
    /// treatment surface and a bitmap per layer; every frame in flight holds a canvas and its decoded layers). At least one.
    /// The queue holds as many frames as there are rasterizers.
    /// </summary>
    public (int Rasterizers, int QueuedFrames) Resolve(ExportOutput output, int videoLayers, bool hardwareEncoder,
        int? processorCount = null, long? availableMemoryBytes = null)
    {
        var rasterizers = Rasterizers ?? Override() ?? Auto(output, videoLayers, hardwareEncoder,
            processorCount ?? Environment.ProcessorCount, availableMemoryBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        rasterizers = Math.Clamp(rasterizers, 1, MaxRasterizers);
        return (rasterizers, Math.Max(1, QueuedFrames ?? rasterizers));
    }

    internal static int Auto(ExportOutput output, int videoLayers, bool hardwareEncoder, int processors, long availableMemory)
    {
        var byProcessors = Math.Max(1, hardwareEncoder ? processors / 2 : processors / 3);
        var canvas = 4L * output.Size.Width * output.Size.Height;
        // A frame in flight: its canvas, its decoded layers (assumed canvas-size; 4K sources are), the queue's copy of them;
        // a rasterizer: its render target, a treatment surface, a bitmap per layer.
        var layers = Math.Max(1, videoLayers);
        var perRasterizer = canvas * (2 + 1 + 2L * layers);
        var byMemory = (int)Math.Clamp(availableMemory / 4 / Math.Max(1, perRasterizer), 1, int.MaxValue);
        return Math.Max(1, Math.Min(byProcessors, byMemory));
    }

    private static int? Override() =>
        int.TryParse(Environment.GetEnvironmentVariable(RasterizersVariable), out var n) && n > 0 ? n : null;
}

/// <summary>
/// The export's video stage as a bounded pipeline (D059 E2):
/// <code>
/// ExportFrameSource (decoders, D009 frame selection; one producer, frames in ascending order)
///   → bounded queue of decoded frames
///   → K rasterizers, each on its own thread with its own ICompositionRasterizer (the Preview's painter)
///   → results in frame order → the encoder (one writer, strictly in order)
/// </code>
/// What is drawn for frame n and in which order the canvases reach the encoder are unchanged — every frame is the same
/// plan rendered by the same painter; only how many frames are worked on at once changed. Memory is bounded: at most
/// <c>queue + K + 1</c> decoded frames and <c>K + 2</c> canvases exist (a rasterizer takes a canvas before it takes a
/// frame, so the frame the writer needs next always has one). Any failure — a decoder, a rasterizer, the encoder — or a
/// cancellation stops every stage; the stages are all ended (the producer awaited, the threads joined) before the method
/// returns, so the caller can dispose the frame source, the rasterizers and the encoding. The first failure propagates
/// unchanged (cancellation by the caller wins).
/// <para>
/// Thread safety (checked in Phase 40; see D059 §2): a rasterizer is used by one thread only (its render target, its
/// bitmaps, its <c>TextGlyphCache</c> and <c>TreatmentSurface</c> are its own). Shared between threads, read-only: the
/// snapshot, the decoded frames (several output frames may show one source frame), the draw plans' immutable data.
/// <c>BackgroundBlur</c>'s cache is a <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>
/// (thread safe; two threads may compute the same deterministic copy).
/// </para>
/// </summary>
internal sealed class ExportVideoPipeline
{
    private readonly ExportFrameSource _frames;
    private readonly IReadOnlyList<ICompositionRasterizer> _rasterizers;
    private readonly IExportEncoding _encoding;
    private readonly ExportOutput _output;
    private readonly int _queued;
    private readonly ILogger _logger;
    private Exception? _failure;

    public ExportVideoPipeline(ExportFrameSource frames, IReadOnlyList<ICompositionRasterizer> rasterizers, IExportEncoding encoding,
        ExportOutput output, int queuedFrames, ILogger logger)
    {
        _frames = frames;
        _rasterizers = rasterizers;
        _encoding = encoding;
        _output = output;
        _queued = queuedFrames;
        _logger = logger;
    }

    public async Task RunAsync(IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var total = _output.FrameCount;
        var stride = _output.Size.Width * 4;
        var canvasBytes = (long)stride * _output.Size.Height;
        progress?.Report(new ExportProgress(ExportStage.Video, 0, total));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        var queue = Channel.CreateBounded<ExportFrame>(new BoundedChannelOptions(_queued)
        {
            SingleWriter = true, SingleReader = false, FullMode = BoundedChannelFullMode.Wait
        });
        var canvases = new CanvasPool(_rasterizers.Count + 2, canvasBytes);
        var results = new OrderedResults();

        var producer = Task.Run(async () =>
        {
            try
            {
                for (long n = 0; n < total; n++)
                    await queue.Writer.WriteAsync(await _frames.GetFrameAsync(n, token), token);
                queue.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                Fail(ex, stop);
                queue.Writer.TryComplete();   // the stop ends the rasterizers; the failure is recorded
            }
        }, CancellationToken.None);

        var workers = _rasterizers.Select((rasterizer, i) =>
        {
            var thread = new Thread(() => Work(rasterizer, queue.Reader, canvases, results, stride, stop))
            {
                IsBackground = true, Name = $"export-rasterizer-{i + 1}"
            };
            thread.Start();
            return thread;
        }).ToList();

        try
        {
            for (long n = 0; n < total; n++)
            {
                var canvas = await results.TakeAsync(n, token);
                await _encoding.WriteFrameAsync(canvas.AsMemory(0, (int)canvasBytes), stride, ct);
                canvases.Return(canvas);
                progress?.Report(new ExportProgress(ExportStage.Video, n + 1, total));
            }
        }
        catch (Exception ex)
        {
            Fail(ex, stop);
        }
        finally
        {
            // Every stage ends before anything is disposed by the caller: the producer (it may be reading a decoder) is
            // awaited, the rasterizer threads joined.
            stop.Cancel();
            queue.Writer.TryComplete();
            try { await producer; } catch (Exception) { /* recorded by the producer itself */ }
            // Never on a rasterizer thread (Fail hands the stop to the pool), but never join oneself either.
            foreach (var thread in workers)
                if (thread != Thread.CurrentThread) thread.Join();
        }

        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
        if (_failure is { } failure) ExceptionDispatchInfo.Throw(failure);
    }

    private void Work(ICompositionRasterizer rasterizer, ChannelReader<ExportFrame> queue, CanvasPool canvases, OrderedResults results,
        int stride, CancellationTokenSource stop)
    {
        var token = stop.Token;
        try
        {
            while (true)
            {
                var canvas = canvases.Rent(token);   // before the frame: the frame taken always has a canvas
                ExportFrame? frame = null;
                while (!queue.TryRead(out frame))
                {
                    if (!queue.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult()) break;
                }
                if (frame is null)
                {
                    canvases.Return(canvas);
                    return;   // the queue is complete: no frame left
                }
                rasterizer.Render(frame.DrawPlan(), canvas.AsSpan(0, stride * _output.Size.Height), stride, token);
                results.Put(frame.Index, canvas);
            }
        }
        catch (Exception ex)
        {
            Fail(ex, stop);
        }
    }

    /// <summary>Records the first failure and stops every stage. A cancellation caused by that stop is not a failure.
    /// The stop runs on the thread pool, never on the failing stage's own thread: its callbacks continue the writer, whose
    /// end joins the rasterizer threads — run inline on a rasterizer thread that would wait for itself (found by the
    /// benchmark, D059 §2).</summary>
    private void Fail(Exception ex, CancellationTokenSource stop)
    {
        if (ex is OperationCanceledException && stop.IsCancellationRequested) return;
        if (Interlocked.CompareExchange(ref _failure, ex, null) is null && ex is not OperationCanceledException)
            _logger.LogWarning(ex, "The export pipeline stops: {Message}", ex.Message);
        ThreadPool.UnsafeQueueUserWorkItem(static s =>
        {
            try { ((CancellationTokenSource)s!).Cancel(); } catch (ObjectDisposedException) { }
        }, stop);
    }

    /// <summary>At most <c>capacity</c> canvases, created on first need and reused.</summary>
    private sealed class CanvasPool(int capacity, long bytes)
    {
        private readonly SemaphoreSlim _free = new(capacity, capacity);
        private readonly ConcurrentBag<byte[]> _canvases = new();

        public byte[] Rent(CancellationToken ct)
        {
            _free.Wait(ct);
            return _canvases.TryTake(out var canvas) ? canvas : new byte[bytes];
        }

        public void Return(byte[] canvas)
        {
            _canvases.Add(canvas);
            _free.Release();
        }
    }

    /// <summary>The rasterized canvases by frame index; the writer takes them in order. A canvas that is ready is handed
    /// over even after a stop (the frames before a failure are written, as by the sequential export).</summary>
    private sealed class OrderedResults
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, byte[]> _ready = new();
        private TaskCompletionSource<byte[]>? _waiter;
        private long _waitingFor = -1;

        public void Put(long index, byte[] canvas)
        {
            TaskCompletionSource<byte[]>? waiter = null;
            lock (_gate)
            {
                if (_waiter is not null && _waitingFor == index) (waiter, _waiter) = (_waiter, null);
                else _ready[index] = canvas;
            }
            waiter?.TrySetResult(canvas);
        }

        public async ValueTask<byte[]> TakeAsync(long index, CancellationToken ct)
        {
            TaskCompletionSource<byte[]> waiter;
            lock (_gate)
            {
                if (_ready.Remove(index, out var canvas)) return canvas;
                waiter = _waiter = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waitingFor = index;
            }
            return await waiter.Task.WaitAsync(ct);
        }
    }
}
