using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Research;

namespace AiVideoEditor.Project.Research;

/// <summary>
/// The COROS synchronisation study around its file (Step 35.3b1, D050 B5): creates a study from the project's training and
/// videos, opens and saves it, identifies the set by SHA-256 and size, and freezes revisions. It reads the project's data
/// but never changes the project. File states live in this session only: a full hash makes a subject
/// <see cref="StudyFileState.Verified"/>, a quick stamp at most <see cref="StudyFileState.QuickOk"/>; before a freeze every
/// subject of the set (the FIT and every video not retired) must be verified and is re-checked by size and time — a
/// change after that last check is not detected. Nothing is ever moved or deleted silently: a changed file is accepted
/// only explicitly, which makes the dependent marks (and, for the FIT, the exclusions) stale.
/// </summary>
public sealed class SyncStudyService : ISyncStudyService
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

    public async Task<StudyResult> OpenStudyAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await OpenAsync(path, ct).ConfigureAwait(false);
            return StudyResult.Done();
        }
        catch (SyncStudyFileException ex)
        {
            return StudyResult.Refused($"{ex.Error}: {ex.Message}");
        }
    }

    public async Task<StudyResult> SaveStudyAsync(string? projectFolder, CancellationToken ct = default)
    {
        if (Study is null || StudyPath is null) return StudyResult.Refused("no study is open");
        try
        {
            await SaveAsync(StudyPath, projectFolder, ct).ConfigureAwait(false);
            return StudyResult.Done();
        }
        catch (SyncStudyFileException ex)
        {
            return StudyResult.Refused($"{ex.Error}: {ex.Message}");
        }
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
        foreach (var key in SetSubjects()) await VerifyAsync(key, progress, ct).ConfigureAwait(false);
        return SetSubjects().ToDictionary(k => k, StateOf);
    }

    /// <summary>
    /// The full check of one subject (Step 35.3R1 — a video before it is marked): its SHA-256 and size compared with the
    /// snapshot, its stamp unchanged across the read; the whole set is not read. Returns the new state.
    /// </summary>
    public async Task<StudyFileState> VerifyAsync(Guid subjectKey, IProgress<StudyHashProgress>? progress = null, CancellationToken ct = default)
    {
        if (Study is null || !SubjectExists(subjectKey)) throw new InvalidOperationException("No such FIT or video in the open study.");
        var (size, path, _) = Expected(subjectKey);
        var sha = subjectKey == Study.Draft.Fit.FitKey ? Study.Draft.Fit.Sha256 : Study.Draft.Video(subjectKey)!.Sha256;
        FileFingerprint? print;
        try
        {
            print = await _fingerprints.HashAsync(path, Progress(path, progress), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _checks[subjectKey] = (StudyFileState.Unreadable, null);
            return StudyFileState.Unreadable;
        }
        if (print is null) _checks[subjectKey] = (StudyFileState.Missing, null);
        else if (print.ChangedDuringRead) _checks[subjectKey] = (StudyFileState.ChangedDuringRead, print.After);
        else if (print.Sha256 == sha && print.Before.SizeBytes == size)
        {
            _checks[subjectKey] = (StudyFileState.Verified, print.After);
            Editor.Touch(subjectKey, print.After.LastWriteUtcTicks);
        }
        else _checks[subjectKey] = (StudyFileState.Changed, print.After);
        return StateOf(subjectKey);
    }

    /// <summary>
    /// Whether the subject is verified and unchanged since — its size and time re-read right now equal those of its check
    /// (Step 35.3R1: before every marking action). A difference drops <see cref="StudyFileState.Verified"/>, so a new full
    /// check is needed; a change after this re-check is not detected.
    /// </summary>
    public bool IsFresh(Guid subjectKey)
    {
        if (StateOf(subjectKey) != StudyFileState.Verified || Study is null || !SubjectExists(subjectKey)) return false;
        var stamp = _fingerprints.Stamp(Expected(subjectKey).Path);
        if (stamp == _checks[subjectKey].Stamp) return true;
        _checks[subjectKey] = (stamp is null ? StudyFileState.Missing : StudyFileState.Changed, stamp);
        return false;
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

    /// <summary>
    /// Why the study may not be evaluated now (empty when it may; Step 35.3b2): only the latest frozen revision is
    /// evaluated; the draft's set — the FIT and the videos not retired, by SHA-256 and size — must equal that revision's;
    /// every file verified in full and re-checked by size and time right now; no climb confirmed by two active marks.
    /// After any change of the data a new freeze is needed.
    /// </summary>
    public IReadOnlyList<string> EvaluationProblems()
    {
        if (Study is null) return new[] { "no study is open" };
        if (Study.Revisions.Count == 0) return new[] { "no frozen revision" };
        var revision = Study.Revisions[^1].Content;
        var problems = new List<string>();
        if (SetIdentity(revision) != SetIdentity(Study.Draft))
            problems.Add($"the set differs from revision {Study.Revisions[^1].Number}: freeze again");
        problems.AddRange(ReadinessProblems());
        foreach (var climb in Conflicts(revision))
            problems.Add($"climb {climb.ClimbNumber} is confirmed by more than one active mark");
        return problems;
    }

    private static string SetIdentity(StudyContent content) => string.Join("|",
        content.Videos.Where(v => v.Retired is null).Select(v => $"{v.VideoKey:D}:{v.Sha256}:{v.SizeBytes}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .Prepend($"{content.Fit.FitKey:D}:{content.Fit.Sha256}:{content.Fit.SizeBytes}"));

    /// <summary>The climbs confirmed by two or more active marks (a conflict a human resolves; no camera is chosen).</summary>
    public static IReadOnlyList<ClimbIdentity> Conflicts(StudyContent content) =>
        content.Decisions
            .Where(d => d.State == MatchState.Confirmed && d.Climb is not null && content.Mark(d.MarkId) is { IsActive: true } m
                        && content.Video(m.VideoKey)?.Retired is null)
            .GroupBy(d => d.Climb!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(c => c.ClimbKey)
            .ToList();

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

    // ---- evaluation and explanations (Step 35.3b2d) ---------------------------------------------------------------

    /// <summary>The export files of evaluation <paramref name="seq"/>: <c>&lt;study&gt;.eval-&lt;seq&gt;.json</c> and <c>.csv</c>.</summary>
    public static (string Json, string Csv) ExportPaths(string studyPath, long seq)
    {
        var full = Path.GetFullPath(studyPath);
        var stem = full.EndsWith(SyncStudyFileStore.Extension, StringComparison.OrdinalIgnoreCase)
            ? full[..^SyncStudyFileStore.Extension.Length]
            : Path.Combine(Path.GetDirectoryName(full) ?? "", Path.GetFileNameWithoutExtension(full));
        return ($"{stem}.eval-{seq}.json", $"{stem}.eval-{seq}.csv");
    }

    /// <summary>
    /// Evaluates the latest frozen revision under <paramref name="protocol"/> (D050 B6): every file of the set hashed in
    /// full, then <see cref="EvaluationProblems"/> — any problem refuses and nothing is written; the export files must not
    /// exist (never overwritten). Then the report is recorded as a new evaluation record and the study saved atomically
    /// (a failed save drops the record by reloading the file); then the exports are written, each only if it does not exist.
    /// An export that fails after the save leaves the record in the study. An earlier record is never changed.
    /// </summary>
    public async Task<StudyEvaluationRun> EvaluateAsync(EvaluationProtocol protocol, IProgress<StudyHashProgress>? progress = null,
        Action? beforeExport = null, CancellationToken ct = default)
    {
        if (Study is null || StudyPath is null) return StudyEvaluationRun.Refused("no study is open");
        await VerifyAsync(progress, ct).ConfigureAwait(false);
        if (EvaluationProblems() is { Count: > 0 } problems) return StudyEvaluationRun.Refused(problems);

        var (json, csv) = ExportPaths(StudyPath, Study.NextSeq);
        var taken = new[] { json, csv }.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (taken.Count > 0) return StudyEvaluationRun.Refused(taken.Select(p => $"{Path.GetFileName(p)}: already exists, never overwritten").ToList());

        var report = StudyEvaluator.Evaluate(Study.Revisions[^1], protocol);
        var edit = Editor.RecordEvaluation(report);
        if (!edit.Ok) return StudyEvaluationRun.Refused(edit.Refusal!.Value.ToString());
        var record = Study.Evaluations[^1];
        if (await SaveOrReloadAsync(ct).ConfigureAwait(false) is { } saveProblem) return StudyEvaluationRun.Refused(saveProblem);

        beforeExport?.Invoke();
        string? exportProblem = null;
        foreach (var (path, text) in new[] { (json, record.Report), (csv, StudyCanonicalJson.EvaluationCsv(report)) })
        {
            try
            {
                await WriteNewAsync(path, text, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                exportProblem = (exportProblem is null ? "" : exportProblem + "; ")
                    + $"{Path.GetFileName(path)} not written ({ex.Message}); evaluation {record.Seq} is saved in the study";
            }
        }
        return new StudyEvaluationRun(Array.Empty<string>(), record, report, json, csv, exportProblem);
    }

    /// <summary>
    /// Records explanations of evaluation <paramref name="evaluationSeq"/> (D050 B6, condition 2) — all or none: each climb
    /// by its number in that record's report, its category fitting the climb's status there (the editor refuses a misfit),
    /// the evidence required; then the study is saved atomically. Returns the verdict of that record with the explanations
    /// in force — read from the stored report, never recomputed. Nothing else is written.
    /// </summary>
    public async Task<StudyExplanationRun> ExplainAsync(long evaluationSeq, IReadOnlyList<ExplanationInput> explanations, CancellationToken ct = default)
    {
        if (Study is null || StudyPath is null) return new(new[] { "no study is open" }, null);
        if (Study.Evaluation(evaluationSeq) is not { } record) return new(new[] { $"no evaluation {evaluationSeq}" }, null);
        if (explanations.Count == 0) return new(new[] { "no explanation given" }, null);

        var reported = StudyEvaluator.ReportedClimbs(record.Report);
        var problems = new List<string>();
        var resolved = new List<(ClimbIdentity Climb, ExplanationInput Input)>();
        foreach (var input in explanations)
        {
            if (reported.FirstOrDefault(c => c.ClimbNumber == input.ClimbNumber)?.Climb is not { } climb)
                problems.Add($"climb {input.ClimbNumber}: {StudyRefusal.ClimbNotEvaluated}");
            else if (string.IsNullOrWhiteSpace(input.Evidence))
                problems.Add($"climb {input.ClimbNumber}: {StudyRefusal.EvidenceRequired}");
            else if (StudyEvaluator.ExplanationProblem(record.Report, climb, input.Category) is { } misfit)
                problems.Add($"climb {input.ClimbNumber}: {misfit} ({StudyCanonicalJson.Name(input.Category)})");
            else resolved.Add((climb, input));
        }
        if (problems.Count > 0) return new(problems, null);

        foreach (var (climb, input) in resolved)
        {
            var edit = Editor.AddExplanation(evaluationSeq, climb, input.Category, input.Evidence);
            if (edit.Ok) continue;
            await OpenAsync(StudyPath, ct).ConfigureAwait(false);             // drop what was added in memory
            return new(new[] { $"climb {input.ClimbNumber}: {edit.Refusal}" }, null);
        }
        if (await SaveOrReloadAsync(ct).ConfigureAwait(false) is { } saveProblem) return new(new[] { saveProblem }, null);
        return new(Array.Empty<string>(), StudyEvaluator.FinalVerdict(record.Report, record.ReportHash, evaluationSeq,
            Study.ActiveExplanations(long.MaxValue)));
    }

    /// <summary>Saves the study at its path; on failure reloads it from the file (dropping the unsaved records) and says why.</summary>
    private async Task<string?> SaveOrReloadAsync(CancellationToken ct)
    {
        try
        {
            await SaveAsync(StudyPath!, null, ct).ConfigureAwait(false);
            return null;
        }
        catch (SyncStudyFileException ex)
        {
            await OpenAsync(StudyPath!, ct).ConfigureAwait(false);
            return $"the study was not saved ({ex.Error}: {ex.Message}); nothing was recorded";
        }
    }

    /// <summary>Writes a new UTF-8 file (no BOM); an existing file is never overwritten.</summary>
    private static async Task WriteNewAsync(string path, string text, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
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
        // The watch's offset only as the parser found it usable (null otherwise) — an observation, never the hypothesis h.
        var offset = workout.UtcOffset is { } o ? (int?)checked((int)(o.Ticks / TimeSpan.TicksPerMinute)) : null;
        return (print, new StudyFit(fitKey, print.Sha256, print.Before.SizeBytes, TrainingActivity.Of(workout), climbs, offset));
    }

    private async Task<(FileFingerprint, StudyVideo)> VideoSnapshotAsync(MediaAsset asset, IProgress<StudyHashProgress>? progress, CancellationToken ct)
    {
        var print = await HashOrThrowAsync(asset.FilePath, progress, ct).ConfigureAwait(false);
        var probe = asset.Metadata?.RecordingTimeProbe;
        // Frame rates as ffprobe stated them; the constant-rate state stays unknown (no reliable confirmation in 35.3b2).
        // The project stores no failed probe (M2), so an unprobed video is «not probed» here — either blocks a freeze (I8).
        return (print, new StudyVideo(Guid.Empty, Path.GetFileName(asset.FilePath), print.Before.SizeBytes, print.Sha256,
            asset.Metadata?.Duration.Ticks ?? 0, probe is not null, probe?.Tag, probe?.Origin, 0, null,
            FrameRate: Rate(asset.Metadata?.FrameRate), AvgFrameRate: Rate(asset.Metadata?.AvgFrameRate)));
    }

    private static StudyRate? Rate(Core.Common.FrameRate? rate) =>
        rate is { } r && r.Numerator > 0 && r.Denominator > 0 ? new StudyRate(r.Numerator, r.Denominator) : null;
}
