using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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

    private void Reset()
    {
        _pressPoint = null;
        _pressedItem = null;
    }

    private static MediaBrowserItemViewModel? ItemFrom(object? source) =>
        (source as StyledElement)?.DataContext as MediaBrowserItemViewModel;
}
