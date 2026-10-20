using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// The glyph geometry of outlined texts (D043 "Refined in Step 27.1", variant B; conditions 2–4). Avalonia creates a
/// glyph <see cref="Geometry"/> only on the UI thread (it is an <c>AvaloniaObject</c>), so every geometry is built there and
/// drawn there once — Avalonia then creates and keeps its platform geometry — and only read afterwards, also on another
/// thread (the export's). One cache per drawing consumer: the Preview's control has one (UI thread), each export's
/// rasterizer its own (its thread; sequential use), so one geometry object is never drawn by two threads at once — the
/// guarantee is not taken from Avalonia, which documents none.
/// <list type="bullet">
/// <item>Key: everything that shapes the glyphs and their place in the box — the text, the family, the size, Bold, the
/// alignment (the box follows from them). Colours, the thickness, opacities and the transform are not in it: they go to
/// the pen, the brushes and the pushed state.</item>
/// <item>Bounded: at most <see cref="Capacity"/> entries, the least recently used dropped first.</item>
/// <item>Thread safe: the entries are guarded by a lock; a build runs outside it.</item>
/// <item>Off the UI thread a missing geometry is requested from the UI thread and waited for with the caller's cancellation;
/// the wait also ends when the dispatcher shuts down (the application closes) or after <see cref="UiTimeout"/> — an
/// <see cref="ExportException"/> (<see cref="ExportFailure.RenderFailed"/>) then, a cancellation an
/// <see cref="OperationCanceledException"/>. It never waits without an end.</item>
/// </list>
/// </summary>
public sealed class TextGlyphCache
{
    /// <summary>The entries kept (distinct outlined texts of a project are few).</summary>
    public const int DefaultCapacity = 64;

    /// <summary>How long a request to the UI thread may take before the export fails.</summary>
    public static readonly TimeSpan DefaultUiTimeout = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<TextGlyphKey, LinkedListNode<(TextGlyphKey Key, Geometry? Glyphs)>> _entries = new();
    private readonly LinkedList<(TextGlyphKey Key, Geometry? Glyphs)> _order = new();   // most recent first
    private readonly IUiThread _ui;

    public TextGlyphCache(int capacity = DefaultCapacity, TimeSpan? uiTimeout = null, IUiThread? ui = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        UiTimeout = uiTimeout ?? DefaultUiTimeout;
        _ui = ui ?? AvaloniaUiThread.Instance;
    }

    public int Capacity { get; }
    public TimeSpan UiTimeout { get; }

    /// <summary>Entries held (diagnostics, tests).</summary>
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Builds made (diagnostics, tests): a hit makes none.</summary>
    public int Builds { get; private set; }

    /// <summary>The glyph geometry of <paramref name="text"/> in the painter's local units (the box centred on the origin),
    /// or null when the text has no glyphs.</summary>
    public Geometry? Get(TextProperties text, CancellationToken ct = default)
    {
        var key = TextGlyphKey.Of(text);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var hit))
            {
                _order.Remove(hit);
                _order.AddFirst(hit);
                return hit.Value.Glyphs;
            }
        }

        var built = _ui.CheckAccess() ? Build(text) : RequestFromUiThread(text, ct);
        lock (_gate)
        {
            Builds++;
            if (_entries.TryGetValue(key, out var raced)) return raced.Value.Glyphs;   // another caller built it meanwhile
            _entries[key] = _order.AddFirst((key, built));
            while (_entries.Count > Capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _entries.Remove(last.Value.Key);
            }
        }
        return built;
    }

    private Geometry? RequestFromUiThread(TextProperties text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = _ui.InvokeAsync(() => Build(text), ct);
        var shutdown = _ui.Shutdown;
        // Throws OperationCanceledException when ct is cancelled while waiting.
        var done = Task.WaitAny(new[] { request, shutdown }, (int)Math.Min(int.MaxValue, UiTimeout.TotalMilliseconds), ct);
        if (done == 0)
        {
            if (request.IsCanceled)
            {
                ct.ThrowIfCancellationRequested();
                throw new ExportException(ExportFailure.RenderFailed, "The interface ended before the text outline was prepared.");
            }
            return request.GetAwaiter().GetResult();   // a build's own failure propagates
        }
        if (done == 1)
            throw new ExportException(ExportFailure.RenderFailed, "The application is closing; the text outline could not be prepared.");
        throw new ExportException(ExportFailure.RenderFailed,
            $"The interface did not prepare the text outline within {UiTimeout.TotalSeconds:0} s.");
    }

    /// <summary>On the UI thread: the geometry of the painter's layout at the box's origin, drawn once so that Avalonia
    /// creates its platform geometry here (afterwards it is only read).</summary>
    internal static Geometry? Build(TextProperties text)
    {
        var layout = CompositionPainter.Layout(text);
        var geometry = layout.BuildGeometry(CompositionPainter.BoxOrigin(layout));
        if (geometry is null) return null;
        using var warm = new RenderTargetBitmap(new PixelSize(1, 1), new Vector(96, 96));
        using (var context = warm.CreateDrawingContext())
            context.DrawGeometry(Brushes.White, null, geometry);
        return geometry;
    }
}

/// <summary>What shapes a text's glyphs and their place in its box (D043 condition 2).</summary>
public readonly record struct TextGlyphKey(string Text, string FontFamily, double FontSize, bool Bold, Core.Entities.TextAlignment Alignment)
{
    public static TextGlyphKey Of(TextProperties t) => new(t.Text, t.FontFamily, t.FontSize, t.Bold, t.Alignment);
}

/// <summary>The UI thread as <see cref="TextGlyphCache"/> needs it (a seam for the shutdown and timeout tests).</summary>
public interface IUiThread
{
    bool CheckAccess();

    /// <summary>Runs <paramref name="build"/> on the UI thread; cancelled with <paramref name="ct"/> while it waits.</summary>
    Task<Geometry?> InvokeAsync(Func<Geometry?> build, CancellationToken ct);

    /// <summary>Completes when the UI thread's dispatcher starts to shut down.</summary>
    Task Shutdown { get; }
}

/// <summary>Avalonia's UI thread: <see cref="Dispatcher.UIThread"/> and its <see cref="Dispatcher.ShutdownStarted"/>.</summary>
public sealed class AvaloniaUiThread : IUiThread
{
    public static readonly AvaloniaUiThread Instance = new();

    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _subscribed;

    private AvaloniaUiThread() { }

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public Task<Geometry?> InvokeAsync(Func<Geometry?> build, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(build, DispatcherPriority.Send, ct).GetTask();

    public Task Shutdown
    {
        get
        {
            if (Interlocked.Exchange(ref _subscribed, 1) == 0)
                Dispatcher.UIThread.ShutdownStarted += (_, _) => _shutdown.TrySetResult();
            return _shutdown.Task;
        }
    }
}
