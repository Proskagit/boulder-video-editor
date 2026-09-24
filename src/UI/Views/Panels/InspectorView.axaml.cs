using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AiVideoEditor.UI.Views.Panels;

public partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();

        // A numeric field left empty changed nothing (null in the view model); once it loses
        // focus it shows the model's value again, like text it could not parse (NumericInput).
        AddHandler(LostFocusEvent, OnFieldLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<NumericUpDown>(includeSelf: true) is { Value: null })
            (DataContext as InspectorViewModel)?.ShowModelValues();
    }
}
