using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Research;

namespace AiVideoEditor.Project.Research;

/// <summary>Hashing progress: the file being read and the bytes read of it.</summary>
public readonly record struct StudyHashProgress(string Path, long BytesRead, long SizeBytes);

/// <summary>The outcome of a service operation: done, or refused with the reason (the study is unchanged).</summary>
public readonly record struct StudyResult(bool Ok, string? Problem, StudyRevision? Revision = null)
{
    public static StudyResult Done(StudyRevision? revision = null) => new(true, null, revision);

    public static StudyResult Refused(string problem) => new(false, problem);
}

/// <summary>
/// The COROS synchronisation study around its file (Step 35.3b1, D050 B5): creates a study from the project's training and
/// videos, opens and saves it, identifies the set by SHA-256 and size, and freezes revisions. It reads the project's data
/// but never changes the project. File states live in this session only: a full hash makes a subject
/// <see cref="StudyFileState.Verified"/>, a quick stamp at most <see cref="StudyFileState.QuickOk"/>; before a freeze every
/// subject of the set (the FIT and every video not retired) must be verified and is re-checked by size and time — a
/// change after that last check is not detected. Nothing is ever moved or deleted silently: a changed file is accepted
/// only explicitly, which makes the dependent marks (and, for the FIT, the exclusions) stale.
/// </summary>
public sealed class SyncStudyService
{
    private readonly IFileFingerprintService _fingerprints;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SyncStudyFileStore _store;
    private readonly Dictionary<Guid, (StudyFileState State, FileStamp? Stamp)> _checks = new();

    public SyncStudyService(IFileFingerprintService fingerprints, Func<DateTimeOffset> clock, SyncStudyFileStore? store = null)
    {
        _fingerprints = fingerprints;
        _clock = clock;
        _store = store ?? new SyncStudyFileStore();
    }

    public SyncStudy? Study { get; private set; }

    public string? StudyPath { get; private set; }

    /// <summary>The draft's editor: marks, decisions, exclusions, retirement (no file is read).</summary>
    public SyncStudyEditor Editor => new(Study ?? throw new InvalidOperationException("No study is open."), _clock);

    public StudyFileState StateOf(Guid subjectKey) => _checks.TryGetValue(subjectKey, out var c) ? c.State : StudyFileState.NotChecked;

    /// <summary>The subjects whose files form the checked set: the FIT and every video not retired.</summary>
    public IReadOnlyList<Guid> SetSubjects() => Study is null ? Array.Empty<Guid>()
        : Study.Draft.Videos.Where(v => v.Retired is null).Select(v => v.VideoKey).Prepend(Study.Draft.Fit.FitKey).ToList();

    // ---- create, open, save ----------------------------------------------------------------------------------------

    /// <summary>A new study of the FIT and the videos (in the given order), every file hashed in full.</summary>
    public async Task<SyncStudy> CreateAsync(string fitPath, ClimbingWorkout workout, IReadOnlyList<MediaAsset> videos,
        IProgress<StudyHashProgress>? progress = null, CancellationToken ct = default)
    {
        if (videos.Any(v => v.Kind != MediaKind.Video)) throw new ArgumentException("Only videos form a study's set.", nameof(videos));
        var (fitPrint, fit) = await FitSnapshotAsync(fitPath, workout, Guid.NewGuid(), progress, ct).ConfigureAwait(false);
        var snapshots = new List<(StudyVideo Video, StudyLocation Location, FileFingerprint Print)>();
        foreach (var asset in videos)
        {
            var (print, video) = await VideoSnapshotAsync(asset, progress, ct).ConfigureAwait(false);
            var keyed = video with { VideoKey = Guid.NewGuid() };
            snapshots.Add((keyed, new StudyLocation(keyed.VideoKey, Path.GetFullPath(asset.FilePath), print.After.LastWriteUtcTicks), print));
        }

        Study = SyncStudyEditor.Create(Guid.NewGuid(), fit, snapshots.Select(s => (s.Video, s.Location)).ToList(),
            new StudyLocation(fit.FitKey, Path.GetFullPath(fitPath), fitPrint.After.LastWriteUtcTicks), _clock);
        StudyPath = null;
        _checks.Clear();
        _checks[fit.FitKey] = (StudyFileState.Verified, fitPrint.After);
        foreach (var s in snapshots) _checks[s.Video.VideoKey] = (StudyFileState.Verified, s.Print.After);
        return Study;
    }

