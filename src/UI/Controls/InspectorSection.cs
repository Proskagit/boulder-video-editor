using AiVideoEditor.Core.Interfaces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiVideoEditor.UI.Controls;

/// <summary>
/// D057 §3.1: one collapsible section of the Inspector — an <see cref="Expander"/> with a title and an optional short summary
/// shown in its header (e.g. «Зацепки · 12»). Its open / closed state belongs to the Inspector, keyed by the stable
/// <see cref="SectionId"/> (never per clip, never reset by a selection) and kept across restarts in the user's layout
/// (<see cref="InspectorSectionStates"/>).
/// </summary>
public sealed class InspectorSection : Expander
{
    public static readonly StyledProperty<string> SectionIdProperty =
        AvaloniaProperty.Register<InspectorSection, string>(nameof(SectionId), string.Empty);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<InspectorSection, string?>(nameof(Title));

    public static readonly StyledProperty<object?> SummaryProperty =
        AvaloniaProperty.Register<InspectorSection, object?>(nameof(Summary));

    /// <summary>Whether the section starts collapsed while the user has not chosen (the FIT's service section).</summary>
    public static readonly StyledProperty<bool> CollapsedByDefaultProperty =
        AvaloniaProperty.Register<InspectorSection, bool>(nameof(CollapsedByDefault));

    private readonly TextBlock _title = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#C8C8C8")) };
    private readonly TextBlock _summary = new()
    {
        FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8A8A8A")), Margin = new Thickness(8, 0, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private bool _loading;

    public InspectorSection()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(10, 6, 10, 8);
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_title, Dock.Left);
        header.Children.Add(_title);
        header.Children.Add(_summary);
        Header = header;
        IsExpanded = true;
    }

    protected override Type StyleKeyOverride => typeof(Expander);

    public string SectionId
    {
        get => GetValue(SectionIdProperty);
        set => SetValue(SectionIdProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? Summary
    {
        get => GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public bool CollapsedByDefault
    {
        get => GetValue(CollapsedByDefaultProperty);
        set => SetValue(CollapsedByDefaultProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty) _title.Text = Title;
        else if (change.Property == SummaryProperty) UpdateSummary();
        else if (change.Property == SectionIdProperty || change.Property == CollapsedByDefaultProperty) LoadState();
        else if (change.Property == IsExpandedProperty)
        {
            UpdateSummary();
            if (!_loading && SectionId.Length > 0) InspectorSectionStates.SetCollapsed(SectionId, !IsExpanded);
        }
    }

    private void LoadState()
    {
        if (SectionId.Length == 0) return;
        _loading = true;
        IsExpanded = !InspectorSectionStates.IsCollapsed(SectionId, CollapsedByDefault);
        _loading = false;
    }

    /// <summary>The summary is shown only while collapsed — open, the fields themselves tell it.</summary>
    private void UpdateSummary()
    {
        var text = Summary?.ToString();
        _summary.Text = !IsExpanded && !string.IsNullOrEmpty(text) ? "· " + text : null;
    }
}

/// <summary>
/// D057 §3.1: the open / closed state of the Inspector's sections — one state, keyed by the section's id, in the user's
/// working layout (<see cref="IUiStateService"/>). The application sets <see cref="Store"/> at start; without it (tests) the
/// state lives in memory.
/// </summary>
public static class InspectorSectionStates
{
    public static IUiStateService Store { get; set; } = new InMemoryUiState();

    public static bool IsCollapsed(string id, bool collapsedByDefault = false) =>
        Store.Current.InspectorSections.TryGetValue(id, out var collapsed) ? collapsed : collapsedByDefault;

    public static void SetCollapsed(string id, bool collapsed) =>
        Store.Update(s => s with { InspectorSections = s.InspectorSections.SetItem(id, collapsed) });
}
