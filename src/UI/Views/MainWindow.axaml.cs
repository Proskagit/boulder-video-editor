using Avalonia.Controls;
using AiVideoEditor.Core.Common;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.UI.Views;

/// <summary>
/// Code-behind is intentionally minimal: it only wires up the XAML component and
/// assigns the injected view model. All state and behavior live in
/// <see cref="MainWindowViewModel"/> and the panel view models it composes.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Parameterless constructor required by the Avalonia XAML previewer/designer.
    /// Builds a throwaway view model graph with no real services behind it — never used
    /// at runtime, where <see cref="MainWindow(MainWindowViewModel)"/> is resolved via DI.</summary>
    public MainWindow() : this(BuildDesignTimeViewModel())
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private static MainWindowViewModel BuildDesignTimeViewModel()
    {
        var undoRedo = new UndoRedoService();

        return new MainWindowViewModel(
            new ToolbarViewModel(undoRedo, NullLogger<ToolbarViewModel>.Instance),
            new MediaBrowserViewModel(NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(),
            new TimelineViewModel(),
            NullLogger<MainWindowViewModel>.Instance);
    }
}
