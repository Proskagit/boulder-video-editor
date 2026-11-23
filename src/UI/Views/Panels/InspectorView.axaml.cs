using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Controls;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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

        HideEmptyCategoryPanels(this);

        // D057 §3.1: the scroll position per kind of item, kept in the user's layout (the same store as the sections).
        _scroll = new InspectorScrollMemory(InspectorSectionStates.Store);
        InspectorScroll.ScrollChanged += (_, _) =>
        {
            _scroll.Scrolled(InspectorScroll.Offset.Y);
            NoteCategoryAtTop();
        };
    }

    /// <summary>
    /// D079 amendment 1: a <c>Panel.category</c> (a section's anchor for the rail) is shown only while a section in it is — a
    /// panel left visible around a hidden section is empty but still takes the list's spacing (16 px each), which left blank
    /// gaps between the sections (the owner's screenshots of 1.13.0: above «Клип», between «Клип» and «Слежение и люди»).
    /// </summary>
    internal static void HideEmptyCategoryPanels(Control root)
    {
        foreach (var panel in root.GetLogicalDescendants().OfType<Panel>().Where(p => p.Classes.Contains("category")).ToList())
        {
            var children = panel.Children.ToList();
            void Update() => panel.IsVisible = children.Any(c => c.IsVisible);
            foreach (var child in children)
                child.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) Update(); };
            Update();
        }
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

    /// <summary>The group whose section is at the top of the visible list is lit on the rail. D080 §2: a group's sections need
    /// not stand together («Клип»'s transform first, its clip section last), so the lit group is the one of the nearest shown
    /// section at or above the list's top, by position.</summary>
    private void NoteCategoryAtTop()
    {
        if (_viewModel is not { IsSingleClipSelected: true }) return;
        var top = InspectorScroll.Offset.Y + 12;
        var shown = InspectorScroll.GetVisualDescendants().OfType<Panel>()
            .Where(p => p.Classes.Contains("category") && p.IsEffectivelyVisible && p.Bounds.Height > 0 &&
                        Enum.TryParse<InspectorCategory>(p.Tag as string, out _))
            .Select(p => (Category: Enum.Parse<InspectorCategory>((string)p.Tag!), Top: TopOf(p)))
            .ToList();
        InspectorCategory? atTop = null;
        var nearest = double.NegativeInfinity;
        foreach (var (category, at) in shown)
            if (at <= top && at >= nearest) (atTop, nearest) = (category, at);
        // At the end of the list the last groups can't reach the top: the one whose button was pressed stays lit while a
        // section of it is shown below the top.
        var atEnd = InspectorScroll.Offset.Y >= InspectorScroll.Extent.Height - InspectorScroll.Viewport.Height - 1;
        var selected = _viewModel.SelectedCategory;
        if (atEnd && shown.Any(s => s.Category == selected && s.Top > top)) return;
        if (atTop is { } lit) _viewModel.NoteCategoryAtTop(lit);
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
