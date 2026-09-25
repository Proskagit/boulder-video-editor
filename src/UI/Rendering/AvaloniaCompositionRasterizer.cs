using AiVideoEditor.Core.Composition;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// The export's rasterizer (D023, Phase 8 Step 3): paints a canvas-size <see cref="CompositionDrawPlan"/>
/// with <see cref="CompositionPainter"/> — the Preview's own drawing routine — into an offscreen
/// <see cref="RenderTargetBitmap"/> and copies the BGRA pixels out. Measured with Avalonia 11.1.3: it runs
/// on any thread once the Avalonia platform is initialised (the app's or a test's), produces the same bytes
/// as on the UI thread, and does not leak across thousands of frames. One instance per export; not
/// thread-safe; keeps its render target and one bitmap per visible clip between frames.
/// </summary>
public sealed class AvaloniaCompositionRasterizer : ICompositionRasterizer
{
    private readonly Dictionary<Guid, WriteableBitmap> _bitmaps = new();
    private RenderTargetBitmap? _target;
    private bool _disposed;

    public void Render(CompositionDrawPlan plan, Span<byte> target, int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var canvas = plan.Canvas;
        if (!canvas.IsValid) throw new ArgumentException("The plan has no canvas.", nameof(plan));
        if (plan.CanvasToTarget != Affine2D.Identity)
            throw new ArgumentException("The export draws at the canvas size (identity canvas → target).", nameof(plan));
        if (stride < canvas.Width * 4 || target.Length < (long)stride * canvas.Height)
            throw new ArgumentException("The target is smaller than the canvas.", nameof(target));

        if (_target is null || _target.PixelSize.Width != canvas.Width || _target.PixelSize.Height != canvas.Height)
        {
            _target?.Dispose();
            _target = new RenderTargetBitmap(new PixelSize(canvas.Width, canvas.Height), new Vector(96, 96));
            if (_target.Format != PixelFormat.Bgra8888)
                throw new NotSupportedException($"The render target is {_target.Format}, not BGRA 8888.");
        }

        // One bitmap per clip of this frame, reused while the clip stays visible.
        var visible = new HashSet<Guid>();
        foreach (var frame in plan.Operations.OfType<FrameDraw>())
        {
            visible.Add(frame.ClipId);
            var bitmap = _bitmaps[frame.ClipId] = FrameBitmap.Ensure(_bitmaps.GetValueOrDefault(frame.ClipId), frame.Frame);
            FrameBitmap.Copy(frame.Frame, bitmap);
        }
        foreach (var gone in _bitmaps.Keys.Where(id => !visible.Contains(id)).ToList())
        {
            _bitmaps[gone].Dispose();
            _bitmaps.Remove(gone);
        }

        using (var context = _target.CreateDrawingContext())
            CompositionPainter.Paint(context, plan, frame => _bitmaps[frame.ClipId]);

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
    }
}
