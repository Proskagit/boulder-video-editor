using System.Collections.ObjectModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Top toolbar. New / Open / Save / Save As go through <see cref="ProjectFileWorkflow"/> (folder
/// picker, "save changes?" prompt, status messages); Export goes through <see cref="ExportWorkflow"/>.
/// Import here calls the exact same <see cref="MediaImportWorkflow"/> as the
/// Media Browser's own Import button, so the two stay identical with no duplicated logic.
/// The project commands are async commands: while one runs it can't be started again. While an export
/// runs (<see cref="EditingLock"/>) every command here is disabled. <see cref="Recent"/> is the <c>Recent ▾</c> drop-down
/// next to Open (D026 §6), absent when not given. "Project Settings…" (D028, Step 13.6) opens the canvas / frame-rate dialog
/// through <see cref="ProjectSettingsWorkflow"/>; its tooltip shows the current settings. «Тренировка COROS…» (Phase Coros
/// C1) imports a FIT training into the session through <see cref="TrainingImportWorkflow"/>; its tooltip shows the
/// training held, if any.
/// </summary>
public sealed partial class ToolbarViewModel : ViewModelBase
{
    private readonly IUndoRedoService _undoRedoService;
    private readonly ProjectFileWorkflow _projectFiles;
    private readonly MediaImportWorkflow _importWorkflow;
    private readonly StatusService _status;
    private readonly ExportWorkflow? _exportWorkflow;
    private readonly EditingLock _editingLock;
    private readonly ProjectSettingsWorkflow? _projectSettings;
    private readonly TrainingImportWorkflow? _trainingImport;
    private readonly TrainingSessionService? _trainingSession;
    private readonly ITimelineEditService? _edit;
    private readonly IFitMarkEditService? _fitMarks;
    private readonly FitSettingsWorkflow? _fitSettings;

    /// <param name="exportWorkflow">Without it Export reports that exporting is unavailable.</param>
    /// <param name="editingLock">The app's shared lock; a private one when not given.</param>
    /// <param name="recentProjects">The <c>Recent ▾</c> drop-down; without it the button is hidden.</param>
    /// <param name="projectSettings">Project Settings…; without it the command reports that it is unavailable.</param>
    /// <param name="trainingImport">«Тренировка COROS…»; without it the command is disabled.</param>
    /// <param name="trainingSession">The project's training, for the tooltip, the menu and the clock correction.</param>
    /// <param name="edit">Edits the training's clock correction (one Undo step); without it the field is read-only.</param>
    /// <param name="fitMarks">«Вернуть» of the declined FIT climbs (D051 §5.7); without it the list is not shown.</param>
    /// <param name="fitSettings">«Настройки FIT…» (D051 §8.3); without it the item is hidden.</param>
    public ToolbarViewModel(
        IUndoRedoService undoRedoService,
        ProjectFileWorkflow projectFiles,
        MediaImportWorkflow importWorkflow,
        StatusService status,
        ExportWorkflow? exportWorkflow = null,
        EditingLock? editingLock = null,
        RecentProjectsViewModel? recentProjects = null,
        ProjectSettingsWorkflow? projectSettings = null,
        TrainingImportWorkflow? trainingImport = null,
        TrainingSessionService? trainingSession = null,
        ITimelineEditService? edit = null,
        IFitMarkEditService? fitMarks = null,
        FitSettingsWorkflow? fitSettings = null,
        YouTubeTimestampsWorkflow? youTube = null)
    {
        _youTube = youTube;
        _fitMarks = fitMarks;
        _fitSettings = fitSettings;
        _trainingImport = trainingImport;
        _trainingSession = trainingSession;
        _edit = edit;
        if (_trainingSession is not null)
            _trainingSession.Changed += (_, _) => OnTrainingChanged();
        _projectSettings = projectSettings;
        if (_projectSettings is not null)
            _projectSettings.SummaryChanged += (_, _) => OnPropertyChanged(nameof(ProjectSettingsToolTip));
        Recent = recentProjects;
        _undoRedoService = undoRedoService;
        _projectFiles = projectFiles;
        _importWorkflow = importWorkflow;
        _status = status;
        _exportWorkflow = exportWorkflow;
        _editingLock = editingLock ?? new EditingLock();
        _undoRedoService.StateChanged += OnUndoRedoStateChanged;
        _editingLock.PropertyChanged += (_, _) => NotifyAllCommands();
        if (_exportWorkflow is not null)
            _exportWorkflow.IsRunningChanged += (_, _) => ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The <c>Recent ▾</c> drop-down, or null.</summary>
    public RecentProjectsViewModel? Recent { get; }

    private bool CanEdit() => !_editingLock.IsLocked;

    /// <summary>"Project Settings — 1920 × 1080 · 25 FPS".</summary>
    public string ProjectSettingsToolTip => _projectSettings is null
        ? Strings.Settings_Title
        : string.Format(Strings.Toolbar_SettingsTip, _projectSettings.Summary);

    /// <summary>What «Тренировка COROS…» does, or — once a training is held — its summary and file.</summary>
    public string TrainingToolTip => _trainingSession?.Training is not { } training
        ? Strings.Toolbar_TrainingTip
        : string.Format(Strings.Toolbar_TrainingLoadedTip,
            training.Workout is { } workout ? TrainingText.Summary(workout) : Path.GetFileName(training.FilePath),
            Path.GetFileName(training.FilePath), TrainingText.State(training.Availability));

    /// <summary>A training is attached to the project (the menu's «Удалить тренировку» and the clock correction).</summary>
    public bool HasTraining => _trainingSession?.Training is not null;

    /// <summary>The training's clock correction W − C in whole frames of the project (D049 §2; D050 amendment B3: a setting
    /// of the training, not a video's recording start). Setting it is one Undo step.</summary>
    public decimal? TrainingClockCorrectionFrames
    {
        get => _trainingSession?.Training is { } t ? t.ClockCorrection.ToNearestFrame(_trainingSession.ProjectFrameRate) : null;
        set
        {
            if (value is not { } frames || _edit is null || _trainingSession?.Training is null || !CanEdit()) return;
            var result = _edit.SetTrainingClockCorrection(MediaTime.FromFrame((long)decimal.Round(frames), _trainingSession.ProjectFrameRate));
            if (!result.Success && result.Message is { } message) _status.Report(message);
            OnPropertyChanged();
        }
    }

    private void NotifyAllCommands()
    {
        NewProjectCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        ImportMediaCommand.NotifyCanExecuteChanged();
        ImportTrainingCommand.NotifyCanExecuteChanged();
        RelinkTrainingCommand.NotifyCanExecuteChanged();
        RemoveTrainingCommand.NotifyCanExecuteChanged();
        ProjectSettingsCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        FitSettingsCommand.NotifyCanExecuteChanged();
        AppSettingsCommand.NotifyCanExecuteChanged();
        foreach (var item in DeclinedClimbs) ((RelayCommand)item.Restore).NotifyCanExecuteChanged();
    }

    /// <summary>«Отклонённые подъёмы» (D051 §5.7): each with «Вернуть», one Undo step.</summary>
    public ObservableCollection<DeclinedClimbItem> DeclinedClimbs { get; } = new();

    public bool HasDeclinedClimbs => DeclinedClimbs.Count > 0;

    /// <summary>«Настройки FIT…» is offered (with or without a training — D051 §8.3).</summary>
    public bool HasFitSettings => _fitSettings is not null;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task FitSettings() => _fitSettings?.RunAsync() ?? Task.CompletedTask;

    private readonly YouTubeTimestampsWorkflow? _youTube;

    /// <summary>D057 §3.9: «Таймкоды YouTube…».</summary>
    public bool HasYouTubeTimestamps => _youTube is not null;

    [RelayCommand]
    private Task YouTubeTimestamps() => _youTube?.RunAsync() ?? Task.CompletedTask;

    /// <summary>D057 §3.8: «Настройки…» — the application's settings window.</summary>
    public bool HasAppSettings => _fitSettings?.HasAppSettings == true;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task AppSettings() => _fitSettings?.RunAsync(AppSettingsSection.General) ?? Task.CompletedTask;

    private void RefreshDeclinedClimbs()
    {
        var training = _trainingSession?.Training;
        var declined = _fitMarks is null ? [] : training?.DeclinedClimbs.ToList() ?? [];
        if (declined.Select(d => d.Climb).SequenceEqual(DeclinedClimbs.Select(i => i.Climb))) return;
        DeclinedClimbs.Clear();
        var workout = training?.Workout;
        foreach (var entry in declined)
        {
            var start = FitMarkRules.ClimbOf(workout, entry.Climb)?.StartUtc ?? entry.Climb.ClimbStartUtc;
            var climb = entry.Climb;
            DeclinedClimbs.Add(new DeclinedClimbItem(climb,
                string.Format(Strings.Toolbar_DeclinedClimb, climb.ClimbNumber, TrainingText.WatchClock(start, workout?.UtcOffset)),
                new RelayCommand(() => Restore(climb), CanEdit)));
        }
        OnPropertyChanged(nameof(HasDeclinedClimbs));
    }

    private void Restore(ClimbIdentity climb)
    {
        if (_fitMarks is null || !CanEdit()) return;
        var result = _fitMarks.RestoreClimb(climb);
        _status.Report(result.Success ? Strings.Toolbar_Restored : result.Message ?? "");
    }

    private void OnTrainingChanged()
    {
        RefreshDeclinedClimbs();
        OnPropertyChanged(nameof(TrainingToolTip));
        OnPropertyChanged(nameof(HasTraining));
        OnPropertyChanged(nameof(TrainingClockCorrectionFrames));
        RelinkTrainingCommand.NotifyCanExecuteChanged();
        RemoveTrainingCommand.NotifyCanExecuteChanged();
    }

    private void OnUndoRedoStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TrainingClockCorrectionFrames));   // an Undo / Redo of the clock correction
        RefreshDeclinedClimbs();                                    // a decline, «Вернуть» or their Undo
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task NewProject() => _projectFiles.NewProjectAsync();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task Open() => _projectFiles.OpenProjectAsync();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task Save() => _projectFiles.SaveAsync();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task SaveAs() => _projectFiles.SaveAsAsync();

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _undoRedoService.Undo();

    private bool CanUndo() => CanEdit() && _undoRedoService.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _undoRedoService.Redo();

    private bool CanRedo() => CanEdit() && _undoRedoService.CanRedo;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task ImportMedia() => _importWorkflow.RunAsync();

    /// <summary>«Тренировка COROS…» (Phase Coros C1): a FIT training into the session, not into the project.</summary>
    [RelayCommand(CanExecute = nameof(CanImportTraining))]
    private Task ImportTraining() => _trainingImport!.RunAsync();

    private bool CanImportTraining() => CanEdit() && _trainingImport is not null;

    /// <summary>«Указать файл…»: only while the training's file is not usable (D049 §9).</summary>
    [RelayCommand(CanExecute = nameof(CanRelinkTraining))]
    private Task RelinkTraining() => _trainingImport!.RelinkAsync();

    private bool CanRelinkTraining() => CanImportTraining() && _trainingSession?.Training is { IsUsable: false };

    /// <summary>«Удалить тренировку»: the training and its clock correction; the videos' recording starts stay.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveTraining))]
    private void RemoveTraining() => _trainingImport!.Remove();

    private bool CanRemoveTraining() => CanImportTraining() && HasTraining;

    /// <summary>Project Settings… (D028, Step 13.6): the canvas size and the frame rate; disabled during an export.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ProjectSettings()
    {
        if (_projectSettings is null)
        {
            _status.Report(Strings.Toolbar_SettingsUnavailable);
            return;
        }
        await _projectSettings.RunAsync();
    }

    /// <summary>Export (Phase 8 Step 7): preflight, output file, progress window — see <see cref="ExportWorkflow"/>.
    /// Not available while an export runs or unwinds.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        if (_exportWorkflow is null)
        {
            _status.Report(Strings.Toolbar_ExportUnavailable);
            return;
        }
        await _exportWorkflow.RunAsync();
    }

    private bool CanExport() => CanEdit() && _exportWorkflow is not { IsRunning: true };
}

/// <summary>A declined FIT climb in the COROS menu with its «Вернуть».</summary>
public sealed record DeclinedClimbItem(ClimbIdentity Climb, string Label, IRelayCommand Restore);
