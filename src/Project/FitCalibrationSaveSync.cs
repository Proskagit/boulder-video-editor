using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project;

/// <summary>
/// Step 37.5 (D051 §5.8, §6.4): after every successful Save / Save As of a project with a usable training, the
/// observations of its active usable marks go to the global calibration (off the UI thread, by the store). Autosave and
/// recovery never synchronise. A training whose FIT is not usable now is left alone — what its climbs are is not known, so
/// no record is touched (§5.9). A failure of the store is logged and never fails the save; the next save repeats it. On
/// construction the store is read once, so the estimate for new proposals is current.
/// </summary>
public sealed class FitCalibrationSaveSync
{
    private readonly IProjectService _projects;
    private readonly IFitCalibrationStore _store;
    private readonly ILogger<FitCalibrationSaveSync> _logger;

    public FitCalibrationSaveSync(IProjectService projects, IFitCalibrationStore store, ILogger<FitCalibrationSaveSync> logger)
    {
        _projects = projects;
        _store = store;
        _logger = logger;
        projects.ProjectSaved += (_, _) => _ = SynchronizeAsync();
        Loading = LoadAsync();
    }

    /// <summary>The first read of the store (tests await it).</summary>
    internal Task Loading { get; }

    /// <summary>The synchronisation of the last save, if any (tests await it).</summary>
    internal Task Last { get; private set; } = Task.CompletedTask;

    private async Task LoadAsync()
    {
        try
        {
            await _store.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FIT calibration: the store couldn't be read at start.");
        }
    }

    private Task SynchronizeAsync()
    {
        var project = _projects.Current;
        if (project.Training is not { IsUsable: true } training) return Last = Task.CompletedTask;
        // Taken on the UI thread, right after the save: the marks as saved.
        var observations = FitMarkRules.Observations(project);
        var trainings = new HashSet<TrainingActivity> { training.Activity };
        return Last = RunAsync(trainings, observations);
    }

    private async Task RunAsync(IReadOnlySet<TrainingActivity> trainings, IReadOnlyCollection<Core.Telemetry.Calibration.CalibrationObservation> observations)
    {
        try
        {
            if (!await _store.SynchronizeAsync(trainings, observations).ConfigureAwait(false))
                _logger.LogWarning("FIT calibration: the observations of the save weren't written; the next save repeats them.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FIT calibration: the observations of the save weren't written.");
        }
    }
}
