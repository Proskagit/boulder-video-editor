using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AiVideoEditor.UI.Rendering;

/// <summary>
/// The offscreen buffers of a template's treatment (D045 §3): the render target what lies below the template is drawn into,
/// its pixels read back and treated, and two bitmaps the treated pixels go into (two, so a second treatment on the same
/// frame can draw the first one's result while writing its own). Reused from frame to frame while the size stays; one per
/// user (the Preview's view, each export), used from one thread at a time.
/// </summary>
public sealed class TreatmentSurface : IDisposable
{
    private RenderTargetBitmap? _target;
    private readonly WriteableBitmap?[] _results = new WriteableBitmap?[2];
    private int _next;
    private bool _disposed;

    public PixelSize Size { get; private set; }

    /// <summary>The pixel buffer of the last read-back (BGRA, <c>Size.Width × 4</c> per row).</summary>
    public byte[] Pixels { get; private set; } = Array.Empty<byte>();

    /// <summary>The render target, (re)created at <paramref name="size"/>.</summary>
    public RenderTargetBitmap Target(PixelSize size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_target is null || Size != size)
        {
            _target?.Dispose();
            foreach (var r in _results) r?.Dispose();
            Array.Clear(_results);
            _target = new RenderTargetBitmap(size, new Vector(96, 96));
            if (_target.Format != PixelFormat.Bgra8888)
                throw new NotSupportedException($"The render target is {_target.Format}, not BGRA 8888.");
            Size = size;
            Pixels = new byte[size.Width * size.Height * 4];
        }
        return _target;
    }

    /// <summary>Copies the render target's pixels into <see cref="Pixels"/>.</summary>
    public unsafe void ReadBack()
    {
        fixed (byte* p = Pixels)
            _target!.CopyPixels(new PixelRect(0, 0, Size.Width, Size.Height), (nint)p, Pixels.Length, Size.Width * 4);
    }

    /// <summary>The next result bitmap, filled with <see cref="Pixels"/>.</summary>
    public WriteableBitmap Upload()
    {
        var bitmap = _results[_next] ??= new WriteableBitmap(Size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _next = 1 - _next;
        using var locked = bitmap.Lock();
        var row = Size.Width * 4;
        for (var y = 0; y < Size.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(Pixels, y * row, locked.Address + y * locked.RowBytes, row);
        return bitmap;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _target?.Dispose();
        foreach (var r in _results) r?.Dispose();
    }
}
