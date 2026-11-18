using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.ViewModels;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>What the last <see cref="ExportWorkflow.RunAsync"/> ended with.</summary>
public enum ExportOutcomeKind
{
    /// <summary>Stopped before anything was exported, by the user (warnings, file picker, replace question) or
    /// because an export is already running.</summary>
    NotStarted,
    /// <summary>The preflight found errors; nothing was encoded.</summary>
    PreflightFailed,
    Succeeded,
    Cancelled,
    /// <summary>The export failed (<see cref="ExportOutcome.Failure"/>, or null for an unexpected error).</summary>
    Failed
}

public sealed record ExportOutcome(ExportOutcomeKind Kind, string Message, string? OutputPath = null, ExportFailure? Failure = null,
    TimeSpan? Elapsed = null);

/// <summary>
/// The UI side of an export (Phase 8 Step 7, D023), in the style of <see cref="ProjectFileWorkflow"/>:
/// <list type="number">
/// <item>The media files are checked again (<see cref="IProjectService.RecheckMediaAsync"/>, D026 §2), then
/// <see cref="ExportPreflight"/> on the current project (the only source of the rules). Its output-file
/// issues are left for the second check, after the file is chosen; other errors are listed and stop here; warnings
/// are listed and the user may continue.</item>
/// <item>The output <c>.mp4</c> from the export dialog (<see cref="IExportDialog"/>: folder, name — «Обзор…» opens the
/// save-file picker —, the output's line, the YouTube timestamps to copy), starting at <c>LastExportSettings.OutputPath</c>;
/// without a dialog the save-file picker alone. Cancelling ends the flow silently.</item>
/// <item>The preflight again with that file — it builds the <see cref="ExportJob"/> and its snapshot, which the
/// export alone uses from then on. An existing file is replaced only after the user confirms it (the encoder
/// replaces it atomically, on success only).</item>
/// <item><see cref="IExportService.ExportAsync"/> in the background (D060 §5): the detailed window is not modal and can be
/// hidden into the main window's compact indicator; the editor stays fully usable — the job's snapshot is the export's own
/// independent copy of the project (D060 §2), so edits, saves and even another project never reach it. Cancel only cancels
/// the token; <see cref="CancelAndWaitAsync"/> also waits for the cleanup (closing the app).</item>
/// <item>The result: success (path, <c>LastExportSettings.OutputPath</c> updated without making the project dirty),
/// cancellation (a status message, not an error), or a failure by its <see cref="ExportFailure"/> — anything else
/// (e.g. from the rasterizer) as an unexpected error, logged with the exception.</item>
/// </list>
/// D060 §2: before anything else, a project with unsaved changes is saved — «Сохранить и экспортировать» or «Отмена» —, so
/// what is exported is exactly what the editor shows and the project file holds.
/// </summary>
public sealed class ExportWorkflow
{
    private readonly IProjectService _projects;
    private readonly IExportService _export;
    private readonly IFontCatalog _fonts;
    private readonly IFilePickerService _picker;
    private readonly IDialogService _dialogs;
    private readonly IExportProgressDialog _progressDialog;
    private readonly EditingLock _editingLock;
    private readonly StatusService _status;
    private readonly ILogger<ExportWorkflow> _logger;
    private readonly InOutRangeService? _inOut;
    private readonly CameraMotionCoordinator? _cameraMotion;

    /// <summary>D061: the Preview's own people analyses (Follow, Occlusion), so the export draws what the Preview draws.</summary>
    private readonly PeopleAnalysisCoordinator? _people;
    private readonly AudioProcessingCoordinator? _audio;
    private bool _running;
    private readonly Func<Task<bool>>? _saveProject;
    private CancellationTokenSource? _exportCts;
    private Task? _exportTask;
    private bool _closingApp;

    private readonly IUserSettingsService? _settings;
    private readonly IExportDialog? _exportDialog;
    private readonly IExportEncoderCapabilities? _encoders;
    private readonly ITimelineEditService? _edit;

