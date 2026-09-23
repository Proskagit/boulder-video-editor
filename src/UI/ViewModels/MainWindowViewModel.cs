using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// Root shell view model for the main window. It owns no editing state itself —
/// it composes the five panel view models plus the shared status bar, and wires
/// the one cross-panel interaction that exists so far: Media Browser selection
/// updating the Inspector. Panels never reference each other directly; this is
/// the one place that's allowed to connect them.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    public string Title => "AI Video Editor";

    public ToolbarViewModel Toolbar { get; }
    public MediaBrowserViewModel MediaBrowser { get; }
    public PreviewViewModel Preview { get; }
    public InspectorViewModel Inspector { get; }
    public TimelineViewModel Timeline { get; }
    public StatusService Status { get; }

    public MainWindowViewModel(
        ToolbarViewModel toolbar,
        MediaBrowserViewModel mediaBrowser,
        PreviewViewModel preview,
        InspectorViewModel inspector,
        TimelineViewModel timeline,
        StatusService status,
        ILogger<MainWindowViewModel> logger)
    {
        Toolbar = toolbar;
        MediaBrowser = mediaBrowser;
        Preview = preview;
        Inspector = inspector;
        Timeline = timeline;
        Status = status;

        MediaBrowser.SelectionChanged += (_, asset) =>
        {
            if (asset is null)
                Inspector.ClearSelection();
            else
                Inspector.ShowMedia(asset);
        };

        logger.LogInformation("Application shell initialized.");
    }
}
