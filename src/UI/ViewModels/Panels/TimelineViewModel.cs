using System.Collections.ObjectModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Bottom Timeline panel: a projection of the real <see cref="Sequence"/> plus
/// view state (zoom, playhead, selection, drag previews). It never mutates tracks
/// or clips itself — every edit goes through <see cref="ITimelineEditService"/>,
/// and the panel refreshes from <see cref="IProjectService.TimelineChanged"/>
/// (which also fires on Undo/Redo). Pointer gestures arrive from the view as
/// content-space X coordinates; all time math is done here via
/// <see cref="TimelineCoordinateMapper"/> and the exact frame grid.
/// </summary>
public sealed partial class TimelineViewModel : ViewModelBase
{
    public const double RulerHeight = 24;
    public const double SnapTolerancePixels = 8;
    public const double DragThresholdPixels = 3;
    private const double ZoomStep = 1.25;
    private static readonly MediaTime TrailingSpace = MediaTime.FromSeconds(30);

    private readonly IProjectService _projectService;
    private readonly ITimelineEditService _edit;
    private readonly AttemptEventsService? _attemptEvents;
    private readonly StatusService _status;
    private readonly ILogger<TimelineViewModel> _logger;
    private readonly EditingLock _editingLock;
    private readonly InOutRangeService _inOut;
    private readonly WaveformCoordinator? _waveforms;
    private readonly FilmstripCoordinator? _filmstrips;
    private readonly IDialogService? _dialogs;

    private readonly Dictionary<Guid, TimelineClipViewModel> _clipViewModels = new();
    private readonly Dictionary<Track, TimelineTrackViewModel> _trackViewModels = new();
    private readonly Dictionary<Guid, TimelineTransitionViewModel> _transitionViewModels = new();
    private Guid? _selectedTransitionId;

    /// <summary>Length of a dissolve added with the Dissolve command (the longest that fits when less fits, D025).</summary>
    public static readonly MediaTime DefaultDissolveDuration = MediaTime.FromSeconds(1);
    private readonly List<Guid> _selection = new(); // last = primary

    private Gesture? _gesture;
    private double _viewportWidth;
    private double _scrollOffsetX;

    public TimelineViewModel(
        IProjectService projectService,
        ITimelineEditService edit,
        StatusService status,
        ILogger<TimelineViewModel> logger,
        EditingLock? editingLock = null,
        WaveformCoordinator? waveforms = null,
        IDialogService? dialogs = null,
        InOutRangeService? inOut = null,
        SourceViewerService? source = null,
        ITextBoxMeasurer? textBoxes = null,
        IUserSettingsService? userSettings = null,
        AudioProcessingCoordinator? audio = null,
        FilmstripCoordinator? filmstrips = null,
        AttemptEventsService? attemptEvents = null)
    {
        _attemptEvents = attemptEvents;   // D078: the lift-offs' magnet for the orange's end
        if (userSettings is not null) FollowsPlayheadSetting = () => userSettings.Current.TimelineFollowsPlayhead;
        Mixer = new MixerViewModel(projectService, edit, status, audio, () => !(editingLock?.IsLocked ?? false));
        _source = source;
        _textBoxes = textBoxes;
        if (source is not null) source.SourceChanged += (_, _) => NotifySourceCommands();
        _inOut = inOut ?? new InOutRangeService(projectService);
        _inOut.RangeChanged += (_, _) => RefreshRange();
        _projectService = projectService;
        _edit = edit;
        _status = status;
        _logger = logger;
        _editingLock = editingLock ?? new EditingLock();
        _editingLock.PropertyChanged += (_, _) => OnEditingLockChanged();

        _projectService.TimelineChanged += (_, _) => Refresh();
        _projectService.TimelineChanged += (_, _) => FitWhenTheFirstClipsArrive();
        _projectService.ProjectChanged += (_, _) => OnProjectReplaced();
        _projectService.MediaAssetsChanged += (_, _) =>
        {
            RefreshClipNames();
            RefreshWaveforms(); // a relinked file's old waveform is gone (D026 §3) — not only replaced by a new one
            RefreshFilmstrips();
        };
        _waveforms = waveforms;
        _filmstrips = filmstrips;
        if (filmstrips is not null)
            filmstrips.FilmstripReady += (_, _) => RefreshFilmstrips();
        _dialogs = dialogs;
        if (waveforms is not null)
            waveforms.WaveformReady += (_, _) => RefreshWaveforms();

        _pixelsPerSecond = Sequence.ZoomPixelsPerSecond;
        Refresh();
        _hadClips = HasClips();     // an opened project keeps its saved zoom
        _userZoomed = false;
        PrepareInitialZoom();
    }

