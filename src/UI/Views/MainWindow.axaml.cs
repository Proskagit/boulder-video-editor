using Avalonia.Controls;
using Avalonia.Input;
using System.Windows.Input;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
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

    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Opened += async (_, _) => await viewModel.OnWindowOpenedAsync();
    }

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
            _closeApproved = true;
            Close();
        }
        finally
        {
            _closePending = false;
        }
    }

    /// <summary>
    /// Editor shortcuts. Handled on the bubbling KeyDown at window level, i.e. only when
    /// no focused control consumed the key — and never while a text input has focus:
    /// a TextBox (including the one inside NumericUpDown) doesn't mark plain letter keys
    /// as handled on KeyDown (text arrives via TextInput), so "S" would otherwise split
    /// the timeline while the user types.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainWindowViewModel vm) return;
        if (e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox) return;

        var command = ShortcutFor(vm, e.Key, e.KeyModifiers);
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
        else if (command is not null)
        {
            e.Handled = true; // a known shortcut that just isn't available right now
        }
    }

    private static ICommand? ShortcutFor(MainWindowViewModel vm, Key key, KeyModifiers modifiers)
    {
        var ctrl = modifiers == KeyModifiers.Control;
        var ctrlShift = modifiers == (KeyModifiers.Control | KeyModifiers.Shift);
        var shift = modifiers == KeyModifiers.Shift;
        var none = modifiers == KeyModifiers.None;
        var timeline = vm.Timeline;

        return key switch
        {
            Key.N when ctrl => vm.Toolbar.NewProjectCommand,
            Key.O when ctrl => vm.Toolbar.OpenCommand,
            Key.S when ctrl => vm.Toolbar.SaveCommand,
            Key.S when ctrlShift => vm.Toolbar.SaveAsCommand,
            Key.Z when ctrl => vm.Toolbar.UndoCommand,
            Key.Y when ctrl => vm.Toolbar.RedoCommand,
            Key.Z when ctrlShift => vm.Toolbar.RedoCommand,
            Key.Delete or Key.Back when none => timeline.DeleteSelectedCommand,
            Key.S when none => timeline.SplitAtPlayheadCommand,
            Key.N when none => timeline.ToggleSnappingCommand,
            Key.Left when none => timeline.StepBackwardCommand,
            Key.Right when none => timeline.StepForwardCommand,
            Key.Left when shift => timeline.StepBackwardSecondCommand,
            Key.Right when shift => timeline.StepForwardSecondCommand,
            Key.Space when none => vm.Preview.PlayPauseCommand,
            Key.Home when none => timeline.GoToStartCommand,
            Key.End when none => timeline.GoToEndCommand,
            Key.OemPlus or Key.Add when ctrl => timeline.ZoomInCommand,
            Key.OemMinus or Key.Subtract when ctrl => timeline.ZoomOutCommand,
            _ => null
        };
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
        public Core.Entities.Project CreateNew(string name, ProjectSettings? settings = null) => Current;
        public Task<Core.Entities.Project> OpenAsync(string projectFolderPath, CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<MediaAsset> DetectMissingMedia() => Array.Empty<MediaAsset>();
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
        public TimelineEditResult AddClip(Guid mediaAssetId, Guid? trackId = null, MediaTime? start = null) => Nothing;
        public TimelineEditResult MoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null) => Nothing;
        public string? CanMoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null) => "Design time.";
        public TimelineEditResult TrimClip(Guid clipId, ClipEdge edge, MediaTime edgeTime) => Nothing;
        public (MediaTime Start, MediaTime End)? PreviewTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime) => null;
        public TimelineEditResult Split(MediaTime at, IReadOnlyCollection<Guid>? clipIds = null) => Nothing;
        public TimelineEditResult DeleteClips(IReadOnlyCollection<Guid> clipIds) => Nothing;
        public TimelineEditResult AddTrack(TrackType type) => Nothing;
        public TimelineEditResult SetClipProperties(Guid clipId, ClipPropertyChange change) => Nothing;
        public SnapResult Snap(IReadOnlyList<MediaTime> candidates, MediaTime tolerance, IReadOnlyCollection<Guid> excludedClipIds) => SnapResult.None;
    }

    private sealed class DesignTimePlaybackService : IPlaybackService
    {
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
        public PlaybackFrame Update() => new(MediaTime.Zero, 0, PlaybackState.Paused, false, PreviewPicture.Black, true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DesignTimeFilePickerService : IFilePickerService
    {
        public Task<IReadOnlyList<string>> PickFilesAsync(FilePickerRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