    /// <summary>Opens a study and runs the quick check (size and time) of its set.</summary>
    public async Task<SyncStudy> OpenAsync(string path, CancellationToken ct = default)
    {
        var study = await _store.LoadAsync(path, ct).ConfigureAwait(false);
        Study = study;
        StudyPath = Path.GetFullPath(path);
        _checks.Clear();
        QuickCheck();
        return study;
    }

    public async Task SaveAsync(string path, string? projectFolder, CancellationToken ct = default)
    {
        await _store.SaveAsync(path, Study ?? throw new InvalidOperationException("No study is open."), projectFolder, ct).ConfigureAwait(false);
        StudyPath = Path.GetFullPath(path);
    }

    // ---- checks ----------------------------------------------------------------------------------------------------

    /// <summary>The quick check of the set: a stamp equal to the snapshot's size and the location's time is
    /// <see cref="StudyFileState.QuickOk"/> (a verified subject with an unchanged stamp stays verified).</summary>
    public void QuickCheck()
    {
        foreach (var key in SetSubjects())
        {
            var (size, path, lastWrite) = Expected(key);
            var stamp = _fingerprints.Stamp(path);
            if (stamp is null) _checks[key] = (StudyFileState.Missing, null);
            else if (_checks.TryGetValue(key, out var c) && c.State == StudyFileState.Verified && c.Stamp == stamp) continue;
            else if (stamp.Value.SizeBytes == size && stamp.Value.LastWriteUtcTicks == lastWrite) _checks[key] = (StudyFileState.QuickOk, stamp);
            else _checks[key] = (StudyFileState.Changed, stamp);
        }
    }

