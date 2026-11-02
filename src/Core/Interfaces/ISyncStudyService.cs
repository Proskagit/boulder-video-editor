using AiVideoEditor.Core.Telemetry.Research;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>Hashing progress: the file being read and the bytes read of it.</summary>
public readonly record struct StudyHashProgress(string Path, long BytesRead, long SizeBytes);

/// <summary>The outcome of a study operation: done, or refused with the reason (the study is unchanged).</summary>
public readonly record struct StudyResult(bool Ok, string? Problem, StudyRevision? Revision = null)
{
    public static StudyResult Done(StudyRevision? revision = null) => new(true, null, revision);

    public static StudyResult Refused(string problem) => new(false, problem);
}

/// <summary>
/// The COROS synchronisation study as the reference-marking tool sees it (Step 35.3R1; implemented by
/// <c>Project.Research.SyncStudyService</c>, like <see cref="IProjectService"/>): the open study and its file, its editor,
/// the file checks of its set and the freeze. Never touches the project.
/// </summary>
public interface ISyncStudyService
{
    SyncStudy? Study { get; }

    string? StudyPath { get; }

    /// <summary>The draft's editor (no file is read).</summary>
    SyncStudyEditor Editor { get; }

    StudyFileState StateOf(Guid subjectKey);

    /// <summary>Opens the study file (a quick check of its set follows); never creates one.</summary>
    Task<StudyResult> OpenStudyAsync(string path, CancellationToken ct = default);

    /// <summary>Saves the study to its own file; a path inside <paramref name="projectFolder"/> is refused.</summary>
    Task<StudyResult> SaveStudyAsync(string? projectFolder, CancellationToken ct = default);

    /// <summary>The full check of one subject (SHA-256 and size against the snapshot).</summary>
    Task<StudyFileState> VerifyAsync(Guid subjectKey, IProgress<StudyHashProgress>? progress = null, CancellationToken ct = default);

    /// <summary>The full check of the whole set.</summary>
    Task<IReadOnlyDictionary<Guid, StudyFileState>> VerifyAsync(IProgress<StudyHashProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Verified and unchanged since by size and time, re-read now.</summary>
    bool IsFresh(Guid subjectKey);

    /// <summary>Freezes the draft when the whole set is ready.</summary>
    StudyResult Freeze(string note);
}
