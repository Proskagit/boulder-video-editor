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
    private bool _marqueeStarted;

    // D037 §4: while a drag is near an edge of the view, a timer scrolls it and replays the pointer at the same view place.
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _pointerViewportX;
    private double _pointerLanesY;

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
        TrackRows.PointerCaptureLost += (_, _) =>
        {
            if (_pressedClip is not null) CancelGesture();
            if (_marqueeStarted) CancelMarquee();
        };

        TrackRows.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        TrackRows.AddHandler(DragDrop.DragLeaveEvent, (_, _) => _viewModel?.ClearDropTargets());
        TrackRows.AddHandler(DragDrop.DropEvent, OnDrop);

        _autoScroll.Tick += (_, _) => AutoScrollTick();

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
        if (e.Key == Key.Escape && _marqueeStarted)
        {
            CancelMarquee();
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

        // A dissolve zone is drawn over the clips but never hides their trim handles: only a press on a clip body (no
        // handle, no Ctrl, no Alt — Alt slips the clip) inside a zone selects the dissolve.
        if (edge is null && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !TimelineGestureModifiers.IsSlip(e.KeyModifiers) &&
            _viewModel.TransitionAt(TrackAt(e.GetPosition(TrackRows).Y), e.GetPosition(ContentRoot).X) is { } transition)
        {
            _viewModel.OnTransitionPressed(transition);
            e.Handled = true;
            return;
        }

        if (clip is null)
        {
            // D037 §1: an empty place starts a marquee; a press without a drag clears the selection on release (with Ctrl
            // it leaves it).
            _viewModel.BeginMarquee(e.GetPosition(ContentRoot).X, e.GetPosition(TrackRows).Y,
                additive: TimelineGestureModifiers.IsToggle(e.KeyModifiers));
            _marqueeStarted = true;
            e.Pointer.Capture(TrackRows);
            e.Handled = true;
            return;
        }

        _pressedWithToggle = TimelineGestureModifiers.IsToggle(e.KeyModifiers);
        var x = e.GetPosition(ContentRoot).X;

        if (edge is { } trimEdge && !_pressedWithToggle)
        {
            _viewModel.BeginTrim(clip, trimEdge, x, ripple: TimelineGestureModifiers.IsRippleTrim(e.KeyModifiers),
                alone: TimelineGestureModifiers.IsTrimAlone(e.KeyModifiers));
        }
        else if (edge is null && TimelineGestureModifiers.IsSlip(e.KeyModifiers))
        {
            if (!_viewModel.BeginSlip(clip, x))
            {
                e.Handled = true;
                return; // not a clip that slips (the status says so): no drag
            }
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
        if (_viewModel is null) return;
        if (_marqueeStarted || _gestureStarted)
        {
            _pointerViewportX = e.GetPosition(ContentScroll).X;
            _pointerLanesY = e.GetPosition(TrackRows).Y;
            UpdateAutoScroll();
        }
        if (_marqueeStarted)
        {
            _viewModel.UpdateMarquee(e.GetPosition(ContentRoot).X, e.GetPosition(TrackRows).Y);
            return;
        }
        if (!_gestureStarted) return;
        _viewModel.UpdateGesture(e.GetPosition(ContentRoot).X, TrackAt(e.GetPosition(TrackRows).Y));
    }

    private void OnTracksReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel is null) return;
        _autoScroll.Stop();
        if (_marqueeStarted)
        {
            _marqueeStarted = false;
            _viewModel.EndMarquee();
            e.Pointer.Capture(null);
            return;
        }
        if (!_gestureStarted) return;
        var wasDrag = _viewModel.IsGestureActive;
        var clip = _pressedClip;

        _gestureStarted = false;
        _pressedClip = null;
        _viewModel.EndGesture();
        e.Pointer.Capture(null);

        if (!wasDrag && clip is not null)
            _viewModel.OnClipClicked(clip, _pressedWithToggle);
    }

    private void UpdateAutoScroll()
    {
        var scrolling = (_marqueeStarted || _gestureStarted)
                        && TimelineViewModel.AutoScrollStep(_pointerViewportX, ContentScroll.Viewport.Width) != 0;
        if (scrolling) _autoScroll.Start();
        else _autoScroll.Stop();
    }

    private void AutoScrollTick()
    {
        if (_viewModel is null || !(_marqueeStarted || _gestureStarted))
        {
            _autoScroll.Stop();
            return;
        }
        var step = TimelineViewModel.AutoScrollStep(_pointerViewportX, ContentScroll.Viewport.Width);
        var max = Math.Max(0, ContentScroll.Extent.Width - ContentScroll.Viewport.Width);
        var offset = Math.Clamp(ContentScroll.Offset.X + step, 0, max);
        if (step == 0 || offset == ContentScroll.Offset.X)
        {
            _autoScroll.Stop();
            return;
        }
        ContentScroll.Offset = new Vector(offset, ContentScroll.Offset.Y);
        var contentX = _pointerViewportX + offset;
        if (_marqueeStarted) _viewModel.UpdateMarquee(contentX, _pointerLanesY);
        else _viewModel.UpdateGesture(contentX, TrackAt(_pointerLanesY));
    }

    private void CancelMarquee()
    {
        _autoScroll.Stop();
        _marqueeStarted = false;
        _viewModel?.CancelMarquee();
    }

    private void CancelGesture()
    {
        _autoScroll.Stop();
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
        var track = TrackAt(e.GetPosition(TrackRows).Y);
        if (e.Data.Get(DragFormats.MediaAssetId) is string text && Guid.TryParse(text, out var assetId))
            _viewModel.ShowDropPreview(assetId, track, e.GetPosition(ContentRoot).X);   // D037 §2: the snap indicator
        else
            _viewModel.ShowDropTarget(track);
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
