using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.UI.Localization;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// The commands of «Тренировка COROS…» (Phase 35, D049 §9, D050 amendment B3): «Импортировать…» picks one <c>.fit</c>
/// file, reads it and attaches it to the project (a training already attached is replaced only after a confirmation; the
/// clock correction starts at 0); «Указать файл…» points a training whose file is not available at another file
/// (another activity only after a confirmation); «Удалить тренировку» removes it with its clock correction (the videos'
/// overrides stay). These are project changes — the project becomes dirty — but not Undo steps. The status bar shows the
/// result; a refused file shows its reason and leaves the project unchanged.
/// <para>
/// Like a media import (D027 §7) the work belongs to the project it was started in: if another project became current
/// while the file was read, nothing is changed.
/// </para>
/// </summary>
public sealed class TrainingImportWorkflow
{
    private readonly IFilePickerService _filePicker;
    private readonly ITrainingImportService _importService;
    private readonly IProjectService _projectService;
    private readonly TrainingSessionService _session;
    private readonly StatusService _status;
    private readonly ILogger<TrainingImportWorkflow> _logger;
    private readonly IDialogService? _dialogs;
    private readonly Func<Task> _showStatus;
    private readonly IFitMarkEditService? _fitMarks;

    /// <param name="dialogs">Asks before a replace or a relink to another activity; without it those are refused.</param>
    public TrainingImportWorkflow(
        IFilePickerService filePicker,
        ITrainingImportService importService,
        IProjectService projectService,
        TrainingSessionService session,
        StatusService status,
        ILogger<TrainingImportWorkflow> logger,
        Func<Task>? showStatus = null,
        IDialogService? dialogs = null,
        IFitMarkEditService? fitMarks = null)
    {
        _fitMarks = fitMarks;
        _filePicker = filePicker;
        _importService = importService;
        _projectService = projectService;
        _session = session;
        _status = status;
        _logger = logger;
        _dialogs = dialogs;
        // Let the window render the "Importing…" status before the file is read.
        _showStatus = showStatus ?? (() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask());
    }

    /// <summary>«Импортировать…».</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var project = _projectService.Current;
        if (await PickAndReadAsync(project, ct) is not { } read) return;

        if (_session.Training is not null &&
            !await ConfirmAsync(Strings.Training_ReplaceTitle, Strings.Training_ReplaceMessage, Strings.Training_ReplaceButton))
        {
            _status.Report(Strings.Training_ReplaceCancelled);
            return;
        }
        if (ProjectChanged(project)) return;

        _session.Attach(read.File, read.Path);
        _status.Report(TrainingText.Imported(read.File.Workout));
        // D053 §7 (mode B): the import makes a green route area for every climb found on a video of the timeline — its own
        // Undo step after the import (which is none, D049 §11). Its count joins the import's message.
        if (_fitMarks?.CreateRoutesFromFit() is { Success: true, NoChange: false, Message: { } created })
            _status.Report(TrainingText.Imported(read.File.Workout) + " " + created);
    }

    /// <summary>«Указать файл…»: only while the training's file is not available (D026 PO-6 analogue).</summary>
    public async Task RelinkAsync(CancellationToken ct = default)
    {
        var project = _projectService.Current;
        if (_session.Training is not { IsUsable: false } training) return;
        if (await PickAndReadAsync(project, ct) is not { } read) return;

        if (TrainingActivity.Of(read.File.Workout) != training.Activity &&
            !await ConfirmAsync(Strings.Training_OtherActivityTitle, Strings.Training_OtherActivityMessage, Strings.Training_OtherActivityButton))
        {
            _status.Report(Strings.Training_RelinkCancelled);
            return;
        }
        if (ProjectChanged(project) || !ReferenceEquals(project.Training, training)) return;

        _session.Relink(read.File, read.Path);
        _status.Report(string.Format(Strings.Training_Relinked, TrainingText.Summary(read.File.Workout)));
    }

    /// <summary>«Удалить тренировку».</summary>
    public void Remove()
    {
        if (_session.Training is null) return;
        _session.Remove();
        _status.Report(Strings.Training_Removed);
    }

    private async Task<(TrainingFile File, string Path)?> PickAndReadAsync(Core.Entities.Project project, CancellationToken ct)
    {
        var paths = await _filePicker.PickFilesAsync(new FilePickerRequest
        {
            Title = Strings.Training_PickerTitle,
            AllowMultiple = false,
            FileTypeFilters = new[]
            {
                new FilePickerFileTypeFilter { Name = Strings.Training_FileFilter, Patterns = new[] { "*" + ITrainingImportService.FitExtension } }
            }
        }, ct);
        if (paths.Count == 0)
        {
            _logger.LogInformation("Training: file picker was cancelled.");
            return null;
        }
        if (ProjectChanged(project)) return null;

        var path = paths[0];
        _status.Report(Strings.Training_Importing);
        await _showStatus();
        if (ProjectChanged(project)) return null;

        TrainingFile file;
        try
        {
            file = await _importService.LoadAsync(path, ct);
        }
        catch (TrainingFileException ex)
        {
            _logger.LogWarning(ex, "Training: '{Path}' refused ({Error}).", path, ex.Error);
            _status.Report(string.Format(Strings.Training_Refused, TrainingText.Error(ex.Error)));
            return null;
        }
        catch
        {
            _status.Report(Strings.Training_DidntFinish);
            throw;
        }
        return ProjectChanged(project) ? null : (file, path);
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirm)
    {
        if (_dialogs is null) return false;
        var answer = await _dialogs.AskAsync(new DialogRequest { Title = title, Message = message, Buttons = new[] { confirm, Strings.Common_Cancel } });
        return answer == 0;
    }

    /// <summary>True — with the status message — when <paramref name="project"/>, the project the work started in, is
    /// no longer the current one (D027 §7).</summary>
    private bool ProjectChanged(Core.Entities.Project project)
    {
        if (ReferenceEquals(project, _projectService.Current)) return false;
        _logger.LogInformation("Training: another project became current meanwhile; nothing was changed.");
        _status.Report(Strings.Training_ProjectChanged);
        return true;
    }
}
