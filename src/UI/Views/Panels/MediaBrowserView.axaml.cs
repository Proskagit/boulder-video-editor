using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AiVideoEditor.UI.Views.Panels;

/// <summary>View glue: double-click adds the item to the timeline, and dragging an item
/// starts a drag-and-drop carrying its asset id (dropped onto a timeline track).</summary>
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
            if (DataContext is MediaBrowserViewModel vm && ItemFrom(e.Source) is not null &&
                vm.AddToTimelineCommand.CanExecute(null))
            {
                vm.AddToTimelineCommand.Execute(null);
            }
        };

        // Tunnel: see the press before the ListBox handles it for selection.
        MediaList.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        MediaList.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        MediaList.AddHandler(PointerReleasedEvent, (_, _) => Reset(), RoutingStrategies.Tunnel);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(MediaList).Properties.IsLeftButtonPressed) return;
        _pressedItem = ItemFrom(e.Source);
        _pressPoint = _pressedItem is null ? null : e.GetPosition(MediaList);
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
        var data = new DataObject();
        data.Set(DragFormats.MediaAssetId, item.Asset.Id.ToString());
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
