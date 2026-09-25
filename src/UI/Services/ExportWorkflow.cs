using System.Text;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
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
/// <item><see cref="ExportPreflight"/> on the current project (the only source of the rules). Its output-file
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
        ILogger<ExportWorkflow> logger)
    {
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
            return new ExportOutcome(ExportOutcomeKind.NotStarted, "An export is already running.");

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
        var project = _projects.Current;
        if (project is null)
            return Report(new ExportOutcome(ExportOutcomeKind.NotStarted, "There is no project to export."));

        var environment = ExportPreflightEnvironment.Default(await _export.IsAvailableAsync(), IsFontInstalled);

        // 1. Everything but the output file, which is chosen next.
        var check = ExportPreflight.Check(project, null, environment);
        var errors = check.Errors.Where(i => !IsAboutOutputFile(i.Kind)).ToList();
        var warnings = check.Warnings.ToList();
        if (errors.Count > 0)
            return await PreflightFailedAsync(errors, warnings);
        if (warnings.Count > 0 && !await ContinueDespiteWarningsAsync(warnings))
            return Report(new ExportOutcome(ExportOutcomeKind.NotStarted, "Export cancelled."));

        // 2. The output file.
        var outputPath = await _picker.PickSaveFileAsync(OutputRequest(project));
        if (outputPath is null)
            return new ExportOutcome(ExportOutcomeKind.NotStarted, "No output file was chosen.");

        // 3. The job: the preflight again, now with the file (it takes the snapshot the export uses).
        check = ExportPreflight.Check(project, outputPath, environment);
        if (check.Job is not { } job)
            return await PreflightFailedAsync(check.Errors.ToList(), check.Warnings.ToList());

        if (File.Exists(job.OutputPath) && !await ConfirmReplaceAsync(job.OutputPath))
            return Report(new ExportOutcome(ExportOutcomeKind.NotStarted, "Export cancelled."));

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
            return new ExportOutcome(ExportOutcomeKind.Succeeded, $"Exported to {job.OutputPath}.", job.OutputPath);
        }
        catch (OperationCanceledException)
        {
            var kept = File.Exists(job.OutputPath) ? " The existing file was left unchanged." : "";
            return new ExportOutcome(ExportOutcomeKind.Cancelled, $"Export cancelled.{kept}", job.OutputPath);
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
                $"The export failed because of an unexpected error ({ex.GetType().Name}: {ex.Message}). " +
                "No file was written; details are in the log.", job.OutputPath);
        }
        finally
        {
            // Only now — the export has finished, also after a cancel — the window closes and editing resumes.
            _progressDialog.Close();
            Progress = null;
        }
    }

    public static string FailureMessage(ExportException ex) => ex.Failure switch
    {
        ExportFailure.EncoderUnavailable =>
            "ffmpeg could not be found, so the video can't be encoded. Install ffmpeg (or set \"Ffmpeg:FfmpegPath\" in " +
            $"appsettings.json) and try again. ({ex.Message})",
        ExportFailure.DecodeFailed =>
            $"A source file could not be read, so the export was stopped. Check that the media is still available. ({ex.Message})",
        ExportFailure.EncodeFailed =>
            $"The video could not be encoded, so the export was stopped. ({ex.Message})",
        ExportFailure.OutputFailed =>
            $"The output file could not be written. Check the folder and that the file is not open elsewhere. ({ex.Message})",
        _ => $"The export failed. ({ex.Message})"
    } + " No file was written; an existing file was left unchanged.";

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
            Title = "Export — choose the output file",
            StartFolder = folder ?? project.ProjectFolderPath,
            SuggestedFileName = string.IsNullOrEmpty(name) ? project.Name + ExportFormat.FileExtension : name,
            DefaultExtension = ExportFormat.FileExtension.TrimStart('.'),
            FileTypeFilters = new[] { new FilePickerFileTypeFilter { Name = "MP4 video", Patterns = new[] { "*" + ExportFormat.FileExtension } } }
        };
    }

    private async Task<ExportOutcome> PreflightFailedAsync(IReadOnlyList<ExportIssue> errors, IReadOnlyList<ExportIssue> warnings)
    {
        var message = new StringBuilder("The project can't be exported:\n");
        AppendIssues(message, "Errors", errors);
        if (warnings.Count > 0) AppendIssues(message.Append('\n'), "Warnings", warnings);
        await _dialogs.AskAsync(new DialogRequest { Title = "Export not possible", Message = message.ToString(), Buttons = new[] { "OK" } });
        var outcome = new ExportOutcome(ExportOutcomeKind.PreflightFailed,
            errors.Count == 1 ? $"Export not possible: {errors[0].Message}" : $"Export not possible: {errors.Count} problems.");
        return Report(outcome);
    }

    private async Task<bool> ContinueDespiteWarningsAsync(IReadOnlyList<ExportIssue> warnings)
    {
        var message = new StringBuilder("The export can run, but note:\n");
        AppendIssues(message, "Warnings", warnings);
        message.Append("\nContinue with the export?");
        var choice = await _dialogs.AskAsync(new DialogRequest { Title = "Export warnings", Message = message.ToString(), Buttons = new[] { "Continue", "Cancel" } });
        return choice == 0;
    }

    private async Task<bool> ConfirmReplaceAsync(string path)
    {
        var choice = await _dialogs.AskAsync(new DialogRequest
        {
            Title = "Replace file?",
            Message = $"\"{path}\" already exists. Replace it with the export?\n\nThe file is replaced only when the export succeeds.",
            Buttons = new[] { "Replace", "Cancel" }
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
                await _dialogs.AskAsync(new DialogRequest { Title = "Export finished", Message = $"The video was exported to\n{outcome.OutputPath}", Buttons = new[] { "OK" } });
                break;
            case ExportOutcomeKind.Failed:
                await _dialogs.AskAsync(new DialogRequest { Title = "Export failed", Message = outcome.Message, Buttons = new[] { "OK" } });
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