    /// <summary>The full check: every subject of the set hashed and compared with its snapshot (SHA-256 and size), its
    /// stamp unchanged across the read.</summary>
    public async Task<IReadOnlyDictionary<Guid, StudyFileState>> VerifyAsync(IProgress<StudyHashProgress>? progress = null, CancellationToken ct = default)
    {
        var editor = Editor;
        foreach (var key in SetSubjects())
        {
            var (size, path, _) = Expected(key);
            var sha = key == Study!.Draft.Fit.FitKey ? Study.Draft.Fit.Sha256 : Study.Draft.Video(key)!.Sha256;
            FileFingerprint? print;
            try
            {
                print = await _fingerprints.HashAsync(path, Progress(path, progress), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _checks[key] = (StudyFileState.Unreadable, null);
                continue;
            }
            if (print is null) _checks[key] = (StudyFileState.Missing, null);
            else if (print.ChangedDuringRead) _checks[key] = (StudyFileState.ChangedDuringRead, print.After);
            else if (print.Sha256 == sha && print.Before.SizeBytes == size)
            {
                _checks[key] = (StudyFileState.Verified, print.After);
                editor.Touch(key, print.After.LastWriteUtcTicks);
            }
            else _checks[key] = (StudyFileState.Changed, print.After);
        }
        return SetSubjects().ToDictionary(k => k, StateOf);
    }

    /// <summary>Why the set may not be frozen or evaluated now (empty when it may): every subject verified, its size and
    /// time re-checked right now (a difference drops <see cref="StudyFileState.Verified"/>), every video's metadata complete.</summary>
    public IReadOnlyList<string> ReadinessProblems()
    {
        var problems = new List<string>();
        foreach (var key in SetSubjects())
        {
            var name = Name(key);
            if (StateOf(key) != StudyFileState.Verified)
            {
                problems.Add($"{name}: {StateOf(key)}, not verified");
                continue;
            }
            var stamp = _fingerprints.Stamp(Expected(key).Path);
            if (stamp != _checks[key].Stamp)
            {
                _checks[key] = (stamp is null ? StudyFileState.Missing : StudyFileState.Changed, stamp);
                problems.Add($"{name}: changed since it was verified");
            }
        }
        foreach (var video in Study?.Draft.Videos.Where(v => v.Retired is null && !v.MetadataComplete) ?? Enumerable.Empty<StudyVideo>())
            problems.Add($"{video.OriginalFileName}: duration or recording-time probe missing");
        return problems;
    }

    /// <summary>Freezes the draft into the next revision when the set is ready (I8).</summary>
    public StudyResult Freeze(string note)
    {
        var problems = ReadinessProblems();
        return problems.Count > 0 ? StudyResult.Refused(string.Join("; ", problems)) : StudyResult.Done(Editor.Freeze(note));
    }

    // ---- set changes -----------------------------------------------------------------------------------------------

    public async Task<StudyResult> AddVideoAsync(MediaAsset asset, string note = "", IProgress<StudyHashProgress>? progress = null, CancellationToken ct = default)
    {
        if (asset.Kind != MediaKind.Video) return StudyResult.Refused("not a video");
        var (print, video) = await VideoSnapshotAsync(asset, progress, ct).ConfigureAwait(false);
        var edit = Editor.AddVideo(video, Path.GetFullPath(asset.FilePath), print.After.LastWriteUtcTicks, note);
        if (!edit.Ok) return StudyResult.Refused(edit.Refusal!.Value.ToString());
        _checks[edit.CreatedId!.Value] = (StudyFileState.Verified, print.After);
        return StudyResult.Done();
    }

    /// <summary>The subject's file is at <paramref name="newPath"/>: accepted only when its SHA-256 and size equal the
    /// snapshot — the key and the content stay, the move is journalled. An equal file name proves nothing.</summary>
    public async Task<StudyResult> RelocateAsync(Guid subjectKey, string newPath, string note = "", IProgress<StudyHashProgress>? progress = null, CancellationToken ct = default)
    {
        if (Study is null || !SubjectExists(subjectKey)) return StudyResult.Refused("no such FIT or video");
        var (size, _, _) = Expected(subjectKey);
        var sha = subjectKey == Study.Draft.Fit.FitKey ? Study.Draft.Fit.Sha256 : Study.Draft.Video(subjectKey)!.Sha256;
        var print = await HashOrRefuseAsync(newPath, progress, ct).ConfigureAwait(false);
        if (print.Problem is { } problem) return StudyResult.Refused(problem);
        if (print.Print!.Sha256 != sha || print.Print.Before.SizeBytes != size) return StudyResult.Refused("the file has other content (SHA-256 or size differ)");
        var edit = Editor.Relocate(subjectKey, Path.GetFullPath(newPath), print.Print.After.LastWriteUtcTicks, note);
        if (!edit.Ok) return StudyResult.Refused(edit.Refusal!.Value.ToString());
        _checks[subjectKey] = (StudyFileState.Verified, print.Print.After);
        return StudyResult.Done();
    }

    /// <summary>Accepts the video's other content (explicitly): a new snapshot under the same key; its marks become stale.</summary>
    public async Task<StudyResult> AcceptChangedVideoAsync(Guid videoKey, MediaAsset asset, string note, IProgress<StudyHashProgress>? progress = null, CancellationToken ct = default)
    {
        if (Study?.Draft.Video(videoKey) is null) return StudyResult.Refused("no such video");
        if (asset.Kind != MediaKind.Video) return StudyResult.Refused("not a video");
        var (print, video) = await VideoSnapshotAsync(asset, progress, ct).ConfigureAwait(false);
        var edit = Editor.AcceptChangedVideo(videoKey, video, Path.GetFullPath(asset.FilePath), print.After.LastWriteUtcTicks, note);
        if (!edit.Ok) return StudyResult.Refused(edit.Refusal!.Value.ToString());
        _checks[videoKey] = (StudyFileState.Verified, print.After);
        return StudyResult.Done();
    }

    /// <summary>Accepts another FIT (explicitly): every mark and exclusion not withdrawn becomes stale.</summary>
    public async Task<StudyResult> AcceptChangedFitAsync(string fitPath, ClimbingWorkout workout, string note, IProgress<StudyHashProgress>? progress = null, CancellationToken ct = default)
    {
        if (Study is null) return StudyResult.Refused("no study is open");
        var (print, fit) = await FitSnapshotAsync(fitPath, workout, Study.Draft.Fit.FitKey, progress, ct).ConfigureAwait(false);
        var edit = Editor.AcceptChangedFit(fit, Path.GetFullPath(fitPath), print.After.LastWriteUtcTicks, note);
        if (!edit.Ok) return StudyResult.Refused(edit.Refusal!.Value.ToString());
        _checks[fit.FitKey] = (StudyFileState.Verified, print.After);
        return StudyResult.Done();
    }

    /// <summary>Takes a video out of the checked set and the evaluation (it stays with its marks and decisions).</summary>
    public StudyResult RetireVideo(Guid videoKey, string reason)
    {
        var edit = Editor.RetireVideo(videoKey, reason);
        if (!edit.Ok) return StudyResult.Refused(edit.Refusal!.Value.ToString());
        _checks.Remove(videoKey);
        return StudyResult.Done();
    }

    /// <summary>Brings a retired video back (explicitly); it is quick-checked and must be verified before a freeze.</summary>
    public Task<StudyResult> RestoreVideoAsync(Guid videoKey, string note)
    {
        var edit = Editor.RestoreVideo(videoKey, note);
        if (!edit.Ok) return Task.FromResult(StudyResult.Refused(edit.Refusal!.Value.ToString()));
        _checks.Remove(videoKey);
        QuickCheck();
        return Task.FromResult(StudyResult.Done());
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private bool SubjectExists(Guid key) => key == Study!.Draft.Fit.FitKey || Study.Draft.Video(key) is not null;

    private string Name(Guid key) => key == Study!.Draft.Fit.FitKey ? "FIT" : Study.Draft.Video(key)?.OriginalFileName ?? key.ToString();

    private (long Size, string Path, long? LastWrite) Expected(Guid key)
    {
        var location = Study!.Location(key) ?? throw new InvalidOperationException($"No location for {key}.");
        var size = key == Study.Draft.Fit.FitKey ? Study.Draft.Fit.SizeBytes : Study.Draft.Video(key)!.SizeBytes;
        return (size, location.CurrentPath, location.LastWriteUtcTicks);
    }

    private static IProgress<long>? Progress(string path, IProgress<StudyHashProgress>? progress)
    {
        if (progress is null) return null;
        var size = new FileInfo(path) is { Exists: true } f ? f.Length : 0;
        return new InlineProgress(read => progress.Report(new StudyHashProgress(path, read, size)));
    }

    /// <summary>Reports on the reading thread, in order (no synchronisation context involved).</summary>
    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private async Task<(FileFingerprint? Print, string? Problem)> HashOrRefuseAsync(string path, IProgress<StudyHashProgress>? progress, CancellationToken ct)
    {
        try
        {
            var print = await _fingerprints.HashAsync(path, Progress(path, progress), ct).ConfigureAwait(false);
            if (print is null) return (null, $"{Path.GetFileName(path)}: not found");
            return print.ChangedDuringRead ? (null, $"{Path.GetFileName(path)}: changed while it was read") : (print, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"{Path.GetFileName(path)}: cannot be read ({ex.Message})");
        }
    }

    private async Task<FileFingerprint> HashOrThrowAsync(string path, IProgress<StudyHashProgress>? progress, CancellationToken ct)
    {
        var (print, problem) = await HashOrRefuseAsync(path, progress, ct).ConfigureAwait(false);
        return print ?? throw new SyncStudyFileException(SyncStudyFileError.Unreadable, problem!);
    }

    private async Task<(FileFingerprint, StudyFit)> FitSnapshotAsync(string fitPath, ClimbingWorkout workout, Guid fitKey,
        IProgress<StudyHashProgress>? progress, CancellationToken ct)
    {
        var print = await HashOrThrowAsync(fitPath, progress, ct).ConfigureAwait(false);
        var climbs = workout.Climbs.Select(c => new StudyClimb(c.Index, c.ClimbNumber!.Value, c.StartUtc, c.Elapsed.Ticks, c.EndUtc)).ToList();
        return (print, new StudyFit(fitKey, print.Sha256, print.Before.SizeBytes, TrainingActivity.Of(workout), climbs));
    }

    private async Task<(FileFingerprint, StudyVideo)> VideoSnapshotAsync(MediaAsset asset, IProgress<StudyHashProgress>? progress, CancellationToken ct)
    {
        var print = await HashOrThrowAsync(asset.FilePath, progress, ct).ConfigureAwait(false);
        var probe = asset.Metadata?.RecordingTimeProbe;
        return (print, new StudyVideo(Guid.Empty, Path.GetFileName(asset.FilePath), print.Before.SizeBytes, print.Sha256,
            asset.Metadata?.Duration.Ticks ?? 0, probe is not null, probe?.Tag, probe?.Origin, 0, null));
    }
}
