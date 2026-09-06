using Avalonia.Controls;
using AiVideoEditor.UI.ViewModels;

namespace AiVideoEditor.UI.Views;

/// <summary>
/// Code-behind is intentionally minimal: it only wires up the XAML component and
/// assigns the injected view model. All state and behavior live in
/// <see cref="MainWindowViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Parameterless constructor required by the Avalonia XAML previewer/designer.</summary>
    public MainWindow() : this(new MainWindowViewModel(new AiVideoEditor.Core.Common.UndoRedoService(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindowViewModel>.Instance))
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
