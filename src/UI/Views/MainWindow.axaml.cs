using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.UI.Views;

/// <summary>
/// Code-behind is intentionally minimal: it wires up the XAML component, assigns the
/// injected view model and routes keyboard shortcuts to view-model commands. All
/// state and behavior live in <see cref="MainWindowViewModel"/> and its panels.
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

    private bool _closeApproved;
    private bool _closePending;

    public MainWindow(MainWindowViewModel viewModel) : this(viewModel, new InMemoryUiState())
    {
    }

    public MainWindow(MainWindowViewModel viewModel, IUiStateService layout)
    {
        _layout = layout;
        DataContext = viewModel;
        InitializeComponent();
        RestoreLayout();
        Opened += async (_, _) => await viewModel.OnWindowOpenedAsync();
        Activated += (_, _) => viewModel.OnWindowActivated();
        PositionChanged += (_, _) => SaveWindow();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ClientSizeProperty || e.Property == WindowStateProperty) SaveWindow();
        };
        foreach (var splitter in new[] { PanelsGrid.Children.OfType<GridSplitter>(), RootGrid.Children.OfType<GridSplitter>() }.SelectMany(s => s))
            splitter.DragCompleted += (_, _) => SavePanels();
    }

    private readonly IUiStateService _layout;

    /// <summary>D057 §3.4: the user's window and panels as they were — the window only where a screen shows it.</summary>
    private void RestoreLayout()
    {
        var state = _layout.Current;
        PanelsGrid.ColumnDefinitions[0].Width = new GridLength(WindowLayout.SidePanel(state.MediaBrowserWidth, 260));
        PanelsGrid.ColumnDefinitions[4].Width = new GridLength(WindowLayout.SidePanel(state.InspectorWidth, 300));
        RootGrid.RowDefinitions[3].Height = new GridLength(WindowLayout.Timeline(state.TimelineHeight, 240));
        if (state.Window is not { } placement) return;
        var screens = Screens.All.Select(s => s.WorkingArea).ToList();
        var scaling = Screens.Primary?.Scaling ?? 1;
        if (screens.Count > 0 && !WindowLayout.IsOnScreen(placement, screens, scaling))
        {
            if (placement.Maximized) WindowState = WindowState.Maximized;
            return;
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(placement.X, placement.Y);
        Width = WindowLayout.Window(placement.Width, WindowLayout.MinWindowWidth);
        Height = WindowLayout.Window(placement.Height, WindowLayout.MinWindowHeight);
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>The window's normal bounds are kept while it is maximised, so restoring it later gives them back.</summary>
    private void SaveWindow()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        var maximized = WindowState == WindowState.Maximized;
        _layout.Update(s =>
        {
            var old = s.Window;
            var placement = maximized && old is not null
                ? old with { Maximized = true }
                : maximized
                    ? new WindowPlacement(Position.X, Position.Y, Width, Height, true)
                    : new WindowPlacement(Position.X, Position.Y, ClientSize.Width, ClientSize.Height, false);
            return s with { Window = placement };
        });
    }

    private void SavePanels() => _layout.Update(s => s with
    {
        MediaBrowserWidth = PanelsGrid.ColumnDefinitions[0].ActualWidth,
        InspectorWidth = PanelsGrid.ColumnDefinitions[4].ActualWidth,
        TimelineHeight = RootGrid.RowDefinitions[3].ActualHeight
    });

    /// <summary>
    /// Closing is asynchronous work (the unsaved-changes prompt, then flushing autosave), but
    /// <see cref="Window.OnClosing"/> is synchronous: the first close is cancelled, the work runs,
    /// and if the view model agrees the window is closed again with approval.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeApproved || e.Cancel || DataContext is not MainWindowViewModel vm) return;

        e.Cancel = true;
        if (_closePending) return;
        _closePending = true;
        _ = CloseWhenReadyAsync(vm);
    }

    private async Task CloseWhenReadyAsync(MainWindowViewModel vm)
    {
        try
        {
            if (!await vm.PrepareToCloseAsync()) return;
            SavePanels();
            _layout.Flush();               // D057 §3.4: the layout written before the process ends
            _closeApproved = true;
            Close();
        }
        finally
        {
            _closePending = false;
        }
    }

    /// <summary>Editor shortcuts (<see cref="ShortcutRouter"/>): on the bubbling KeyDown at window level, i.e. only
    /// when no focused control consumed the key, and never while a text input has focus.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainWindowViewModel vm) return;
        if (ShortcutRouter.Handle(vm, e.Key, e.KeyModifiers, e.Source, FocusManager?.GetFocusedElement()))
            e.Handled = true;
    }

    private static MainWindowViewModel BuildDesignTimeViewModel()
    {
        var undoRedo = new UndoRedoService();
        var projectService = new DesignTimeProjectService();
        var mediaImportService = new DesignTimeMediaImportService();
        var mediaAnalysisService = new DesignTimeMediaAnalysisService();
        var filePicker = new DesignTimeFilePickerService();
        var status = new StatusService();
        var analysisCoordinator = new MediaAnalysisCoordinator(
            mediaAnalysisService, projectService, NullLogger<MediaAnalysisCoordinator>.Instance);
        var importWorkflow = new MediaImportWorkflow(
            filePicker, mediaImportService, projectService, analysisCoordinator, status,
            NullLogger<MediaImportWorkflow>.Instance);
        var projectFiles = new ProjectFileWorkflow(
            projectService, analysisCoordinator, new DesignTimeAutosaveService(), new DesignTimeDialogService(), filePicker, status,
            NullLogger<ProjectFileWorkflow>.Instance);

        return new MainWindowViewModel(
            new ToolbarViewModel(undoRedo, projectFiles, importWorkflow, status),
            new MediaBrowserViewModel(projectService, importWorkflow, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(status, new DesignTimePlaybackService(), projectService, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(new DesignTimeTimelineEditService(), status),
            new TimelineViewModel(projectService, new DesignTimeTimelineEditService(), status, NullLogger<TimelineViewModel>.Instance),
            status,
            projectFiles,
            projectService,
            NullLogger<MainWindowViewModel>.Instance);
    }

    // --- Design-time-only stubs -------------------------------------------------
    // Minimal, inert implementations of the Core service interfaces, used only by
    // the XAML previewer above. Keeping these here (rather than referencing the
    // real Media/Project/Video subsystem projects) keeps UI's dependency list to Core only.

    private sealed class DesignTimeProjectService : IProjectService
    {
        public Core.Entities.Project Current { get; } = new() { Name = "Design Time" };
        public event EventHandler? ProjectChanged { add { } remove { } }
        public event EventHandler? MediaAssetsChanged { add { } remove { } }
        public event EventHandler? TimelineChanged { add { } remove { } }
        public event EventHandler? SaveStateChanged { add { } remove { } }
        public event EventHandler? ProjectSaved { add { } remove { } }
        public Task<Core.Entities.Project> RestoreRecoveryAsync(string recoveryFilePath, CancellationToken ct = default) => Task.FromResult(Current);
        public void SetTraining(Core.Telemetry.TrainingReference? training) { }
        public Core.Entities.Project CreateNew(string name, ProjectSettings? settings = null) => Current;
        public Task<Core.Entities.Project> OpenAsync(string projectFolderPath, CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecheckMediaAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<MediaAvailabilityChangedEventArgs>? MediaAvailabilityChanged { add { } remove { } }
        public event EventHandler<MediaRelinkedEventArgs>? MediaRelinked { add { } remove { } }
        public void NotifyMediaRelinked(IReadOnlyList<MediaFileReplacement> replacements) { }
        public MediaAddResult AddMediaAssets(IEnumerable<MediaAsset> assets) => new();
        public void NotifyMediaAssetsChanged() { }
        public void NotifyTimelineChanged() { }
    }

    private sealed class DesignTimeAutosaveService : IAutosaveService
    {
        public event EventHandler<string>? AutosaveCompleted { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public Task<bool> AutosaveNowAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<RecoveryScanResult> FindRecoveryAsync(CancellationToken ct = default) => Task.FromResult(RecoveryScanResult.None);
        public Task DiscardRecoveryAsync(RecoveryCandidate candidate) => Task.CompletedTask;
        public Task DiscardRecoveryAsync(Guid projectId) => Task.CompletedTask;
        public Task ShutdownAsync(bool keepUnsavedChanges = true) => Task.CompletedTask;
    }

    private sealed class DesignTimeDialogService : IDialogService
    {
        public Task<int?> AskAsync(DialogRequest request) => Task.FromResult<int?>(null);
    }

    private sealed class DesignTimeMediaImportService : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromResult(new MediaImportBatchResult());
    }

    private sealed class DesignTimeMediaAnalysisService : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "Design time — not analyzed."));
    }

    private sealed class DesignTimeTimelineEditService : ITimelineEditService
    {
        private static readonly TimelineEditResult Nothing = TimelineEditResult.Fail("Design time.");
        public FrameRate FrameRate => FrameRate.Default;
        public string? GetAddBlockReason(MediaAsset asset) => "Design time.";
        public TimelineEditResult AddClip(Guid mediaAssetId, Guid? trackId = null, MediaTime? start = null, bool insertOnDrop = false) => Nothing;
        public TimelineEditResult AddClips(IReadOnlyList<Guid> mediaAssetIds) => Nothing;
        public TimelineEditResult AddClips(IReadOnlyList<Guid> mediaAssetIds, Guid? trackId, MediaTime? start, bool insertOnDrop) => Nothing;
        public DropPreview? PreviewAddClips(IReadOnlyList<Guid> mediaAssetIds, Guid trackId, MediaTime start, bool insertOnDrop) => null;
        public TimelineEditResult SetClipBackground(Guid clipId, ClipBackground background) => Nothing;
        public MediaTime? AddedClipDuration(Guid mediaAssetId) => null;
        public TimelineEditResult AddTextClip(MediaTime start, Func<TextProperties, (double Width, double Height)>? measureBox = null) => Nothing;
        public TimelineEditResult AddTemplateClip(TemplateKind kind, MediaTime start) => Nothing;
        public TimelineEditResult SetClipSpeed(Guid clipId, ClipSpeed speed) => Nothing;
        public TimelineEditResult LayOutIntroAndFinal(Guid videoClipId, MediaTime t, Guid? routeId = null) => Nothing;
        public OverlayStripState? OverlayStripOf(Guid videoClipId, Guid? routeId = null) => null;
        public TimelineEditResult EditOverlayStrip(Guid videoClipId, OverlayStripHandle handle, long frameDelta, Guid? routeId = null) => Nothing;
        public OverlayStripPreview? PreviewOverlayStrip(Guid videoClipId, OverlayStripHandle handle, long frameDelta, Guid? routeId = null) => null;
        public TimelineEditResult DeleteHoldMarkup(Guid videoClipId, Guid? routeId = null) => Nothing;
        public TimelineEditResult AddTransition(Guid leftClipId, Guid rightClipId, MediaTime duration) => Nothing;
        public TimelineEditResult RemoveTransition(Guid transitionId) => Nothing;
        public TimelineEditResult SetTransitionDuration(Guid transitionId, MediaTime duration) => Nothing;
        public long? MaxTransitionFrames(Guid leftClipId, Guid rightClipId) => null;
        public TimelineEditResult AddTransition(Guid leftClipId, Guid rightClipId, string typeId) => Nothing;
        public TimelineEditResult SetTransitionType(Guid transitionId, string typeId) => Nothing;
        public TimelineEditResult SetTransitionDirection(Guid transitionId, TransitionDirection direction) => Nothing;
        public long? MaxTransitionFrames(Guid leftClipId, Guid rightClipId, string typeId) => null;
        public TimelineEditResult MoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null, bool insertOnDrop = false) => Nothing;
        public string? CanMoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null, bool insertOnDrop = false) => "Design time.";
        public MovePreview? PreviewMove(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null, bool insertOnDrop = false) => null;
        public TimelineEditResult TrimClip(Guid clipId, ClipEdge edge, MediaTime edgeTime, bool alone = false) => Nothing;
        public (MediaTime Start, MediaTime End)? PreviewTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime, bool alone = false) => null;
        public TimelineEditResult Split(MediaTime at, IReadOnlyCollection<Guid>? clipIds = null) => Nothing;
        public TimelineEditResult DeleteClips(IReadOnlyCollection<Guid> clipIds) => Nothing;
        public TimelineEditResult RippleDeleteClips(IReadOnlyCollection<Guid> clipIds) => Nothing;
        public TimelineEditResult CloseGap(Guid trackId, MediaTime at) => Nothing;
        public TimelineEditResult CloseGapBefore(Guid clipId) => Nothing;
        public TimelineClipboard? CopyClips(IReadOnlyCollection<Guid> clipIds) => null;
        public TimelineEditResult PasteClips(TimelineClipboard clipboard, MediaTime at) => Nothing;
        public TimelineEditResult DuplicateClips(IReadOnlyCollection<Guid> clipIds) => Nothing;
        public TimelineEditResult AddMarker(MediaTime at) => Nothing;
        public TimelineEditResult RemoveMarkerAt(MediaTime at) => Nothing;
        public MediaTime? NextMarker(MediaTime from) => null;
        public MediaTime? PreviousMarker(MediaTime from) => null;
        public TimelineEditResult AddTrack(TrackType type) => Nothing;
        public TimelineEditResult DeleteTrack(Guid trackId) => Nothing;
        public string? GetDeleteTrackBlockReason(Guid trackId) => "Design time.";
        public TimelineEditResult MoveTrack(Guid trackId, int direction) => Nothing;
        public TimelineEditResult TrimToPlayhead(IReadOnlyCollection<Guid> clipIds, ClipEdge edge, MediaTime playhead, bool ripple) => Nothing;
        public TimelineEditResult RippleTrimClip(Guid clipId, ClipEdge edge, MediaTime edgeTime) => Nothing;
        public TimelineEditResult SlipClip(Guid clipId, long frames) => Nothing;
        public SlipPreview? PreviewSlip(Guid clipId, long frames) => null;
        public TimelineEditResult InsertClip(Guid mediaAssetId, MediaTime? sourceIn, MediaTime? sourceOut, MediaTime at, Guid? trackId = null, bool soundOnly = false) => Nothing;
        public TimelineEditResult OverwriteClip(Guid mediaAssetId, MediaTime? sourceIn, MediaTime? sourceOut, MediaTime at, Guid? trackId = null, bool soundOnly = false) => Nothing;
        public SourceGrid? GetSourceGrid(MediaAsset asset, out string? reason) { reason = null; return null; }
        public TimelineEditResult UnlinkAudio(Guid videoClipId) => Nothing;
        public string? GetUnlinkAudioBlockReason(Guid clipId) => null;
        public TimelineEditResult LinkClips(IReadOnlyCollection<Guid> clipIds) => Nothing;
        public TimelineEditResult UnlinkClips(IReadOnlyCollection<Guid> clipIds) => Nothing;
        public RippleTrimPreview? PreviewRippleTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime) => null;
        public TimelineEditResult SetTrackMuted(Guid trackId, bool muted) => Nothing;
        public TimelineEditResult SetTrackHidden(Guid trackId, bool hidden) => Nothing;
        public TimelineEditResult SetTrackLocked(Guid trackId, bool locked) => Nothing;
        public int CountClipsUsing(Guid mediaAssetId) => 0;
        public string? GetRemoveMediaBlockReason(Guid mediaAssetId) => "Design time.";
        public TimelineEditResult RemoveMedia(Guid mediaAssetId) => Nothing;
        public TimelineEditResult SetClipProperties(Guid clipId, ClipPropertyChange change) => Nothing;
        public TimelineEditResult AddHold(Guid clipId, Hold hold, Guid? routeId = null) => Nothing;
        public TimelineEditResult AddHolds(Guid clipId, IEnumerable<Hold> holds, Guid? routeId = null) => Nothing;
        public TimelineEditResult RemoveHold(Guid clipId, Guid holdId, Guid? routeId = null) => Nothing;
        public TimelineEditResult ReplaceHoldContour(Guid clipId, Guid holdId, ImmutableArray<NormalizedPoint> points, Guid? routeId = null) => Nothing;
        public TimelineEditResult SetHoldStyle(Guid clipId, HoldStyle style, Guid? routeId = null) => Nothing;
        public TimelineEditResult SetHoldAnimation(Guid clipId, HoldAnimation? animation, Guid? routeId = null) => Nothing;
        public TimelineEditResult SetHoldFollowsCamera(Guid clipId, bool follows, Guid? routeId = null) => Nothing;
        public TimelineEditResult FrameClip(Guid clipId, ClipFraming framing) => Nothing;
        public TimelineEditResult SetCanvasSize(int width, int height) => Nothing;
        public TimelineEditResult SetFrameRate(FrameRate rate) => Nothing;
        public TimelineEditResult SetProjectSettings(int width, int height, FrameRate? rate, ExportEncoding? export = null) => Nothing;
        public TimelineEditResult SetTrainingClockCorrection(MediaTime correction) => Nothing;
        public TimelineEditResult SetRecordingZeroOverride(Guid mediaAssetId, DateTimeOffset? zero) => Nothing;
        public TimelineEditResult SetExportSettings(ExportEncoding export) => Nothing;
        public SnapResult Snap(IReadOnlyList<MediaTime> candidates, MediaTime tolerance, IReadOnlyCollection<Guid> excludedClipIds,
            Func<SnapResult, bool>? accept = null) => SnapResult.None;
    }

    private sealed class DesignTimePlaybackService : IPlaybackService
    {
        public void SetPlaybackRange(PlaybackRange? range) { }
        public MediaTime PlaybackEnd => MediaTime.Zero;
        public PlaybackState State => PlaybackState.Paused;
        public bool IsBuffering => false;
        public bool IsAvailable => false;
        public bool IsAudioAvailable => false;
        public MediaTime Position => MediaTime.Zero;
        public MediaTime Duration => MediaTime.Zero;
        public event EventHandler? StateChanged { add { } remove { } }
        public void UpdateSnapshot(PlaybackSnapshot snapshot) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public Task<bool> SeekAsync(MediaTime position, CancellationToken ct = default) => Task.FromResult(true);
        public PlaybackFrame Update() => new(MediaTime.Zero, 0, PlaybackState.Paused, false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DesignTimeFilePickerService : IFilePickerService
    {
        public Task<IReadOnlyList<string>> PickFilesAsync(FilePickerRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFileAsync(SaveFilePickerRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
