using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// Root shell view model for the main window. It owns no editing state itself —
/// it composes the five panel view models plus the shared status bar, and wires the
/// cross-panel interactions. Panels never reference each other directly; this is the
/// one place that's allowed to connect them:
/// <list type="bullet">
/// <item>Media Browser or timeline selection → Inspector (the two are mutually exclusive);</item>
/// <item>Media Browser "Add to Timeline" → Timeline;</item>
/// <item>timeline playhead ↔ Preview transport / playback (seek on user moves, follow playback).</item>
/// </list>
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private bool _suppressMediaSelection;

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
            if (_suppressMediaSelection) return;

            if (asset is null)
            {
                if (Inspector.IsMediaSelected) Inspector.ClearSelection();
                return;
            }

            Timeline.ClearSelectionSilently();
            Inspector.ShowMedia(asset);
        };

        Timeline.SelectionChanged += (_, selection) =>
        {
            if (selection is null)
            {
                if (Inspector.IsTimelineClipSelected) Inspector.ClearSelection();
                return;
            }

            _suppressMediaSelection = true;
            MediaBrowser.SelectedItem = null;
            _suppressMediaSelection = false;
            Inspector.ShowClip(selection);
        };

        MediaBrowser.AddToTimelineRequested += (_, asset) => Timeline.AddMedia(asset);

        // Playhead ↔ playback. User moves (SeekRequested) seek playback; playback positions are
        // shown with ShowPlaybackPosition, which never raises SeekRequested — no feedback loop.
        Timeline.PlayheadChanged += (_, _) => UpdatePreviewPosition();
        Timeline.SeekRequested += (_, position) => Preview.Seek(position);
        Preview.PlaybackPositionChanged += (_, position) => Timeline.ShowPlaybackPosition(position);
        Preview.FrameStepRequested += (_, frames) => Timeline.StepFrames(frames);
        UpdatePreviewPosition();

        logger.LogInformation("Application shell initialized.");
    }

    private void UpdatePreviewPosition() =>
        Preview.SetPosition(Timeline.Playhead, Timeline.SequenceDuration, Timeline.FrameRate);
}
