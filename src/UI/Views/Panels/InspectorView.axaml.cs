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
        InspectorScroll.ScrollChanged += (_, _) =>
        {
            _scroll.Scrolled(InspectorScroll.Offset.Y);
            NoteCategoryAtTop();
        };
    }

    /// <summary>D068 amendment 1: the first shown section of a group (a <c>Panel.category</c> whose Tag is its name).</summary>
    private Control? CategoryPanel(InspectorCategory category) =>
        InspectorScroll.GetVisualDescendants().OfType<Panel>()
            .FirstOrDefault(p => p.Classes.Contains("category") && p.Tag as string == category.ToString() && p.IsEffectivelyVisible &&
                                 p.Bounds.Height > 0);

    private double TopOf(Control panel) =>
        panel.TranslatePoint(default, (Visual)InspectorScroll.Content!) is { } at ? at.Y : 0;

    private void OnScrollToCategory(object? sender, InspectorCategory category)
    {
        if (CategoryPanel(category) is not { } panel) return;
        var max = Math.Max(0, InspectorScroll.Extent.Height - InspectorScroll.Viewport.Height);
        InspectorScroll.Offset = InspectorScroll.Offset.WithY(Math.Min(max, Math.Max(0, TopOf(panel) - 4)));
    }

    /// <summary>The group whose section is at the top of the visible list is lit on the rail.</summary>
    private void NoteCategoryAtTop()
    {
        if (_viewModel is not { IsSingleClipSelected: true }) return;
        var top = InspectorScroll.Offset.Y + 12;
        InspectorCategory? atTop = null;
        foreach (var category in Enum.GetValues<InspectorCategory>())
        {
            if (CategoryPanel(category) is { } panel && TopOf(panel) <= top) atTop = category;
        }
        // At the end of the list the last group can't reach the top: the one whose button was pressed stays lit.
        var atEnd = InspectorScroll.Offset.Y >= InspectorScroll.Extent.Height - InspectorScroll.Viewport.Height - 1;
        if (atTop is { } shown && !(atEnd && _viewModel.SelectedCategory > shown)) _viewModel.NoteCategoryAtTop(shown);
    }

    private readonly InspectorScrollMemory _scroll;
    private InspectorViewModel? _viewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.Shown -= OnShown;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ScrollToCategoryRequested -= OnScrollToCategory;
        }
        _viewModel = DataContext as InspectorViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Shown += OnShown;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ScrollToCategoryRequested += OnScrollToCategory;
        }
    }

    /// <summary>D062 amendment 1: a selected orange opens its «Зацепки» section — it is the only one shown.</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectorViewModel.IsHoldsOnly) && _viewModel is { IsHoldsOnly: true }) HoldsSection.IsExpanded = true;
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