    public ExportWorkflow(
        IProjectService projects,
        IExportService export,
        IFontCatalog fonts,
        IFilePickerService picker,
        IDialogService dialogs,
        IExportProgressDialog progressDialog,
        EditingLock editingLock,
        StatusService status,
        ILogger<ExportWorkflow> logger,
        InOutRangeService? inOut = null,
        CameraMotionCoordinator? cameraMotion = null,
        IUserSettingsService? settings = null,
        IExportDialog? exportDialog = null,
        IExportEncoderCapabilities? encoders = null,
        ITimelineEditService? edit = null,
        ProjectFileWorkflow? projectFiles = null,
        Func<Task<bool>>? saveProject = null,
        PeopleAnalysisCoordinator? people = null, AudioProcessingCoordinator? audio = null)
    {
        _people = people;
        _audio = audio;
        // Save as the toolbar's Save does it (Save As for a project never saved); a test may give its own.
        _saveProject = saveProject ?? (projectFiles is null ? null : projectFiles.SaveAsync);
        _exportDialog = exportDialog;
        _encoders = encoders;
        _edit = edit;
        _settings = settings;
        _inOut = inOut;
        _cameraMotion = cameraMotion;
        _projects = projects;
        _export = export;
        _fonts = fonts;
        _picker = picker;
        _dialogs = dialogs;
        _progressDialog = progressDialog;
        _editingLock = editingLock;
        _status = status;
        _logger = logger;
    }

    /// <summary>True from the start of <see cref="RunAsync"/> until it returns (dialogs included).</summary>
    public bool IsRunning => _running;

