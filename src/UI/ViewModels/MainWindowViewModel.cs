using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.ViewModels.Panels;
using CommunityToolkit.Mvvm.Input;
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
    private readonly MediaAvailabilityMonitor? _mediaAvailability;
    private readonly ExportWorkflow? _export;
    private readonly IDialogService? _dialogs;

    /// <summary>D060 §5: the export running in the background (its progress), or null — the status bar's compact indicator.</summary>
    public ExportProgressViewModel? ExportProgress => _export?.Progress;

    /// <summary>The compact indicator is shown.</summary>
    public bool IsExportActive => ExportProgress is not null;

    /// <summary>Opens the detailed window of the running export.</summary>
    public IRelayCommand ShowExportCommand { get; }

    /// <summary>Asks, then cancels the running export.</summary>
    public IAsyncRelayCommand CancelExportCommand { get; }

    /// <summary>"Name — Boulder Video Editor", with a "*" after the name while there are unsaved changes.</summary>
    public string Title => $"{_projectService.Current.Name}{(_projectService.Current.IsDirty ? "*" : "")} — Boulder Video Editor";

    public ToolbarViewModel Toolbar { get; }

    /// <summary>D068 item 5: the first screen at startup.</summary>
    public StartScreenViewModel StartScreen { get; }
    public MediaBrowserViewModel MediaBrowser { get; }
    public PreviewViewModel Preview { get; }
    public InspectorViewModel Inspector { get; }
    public TimelineViewModel Timeline { get; }
    public StatusService Status { get; }

    /// <summary>The AI hold markup of the selected video clip (D034): the Inspector's "Holds" section and the markup
    /// mode over the Preview. Null without the model service (design time, tests).</summary>
    public HoldMarkupViewModel? HoldMarkup { get; }

    /// <summary>The Preview's Move / Crop tools (D038). Null when not composed (design time, some tests).</summary>
    public PreviewTransformViewModel? PreviewTransform { get; }

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
        WaveformCoordinator? waveforms = null,
        MediaAvailabilityMonitor? mediaAvailability = null,
        IMediaRelinkService? relink = null,
        HoldMarkupViewModel? holdMarkup = null,
        PreviewTransformViewModel? previewTransform = null,
        TrainingSessionService? training = null,
        ExportWorkflow? export = null,
        IDialogService? dialogs = null,
        PeopleAnalysisCoordinator? people = null,
        AudioProcessingCoordinator? audioProcessing = null)
    {
        _export = export;
        _dialogs = dialogs;
        ShowExportCommand = new RelayCommand(() => _export?.ShowDetails());
        CancelExportCommand = new AsyncRelayCommand(CancelExportAsync);
        if (_export is not null)
            _export.ProgressChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ExportProgress));
                OnPropertyChanged(nameof(IsExportActive));
            };
        // A file found not to fit its clips after a later analysis (D026 §3, Step 11.4): said, never undone by itself.
        if (relink is not null)
            relink.RelinkedMediaFoundIncompatible += (_, e) => status.ReportError(e.Message);
        _thumbnails = thumbnails;
        _waveforms = waveforms;
        _mediaAvailability = mediaAvailability;
        Toolbar = toolbar;
        MediaBrowser = mediaBrowser;
        Preview = preview;
        Inspector = inspector;
        Timeline = timeline;
        Status = status;
        _projectFiles = projectFiles;
        _projectService = projectService;
        StartScreen = new StartScreenViewModel(toolbar.Recent, toolbar.OpenCommand, projectService);   // D068 item 5
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
        // D057 §3.2: several files go in their recording order (the training's offset reads the file names' times).
        MediaBrowser.AddManyToTimelineRequested += (_, assets) => Timeline.AddMediaMany(
            Core.Telemetry.MediaRecordingOrder.Order(assets, _projectService.Current.Training?.Workout?.UtcOffset));

        HoldMarkup = holdMarkup;
        MediaBrowser.CanSelectAllNow = () => HoldMarkup is not { IsActive: true };
        // D057 §3.3: the clip menu reaches the hold markup and the Inspector's FIT command.
        Timeline.MarkHoldsCommand = holdMarkup?.StartCommand;
        Timeline.RecognizeFitClimbsCommand = Inspector.RecognizeFitClimbsCommand;
        if (holdMarkup is not null) WireHoldMarkup(holdMarkup);
        PreviewTransform = previewTransform;
        if (previewTransform is not null) WirePreviewTransform(previewTransform);

        // Phase 16 (D031): a double click opens the asset in the Preview's Source mode; the source bar's Insert /
        // Overwrite are the timeline's commands (the target track, the playhead, the selection are the timeline's).
        MediaBrowser.OpenInSourceRequested += (_, asset) =>
        {
            if (Preview.OpenSource(asset) is { } reason) Status.ReportWarning(reason);
        };
        Preview.InsertCommand = Timeline.InsertFromSourceCommand;
        Preview.OverwriteCommand = Timeline.OverwriteFromSourceCommand;

        // Playhead ↔ playback. User moves (SeekRequested) seek playback; playback positions are
        // shown with ShowPlaybackPosition, which never raises SeekRequested — no feedback loop.
        Timeline.PlayheadChanged += (_, _) => UpdatePreviewPosition();
        // D045 §17: «На указатель» sets a template's status moment from the timeline's playhead.
        Inspector.Playhead = () => Timeline.Playhead;
        // Phase 35 (D050 amendment B4): the clip's training readout follows the playhead.
        Inspector.Training = training;
        Inspector.ImportTrainingCommand = Toolbar.ImportTrainingCommand;   // D068 item 14
        WirePeople(people);
        Inspector.AudioProcessing = audioProcessing;   // D064: the clean-up's state in «Звук»
        Timeline.PlayheadChanged += (_, _) => Inspector.OnPlayheadMoved();
        Inspector.GoToKeyframe = direction => Timeline.GoToKeyframe(direction);
        Timeline.SeekRequested += (_, position) => Preview.Seek(position);
        Preview.PlaybackPositionChanged += (_, position) => Timeline.ShowPlaybackPosition(position);
        // D058 §1: the timeline follows the playhead while playing.
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewViewModel.IsPlaying)) Timeline.SetPlaying(Preview.IsPlaying);
        };
        Preview.FrameStepRequested += (_, frames) => Timeline.StepFrames(frames);
        UpdatePreviewPosition();

        logger.LogInformation("Application shell initialized.");
    }

    /// <summary>The main window has been shown: offer recovery of autosaved work, start autosave.</summary>
    public Task OnWindowOpenedAsync() => _projectFiles.StartSessionAsync();

    /// <summary>The main window became active: the media files are checked again (throttled, D026 §2).</summary>
    public void OnWindowActivated() => _mediaAvailability?.OnWindowActivated();

    /// <summary>The main window is about to close; returns false to keep it open. Once closing is agreed,
    /// playback is released and thumbnail and waveform work cancelled here, on the UI thread, before the window
    /// closes and the dispatcher stops.</summary>
    public async Task<bool> PrepareToCloseAsync()
    {
        // D060 §5: a running export is never ended silently — the user keeps it (the window stays open) or cancels it; then
        // the unsaved-changes question; only after both the export is cancelled and waited for (ffmpeg ended, temporary files
        // deleted), so answering Cancel to the second question still leaves the export running.
        var cancelExport = false;
        if (_export?.Progress is { } progress)
        {
            if (_dialogs is null) return false;
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = Strings.Export_CloseTitle,
                Message = string.Format(Strings.Export_CloseQuestion, progress.OutputPath, progress.OverallPercent.ToString("0")),
                Buttons = new[] { Strings.Export_KeepExporting, Strings.Export_CancelAndClose }
            });
            if (choice != 1) return false;
            cancelExport = true;
        }

        if (!await _projectFiles.PrepareToCloseAsync()) return false;

        if (cancelExport && _export is not null)
        {
            Status.Report(Strings.Export_ClosingCancelling);
            await _export.CancelAndWaitAsync();
        }

        _mediaAvailability?.Stop(); // no new media re-check while closing
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

    /// <summary>D061: the Inspector's people section, the Preview's pick of the climber and masks, the hold markup's Follow
    /// position.</summary>
    private void WirePeople(PeopleAnalysisCoordinator? people)
    {
        Inspector.People = people;
        if (people is null) return;
        if (Inspector.HoldMarkup is { } markup)
        {
            markup.FollowPositionAt = (clip, sourceTime) =>
            {
                var project = _projectService.Current;
                if (clip.Follow is not { } follow || !people.Analyses.Tracks.TryGetValue(clip.MediaAssetId, out var tracks) ||
                    Core.Playback.PlaybackSnapshotBuilder.DisplaySize(project.MediaAssets.FirstOrDefault(a => a.Id == clip.MediaAssetId)?.Metadata) is not { } source)
                    return null;
                var canvas = new Core.Composition.FrameSize(project.Settings.FrameWidth, project.Settings.FrameHeight);
                return Core.People.FollowResolution.For(clip, follow, tracks, canvas, source).Framing?.At(sourceTime);
            };
            people.AnalysesChanged += (_, _) => markup.RefreshGeometry();
        }
        // D061 amendment 1: on a followed part the Preview's Move shows the frame where Follow puts it and drags the climber's
        // place in the frame.
        if (PreviewTransform is { } transform)
        {
            transform.FollowAt = (clip, sourceTime) =>
            {
                var project = _projectService.Current;
                if (clip.Follow is not { } follow || !people.Analyses.Tracks.TryGetValue(clip.MediaAssetId, out var tracks) ||
                    Core.Playback.PlaybackSnapshotBuilder.DisplaySize(project.MediaAssets.FirstOrDefault(a => a.Id == clip.MediaAssetId)?.Metadata) is not { } source)
                    return null;
                var canvas = new Core.Composition.FrameSize(project.Settings.FrameWidth, project.Settings.FrameHeight);
                var resolution = Core.People.FollowResolution.For(clip, follow, tracks, canvas, source);
                if (resolution.Framing is not { } framing || resolution.PartAt(sourceTime) is not { State: Core.People.FollowState.Following } part) return null;
                return (part.Index, framing.At(sourceTime));
            };
            people.AnalysesChanged += (_, _) => transform.Refresh();
        }
        Inspector.FollowPlaybackRequested += (_, play) =>
        {
            if (play) Preview.PlayCommand.Execute(null);
            else Preview.PauseCommand.Execute(null);
        };
        Inspector.PersonPickRequested += (_, clipId) => Preview.StartPersonPick(clipId);
        Inspector.PersonPickCancelRequested += (_, _) => Preview.CancelPersonPick();
        Preview.PersonPicked += (_, pick) => Inspector.OnPersonPicked(pick.ClipId, pick.SourceTime, pick.Point);
        Preview.PersonPickEnded += (_, _) => Inspector.OnPersonPickEnded();
        // A pick belongs to the shown clip and frame: another selection, Play or a seek ends it.
        Timeline.SelectionChanged += (_, _) => Preview.CancelPersonPick();
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewViewModel.IsPlaying) && Preview.IsPlaying) Preview.CancelPersonPick();
        };
    }

    /// <summary>D034: the markup follows the selected video clip and the playhead; seek, Play, the Source mode or
    /// another selection leave the markup mode (the confirmed markup stays).</summary>
    private void WireHoldMarkup(HoldMarkupViewModel markup)
    {
        Inspector.HoldMarkup = markup;
        Preview.HoldMarkup = markup;
        markup.Playhead = () => Timeline.Playhead;
        markup.SeekTo = time => Timeline.SetPlayhead(time);   // D048 §2: back to T
        markup.IsPausedOnTimeline = () => !Preview.IsPlaying && !Preview.IsSourceMode;
        Timeline.SelectionChanged += (_, selection) => markup.SetClip(selection?.Clip, selection?.OrangeRouteId);
        Timeline.TransitionSelectionChanged += (_, selection) => { if (selection is not null) markup.SetClip(null); };
        MediaBrowser.SelectionChanged += (_, asset) => { if (asset is not null) markup.SetClip(null); };
        Timeline.PlayheadChanged += (_, _) => markup.OnPlayheadMoved();
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PreviewViewModel.IsPlaying) or nameof(PreviewViewModel.IsSourceMode))
                markup.OnPreviewStateChanged();
        };
    }

    /// <summary>D038: the tools act on the timeline's primary selected clip under the playhead; a click on a picture selects
    /// its clip in the timeline; playback, the Source mode, a save and the hold markup mode end a gesture or the tool.</summary>
    private void WirePreviewTransform(PreviewTransformViewModel transform)
    {
        Preview.Transform = transform;
        transform.Playhead = () => Timeline.Playhead;
        transform.IsPlaying = () => Preview.IsPlaying;
        transform.IsSourceMode = () => Preview.IsSourceMode;
        transform.SelectClip = id => Timeline.SelectClipFromPreview(id);
        // A click on a picture turns Move on (D061 amendment 1) — not while the hold markup or the climber's pick takes clicks.
        transform.OtherModeActive = () => HoldMarkup?.IsActive == true || Preview.IsPickingPerson;
        // The climber's pick takes the Preview's clicks: Move turns off when it starts (as with the hold markup).
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewViewModel.IsPickingPerson) && Preview.IsPickingPerson) transform.TurnOff();
        };
        Timeline.SelectionChanged += (_, selection) => transform.SetClip(selection is { IsOrange: true } ? null : selection?.Clip);   // an orange: no move handles
        Timeline.TransitionSelectionChanged += (_, selection) => { if (selection is not null) transform.SetClip(null); };
        MediaBrowser.SelectionChanged += (_, asset) => { if (asset is not null) transform.SetClip(null); };
        Timeline.PlayheadChanged += (_, _) => transform.OnPlayheadMoved();
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PreviewViewModel.IsPlaying) or nameof(PreviewViewModel.IsSourceMode))
                transform.OnPreviewStateChanged();
        };
        _projectFiles.BeforeSave += (_, _) => transform.CancelGesture();
        transform.OnPreviewStateChanged();

        // A tool and the hold markup mode exclude each other (D038 §1).
        if (HoldMarkup is not { } markup) return;
        transform.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewTransformViewModel.IsActive) && transform.IsActive && markup.IsActive) markup.Exit();
        };
        markup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HoldMarkupViewModel.IsActive) && markup.IsActive) transform.TurnOff();
        };
    }

    private void UpdatePreviewPosition() =>
        Preview.SetPosition(Timeline.Playhead, Timeline.SequenceDuration, Timeline.FrameRate);

    /// <summary>The compact indicator's ✕ (D060 §5): a small target, so it asks first; «Отмена» in the detailed window
    /// cancels at once as before.</summary>
    private async Task CancelExportAsync()
    {
        if (_export?.Progress is not { IsCancelling: false } progress) return;
        if (_dialogs is not null)
        {
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = Strings.Export_CancelQuestionTitle,
                Message = string.Format(Strings.Export_CancelQuestion, progress.OutputPath, progress.OverallPercent.ToString("0")),
                Buttons = new[] { Strings.Export_CancelExport, Strings.Export_KeepExporting }
            });
            if (choice != 0) return;
        }
        // The export may have ended while the question was open: only the same, still running export is cancelled.
        if (ReferenceEquals(_export?.Progress, progress)) progress.Cancel();
    }
}
