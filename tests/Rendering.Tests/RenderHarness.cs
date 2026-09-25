using System.Collections.Immutable;
using System.Runtime.InteropServices;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Rendering;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>
/// Initialises Avalonia once per test run the way the app does (<c>UsePlatformDetect().WithInterFont()</c>)
/// on a dedicated UI thread that runs the dispatcher. The Preview's control is rendered on that thread; the
/// export's rasterizer runs on thread-pool threads, as in an export.
/// </summary>
public sealed class AvaloniaFixture : IDisposable
{
    private static readonly Lazy<AvaloniaFixture> Instance = new(() => new AvaloniaFixture(0));
    private readonly CancellationTokenSource _stop = new();

    public AvaloniaFixture() => _ = Instance.Value;

    private AvaloniaFixture(int _)
    {
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { AppBuilder.Configure<Application>().UsePlatformDetect().WithInterFont().SetupWithoutStarting(); }
            catch (Exception ex) { error = ex; }
            ready.Set();
            if (error is null) Dispatcher.UIThread.MainLoop(_stop.Token);
        }) { IsBackground = true, Name = "Avalonia UI (tests)" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (error is not null) throw new InvalidOperationException("Avalonia could not be initialised.", error);
    }

    public void Dispose() { }
}

[CollectionDefinition(Name)]
public sealed class AvaloniaCollection : ICollectionFixture<AvaloniaFixture>
{
    public const string Name = "Avalonia";
}

/// <summary>A rendered BGRA image (straight alpha) with pixel helpers.</summary>
public sealed record Image(int Width, int Height, byte[] Pixels)
{
    public (byte R, byte G, byte B, byte A) At(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (Pixels[i + 2], Pixels[i + 1], Pixels[i], Pixels[i + 3]);
    }

    /// <summary>Bounding box of the pixels whose green channel exceeds <paramref name="threshold"/>
    /// (white text on black), or null.</summary>
    public (int Left, int Top, int Right, int Bottom)? InkBox(int threshold = 128, int top = 0, int bottom = int.MaxValue)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (var y = Math.Max(0, top); y < Math.Min(Height, bottom); y++)
        for (var x = 0; x < Width; x++)
        {
            if (Pixels[(y * Width + x) * 4 + 1] <= threshold) continue;
            l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y);
        }
        return r < 0 ? null : (l, t, r + 1, b + 1);
    }
}

public static class Render
{
    /// <summary>The export path: the rasterizer on a thread-pool thread.</summary>
    public static Image Export(CompositionDrawPlan plan, AvaloniaCompositionRasterizer? rasterizer = null)
    {
        return Task.Run(() =>
        {
            var owned = rasterizer is null;
            var r = rasterizer ?? new AvaloniaCompositionRasterizer();
            try
            {
                var pixels = new byte[plan.Canvas.Width * plan.Canvas.Height * 4];
                r.Render(plan, pixels, plan.Canvas.Width * 4);
                return new Image(plan.Canvas.Width, plan.Canvas.Height, pixels);
            }
            finally
            {
                if (owned) r.Dispose();
            }
        }).GetAwaiter().GetResult();
    }

    /// <summary>The Preview path: <see cref="CompositionView"/> (the Preview's control) laid out at
    /// <paramref name="width"/> × <paramref name="height"/> and rendered on the UI thread.</summary>
    public static Image Preview(FrameSize canvas, int width, int height, IEnumerable<LayerPicture> layers)
    {
        var list = layers.ToImmutableArray();
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new CompositionView { Canvas = canvas, Layers = list };
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            target.Render(view);
            var pixels = new byte[width * height * 4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { target.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4); }
            finally { handle.Free(); }
            return new Image(width, height, pixels);
        }).GetTask().GetAwaiter().GetResult();
    }
}

/// <summary>Generated sources and layers.</summary>
public static class Scene
{
    public static readonly FrameRate Rate = FrameRate.Fps25;
    public static readonly MediaTime T = MediaTime.Zero;

    /// <summary>A BGRA frame (straight alpha) filled by <paramref name="color"/>(x, y).</summary>
    public static DecodedFrame Frame(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> color)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (r, g, b, a) = color(x, y);
            var i = (y * width + x) * 4;
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, a);
        }
        return new DecodedFrame(width, height, width * 4, pixels, new SourceTimestamp(0, new TimeBase(1, 25)));
    }

    public static DecodedFrame Solid(int width, int height, byte r, byte g, byte b, byte a = 255) => Frame(width, height, (_, _) => (r, g, b, a));

    public static PictureLayer Picture(FrameSize canvas, FrameSize source, VisualProperties visual, SpanStatus status = SpanStatus.Video)
    {
        var span = new PictureSpan(Guid.NewGuid(), Guid.NewGuid(), status, MediaTime.Zero, MediaTime.FromSeconds(1), MediaTime.Zero)
        {
            Visual = visual, SourceSize = source
        };
        return new PictureLayer(Guid.NewGuid(), span, CompositionMath.Layout(canvas, source, visual));
    }

    public static TextLayer Text(FrameSize canvas, TextProperties text, VisualProperties? visual = null)
    {
        var span = new TextSpan(Guid.NewGuid(), MediaTime.Zero, MediaTime.FromSeconds(1), visual ?? VisualProperties.Default, text);
        return new TextLayer(Guid.NewGuid(), span, CompositionMath.TextTransform(canvas, span.Visual));
    }

    public static CompositionDrawPlan Plan(FrameSize canvas, params ResolvedLayer[] layers) =>
        CompositionDrawPlan.Build(canvas, Affine2D.Identity, layers);

    public static LayerPicture AsPreview(ResolvedLayer layer) =>
        layer.Layer is TextLayer ? new LayerPicture(layer.Layer, LayerPictureState.Text) : new LayerPicture(layer.Layer, LayerPictureState.Frame, layer.Frame);
}
