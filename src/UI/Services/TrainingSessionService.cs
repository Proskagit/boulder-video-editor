using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// The current project's COROS training (Phase 35, D049 §9; until Step 35.4 it was session-only). The reference — the FIT
/// file's paths, size, SHA-256, activity and the training's clock correction — is part of the project
/// (<see cref="Core.Entities.Project.Training"/>, saved since <c>project.json</c> v11); this service resolves the file at
/// runtime: after Open / Recover / New and on a re-check it reads the FIT and sets
/// <see cref="TrainingReference.Availability"/> and <see cref="TrainingReference.Workout"/> (never dirty). Attaching,
/// replacing, relinking and removing go through <see cref="IProjectService.SetTraining"/> (dirty, not Undo steps).
/// A missing or unusable file never blocks opening or editing.
/// </summary>
public sealed class TrainingSessionService
{
    private readonly IProjectService _projects;
    private readonly ITrainingImportService? _import;
    private readonly ILogger _logger;

    /// <param name="import">Reads the FIT files; without it a saved training stays <see cref="TrainingAvailability.Unresolved"/>.</param>
    public TrainingSessionService(IProjectService projects, ITrainingImportService? import = null,
        ILogger<TrainingSessionService>? logger = null)
    {
        _projects = projects;
        _import = import;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        projects.ProjectChanged += (_, _) =>
        {
            Changed?.Invoke(this, EventArgs.Empty);
            _ = ResolveAsync();
        };
    }

    /// <summary>The project's training reference, or null.</summary>
    public TrainingReference? Training => _projects.Current.Training;

    /// <summary>The parsed training when its file is usable, or null.</summary>
    public ClimbingWorkout? Workout => Training is { IsUsable: true } t ? t.Workout : null;

    /// <summary>The project's frame grid (the clock correction is edited in its whole frames).</summary>
    public Core.Common.FrameRate ProjectFrameRate => _projects.Current.Settings.FrameRate;

    /// <summary>The FIT file's path, or null.</summary>
    public string? FilePath => Training?.FilePath;

    /// <summary>Raised after the training, its availability or its clock correction may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Called when the training's clock correction changed (an edit, Undo or Redo).</summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Attaches a new training (or replaces the current one — the caller asks first): clock correction 0; the
    /// videos' overrides are untouched (D049 §9).</summary>
    public void Attach(TrainingFile file, string path)
    {
        var reference = new TrainingReference
        {
            FilePath = Path.GetFullPath(path),
            FileSizeBytes = file.FileSizeBytes,
            Sha256 = file.Sha256,
            Activity = TrainingActivity.Of(file.Workout),
            Workout = file.Workout,
            Availability = TrainingAvailability.Available
        };
        _projects.SetTraining(reference);
        NotifyChanged();
    }

    /// <summary>Points the training at another file (the caller confirms another activity first): keeps its id and clock
    /// correction; the file's identity and activity follow the new file.</summary>
    public void Relink(TrainingFile file, string path)
    {
        if (Training is not { } training) return;
        training.FilePath = Path.GetFullPath(path);
        training.FileSizeBytes = file.FileSizeBytes;
        training.Sha256 = file.Sha256;
        training.Activity = TrainingActivity.Of(file.Workout);
        training.Workout = file.Workout;
        training.Availability = TrainingAvailability.Available;
        _projects.SetTraining(training);
        NotifyChanged();
    }

    /// <summary>Removes the training and with it its clock correction; the videos' overrides stay (D049 §9).</summary>
    public void Remove()
    {
        if (Training is null) return;
        _projects.SetTraining(null);
        NotifyChanged();
    }

    /// <summary>Reads the current project's FIT file again and sets its availability (D049 §9): equal bytes →
    /// Available; other bytes of the same activity → Changed (used); another activity → OtherActivity; refused →
    /// Unreadable; no file → Missing. A result for a project that is no longer current is dropped.</summary>
    public async Task ResolveAsync()
    {
        var project = _projects.Current;
        if (project.Training is not { } training || _import is null) return;
        var path = training.FilePath;

        TrainingAvailability availability;
        ClimbingWorkout? workout = null;
        TrainingFile? changed = null;
        try
        {
            var file = await _import.LoadAsync(path);
            var activity = TrainingActivity.Of(file.Workout);
            if (file.FileSizeBytes == training.FileSizeBytes && string.Equals(file.Sha256, training.Sha256, StringComparison.OrdinalIgnoreCase))
                (availability, workout) = (TrainingAvailability.Available, file.Workout);
            else if (activity == training.Activity)
                (availability, workout, changed) = (TrainingAvailability.Changed, file.Workout, file);
            else
                availability = TrainingAvailability.OtherActivity;
        }
        catch (TrainingFileException ex)
        {
            availability = ex.Error == TrainingFileError.FileNotFound ? TrainingAvailability.Missing : TrainingAvailability.Unreadable;
            _logger.LogInformation("Training file '{Path}' is not usable ({Error}).", path, ex.Error);
        }

        if (!ReferenceEquals(project, _projects.Current) || !ReferenceEquals(project.Training, training) || training.FilePath != path)
            return;   // another project, training or file meanwhile
        training.Availability = availability;
        training.Workout = workout;
        if (changed is not null)
        {
            // The same activity in other bytes (D049 §9): used; the new identity is written with the next save.
            training.FileSizeBytes = changed.FileSizeBytes;
            training.Sha256 = changed.Sha256;
        }
        NotifyChanged();
    }
}
