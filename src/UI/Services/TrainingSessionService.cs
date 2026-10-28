using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// The training imported in this session (Phase Coros C1, §6.2 of <c>docs/PHASE_COROS.md</c>): held in memory only and
/// never written to <c>project.json</c>. It belongs to the project it was imported into — as an import does (D027 §7) —
/// and is dropped when another project becomes current (New, Open, Recover). A new import replaces it.
/// </summary>
public sealed class TrainingSessionService
{
    public TrainingSessionService(IProjectService projects)
    {
        projects.ProjectChanged += (_, _) => Clear();
    }

    /// <summary>The imported training, or null.</summary>
    public ClimbingWorkout? Workout { get; private set; }

    /// <summary>The file it was read from, or null.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Raised after <see cref="Workout"/> changed.</summary>
    public event EventHandler? Changed;

    public void Set(ClimbingWorkout workout, string filePath)
    {
        ArgumentNullException.ThrowIfNull(workout);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        Workout = workout;
        FilePath = filePath;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (Workout is null) return;
        Workout = null;
        FilePath = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
