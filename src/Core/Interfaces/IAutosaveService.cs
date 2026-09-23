namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Periodically writes the current project, while it has unsaved changes, to a recovery file
/// that is separate from project.json (which only an explicit Save writes). After a crash the
/// recovery file is offered at the next start.
/// </summary>
/// <remarks>
/// A recovery file becomes obsolete — and is deleted — when the project is saved, when the
/// project is clean again at an autosave, when it is discarded, and at a normal shutdown of a
/// clean project. At shutdown with unsaved changes a final recovery file is written and kept,
/// so those changes can still be recovered.
/// </remarks>
public interface IAutosaveService
{
    /// <summary>Starts the periodic autosave. Call on the UI thread: autosaves run there
    /// (the snapshot is taken there; the file is written in the background).</summary>
    void Start();

    /// <summary>Stops the periodic autosave (an autosave already running completes).</summary>
    void Stop();

    /// <summary>Raised after a recovery file was written, with its path.</summary>
    event EventHandler<string>? AutosaveCompleted;

    /// <summary>Writes the recovery file now if the current project has unsaved changes;
    /// otherwise removes this session's obsolete recovery file for it. Returns true if a
    /// recovery file was written.</summary>
    Task<bool> AutosaveNowAsync(CancellationToken ct = default);

    /// <summary>Looks for recovery files left by an earlier session that didn't end normally.
    /// Damaged files are set aside (renamed, not deleted) and counted; files that are older
    /// than their project's saved project.json are removed as obsolete; files of a running
    /// instance are ignored.</summary>
    Task<RecoveryScanResult> FindRecoveryAsync(CancellationToken ct = default);

    /// <summary>Deletes a recovery file the user chose not to recover.</summary>
    Task DiscardRecoveryAsync(RecoveryCandidate candidate);

    /// <summary>Deletes the recovery file of project <paramref name="projectId"/> — its unsaved
    /// changes were discarded on purpose ("Don't Save"). Call it after the project has been
    /// replaced, so no autosave of it can follow.</summary>
    Task DiscardRecoveryAsync(Guid projectId);

    /// <summary>Normal shutdown: stops autosave and waits for a running one. Then, if the project
    /// has unsaved changes and <paramref name="keepUnsavedChanges"/> is true, writes a final recovery
    /// file so they can be recovered; otherwise (clean project, or "Don't Save") removes it.</summary>
    Task ShutdownAsync(bool keepUnsavedChanges = true);
}

/// <summary>A recovery file that can be offered to the user.</summary>
public sealed record RecoveryCandidate(
    string FilePath,
    Guid ProjectId,
    string ProjectName,
    string? ProjectFolderPath,
    DateTimeOffset AutosavedAt);

/// <summary>Result of <see cref="IAutosaveService.FindRecoveryAsync"/>: the most recent
/// candidate (if any), how many candidates there are in total, and how many damaged recovery
/// files were set aside.</summary>
public sealed record RecoveryScanResult(RecoveryCandidate? Candidate, int CandidateCount, int DamagedFiles)
{
    public static readonly RecoveryScanResult None = new(null, 0, 0);
}
