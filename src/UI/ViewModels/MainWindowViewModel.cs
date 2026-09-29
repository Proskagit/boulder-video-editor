using AiVideoEditor.Core.Interfaces;
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
    private readonly ProjectFileWorkflow _projectFiles;

    private readonly IProjectService _projectService;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly ThumbnailCoordinator? _thumbnails;
    private readonly WaveformCoordinator? _waveforms;

    /// <summary>"Name — AI Video Editor", with a "*" after the name while there are unsaved changes.</summary>
    public string Title => $"{_projectService.Current.Name}{(_projectService.Current.IsDirty ? "*" : "")} — AI Video Editor";

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
        ProjectFileWorkflow projectFiles,
        IProjectService projectService,
        ILogger<MainWindowViewModel> logger,
        ThumbnailCoordinator? thumbnails = null,
        WaveformCoordinator? waveforms = null)
    {
        _thumbnails = thumbnails;
        _waveforms = waveforms;
        Toolbar = toolbar;
        MediaBrowser = mediaBrowser;
        Preview = preview;
        Inspector = inspector;
        Timeline = timeline;
        Status = status;
        _projectFiles = projectFiles;
        _projectService = projectService;
        _logger = logger;
        _projectService.SaveStateChanged += (_, _) => OnPropertyChanged(nameof(Title));

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

        Timeline.TransitionSelectionChanged += (_, selection) =>
        {
            if (selection is null)
            {
                if (Inspector.IsTransitionSelected) Inspector.ClearSelection();
                return;
            }

            _suppressMediaSelection = true;
            MediaBrowser.SelectedItem = null;
            _suppressMediaSelection = false;
            Inspector.ShowTransition(selection);
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

    /// <summary>The main window has been shown: offer recovery of autosaved work, start autosave.</summary>
    public Task OnWindowOpenedAsync() => _projectFiles.StartSessionAsync();

    /// <summary>The main window is about to close; returns false to keep it open. Once closing is agreed,
    /// playback is released and thumbnail and waveform work cancelled here, on the UI thread, before the window
    /// closes and the dispatcher stops.</summary>
    public async Task<bool> PrepareToCloseAsync()
    {
        if (!await _projectFiles.PrepareToCloseAsync()) return false;

        try
        {
            await Preview.ReleasePlaybackAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Releasing playback before closing failed.");
        }
        if (_thumbnails is not null)
            await _thumbnails.ShutdownAsync(); // never throws
        if (_waveforms is not null)
            await _waveforms.ShutdownAsync();  // never throws
        return true;
    }

    private void UpdatePreviewPosition() =>
        Preview.SetPosition(Timeline.Playhead, Timeline.SequenceDuration, Timeline.FrameRate);
}