    /// <summary>The progress of the export currently running (null otherwise).</summary>
    public ExportProgressViewModel? Progress
    {
        get => _progress;
        private set
        {
            _progress = value;
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private ExportProgressViewModel? _progress;

    /// <summary>True while a job is being encoded (from the start of the export until it has finished, cleanup included).</summary>
    public bool IsExporting => _progress is not null;

    public event EventHandler? IsRunningChanged;

    /// <summary>Raised when an export starts or has finished (<see cref="Progress"/> set or cleared).</summary>
    public event EventHandler? ProgressChanged;

    /// <summary>Shows the detailed window of the running export (D060 §5); nothing without one.</summary>
    public void ShowDetails()
    {
        if (IsExporting) _progressDialog.Activate();
    }

    /// <summary>Cancels the running export and waits until it has ended — ffmpeg ended, temporary files deleted, an existing
    /// output file untouched (closing the app, D060 §5). Returns at once without one.</summary>
    public async Task CancelAndWaitAsync()
    {
        var task = _exportTask;
        if (task is null) return;
        _closingApp = true;     // the app is closing: no result dialog for this export
        if (Progress is { } progress) progress.Cancel();
        else _exportCts?.Cancel();
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The cancelled export ended with an error.");
        }
    }

    public async Task<ExportOutcome> RunAsync()
    {
        if (_running || _editingLock.IsLocked)
            return new ExportOutcome(ExportOutcomeKind.NotStarted, Strings.Export_AlreadyRunning);

        SetRunning(true);
        try
        {
            return await RunCoreAsync();
        }
        finally
        {
            SetRunning(false);
        }
    }

    private void SetRunning(bool value)
    {
        _running = value;
        IsRunningChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<ExportOutcome> RunCoreAsync()
    {
        // The files must be there now, whatever the last background check found (D026 §2): a file gone since then is
        // offline for the preflight, one that came back is online again.
        await _projects.RecheckMediaAsync();

        var project = _projects.Current;
        if (project is null)
            return Report(new ExportOutcome(ExportOutcomeKind.NotStarted, Strings.Export_NoProject));

        // D060 §2: unsaved changes are saved first, so the export is exactly the saved project the editor shows.
        if (!await SaveUnsavedChangesAsync(project))
            return Report(new ExportOutcome(ExportOutcomeKind.NotStarted, Strings.Export_Cancelled));

        // 0. With an In / Out range: the range or the entire sequence (D030 §8, Q10). The range is the session's — the
        //    export copies it now; later changes of it don't reach this export.
        ExportRange? range = null;
        if (_inOut?.Frames is { } frames)
        {
            var rate = project.Settings.FrameRate;
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = Strings.Export_Title,
                Message = string.Format(Strings.Export_RangeQuestion,
                    TimeFormat.ToTimecode(MediaTime.FromFrame(frames.First, rate), rate),
                    TimeFormat.ToTimecode(MediaTime.FromFrame(frames.End, rate), rate),
                    Plural.Format(frames.End - frames.First, Strings.Plural_Frames)),
                Buttons = new[] { Strings.Export_Range, Strings.Export_EntireSequence, Strings.Common_Cancel }
            });
            if (choice == 0) range = new ExportRange(frames.First, frames.End);
            else if (choice != 1) return Report(new ExportOutcome(ExportOutcomeKind.NotStarted, Strings.Export_Cancelled));
        }

        // Hold tracking: the Preview's own camera-motion tracks, so the export draws the contours where the Preview does.
        var environment = ExportPreflightEnvironment.Default(await _export.IsAvailableAsync(), IsFontInstalled) with
        {
            CameraMotion = _cameraMotion?.ForExport() ?? ExportCameraMotion.None,
            HeartRateDisplayOffset = _settings?.Current.HeartRateDisplayOffsetFor(project.Training?.Activity.Manufacturer) ?? MediaTime.Zero,
            People = _people?.ForExport() ?? ExportPeople.None,
            Audio = _audio?.ForExport() ?? Core.Audio.ExportAudioAnalyses.None
        };

        // 1. Everything but the output file, which is chosen next.
        var check = ExportPreflight.Check(project, null, environment, range);
        var errors = check.Errors.Where(i => !IsAboutOutputFile(i.Kind)).ToList();
        var warnings = check.Warnings.ToList();
        if (errors.Count > 0)
            return await PreflightFailedAsync(errors, warnings);
        if (warnings.Count > 0 && !await ContinueDespiteWarningsAsync(warnings))
            return Report(new ExportOutcome(ExportOutcomeKind.NotStarted, Strings.Export_Cancelled));

        // 2. The output file: the export dialog (folder, name, the output's line, the YouTube timestamps), or without one the
        //    save-file picker alone.
        var chosen = await ChooseOutputAsync(project, range);
        if (chosen is null)
            return new ExportOutcome(ExportOutcomeKind.NotStarted, Strings.Export_NoOutputFile);
        var outputPath = chosen.OutputPath;

        // D059 §4: export settings changed in the dialog are the project's from now on (one undoable step, as in the project
        // settings dialog); the job takes them either way.
        var encoding = Core.Export.ExportResolutions.Normalize(chosen.Encoding ?? project.Settings.Export,
            new Core.Composition.FrameSize(project.Settings.FrameWidth, project.Settings.FrameHeight));
        if (encoding != project.Settings.Export && _edit is not null) _edit.SetExportSettings(encoding);

        // 3. The job: the preflight again, now with the file (it takes the snapshot the export uses).
        check = ExportPreflight.Check(project, outputPath, environment, range);
        if (check.Job is not { } checkedJob)
            return await PreflightFailedAsync(check.Errors.ToList(), check.Warnings.ToList());
        var job = checkedJob.Encoding == encoding ? checkedJob : new ExportJob(checkedJob.Snapshot, checkedJob.OutputPath, encoding, checkedJob.Range);

        if (File.Exists(job.OutputPath) && !await ConfirmReplaceAsync(job.OutputPath))
            return Report(new ExportOutcome(ExportOutcomeKind.NotStarted, Strings.Export_Cancelled));

        // 4. The export — in the background; the job is the project as it is now (D060 §2).
        var snapshotText = string.Format(Strings.Export_SnapshotLine, project.Name, DateTime.Now.ToString("HH:mm:ss"),
            project.IsDirty ? Strings.Export_SnapshotExportSettingsChanged : Strings.Export_SnapshotSaved,
            job.Output.Size.Width, job.Output.Size.Height);
        // The names a failure message uses, taken now: the project may be edited or replaced while the export runs.
        var names = ExportNames.Of(project);
        var running = ExportAsync(job, names, snapshotText);
        _exportTask = running;
        ExportOutcome outcome;
        try
        {
            outcome = await running;
        }
        finally
        {
            _exportTask = null;
        }
        if (outcome.Kind == ExportOutcomeKind.Succeeded)
            project.LastExportSettings.OutputPath = job.OutputPath; // session state: never dirty, never undoable (D023)
        Report(outcome);
        if (!_closingApp) await ShowOutcomeAsync(outcome);
        _closingApp = false;
        return outcome;
    }

