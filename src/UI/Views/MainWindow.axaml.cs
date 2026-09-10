using Avalonia.Controls;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Services;
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
    /// Builds a throwaway view model graph against no-op design-time stubs of the Core
    /// service interfaces — deliberately NOT the real Media/Project subsystem
    /// implementations, since the UI project must not depend on them (only App, the
    /// composition root, wires concrete subsystems via DI). Never used at runtime.</summary>
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
        var projectService = new DesignTimeProjectService();
        var mediaImportService = new DesignTimeMediaImportService();
        var filePicker = new DesignTimeFilePickerService();
        var status = new StatusService();
        var importWorkflow = new MediaImportWorkflow(
            filePicker, mediaImportService, projectService, status, NullLogger<MediaImportWorkflow>.Instance);

        return new MainWindowViewModel(
            new ToolbarViewModel(undoRedo, projectService, importWorkflow, status),
            new MediaBrowserViewModel(projectService, importWorkflow, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(),
            new TimelineViewModel(),
            status,
            NullLogger<MainWindowViewModel>.Instance);
    }

    // --- Design-time-only stubs -------------------------------------------------
    // Minimal, inert implementations of the Core service interfaces, used only by
    // the XAML previewer above. Keeping these here (rather than referencing the
    // real Media/Project subsystem projects) keeps UI's dependency list to Core only.

    private sealed class DesignTimeProjectService : IProjectService
    {
        public Core.Entities.Project Current { get; } = new() { Name = "Design Time" };
        public event EventHandler? ProjectChanged { add { } remove { } }
        public event EventHandler? MediaAssetsChanged { add { } remove { } }
        public Core.Entities.Project CreateNew(string name, ProjectSettings? settings = null) => Current;
        public Task<Core.Entities.Project> OpenAsync(string projectFolderPath, CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<MediaAsset> DetectMissingMedia() => Array.Empty<MediaAsset>();
        public MediaAddResult AddMediaAssets(IEnumerable<MediaAsset> assets) => new();
    }

    private sealed class DesignTimeMediaImportService : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromResult(new MediaImportBatchResult());
    }

    private sealed class DesignTimeFilePickerService : IFilePickerService
    {
        public Task<IReadOnlyList<string>> PickFilesAsync(FilePickerRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