    private Sequence Sequence => _projectService.Current.Timeline;
    public FrameRate FrameRate => _edit.FrameRate;
    public MediaTime Playhead => Sequence.PlayheadPosition;

    /// <summary>D057 §3.14: the playhead for an edit bound to the project's grid — Split, Q / W, In / Out, markers, Insert /
    /// Overwrite, Paste, + Text, a template: the nearest project frame (the playhead itself may lie between them).</summary>
    public MediaTime EditPlayhead => Playhead.SnapToFrame(FrameRate);

    private FrameRate? _gridRate;
    public MediaTime SequenceDuration => Sequence.Duration();

    public ObservableCollection<TimelineTrackViewModel> Tracks { get; } = new();
    public ObservableCollection<TimelineRulerTickViewModel> RulerTicks { get; } = new();

    /// <summary>The sequence's markers on the ruler (D027 §6), laid out at the current zoom.</summary>
    public ObservableCollection<TimelineMarkerViewModel> Markers { get; } = new();

    [ObservableProperty] private double _pixelsPerSecond;
    [ObservableProperty] private double _contentWidth;
    [ObservableProperty] private double _tracksHeight;
    [ObservableProperty] private double _playheadX;
    [ObservableProperty] private string _frameRateDisplay = "";
    /// <summary>D069 §6: the timeline always snaps (no switch any more; tests may turn it off for exact pointer maths).</summary>
    [ObservableProperty] private bool _snappingEnabled = true;

    /// <summary>D069 §6: the main track's magnet (the tool bar's former «Привязка», N) — a change of the project, one Undo step
    /// (turning it on closes the main track's gaps).</summary>
    public bool MagneticMainTrack
    {
        get => Sequence.MagneticMainTrack;
        set
        {
            if (value != Sequence.MagneticMainTrack && CanEdit())
                Report(_edit.SetMagneticMainTrack(value), successMessage: value ? Strings.Magnet_On : Strings.Magnet_Off);
            OnPropertyChanged();
        }
    }
    [ObservableProperty] private bool _isSnapIndicatorVisible;
    [ObservableProperty] private double _snapIndicatorX;

    /// <summary>No clip on any track: the view shows how to add one (D024 Step 9.8).</summary>
    [ObservableProperty] private bool _isEmpty = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RippleDeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand))]
    [NotifyCanExecuteChangedFor(nameof(NudgeLeftCommand))]
    [NotifyCanExecuteChangedFor(nameof(NudgeRightCommand))]
    [NotifyCanExecuteChangedFor(nameof(NudgeLeftSecondCommand))]
    [NotifyCanExecuteChangedFor(nameof(NudgeRightSecondCommand))]
    private bool _hasSelection;

    /// <summary>A dissolve is selected (and no clip).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private bool _hasTransitionSelection;

    /// <summary>The selected dissolve changed (null = none selected).</summary>
    public event EventHandler<TimelineTransitionSelection?>? TransitionSelectionChanged;

    public double PlayheadHeight => RulerHeight + TracksHeight;

    /// <summary>Playhead moved or the sequence duration may have changed.</summary>
    public event EventHandler? PlayheadChanged;

    /// <summary>The user moved the playhead (click, drag, frame step, Home/End). Raised only for
    /// user actions — never for <see cref="ShowPlaybackPosition"/> or a timeline refresh — so
    /// playback can seek without feeding its own position back into a seek.</summary>
    public event EventHandler<MediaTime>? SeekRequested;

    /// <summary>Primary selected clip changed (null = no timeline selection).</summary>
    public event EventHandler<TimelineClipSelection?>? SelectionChanged;

    /// <summary>The view should scroll horizontally to this content X (after layout).</summary>
    public event EventHandler<double>? ScrollRequested;

    partial void OnTracksHeightChanged(double value) => OnPropertyChanged(nameof(PlayheadHeight));

}
