using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AiVideoEditor.UI.Views.Panels;

/// <summary>View glue: a double click opens a video or audio item in the Preview's Source mode (an image is added to
/// the timeline), and dragging an item starts a drag-and-drop carrying its asset id (dropped onto a timeline track).</summary>
public partial class MediaBrowserView : UserControl
{
    private const double DragStartDistance = 6;

    private Point? _pressPoint;
    private MediaBrowserItemViewModel? _pressedItem;

    public MediaBrowserView()
    {
        InitializeComponent();

        MediaList.DoubleTapped += (_, e) =>
        {
            if (DataContext is not MediaBrowserViewModel vm || ItemFrom(e.Source) is not { } item) return;
            vm.Activate(item);
            // Opened in Source: the list must not keep the focus, or it would take the arrows, Home and End the Source
            // viewer's keys use (the window's shortcuts only see keys no focused control handled).
            if (item.Asset.Kind != Core.Entities.MediaKind.Image)
                TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        };

        // Tunnel: see the press before the ListBox handles it for selection.
        MediaList.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        MediaList.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        MediaList.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);

        // D057 §3.2: Ctrl+A with the list focused selects every file — handled here, so the timeline's Ctrl+A (the window's
        // shortcut, D037 §1) and the markup mode's «accept all» never see it.
        // The context menu acts on the file under the pointer (tunnel: before the menu opens).
        MediaList.AddHandler(ContextRequestedEvent, (_, e) =>
        {
            if (DataContext is MediaBrowserViewModel vm) vm.PrepareContextMenu(ItemFrom(e.Source));
        }, RoutingStrategies.Tunnel);
        MediaList.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control && DataContext is MediaBrowserViewModel { AllowsSelectAll: true } vm)
            {
                vm.SelectAllCommand.Execute(null);
                e.Handled = true;
            }
            // D069 §1: Delete with the list focused removes the selected files (the window's Delete — the timeline's — never
            // sees it).
            else if (e.Key is Key.Delete or Key.Back && e.KeyModifiers == KeyModifiers.None && DataContext is MediaBrowserViewModel removeVm)
            {
                if (removeVm.RemoveCommand.CanExecute(null)) removeVm.RemoveCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    private MediaBrowserViewModel? _revealing;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_revealing is not null) _revealing.RevealRequested -= OnReveal;
        _revealing = DataContext as MediaBrowserViewModel;
        if (_revealing is not null) _revealing.RevealRequested += OnReveal;
    }

    /// <summary>«Показать в проводнике»: Explorer with the file selected.</summary>
    private static void OnReveal(object? sender, string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Explorer is not available (another OS): nothing to show.
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(MediaList).Properties.IsLeftButtonPressed) return;
        // D067 §1: a press on a blank place — below the files or in a row beside its picture and text — starts a rubber band
        // (as Explorer does); a press on a file's picture or text stays the start of dragging it to the timeline.
        if (DataContext is MediaBrowserViewModel marqueeVm && IsBlank(e.Source) && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Control)
        {
            _marqueeStart = e.GetPosition(MarqueeLayer);
            _marqueeKept = e.KeyModifiers == KeyModifiers.Control ? marqueeVm.SelectedItems.ToList() : new List<MediaBrowserItemViewModel>();
            _marqueeMoved = false;
            e.Pointer.Capture(MediaList);
            e.Handled = true;
            return;
        }
        // D069 §1: a press on a row's ✕ is the button's — it neither starts a drag nor selects the file alone on release (the
        // selection it removes stays).
        if (IsInButton(e.Source))
        {
            Reset();
            _deferredSelect = false;
            return;
        }
        _pressedItem = ItemFrom(e.Source);
        _pressPoint = _pressedItem is null ? null : e.GetPosition(MediaList);
        // The owner's acceptance of 2026-11-10: a plain press on a file of a group selection keeps the group — it may be the
        // start of dragging the group; a release without a drag then selects that file alone (as Explorer does).
        _deferredSelect = false;
        if (_pressedItem is { } item && e.KeyModifiers == KeyModifiers.None && e.ClickCount == 1 &&
            DataContext is MediaBrowserViewModel { SelectedItems.Count: > 1 } vm && vm.SelectedItems.Contains(item))
        {
            _deferredSelect = true;
            e.Handled = true;
        }
    }

    private bool _deferredSelect;

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_marqueeStart is not null)
        {
            // A click on a blank place without dragging clears the selection (Ctrl keeps it), as Explorer does.
            if (!_marqueeMoved && DataContext is MediaBrowserViewModel clickVm)
                clickVm.SelectByMarquee(Array.Empty<MediaBrowserItemViewModel>(), _marqueeKept);
            EndMarquee(e.Pointer);
            e.Handled = true;
            return;
        }
        if (_deferredSelect && _pressedItem is { } item && DataContext is MediaBrowserViewModel vm)
        {
            vm.SelectedItems.Clear();
            vm.SelectedItems.Add(item);
            vm.SelectedItem = item;
        }
        _deferredSelect = false;
        Reset();
    }

    private async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_marqueeStart is { } from)
        {
            if (!e.GetCurrentPoint(MediaList).Properties.IsLeftButtonPressed)
            {
                EndMarquee(e.Pointer);
                return;
            }
            var to = e.GetPosition(MarqueeLayer);
            if (!_marqueeMoved && Math.Abs(to.X - from.X) < DragStartDistance && Math.Abs(to.Y - from.Y) < DragStartDistance) return;
            _marqueeMoved = true;
            var band = new Rect(from, to).Normalize();
            Canvas.SetLeft(Marquee, band.X);
            Canvas.SetTop(Marquee, band.Y);
            (Marquee.Width, Marquee.Height, Marquee.IsVisible) = (band.Width, band.Height, true);
            if (DataContext is MediaBrowserViewModel bandVm) bandVm.SelectByMarquee(ItemsIn(band), _marqueeKept);
            e.Handled = true;
            return;
        }
        if (_pressPoint is not { } start || _pressedItem is not { } item) return;
        if (!e.GetCurrentPoint(MediaList).Properties.IsLeftButtonPressed)
        {
            Reset();
            return;
        }

        var delta = e.GetPosition(MediaList) - start;
        if (Math.Abs(delta.X) < DragStartDistance && Math.Abs(delta.Y) < DragStartDistance) return;

        Reset();
        _deferredSelect = false;
        var data = new DataObject();
        data.Set(DragFormats.MediaAssetId, item.Asset.Id.ToString());
        // The owner's acceptance of 2026-11-10: dragging a file of a group selection drags the whole group.
        if (DataContext is MediaBrowserViewModel vm && vm.DraggedGroup(item) is { Count: > 1 } group)
            data.Set(DragFormats.MediaAssetIds, string.Join(",", group.Select(a => a.Id)));
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
    }

    // --- D067 §1: the rubber band ---------------------------------------------------------------------------------------

    private Point? _marqueeStart;
    private bool _marqueeMoved;
    private List<MediaBrowserItemViewModel> _marqueeKept = new();

    private Visual MarqueeLayer => (Visual)Marquee.Parent!;

    private void EndMarquee(IPointer pointer)
    {
        _marqueeStart = null;
        _marqueeMoved = false;
        Marquee.IsVisible = false;
        if (pointer.Captured == MediaList) pointer.Capture(null);
    }

    /// <summary>The files whose rows the band touches (the realized rows — the band covers only what is on the screen).</summary>
    private List<MediaBrowserItemViewModel> ItemsIn(Rect band)
    {
        var touched = new List<MediaBrowserItemViewModel>();
        foreach (var container in MediaList.GetRealizedContainers())
        {
            if (container.DataContext is not MediaBrowserItemViewModel item) continue;
            if (container.TranslatePoint(new Point(0, 0), MarqueeLayer) is not { } origin) continue;
            if (new Rect(origin, container.Bounds.Size).Intersects(band)) touched.Add(item);
        }
        return touched;
    }

    /// <summary>A place of the list that is not a file's picture, text or button: the list's empty part, or a row's blank
    /// part (the row's own background beside its content).</summary>
    private static bool IsBlank(object? source)
    {
        for (var element = source as Visual; element is not null; element = element.GetVisualParent())
        {
            if (element is ListBoxItem) return true;
            if (element is StackPanel or Button or Image or TextBlock) return false;
            if (element is ListBox) return true;
        }
        return true;
    }

    private static bool IsInButton(object? source)
    {
        for (var element = source as Visual; element is not null and not ListBoxItem; element = element.GetVisualParent())
            if (element is Button) return true;
        return false;
    }

    private void Reset()
    {
        _pressPoint = null;
        _pressedItem = null;
    }

    private static MediaBrowserItemViewModel? ItemFrom(object? source) =>
        (source as StyledElement)?.DataContext as MediaBrowserItemViewModel;
}
