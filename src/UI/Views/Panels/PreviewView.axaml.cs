using System.ComponentModel;
using System.Runtime.InteropServices;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AiVideoEditor.UI.Views.Panels;

/// <summary>
/// Drives <see cref="PreviewViewModel.Tick"/> from a UI-thread timer while the view is
/// attached, and copies each new <see cref="DecodedFrame"/> (BGRA) into one of two
/// <see cref="WriteableBitmap"/>s, alternating so the bitmap being written is never the one
/// on screen. Everything here runs on the UI thread; decoding happens in the playback service.
/// </summary>
public partial class PreviewView : UserControl
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(10);

    private readonly DispatcherTimer _timer;
    private readonly WriteableBitmap?[] _bitmaps = new WriteableBitmap?[2];
    private int _nextBitmap;
    private PreviewViewModel? _viewModel;

    public PreviewView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Render, (_, _) => _viewModel?.Tick());
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as PreviewViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ShowFrame(_viewModel?.CurrentFrame);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewViewModel.CurrentFrame))
            ShowFrame(_viewModel?.CurrentFrame);
    }

    private void ShowFrame(DecodedFrame? frame)
    {
        if (frame is null)
        {
            FrameImage.Source = null;
            return;
        }

        var bitmap = _bitmaps[_nextBitmap];
        if (bitmap is null || bitmap.PixelSize.Width != frame.Width || bitmap.PixelSize.Height != frame.Height)
        {
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _bitmaps[_nextBitmap] = bitmap;
        }

        using (var target = bitmap.Lock())
        {
            // DecodedFrame buffers are array-backed; copy rows straight from the array.
            var source = MemoryMarshal.TryGetArray(frame.Pixels, out var segment)
                ? segment
                : new ArraySegment<byte>(frame.Pixels.ToArray());
            var rowBytes = frame.Width * DecodedFrame.BytesPerPixel;
            for (var y = 0; y < frame.Height; y++)
                Marshal.Copy(source.Array!, source.Offset + y * frame.Stride, target.Address + y * target.RowBytes, rowBytes);
        }

        FrameImage.Source = bitmap;
        FrameImage.InvalidateVisual();
        _nextBitmap ^= 1;
    }
}
