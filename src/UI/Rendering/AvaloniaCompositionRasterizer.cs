using AiVideoEditor.Core.Composition;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// The export's rasterizer (D023, Phase 8 Step 3): paints a canvas-size <see cref="CompositionDrawPlan"/> (or, D060 §3, one
/// stretched onto the chosen output size, <see cref="CompositionDrawPlan.TargetSize"/>)
/// with <see cref="CompositionPainter"/> — the Preview's own drawing routine — into an offscreen
/// <see cref="RenderTargetBitmap"/> and copies the BGRA pixels out. Measured with Avalonia 11.1.3: it runs
/// on any thread once the Avalonia platform is initialised (the app's or a test's), produces the same bytes
/// as on the UI thread, and does not leak across thousands of frames. One instance per export; not
/// thread-safe; keeps its render target and one bitmap per visible clip between frames.
/// </summary>
public sealed class AvaloniaCompositionRasterizer : ICompositionRasterizer
{
    /// <summary>Per clip: its frame and, apart, its blurred background (the same clip id).</summary>
    private readonly Dictionary<(Guid Clip, bool Background), WriteableBitmap> _bitmaps = new();

    /// <summary>This export's own glyphs of outlined texts (D043 variant B): built on the UI thread, drawn only here.</summary>
    private readonly TextGlyphCache _glyphs = new();

    /// <summary>The offscreen buffers of a template's treatment (D045 §3) at the canvas size.</summary>
    private readonly TreatmentSurface _treatment = new();
    private RenderTargetBitmap? _target;
    private bool _disposed;

    private static readonly object WarmGate = new();
    private static bool _warm;

    /// <summary>D059 §2: several rasterizers draw at once on their own threads. Avalonia's static initialisation is not safe
    /// against that: measured on 11.1.3, two threads drawing their first text at the same moment made
    /// <c>Avalonia.Media.GlyphRun</c>'s type initializer throw — and a type initializer that threw stays broken for the whole
    /// process. So the first rasterizer, before any parallel use, draws everything the painter uses once (text, a pen, an
    /// opacity mask, a bitmap) on its creator's thread, under a lock.</summary>
    public AvaloniaCompositionRasterizer()
    {
        lock (WarmGate)
        {
            if (_warm) return;
            Warm();
            _warm = true;
        }
    }

    private static void Warm()
    {
        using var target = new RenderTargetBitmap(new PixelSize(8, 8), new Vector(96, 96));
        using var bitmap = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var context = target.CreateDrawingContext())
        {
            var brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Colors.White);
            var text = new Avalonia.Media.FormattedText("Ag", System.Globalization.CultureInfo.InvariantCulture,
                Avalonia.Media.FlowDirection.LeftToRight, Avalonia.Media.Typeface.Default, 4, brush);
            var bold = new Avalonia.Media.FormattedText("Ag", System.Globalization.CultureInfo.InvariantCulture,
                Avalonia.Media.FlowDirection.LeftToRight, new Avalonia.Media.Typeface(Avalonia.Media.FontFamily.Default,
                    Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.Bold), 4, brush);
            using (context.PushOpacityMask(brush, new Rect(0, 0, 8, 8)))
            {
                context.DrawText(text, new Point(0, 0));
                context.DrawText(bold, new Point(0, 4));
            }
            context.DrawLine(new Avalonia.Media.Immutable.ImmutablePen(brush, 1), new Point(0, 0), new Point(8, 8));
            context.DrawImage(bitmap, new Rect(0, 0, 2, 2), new Rect(0, 0, 4, 4));
        }
    }

    public void Render(CompositionDrawPlan plan, Span<byte> target, int stride) => Render(plan, target, stride, CancellationToken.None);

    public void Render(CompositionDrawPlan plan, Span<byte> target, int stride, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!plan.Canvas.IsValid) throw new ArgumentException("The plan has no canvas.", nameof(plan));
        // The export draws at the canvas size (identity), or (D060 §3) at a chosen resolution: the canvas stretched onto
        // TargetSize, exactly filling it — never another mapping (the Preview's viewport has its own control).
        var canvas = plan.TargetSize ?? plan.Canvas;
        if (!canvas.IsValid) throw new ArgumentException("The plan's target has no size.", nameof(plan));
        if (plan.CanvasToTarget != CompositionDrawPlan.Stretch(plan.Canvas, canvas))
            throw new ArgumentException("The export draws the canvas exactly onto its target (identity, or the stretch onto TargetSize).", nameof(plan));
        if (stride < canvas.Width * 4 || target.Length < (long)stride * canvas.Height)
            throw new ArgumentException("The target is smaller than the output.", nameof(target));

        if (_target is null || _target.PixelSize.Width != canvas.Width || _target.PixelSize.Height != canvas.Height)
        {
            _target?.Dispose();
            _target = new RenderTargetBitmap(new PixelSize(canvas.Width, canvas.Height), new Vector(96, 96));
            if (_target.Format != PixelFormat.Bgra8888)
                throw new NotSupportedException($"The render target is {_target.Format}, not BGRA 8888.");
        }

        // One bitmap per clip of this frame, reused while the clip stays visible.
        var visible = new HashSet<(Guid, bool)>();
        foreach (var frame in plan.Operations.OfType<FrameDraw>())
        {
            var key = (frame.ClipId, frame.IsBackground);
            visible.Add(key);
            var bitmap = _bitmaps[key] = FrameBitmap.Ensure(_bitmaps.GetValueOrDefault(key), frame.Frame);
            FrameBitmap.Copy(frame.Frame, bitmap, frame.Color, frame.Effect);   // D069 §7, §8: the colour, the effect — as the Preview
        }
        foreach (var gone in _bitmaps.Keys.Where(id => !visible.Contains(id)).ToList())
        {
            _bitmaps[gone].Dispose();
            _bitmaps.Remove(gone);
        }

        using (var context = _target.CreateDrawingContext())
            CompositionPainter.Paint(context, plan, frame => _bitmaps[(frame.ClipId, frame.IsBackground)], _glyphs, ct, _treatment);

        unsafe
        {
            fixed (byte* pixels = target)
                _target.CopyPixels(new PixelRect(0, 0, canvas.Width, canvas.Height), (nint)pixels, stride * canvas.Height, stride);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var bitmap in _bitmaps.Values) bitmap.Dispose();
        _bitmaps.Clear();
        _target?.Dispose();
        _target = null;
        _treatment.Dispose();
    }
}