    private async Task<ExportOutcome> ExportAsync(ExportJob job, ExportNames names, string snapshotText)
    {
        using var cts = new CancellationTokenSource();
        _exportCts = cts;
        // D060 §5: no editing lock — the editor stays usable; the job never reads the live project. A cancel that arrives
        // after the export ended (the indicator's question still open) does nothing.
        var progress = new ExportProgressViewModel(job.OutputPath, () =>
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }) { SnapshotText = snapshotText };
        Progress = progress;
        _progressDialog.Show(progress);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _export.ExportAsync(job, progress, cts.Token);
            var message = progress.Fallback is { } fallback
                ? string.Format(Strings.Export_SucceededFallback, job.OutputPath, fallback)
                : string.Format(Strings.Export_Succeeded, job.OutputPath);
            return new ExportOutcome(ExportOutcomeKind.Succeeded, message, job.OutputPath, Elapsed: clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            var message = File.Exists(job.OutputPath) ? Strings.Export_CancelledKept : Strings.Export_Cancelled;
            return new ExportOutcome(ExportOutcomeKind.Cancelled, message, job.OutputPath);
        }
        catch (ExportException ex)
        {
            _logger.LogWarning(ex, "Export to {Path} failed ({Failure}).", job.OutputPath, ex.Failure);
            return new ExportOutcome(ExportOutcomeKind.Failed, FailureMessage(ex, names), job.OutputPath, ex.Failure);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export to {Path} failed unexpectedly.", job.OutputPath);
            return new ExportOutcome(ExportOutcomeKind.Failed,
                string.Format(Strings.Export_Unexpected, ex.GetType().Name, ex.Message), job.OutputPath);
        }
        finally
        {
            // Only now — the export has finished, also after a cancel — the window and the indicator go.
            _progressDialog.Close();
            Progress = null;
            _exportCts = null;
        }
    }

    /// <summary>D060 §2: a project with unsaved changes — «Сохранить и экспортировать» saves it, «Отмена» stops; true when there
    /// is nothing unsaved now. Edited again while saving: asked again. D068 Р1: a project never saved (no folder) is exported
    /// without the question — the job is the export's own snapshot (D060 §2), so nothing needs the project's file.</summary>
    private async Task<bool> SaveUnsavedChangesAsync(Core.Entities.Project project)
    {
        if (project.ProjectFolderPath is null) return true;
        while (_projects.Current is { IsDirty: true } current && ReferenceEquals(current, project))
        {
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = Strings.Export_UnsavedTitle,
                Message = string.Format(Strings.Export_UnsavedQuestion, project.Name),
                Buttons = new[] { Strings.Export_SaveAndExport, Strings.Common_Cancel }
            });
            if (choice != 0) return false;
            if (_saveProject is null) return false;     // no way to save here: nothing is exported unsaved
            if (!await _saveProject()) return false;
        }
        return ReferenceEquals(_projects.Current, project);
    }

    // The exception's own message (ffmpeg's detail) stays as it is, in parentheses (D039 §2).
    public static string FailureMessage(ExportException ex) => string.Format(Strings.Export_NoFileWritten, string.Format(ex.Failure switch
    {
        ExportFailure.EncoderUnavailable => Strings.Export_FailEncoderUnavailable,
        ExportFailure.DecodeFailed => Strings.Export_FailDecode,
        ExportFailure.EncodeFailed => Strings.Export_FailEncode,
        ExportFailure.OutputFailed => Strings.Export_FailOutput,
        ExportFailure.RenderFailed => Strings.Export_FailRender,
        ExportFailure.TransitionSourceMissing => Strings.Export_FailTransitionSource,
        _ => Strings.Export_Fail
    }, ex.Message));

    /// <summary>
    /// <see cref="FailureMessage(ExportException)"/>, and for a transition that lacks source frames (D025 §4, D044) the
    /// transition by its type, its two clips, its track and its cut — before rendering "not started", while rendering
    /// "stopped"; either way no file was written and an existing one was left unchanged.
    /// </summary>
    public static string FailureMessage(ExportException ex, Core.Entities.Project project) => FailureMessage(ex, ExportNames.Of(project));

    /// <summary>As <see cref="FailureMessage(ExportException, Core.Entities.Project)"/> with the names taken when the export
    /// started (D060 §2: the project may have changed since).</summary>
    public static string FailureMessage(ExportException ex, ExportNames names)
    {
        if (ex.MissingSource is not { } missing) return FailureMessage(ex);
        return string.Format(Strings.Export_NoFileWritten, string.Format(
            missing.BeforeRendering ? Strings.Export_FailTransitionSourceBefore : Strings.Export_FailTransitionSourceDuring,
            ViewModels.Panels.TransitionNames.Name(missing.TypeId), names.Clip(missing.LeftClipId), names.Clip(missing.RightClipId),
            names.Track(missing.TrackId), TimeFormat.ToTimecode(missing.Cut, names.FrameRate)));
    }

    private static bool IsAboutOutputFile(ExportIssueKind kind) =>
        kind is ExportIssueKind.InvalidOutputPath or ExportIssueKind.OutputFolderMissing or ExportIssueKind.OutputIsProjectMedia;

    private bool IsFontInstalled(string family) =>
        _fonts.FamilyNames.Contains(family, StringComparer.OrdinalIgnoreCase);

    private async Task<ExportDialogResult?> ChooseOutputAsync(Core.Entities.Project project, ExportRange? range)
    {
        var request = OutputRequest(project, DateTime.Now);
        if (_exportDialog is null)
            return await _picker.PickSaveFileAsync(request) is { } picked ? new ExportDialogResult(picked) : null;

        var output = ExportOutput.For(Core.Playback.PlaybackSnapshotBuilder.Build(project, 0), range);
        var suggested = Path.Combine(request.StartFolder ?? "", request.SuggestedFileName ?? project.Name + ExportFormat.FileExtension);
        var dialog = new ExportDialogViewModel(project.Timeline, project.Settings.FrameRate, output, suggested, range,
            (folder, name) => _picker.PickSaveFileAsync(new SaveFilePickerRequest
            {
                Title = request.Title, DefaultExtension = request.DefaultExtension, FileTypeFilters = request.FileTypeFilters,
                StartFolder = string.IsNullOrWhiteSpace(folder) ? request.StartFolder : folder,
                SuggestedFileName = string.IsNullOrWhiteSpace(name) ? request.SuggestedFileName : name
            }), project.Settings.Export, await EncoderAvailability.GetAsync(_encoders));
        return await _exportDialog.ShowAsync(dialog);
    }

    /// <summary>D067 §4: the name an export suggests — the local date and time it is chosen at, <c>yyyyMMdd_HHmm</c>.</summary>
    public static string SuggestedFileName(DateTime now) =>
        now.ToString("yyyyMMdd_HHmm", System.Globalization.CultureInfo.InvariantCulture) + ExportFormat.FileExtension;

    /// <summary>The picker's start: the folder of the last export (else the project's), and always a new name by the time
    /// (D067 §4) — never the last file's name, which a second export would replace.</summary>
    public static SaveFilePickerRequest OutputRequest(Core.Entities.Project project, DateTime now)
    {
        var last = project.LastExportSettings.OutputPath;
        string? folder = null;
        var name = SuggestedFileName(now);
        if (!string.IsNullOrWhiteSpace(last))
        {
            try
            {
                folder = Path.GetDirectoryName(last);
            }
            catch (ArgumentException)
            {
                // an unusable remembered path is simply not suggested
            }
        }
        return new SaveFilePickerRequest
        {
            Title = Strings.Export_PickerTitle,
            // D068 Р1: a project never saved exports to the user's Videos.
            StartFolder = folder ?? project.ProjectFolderPath ?? VideosFolder(),
            SuggestedFileName = name,
            DefaultExtension = ExportFormat.FileExtension.TrimStart('.'),
            FileTypeFilters = new[] { new FilePickerFileTypeFilter { Name = Strings.Export_Mp4Filter, Patterns = new[] { "*" + ExportFormat.FileExtension } } }
        };
    }

    /// <summary>D068 item 13: shows a file in its folder (the app sets <see cref="FileReveal.Select"/>; null: the button does
    /// nothing, as in the tests).</summary>
    public Action<string>? RevealFile { get; set; }

    /// <summary>«1 мин 12 с» / «45 с».</summary>
    public static string Duration(TimeSpan took)
    {
        var seconds = (long)Math.Round(took.TotalSeconds);
        return seconds >= 60
            ? string.Format(Strings.Duration_MinSec, seconds / 60, seconds % 60)
            : string.Format(Strings.Duration_Sec, Math.Max(seconds, 1));
    }

    private static string? VideosFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return Directory.Exists(videos) ? videos : null;
    }

    private async Task<ExportOutcome> PreflightFailedAsync(IReadOnlyList<ExportIssue> errors, IReadOnlyList<ExportIssue> warnings)
    {
        var message = new StringBuilder(Strings.Export_CantExport).Append('\n');
        AppendIssues(message, Strings.Export_Errors, errors);
        if (warnings.Count > 0) AppendIssues(message.Append('\n'), Strings.Export_Warnings, warnings);
        await _dialogs.AskAsync(new DialogRequest { Title = Strings.Export_NotPossibleTitle, Message = message.ToString(), Buttons = new[] { Strings.Common_OK } });
        var outcome = new ExportOutcome(ExportOutcomeKind.PreflightFailed,
            errors.Count == 1 ? string.Format(Strings.Export_NotPossibleOne, errors[0].Message) : Plural.Format(errors.Count, Strings.Export_NotPossibleMany));
        return Report(outcome);
    }

    private async Task<bool> ContinueDespiteWarningsAsync(IReadOnlyList<ExportIssue> warnings)
    {
        var message = new StringBuilder(Strings.Export_CanRunButNote).Append('\n');
        AppendIssues(message, Strings.Export_Warnings, warnings);
        message.Append('\n').Append(Strings.Export_ContinueQuestion);
        var choice = await _dialogs.AskAsync(new DialogRequest { Title = Strings.Export_WarningsTitle, Message = message.ToString(), Buttons = new[] { Strings.Common_Continue, Strings.Common_Cancel } });
        return choice == 0;
    }

    private async Task<bool> ConfirmReplaceAsync(string path)
    {
        var choice = await _dialogs.AskAsync(new DialogRequest
        {
            Title = Strings.Export_ReplaceTitle,
            Message = string.Format(Strings.Export_ReplaceQuestion, path),
            Buttons = new[] { Strings.Common_Replace, Strings.Common_Cancel }
        });
        return choice == 0;
    }

    private static void AppendIssues(StringBuilder message, string heading, IEnumerable<ExportIssue> issues)
    {
        message.Append('\n').Append(heading).Append(":\n");
        foreach (var issue in issues)
            message.Append("  • ").Append(issue.Message).Append('\n');
    }

    private async Task ShowOutcomeAsync(ExportOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case ExportOutcomeKind.Succeeded:
                // D068 item 13: how long it took, and the file shown in its folder on request.
                var finished = outcome.Elapsed is { } took
                    ? string.Format(Strings.Export_FinishedIn, Duration(took), outcome.OutputPath)
                    : string.Format(Strings.Export_Finished, outcome.OutputPath);
                var shown = await _dialogs.AskAsync(new DialogRequest
                {
                    Title = Strings.Export_FinishedTitle, Message = finished, Buttons = new[] { Strings.Export_ShowInFolder, Strings.Common_OK }
                });
                if (shown == 0 && outcome.OutputPath is { } path) RevealFile?.Invoke(path);
                break;
            case ExportOutcomeKind.Failed:
                await _dialogs.AskAsync(new DialogRequest { Title = Strings.Export_FailedTitle, Message = outcome.Message, Buttons = new[] { Strings.Common_OK } });
                break;
            // Cancelled: the status message is enough — cancelling is not an error.
        }
    }

    private ExportOutcome Report(ExportOutcome outcome)
    {
        // D068 item 4: a failed export is an error; a refused preflight a warning; the rest ordinary messages.
        switch (outcome.Kind)
        {
            case ExportOutcomeKind.Failed: _status.ReportError(outcome.Message); break;
            case ExportOutcomeKind.PreflightFailed: _status.ReportWarning(outcome.Message); break;
            default: _status.Report(outcome.Message); break;
        }
        return outcome;
    }
}

