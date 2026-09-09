using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// Root shell view model for the main window. It owns no editing state itself —
/// it just composes the five panel view models (Toolbar, Media Browser, Preview,
/// Inspector, Timeline), each injected via DI so each panel's dependencies stay
/// scoped to what that panel actually needs.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    public string Title => "AI Video Editor";

    public ToolbarViewModel Toolbar { get; }
    public MediaBrowserViewModel MediaBrowser { get; }
    public PreviewViewModel Preview { get; }
    public InspectorViewModel Inspector { get; }
    public TimelineViewModel Timeline { get; }

    public MainWindowViewModel(
        ToolbarViewModel toolbar,
        MediaBrowserViewModel mediaBrowser,
        PreviewViewModel preview,
        InspectorViewModel inspector,
        TimelineViewModel timeline,
        ILogger<MainWindowViewModel> logger)
    {
        Toolbar = toolbar;
        MediaBrowser = mediaBrowser;
        Preview = preview;
        Inspector = inspector;
        Timeline = timeline;

        logger.LogInformation("Application shell initialized (Phase 1 UI skeleton).");
    }
}
