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

public sealed record ExportOutcome(ExportOutcomeKind Kind, string Message, string? OutputPath = null, ExportFailure? Failure = null);

/// <summary>
/// The UI side of an export (Phase 8 Step 7, D023), in the style of <see cref="ProjectFileWorkflow"/>:
/// <list type="number">
/// <item>The media files are checked again (<see cref="IProjectService.RecheckMediaAsync"/>, D026 §2), then
/// <see cref="ExportPreflight"/> on the current project (the only source of the rules). Its output-file
/// issues are left for the second check, after the file is chosen; other errors are listed and stop here; warnings
/// are listed and the user may continue.</item>
/// <item>The output <c>.mp4</c> from the save-file picker (starting at <c>LastExportSettings.OutputPath</c>);
/// cancelling it ends the flow silently.</item>
/// <item>The preflight again with that file — it builds the <see cref="ExportJob"/> and its snapshot, which the
/// export alone uses from then on. An existing file is replaced only after the user confirms it (the encoder
/// replaces it atomically, on success only).</item>
/// <item><see cref="IExportService.ExportAsync"/> behind the modal progress window, with editing locked
/// (<see cref="EditingLock"/>) until the export — including a cancelled one — has finished. Cancel only cancels the
/// token.</item>
/// <item>The result: success (path, <c>LastExportSettings.OutputPath</c> updated without making the project dirty),
/// cancellation (a status message, not an error), or a failure by its <see cref="ExportFailure"/> — anything else
/// (e.g. from the rasterizer) as an unexpected error, logged with the exception.</item>
/// </list>
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
    private bool _running;

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
        InOutRangeService? inOut = null)
    {
        _inOut = inOut;
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
    public ExportProgressViewModel? Progress { get; private set; }

    public event EventHandler? IsRunningChanged;

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

        var environment = ExportPreflightEnvironment.Default(await _export.IsAvailableAsync(), IsFontInstalled);

        // 1. Everything but the output file, which is chosen next.
        var check = ExportPreflight.Check(project, null, environment, range);
        var errors = check.Errors.Where(i => !IsAboutOutputFile(i.Kind)).ToList();
        var warnings = check.Warnings.ToList();
        if (errors.Count > 0)
            return await PreflightFailedAsync(errors, warnings);
        if (warnings.Count > 0 && !await ContinueDespiteWarningsAsync(warnings))
            return Report(new ExportOutcome(ExportOutcomeKind.NotStarted, Strings.Export_Cancelled));

        // 2. The output file.
        var outputPath = await _picker.PickSaveFileAsync(OutputRequest(project));
        if (outputPath is null)
            return new ExportOutcome(ExportOutcomeKind.NotStarted, Strings.Export_NoOutputFile);

        // 3. The job: the preflight again, now with the file (it takes the snapshot the export uses).
        check = ExportPreflight.Check(project, outputPath, environment, range);
        if (check.Job is not { } job)
            return await PreflightFailedAsync(check.Errors.ToList(), check.Warnings.ToList());

        if (File.Exists(job.OutputPath) && !await ConfirmReplaceAsync(job.OutputPath))
            return Report(new ExportOutcome(ExportOutcomeKind.NotStarted, Strings.Export_Cancelled));

        // 4. The export.
        var outcome = await ExportAsync(job);
        if (outcome.Kind == ExportOutcomeKind.Succeeded)
            project.LastExportSettings.OutputPath = job.OutputPath; // session state: never dirty, never undoable (D023)
        Report(outcome);
        await ShowOutcomeAsync(outcome);
        return outcome;
    }

    private async Task<ExportOutcome> ExportAsync(ExportJob job)
    {
        using var cts = new CancellationTokenSource();
        using var locked = _editingLock.Acquire();
        var progress = new ExportProgressViewModel(job.OutputPath, cts.Cancel);
        Progress = progress;
        _progressDialog.Show(progress);
        try
        {
            await _export.ExportAsync(job, progress, cts.Token);
            return new ExportOutcome(ExportOutcomeKind.Succeeded, string.Format(Strings.Export_Succeeded, job.OutputPath), job.OutputPath);
        }
        catch (OperationCanceledException)
        {
            var message = File.Exists(job.OutputPath) ? Strings.Export_CancelledKept : Strings.Export_Cancelled;
            return new ExportOutcome(ExportOutcomeKind.Cancelled, message, job.OutputPath);
        }
        catch (ExportException ex)
        {
            _logger.LogWarning(ex, "Export to {Path} failed ({Failure}).", job.OutputPath, ex.Failure);
            return new ExportOutcome(ExportOutcomeKind.Failed, FailureMessage(ex), job.OutputPath, ex.Failure);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export to {Path} failed unexpectedly.", job.OutputPath);
            return new ExportOutcome(ExportOutcomeKind.Failed,
                string.Format(Strings.Export_Unexpected, ex.GetType().Name, ex.Message), job.OutputPath);
        }
        finally
        {
            // Only now — the export has finished, also after a cancel — the window closes and editing resumes.
            _progressDialog.Close();
            Progress = null;
        }
    }

    // The exception's own message (ffmpeg's detail) stays as it is, in parentheses (D039 §2).
    public static string FailureMessage(ExportException ex) => string.Format(Strings.Export_NoFileWritten, string.Format(ex.Failure switch
    {
        ExportFailure.EncoderUnavailable => Strings.Export_FailEncoderUnavailable,
        ExportFailure.DecodeFailed => Strings.Export_FailDecode,
        ExportFailure.EncodeFailed => Strings.Export_FailEncode,
        ExportFailure.OutputFailed => Strings.Export_FailOutput,
        ExportFailure.RenderFailed => Strings.Export_FailRender,
        _ => Strings.Export_Fail
    }, ex.Message));

    private static bool IsAboutOutputFile(ExportIssueKind kind) =>
        kind is ExportIssueKind.InvalidOutputPath or ExportIssueKind.OutputFolderMissing or ExportIssueKind.OutputIsProjectMedia;

    private bool IsFontInstalled(string family) =>
        _fonts.FamilyNames.Contains(family, StringComparer.OrdinalIgnoreCase);

    private static SaveFilePickerRequest OutputRequest(Core.Entities.Project project)
    {
        var last = project.LastExportSettings.OutputPath;
        string? folder = null, name = null;
        if (!string.IsNullOrWhiteSpace(last))
        {
            try
            {
                folder = Path.GetDirectoryName(last);
                name = Path.GetFileName(last);
            }
            catch (ArgumentException)
            {
                // an unusable remembered path is simply not suggested
            }
        }
        return new SaveFilePickerRequest
        {
            Title = Strings.Export_PickerTitle,
            StartFolder = folder ?? project.ProjectFolderPath,
            SuggestedFileName = string.IsNullOrEmpty(name) ? project.Name + ExportFormat.FileExtension : name,
            DefaultExtension = ExportFormat.FileExtension.TrimStart('.'),
            FileTypeFilters = new[] { new FilePickerFileTypeFilter { Name = Strings.Export_Mp4Filter, Patterns = new[] { "*" + ExportFormat.FileExtension } } }
        };
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
                await _dialogs.AskAsync(new DialogRequest { Title = Strings.Export_FinishedTitle, Message = string.Format(Strings.Export_Finished, outcome.OutputPath), Buttons = new[] { Strings.Common_OK } });
                break;
            case ExportOutcomeKind.Failed:
                await _dialogs.AskAsync(new DialogRequest { Title = Strings.Export_FailedTitle, Message = outcome.Message, Buttons = new[] { Strings.Common_OK } });
                break;
            // Cancelled: the status message is enough — cancelling is not an error.
        }
    }

    private ExportOutcome Report(ExportOutcome outcome)
    {
        _status.Report(outcome.Message);
        return outcome;
    }
}