/// <summary>The names of the video clips and tracks of a project at one moment (D060 §2), for the failure messages of an export
/// that runs while the project is edited: a clip by its media's file name or its text's first line, a track by its name.</summary>
public sealed class ExportNames
{
    private readonly Dictionary<Guid, string> _clips;
    private readonly Dictionary<Guid, string> _tracks;

    private ExportNames(Dictionary<Guid, string> clips, Dictionary<Guid, string> tracks, Core.Common.FrameRate rate)
    {
        _clips = clips;
        _tracks = tracks;
        FrameRate = rate;
    }

    public Core.Common.FrameRate FrameRate { get; }

    public string Clip(Guid id) => _clips.GetValueOrDefault(id, "?");

    public string Track(Guid id) => _tracks.GetValueOrDefault(id, "?");

    public static ExportNames Of(Core.Entities.Project project)
    {
        var assets = project.MediaAssets.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().FileName);
        var tracks = project.Timeline.VideoTracks;
        var clips = new Dictionary<Guid, string>();
        foreach (var clip in tracks.SelectMany(t => t.Clips))
            clips[clip.Id] = clip switch
            {
                Core.Entities.MediaBackedClip media => assets.GetValueOrDefault(media.MediaAssetId, "?"),
                Core.Entities.TextClip text => text.Text.Split('\n')[0].Trim(),
                _ => clip.GetType().Name
            };
        return new ExportNames(clips, tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Name), project.Settings.FrameRate);
    }
}
