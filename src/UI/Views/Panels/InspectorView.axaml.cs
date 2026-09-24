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

        // A field left with a value it doesn't apply — an empty numeric field (null in the view
        // model, NumericInput) or an incomplete color — changed nothing; once it loses focus it
        // shows the model's value again. Fields holding the model value are not touched.
        AddHandler(LostFocusEvent, OnFieldLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Visual source && (source is TextBox || source.FindAncestorOfType<NumericUpDown>(includeSelf: true) is not null))
            (DataContext as InspectorViewModel)?.ShowModelValues();
    }
}
