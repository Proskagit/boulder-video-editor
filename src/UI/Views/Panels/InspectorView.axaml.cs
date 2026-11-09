using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Controls;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiVideoEditor.UI.Views.Panels;

public partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();

        // A field left with a value it doesn't apply — an empty numeric field (null in the view
        // model, NumericInput) or an incomplete color — changed nothing; once it loses focus it
        // shows the model's value again. Fields holding the model value are not touched.
        AddHandler(LostFocusEvent, OnFieldLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);

        // D057 §3.1: the scroll position per kind of item, kept in the user's layout (the same store as the sections).
        _scroll = new InspectorScrollMemory(InspectorSectionStates.Store);
        InspectorScroll.ScrollChanged += (_, _) => _scroll.Scrolled(InspectorScroll.Offset.Y);
    }

    private readonly InspectorScrollMemory _scroll;
    private InspectorViewModel? _viewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.Shown -= OnShown;
        _viewModel = DataContext as InspectorViewModel;
        if (_viewModel is not null) _viewModel.Shown += OnShown;
    }

    private void OnShown(object? sender, EventArgs e)
    {
        if (_viewModel is null) return;
        var target = _scroll.Show(_viewModel.ScrollKey);
        // After the new content's layout: the offset is put back once the sections have their sizes.
        Dispatcher.UIThread.Post(() =>
        {
            InspectorScroll.Offset = InspectorScroll.Offset.WithY(target);
            _scroll.Restored();
        }, DispatcherPriority.Loaded);
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Visual source && (source is TextBox || source.FindAncestorOfType<NumericUpDown>(includeSelf: true) is not null))
            (DataContext as InspectorViewModel)?.ShowModelValues();
    }
}
