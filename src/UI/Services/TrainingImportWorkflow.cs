using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.UI.Localization;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// «Тренировка COROS…» (Phase Coros C1): pick one <c>.fit</c> file → read and parse it → keep the workout in
/// <see cref="TrainingSessionService"/> for this session (never in <c>project.json</c>). The status bar shows the
/// result: the device, the date, the local times and the number of climbs, then what was left out; a refused file
/// shows its reason and leaves the previous training in place.
/// <para>
/// Like a media import (D027 §7) the training belongs to the project it was started in: if another project became
/// current while the file was read, nothing is kept.
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
    private readonly Func<Task> _showStatus;

    public TrainingImportWorkflow(
        IFilePickerService filePicker,
        ITrainingImportService importService,
        IProjectService projectService,
        TrainingSessionService session,
        StatusService status,
        ILogger<TrainingImportWorkflow> logger,
        Func<Task>? showStatus = null)
    {
        _filePicker = filePicker;
        _importService = importService;
        _projectService = projectService;
        _session = session;
        _status = status;
        _logger = logger;
        // Let the window render the "Importing…" status before the file is read.
        _showStatus = showStatus ?? (() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask());
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var project = _projectService.Current;

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
            _logger.LogInformation("Import Training: file picker was cancelled.");
            return;
        }
        if (ProjectChanged(project)) return;

        var path = paths[0];
        _status.Report(Strings.Training_Importing);
        await _showStatus();
        if (ProjectChanged(project)) return;

        ClimbingWorkout workout;
        try
        {
            workout = await _importService.ImportAsync(path, ct);
        }
        catch (TrainingFileException ex)
        {
            _logger.LogWarning(ex, "Import Training: '{Path}' refused ({Error}).", path, ex.Error);
            _status.Report(string.Format(Strings.Training_Refused, TrainingText.Error(ex.Error)));
            return;
        }
        catch
        {
            _status.Report(Strings.Training_DidntFinish);
            throw;
        }
        if (ProjectChanged(project)) return;

        _session.Set(workout, path);
        _status.Report(TrainingText.Imported(workout));
    }

    /// <summary>True — with the status message — when <paramref name="project"/>, the project the import started in, is
    /// no longer the current one: the training is not kept for another project (D027 §7).</summary>
    private bool ProjectChanged(Core.Entities.Project project)
    {
        if (ReferenceEquals(project, _projectService.Current)) return false;
        _logger.LogInformation("Import Training: another project became current while importing; nothing was kept.");
        _status.Report(Strings.Training_ProjectChanged);
        return true;
    }
}
