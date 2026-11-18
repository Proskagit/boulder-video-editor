using Avalonia.Controls;

namespace AiVideoEditor.UI.Views;

/// <summary>D069 §3: the «Сохранить как» window's content (<see cref="ViewModels.SaveAsViewModel"/>).</summary>
public partial class SaveAsView : UserControl
{
    public SaveAsView()
    {
        InitializeComponent();
    }

    /// <summary>The name field focused with its text selected, so typing replaces the offered name.</summary>
    public void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }
}
