using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiVideoEditor.UI.Views.Panels;

/// <summary>
/// View glue only: translates pointer, wheel and drag-drop events into content-space
/// coordinates and calls <see cref="TimelineViewModel"/>. No timeline logic here —
/// hit-testing decides *what* was pressed (ruler, clip body, trim handle, empty lane);
/// the view model decides what that means.
/// </summary>
public partial class TimelineView : UserControl
{
    private TimelineViewModel? _viewModel;
    private bool _scrubbing;
    private TimelineClipViewModel? _pressedClip;
    private bool _pressedWithToggle;
    private bool _gestureStarted;

    public TimelineView()
    {
        InitializeComponent();

        ContentScroll.ScrollChanged += (_, _) => SyncViewport();
        ContentScroll.SizeChanged += (_, _) => SyncViewport();

        Ruler.PointerPressed += OnRulerPressed;
        Ruler.PointerMoved += OnRulerMoved;
        Ruler.PointerReleased += (_, _) => _scrubbing = false;

        TrackRows.PointerPressed += OnTracksPressed;
        TrackRows.PointerMoved += OnTracksMoved;
        TrackRows.PointerReleased += OnTracksReleased;
        TrackRows.PointerCaptureLost += (_, _) => { if (_pressedClip is not null) CancelGesture(); };

        TrackRows.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        TrackRows.AddHandler(DragDrop.DragLeaveEvent, (_, _) => _viewModel?.ClearDropTargets());
        TrackRows.AddHandler(DragDrop.DropEvent, OnDrop);

        // Tunnel so Ctrl+wheel zooms instead of letting the ScrollViewer scroll first.
        ContentScroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.ScrollRequested -= OnScrollRequested;
        _viewModel = DataContext as TimelineViewModel;
        if (_viewModel is not null) _viewModel.ScrollRequested += OnScrollRequested;
    }

    private void OnScrollRequested(object? sender, double offsetX)
    {
        // Content width may change in the same step (zoom) — apply after layout.
        Dispatcher.UIThread.Post(
            () => ContentScroll.Offset = new Vector(offsetX, ContentScroll.Offset.Y),
            DispatcherPriority.Loaded);
    }

    private void SyncViewport()
    {
        HeaderScroll.Offset = new Vector(0, ContentScroll.Offset.Y);
        _viewModel?.SetViewport(ContentScroll.Offset.X, ContentScroll.Viewport.Width);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel?.IsGestureActive == true)
        {
            CancelGesture();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // --- Ruler / playhead -------------------------------------------------------

    private void OnRulerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Ruler).Properties.IsLeftButtonPressed) return;
        Focus();
        _scrubbing = true;
        _viewModel?.SetPlayheadFromX(e.GetPosition(ContentRoot).X);
        e.Handled = true;
    }

    private void OnRulerMoved(object? sender, PointerEventArgs e)
    {
        if (_scrubbing && e.GetCurrentPoint(Ruler).Properties.IsLeftButtonPressed)
            _viewModel?.SetPlayheadFromX(e.GetPosition(ContentRoot).X);
    }

    // --- Clips: select / move / trim ---------------------------------------------

    private void OnTracksPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || !e.GetCurrentPoint(TrackRows).Properties.IsLeftButtonPressed) return;
        Focus();

        var (clip, edge) = HitTestClip(e.Source);
        if (clip is null)
        {
            _viewModel.ClearSelection();
            return;
        }

        _pressedWithToggle = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var x = e.GetPosition(ContentRoot).X;

        if (edge is { } trimEdge && !_pressedWithToggle)
        {
            _viewModel.BeginTrim(clip, trimEdge, x);
        }
        else if (!_viewModel.OnClipPressed(clip, _pressedWithToggle))
        {
            e.Handled = true;
            return; // Ctrl-toggle: no drag
        }
        else
        {
            _viewModel.BeginMove(x);
        }

        _pressedClip = clip;
        _gestureStarted = true;
        e.Pointer.Capture(TrackRows);
        e.Handled = true;
    }

    private void OnTracksMoved(object? sender, PointerEventArgs e)
    {
        if (_viewModel is null || !_gestureStarted) return;
        _viewModel.UpdateGesture(e.GetPosition(ContentRoot).X, TrackAt(e.GetPosition(TrackRows).Y));
    }

    private void OnTracksReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel is null || !_gestureStarted) return;
        var wasDrag = _viewModel.IsGestureActive;
        var clip = _pressedClip;

        _gestureStarted = false;
        _pressedClip = null;
        _viewModel.EndGesture();
        e.Pointer.Capture(null);

        if (!wasDrag && clip is not null)
            _viewModel.OnClipClicked(clip, _pressedWithToggle);
    }

    private void CancelGesture()
    {
        _gestureStarted = false;
        _pressedClip = null;
        _viewModel?.CancelGesture();
    }

    /// <summary>Finds the clip under the pointer and whether a trim handle was hit.</summary>
    private static (TimelineClipViewModel? Clip, ClipEdge? Edge) HitTestClip(object? source)
    {
        ClipEdge? edge = null;
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control { Tag: "TrimStart" }) edge = ClipEdge.Start;
            else if (visual is Control { Tag: "TrimEnd" }) edge = ClipEdge.End;

            if (visual is StyledElement { DataContext: TimelineClipViewModel clip } && visual is Border { Classes: var classes } && classes.Contains("clip"))
                return (clip, edge);
            if (visual is ItemsControl { Name: "TrackRows" })
                break;
        }
        return (null, null);
    }

    private TimelineTrackViewModel? TrackAt(double y)
    {
        if (_viewModel is null || y < 0) return null;
        var index = (int)(y / TimelineTrackViewModel.Height);
        return index < _viewModel.Tracks.Count ? _viewModel.Tracks[index] : null;
    }

    // --- Zoom ---------------------------------------------------------------------

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var factor = e.Delta.Y > 0 ? 1.25 : 1 / 1.25;
        _viewModel.ZoomAtPointer(factor, e.GetPosition(ContentRoot).X);
        e.Handled = true;
    }

    // --- Drag & drop from the Media Browser ---------------------------------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_viewModel is null || !e.Data.Contains(DragFormats.MediaAssetId))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        _viewModel.ShowDropTarget(TrackAt(e.GetPosition(TrackRows).Y));
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.Data.Get(DragFormats.MediaAssetId) is string text && Guid.TryParse(text, out var assetId))
            _viewModel.DropMedia(assetId, TrackAt(e.GetPosition(TrackRows).Y), e.GetPosition(ContentRoot).X);
        else
            _viewModel.ClearDropTargets();
    }
}
