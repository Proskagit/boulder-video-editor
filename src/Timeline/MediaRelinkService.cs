using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Timeline.Commands;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline;

/// <summary>
/// Relink of missing media (D026 §3; product owner decisions PO-1…PO-3, PO-6, PO-9 and the Step 11.4 refinements).
/// <list type="bullet">
/// <item><see cref="CheckAsync"/>: the media are re-checked first (PO-5); only an offline asset is relinked (PO-6). Hard
/// rejects: the file doesn't exist; its type (extension, then the probed streams) is not the asset's; ffprobe is
/// available but can't read it; for video / audio, its duration is shorter than the largest <c>SourceOut</c> of the
/// asset's clips (the <see cref="TimelineValidator"/> rule, so every later edit stays valid); the path belongs to another
/// asset (PO-9). Warnings, only when the old and the new metadata both exist: display size, frame rate, audio stream
/// gone, rotation, start time, codecs, sample rate, channels; and dissolve handles the new file is too short for. A
/// longer duration is no warning. ffprobe unavailable (PO-3): only existence and the extension are checked, with a
/// warning; the asset gets no metadata and stays <see cref="MediaAnalysisStatus.Pending"/> (analysed at a later Open —
/// the ffprobe location is fixed for the app run).</item>
/// <item><see cref="ApplyAsync"/>: re-checks the media and validates again what may have changed since the check, then
/// executes one <see cref="RelinkMediaCommand"/> through the undo history (PO-1). Clips are never adapted.</item>
/// <item><see cref="SearchFolderAsync"/> (D026 §4, Step 11.5): the offline items' exact file names among the files directly
/// in one folder, each match checked by the same <c>CheckCoreAsync</c>; a name of several offline items is given to none.
/// <see cref="ApplyAllAsync"/> applies the confirmed checks as one command after validating each again and within the
/// batch; <see cref="ApplyAsync"/> is it with one item.</item>
/// <item>A later analysis (e.g. after a relink without ffprobe) that finds a file shorter than its clips use, or without
/// the streams of its kind, raises <see cref="RelinkedMediaFoundIncompatible"/> and logs it; nothing is undone.</item>
/// </list>
/// Members are called on the UI thread; the file system and the probe run off it.
/// </summary>
public sealed class MediaRelinkService : IMediaRelinkService
{
    private readonly IProjectService _projects;
    private readonly IUndoRedoService _undoRedo;
    private readonly IMediaAnalysisService _analysis;
    private readonly ILogger<MediaRelinkService> _logger;
    private readonly Func<string, long?> _fileSize;
    private readonly Func<string, IReadOnlyList<string>?> _listFiles;

    // The metadata each asset had when it was last looked at for a later incompatibility (by reference: a new analysis
    // is a new object).
    private readonly Dictionary<MediaAsset, MediaMetadata> _seenMetadata = new(ReferenceEqualityComparer.Instance);

    public MediaRelinkService(IProjectService projects, IUndoRedoService undoRedo, IMediaAnalysisService analysis,
        ILogger<MediaRelinkService> logger)
        : this(projects, undoRedo, analysis, logger, SizeOf, ListFiles)
    {
    }

    /// <param name="fileSize">The size of a file, or null when it isn't there (tests replace the file system).</param>
    /// <param name="listFiles">The files directly in a folder (full paths), or null when it isn't there.</param>
    internal MediaRelinkService(IProjectService projects, IUndoRedoService undoRedo, IMediaAnalysisService analysis,
        ILogger<MediaRelinkService> logger, Func<string, long?> fileSize, Func<string, IReadOnlyList<string>?>? listFiles = null)
    {
        _listFiles = listFiles ?? ListFiles;
        _projects = projects;
        _undoRedo = undoRedo;
        _analysis = analysis;
        _logger = logger;
        _fileSize = fileSize;
        _projects.ProjectChanged += (_, _) => _seenMetadata.Clear();
        _projects.MediaAssetsChanged += (_, _) => LookForIncompatibleMedia();
        LookForIncompatibleMedia();
    }

    public event EventHandler<RelinkedMediaIncompatibleEventArgs>? RelinkedMediaFoundIncompatible;

    private static long? SizeOf(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : null;
    }

    private Task<long?> FileSizeAsync(string path) => Task.Run(() => SizeOrNull(path));

    // ---- Check ----------------------------------------------------------------------------------------------------

    public async Task<RelinkCheck> CheckAsync(Guid assetId, string filePath, CancellationToken ct = default)
    {
        string path;
        try
        {
            path = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Reject(assetId, filePath, Guid.Empty, RelinkRejection.FileNotFound, $"\"{filePath}\" is not a valid file path.");
        }

        await _projects.RecheckMediaAsync(ct);                       // PO-5: what is offline now, not at the last check
        return await CheckCoreAsync(_projects.Current, assetId, path, ct);
    }

    /// <summary>Every rule of a relink check (D026 §3) for an already re-checked project — the one implementation, used
    /// by <see cref="CheckAsync"/> and, per candidate, by <see cref="SearchFolderAsync"/>.</summary>
    private async Task<RelinkCheck> CheckCoreAsync(Core.Entities.Project project, Guid assetId, string path, CancellationToken ct)
    {
        if (Basics(project, assetId, path) is { } basic)
            return basic;
        var asset = project.MediaAssets.First(a => a.Id == assetId);

        var size = await FileSizeAsync(path);
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(project, _projects.Current))
            return Reject(assetId, path, project.Id, RelinkRejection.Stale, "Another project is open now.");
        if (size is not { } bytes)
            return Reject(assetId, path, project.Id, RelinkRejection.FileNotFound, $"\"{Path.GetFileName(path)}\" doesn't exist.");

        var result = await _analysis.AnalyzeAsync(path, ct);
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(project, _projects.Current))
            return Reject(assetId, path, project.Id, RelinkRejection.Stale, "Another project is open now.");

        switch (result.Outcome)
        {
            case MediaAnalysisOutcome.Success when result.Metadata is { } metadata:
            {
                if (StreamProblem(asset.Kind, metadata, Path.GetFileName(path)) is { } streams)
                    return Reject(assetId, path, project.Id, RelinkRejection.WrongMediaType, streams);
                if (TooShort(project, asset, metadata, Path.GetFileName(path)) is { } shortBy)
                    return Reject(assetId, path, project.Id, RelinkRejection.TooShort, shortBy);
                return new RelinkCheck
                {
                    AssetId = assetId, FilePath = path, ProjectId = project.Id, FileSizeBytes = bytes, Metadata = metadata,
                    Warnings = Warnings(project, asset, metadata)
                };
            }
            case MediaAnalysisOutcome.ProbeToolUnavailable:
                // PO-3: not blocked; what can't be checked is said, never turned into an error.
                return new RelinkCheck
                {
                    AssetId = assetId, FilePath = path, ProjectId = project.Id, FileSizeBytes = bytes,
                    Warnings = new[]
                    {
                        new RelinkWarning(RelinkWarningKind.NotChecked,
                            "ffprobe is not available, so only the file type was checked. The media stays unanalysed — its clips " +
                            "can't be edited or exported until it has been analysed (when the project is opened with ffprobe).")
                    }
                };
            case MediaAnalysisOutcome.FileNotFound:
                return Reject(assetId, path, project.Id, RelinkRejection.FileNotFound, $"\"{Path.GetFileName(path)}\" doesn't exist.");
            case MediaAnalysisOutcome.Cancelled:
                ct.ThrowIfCancellationRequested();
                goto default;
            default:
                // ffprobe ran and could not read it (product owner, Step 11.4): its type can't be confirmed.
                return Reject(assetId, path, project.Id, RelinkRejection.UnreadableMedia,
                    $"\"{Path.GetFileName(path)}\" can't be read as media.{(result.ErrorMessage is { } m ? " " + m : "")}");
        }
    }

    /// <summary>The checks that need neither the file system nor a probe; null when they pass.</summary>
    private static RelinkCheck? Basics(Core.Entities.Project project, Guid assetId, string path)
    {
        if (project.MediaAssets.FirstOrDefault(a => a.Id == assetId) is not { } asset)
            return Reject(assetId, path, project.Id, RelinkRejection.AssetNotFound, "The media is not in the project.");
        if (!asset.IsMissing)
            return Reject(assetId, path, project.Id, RelinkRejection.NotOffline,
                $"\"{asset.FileName}\" is not offline — its file is there, nothing to relink.");

        var kind = MediaFileTypes.KindOf(path);
        if (kind is null)
            return Reject(assetId, path, project.Id, RelinkRejection.WrongMediaType,
                $"\"{Path.GetFileName(path)}\" is not a supported media file.");
        if (kind != asset.Kind)
            return Reject(assetId, path, project.Id, RelinkRejection.WrongMediaType,
                $"\"{Path.GetFileName(path)}\" is {Article(kind.Value)} file; \"{asset.FileName}\" is {Article(asset.Kind)}.");

        if (project.MediaAssets.FirstOrDefault(a => a.Id != assetId && SamePath(a.FilePath, path)) is { } other)
            return Reject(assetId, path, project.Id, RelinkRejection.PathInUse,
                $"\"{Path.GetFileName(path)}\" is already in the project as \"{other.FileName}\" — two media items can't share a file.");
        return null;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Article(MediaKind kind) => kind switch
    {
        MediaKind.Video => "a video",
        MediaKind.Audio => "an audio",
        _ => "an image"
    };

    private static RelinkCheck Reject(Guid assetId, string path, Guid projectId, RelinkRejection rejection, string message) =>
        new() { AssetId = assetId, FilePath = path, ProjectId = projectId, Rejection = rejection, Message = message };

    /// <summary>The probed file lacks what its kind needs (a hard reject: another kind of media).</summary>
    private static string? StreamProblem(MediaKind kind, MediaMetadata metadata, string name) => kind switch
    {
        MediaKind.Video when metadata.VideoCodec is null => $"\"{name}\" has no video stream.",
        MediaKind.Audio when metadata.AudioCodec is null => $"\"{name}\" has no audio stream.",
        MediaKind.Image when metadata.Width is not > 0 || metadata.Height is not > 0 => $"\"{name}\" has no picture.",
        _ => null
    };

    /// <summary>The largest source end of the asset's video / audio clips on any track (the validator's
    /// <c>SourceOut ≤ Duration</c> rule); null without such clips.</summary>
    private static MediaTime? RequiredDuration(Core.Entities.Project project, Guid assetId)
    {
        MediaTime? required = null;
        foreach (var clip in AllClips(project).OfType<MediaBackedClip>())
        {
            if (clip.MediaAssetId != assetId || clip is not (VideoClip or AudioClip)) continue;
            if (required is not { } r || clip.SourceOut > r) required = clip.SourceOut;
        }
        return required;
    }

    private static IEnumerable<Clip> AllClips(Core.Entities.Project project) =>
        project.Timeline.VideoTracks.Concat(project.Timeline.AudioTracks).SelectMany(t => t.Clips);

    private static string? TooShort(Core.Entities.Project project, MediaAsset asset, MediaMetadata metadata, string name)
    {
        if (asset.Kind == MediaKind.Image || RequiredDuration(project, asset.Id) is not { } required) return null;
        if (metadata.Duration > MediaTime.Zero && metadata.Duration >= required) return null;
        return metadata.Duration > MediaTime.Zero
            ? $"\"{name}\" is {Time(metadata.Duration)} long, but the clips of \"{asset.FileName}\" use it up to {Time(required)}."
            : $"The length of \"{name}\" is unknown, but the clips of \"{asset.FileName}\" use it up to {Time(required)}.";
    }

    private static string Time(MediaTime time) => time.ToTimeSpan().ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    // ---- Warnings ---------------------------------------------------------------------------------------------------

    private static IReadOnlyList<RelinkWarning> Warnings(Core.Entities.Project project, MediaAsset asset, MediaMetadata now)
    {
        var warnings = new List<RelinkWarning>();
        if (asset.Metadata is { } was)
        {
            var visual = asset.Kind is MediaKind.Video or MediaKind.Image;
            var timed = asset.Kind is MediaKind.Video or MediaKind.Audio;
            var bothHaveAudio = was.AudioCodec is not null && now.AudioCodec is not null;

            if (visual && Size(was) is { } a && Size(now) is { } b && a != b)
                warnings.Add(new(RelinkWarningKind.DisplaySize, $"Picture size: {a.W}×{a.H} before, {b.W}×{b.H} now."));
            if (asset.Kind == MediaKind.Video && Rate(was) is { } ra && Rate(now) is { } rb && ra != rb)
                warnings.Add(new(RelinkWarningKind.FrameRate, $"Frame rate: {Fps(ra)} before, {Fps(rb)} now."));
            if (asset.Kind == MediaKind.Video && was.AudioCodec is not null && now.AudioCodec is null)
                warnings.Add(new(RelinkWarningKind.NoAudio, "The new file has no sound; its clips will be silent."));
            if (visual && (was.DisplayRotation ?? 0) != (now.DisplayRotation ?? 0))
                warnings.Add(new(RelinkWarningKind.Rotation, $"Rotation: {was.DisplayRotation ?? 0}° before, {now.DisplayRotation ?? 0}° now."));
            if (timed && (was.StartTime ?? MediaTime.Zero) != (now.StartTime ?? MediaTime.Zero))
                warnings.Add(new(RelinkWarningKind.StartTime,
                    $"Start time: {Time(was.StartTime ?? MediaTime.Zero)} before, {Time(now.StartTime ?? MediaTime.Zero)} now — the clips may show other moments."));
            if (visual && Differs(was.VideoCodec, now.VideoCodec))
                warnings.Add(new(RelinkWarningKind.VideoCodec, $"Video codec: {was.VideoCodec} before, {now.VideoCodec} now."));
            if (bothHaveAudio && Differs(was.AudioCodec, now.AudioCodec))
                warnings.Add(new(RelinkWarningKind.AudioCodec, $"Audio codec: {was.AudioCodec} before, {now.AudioCodec} now."));
            if (bothHaveAudio && was.AudioSampleRate is { } sa && now.AudioSampleRate is { } sb && sa != sb)
                warnings.Add(new(RelinkWarningKind.SampleRate, $"Sample rate: {sa} Hz before, {sb} Hz now."));
            if (bothHaveAudio && was.AudioChannels is { } ca && now.AudioChannels is { } cb && ca != cb)
                warnings.Add(new(RelinkWarningKind.Channels, $"Audio channels: {ca} before, {cb} now."));
        }

        if (ShortDissolves(project, asset, now) is var count and > 0)
            warnings.Add(new(RelinkWarningKind.DissolveHandles, count == 1
                ? "The new file has too little media beyond a clip for its dissolve; the dissolve will hold a frame."
                : $"The new file has too little media beyond its clips for {count} dissolves; they will hold a frame."));
        return warnings;
    }

    private static (int W, int H)? Size(MediaMetadata m) =>
        m.DisplayWidth is > 0 and var dw && m.DisplayHeight is > 0 and var dh ? (dw, dh)
        : m.Width is > 0 and var w && m.Height is > 0 and var h ? (w, h) : null;

    private static FrameRate? Rate(MediaMetadata m) => m.FrameRate is { IsValid: true } r ? r : m.AvgFrameRate is { IsValid: true } a ? a : null;

    private static string Fps(FrameRate r) => (r.Numerator / (double)r.Denominator).ToString("0.###", CultureInfo.InvariantCulture) + " fps";

    private static bool Differs(string? a, string? b) => a is not null && b is not null && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Dissolves of a clip of the asset whose zone needs more source than the new file has (D025 §4).</summary>
    private static int ShortDissolves(Core.Entities.Project project, MediaAsset asset, MediaMetadata now)
    {
        if (asset.Kind != MediaKind.Video) return 0;
        var rate = project.Settings.FrameRate;
        var relinked = new MediaAsset { Id = asset.Id, FilePath = asset.FilePath, Kind = asset.Kind, Metadata = now };
        var assets = project.MediaAssets.ToDictionary(a => a.Id);
        MediaAsset? AssetOf(Clip clip) => clip is MediaBackedClip m
            ? m.MediaAssetId == asset.Id ? relinked : assets.GetValueOrDefault(m.MediaAssetId)
            : null;

        var count = 0;
        foreach (var track in project.Timeline.VideoTracks)
        {
            foreach (var transition in track.Transitions)
            {
                var left = track.Clips.FirstOrDefault(c => c.Id == transition.LeftClipId);
                var right = track.Clips.FirstOrDefault(c => c.Id == transition.RightClipId);
                if (left is null || right is null) continue;
                var leftUses = left is MediaBackedClip l && l.MediaAssetId == asset.Id;
                var rightUses = right is MediaBackedClip r && r.MediaAssetId == asset.Id;
                if (!leftUses && !rightUses) continue;

                var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(transition.Duration, rate));
                if (leftUses && afterCut > DissolveHandles.After(left, ClipState.Capture(left), AssetOf(left), rate) ||
                    rightUses && beforeCut > DissolveHandles.Before(right, ClipState.Capture(right), AssetOf(right), rate))
                    count++;
            }
        }
        return count;
    }

    // ---- Apply ------------------------------------------------------------------------------------------------------

    public async Task<RelinkResult> ApplyAsync(RelinkCheck check, CancellationToken ct = default) =>
        (await ApplyAllAsync(new[] { check }, ct)).Results[0];

    public async Task<RelinkBatchResult> ApplyAllAsync(IReadOnlyList<RelinkCheck> checks, CancellationToken ct = default)
    {
        var results = new RelinkResult?[checks.Count];
        for (var i = 0; i < checks.Count; i++)
        {
            if (checks[i].Rejection is { } rejected)
                results[i] = new RelinkResult(false, rejected, checks[i].Message, checks[i].AssetId);
        }
        if (results.All(r => r is not null))
            return new RelinkBatchResult(results!);

        await _projects.RecheckMediaAsync(ct);                       // PO-5, again: the user may have taken a while
        var project = _projects.Current;

        // Every file of the batch, off the UI thread in one go (a slow share answers once per file, not per await).
        var paths = checks.Select(c => c.FilePath).ToArray();
        long?[] sizes;
        try
        {
            sizes = await Task.Run(() => paths.Select(SizeOrNull).ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the files of a relink.");
            sizes = new long?[paths.Length];
        }
        ct.ThrowIfCancellationRequested();

        var relinks = new List<MediaRelink>();
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedAssets = new HashSet<Guid>();
        for (var i = 0; i < checks.Count; i++)
        {
            if (results[i] is not null) continue;
            var check = checks[i];
            results[i] = Validate(check, sizes[i]);
            if (results[i] is not null) continue;

            var asset = project.MediaAssets.First(a => a.Id == check.AssetId);
            var after = new MediaFileState(check.FilePath, sizes[i]!.Value, check.Metadata,
                check.Metadata is null ? MediaAnalysisStatus.Pending : MediaAnalysisStatus.Completed, null, IsMissing: false);
            relinks.Add(new MediaRelink(asset, MediaFileState.Capture(asset), after));
            results[i] = new RelinkResult(true, null, $"\"{asset.FileName}\" is linked to {check.FilePath}.", asset.Id);
        }

        if (relinks.Count > 0)
        {
            // One step for everything applied together (PO-1, PO-4): Undo / Redo take the whole batch.
            var description = relinks.Count == 1 ? "Relink Media" : $"Relink {relinks.Count} Media Files";
            _undoRedo.Execute(new RelinkMediaCommand(relinks, _projects.NotifyMediaRelinked, description));
            foreach (var r in relinks)
                _logger.LogInformation("Relinked '{Old}' to '{New}' ({Probe}).", r.Before.FilePath, r.After.FilePath,
                    r.After.Metadata is null ? "not probed: ffprobe unavailable" : "probed");
        }
        return new RelinkBatchResult(results!);

        // What may have changed since the check (the project, the asset, the path, the file, the clips); null = applicable.
        RelinkResult? Validate(RelinkCheck check, long? size)
        {
            if (!ReferenceEquals(project, _projects.Current) || project.Id != check.ProjectId)
                return new RelinkResult(false, RelinkRejection.Stale, "Another project is open now.", check.AssetId);
            if (Basics(project, check.AssetId, check.FilePath) is { } basic)
                return new RelinkResult(false, basic.Rejection, basic.Message, check.AssetId);
            if (!claimedAssets.Add(check.AssetId))
                return new RelinkResult(false, RelinkRejection.Stale,
                    "The media item is already relinked by another entry of this batch.", check.AssetId);
            if (!claimedPaths.Add(check.FilePath))
                return new RelinkResult(false, RelinkRejection.PathInUse,
                    $"\"{Path.GetFileName(check.FilePath)}\" is chosen for another media item of this batch — two media items can't share a file.",
                    check.AssetId);
            if (size is not { } bytes)
                return new RelinkResult(false, RelinkRejection.FileNotFound,
                    $"\"{Path.GetFileName(check.FilePath)}\" doesn't exist any more.", check.AssetId);
            if (bytes != check.FileSizeBytes)
                return new RelinkResult(false, RelinkRejection.Stale,
                    $"\"{Path.GetFileName(check.FilePath)}\" changed since it was checked; check it again.", check.AssetId);
            var asset = project.MediaAssets.First(a => a.Id == check.AssetId);
            if (check.Metadata is { } metadata && TooShort(project, asset, metadata, Path.GetFileName(check.FilePath)) is { } shortBy)
                return new RelinkResult(false, RelinkRejection.TooShort, shortBy, check.AssetId);  // a clip was lengthened meanwhile
            return null;
        }
    }

    private long? SizeOrNull(string path)
    {
        try
        {
            return _fileSize(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not read '{Path}'; treating it as missing.", path);
            return null;
        }
    }

    // ---- Batch search (D026 §4, PO-4) ---------------------------------------------------------------------------------

    public async Task<RelinkSearch> SearchFolderAsync(string folder, CancellationToken ct = default)
    {
        string fullFolder;
        try
        {
            fullFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new RelinkSearch { Folder = folder, Problem = $"\"{folder}\" is not a valid folder path." };
        }

        await _projects.RecheckMediaAsync(ct);                       // PO-5: which media are offline now
        var project = _projects.Current;
        var offline = project.MediaAssets.Where(a => a.IsMissing).ToList();
        if (offline.Count == 0)
            return new RelinkSearch { Folder = fullFolder };

        // The folder's own files only — no subfolders (PO-4) —, listed off the UI thread (a slow or remote folder).
        IReadOnlyList<string>? files;
        try
        {
            files = await Task.Run(() => _listFiles(fullFolder), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the files of {Folder}.", fullFolder);
            files = null;
        }
        ct.ThrowIfCancellationRequested();
        if (files is null)
            return new RelinkSearch { Folder = fullFolder, Problem = $"The folder \"{fullFolder}\" can't be read." };
        if (!ReferenceEquals(project, _projects.Current))
            return new RelinkSearch { Folder = fullFolder, Problem = "Another project is open now." };

        // Exact names, compared as the Windows file system does (case-insensitive); a name that belongs to more than one
        // offline item is never given to any of them (no automatic choice, PO-4).
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            byName.TryAdd(Path.GetFileName(file), file);
        var nameCounts = offline.GroupBy(a => a.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var entries = new List<RelinkSearchEntry>();
        foreach (var asset in offline)
        {
            if (!byName.TryGetValue(asset.FileName, out var candidate))
            {
                entries.Add(new RelinkSearchEntry
                {
                    AssetId = asset.Id, FileName = asset.FileName, Outcome = RelinkSearchOutcome.NotFound,
                    Message = $"\"{asset.FileName}\" is not in the folder."
                });
                continue;
            }
            if (nameCounts[asset.FileName] > 1)
            {
                entries.Add(new RelinkSearchEntry
                {
                    AssetId = asset.Id, FileName = asset.FileName, Outcome = RelinkSearchOutcome.Ambiguous, CandidatePath = candidate,
                    Message = $"{nameCounts[asset.FileName]} offline media items are named \"{asset.FileName}\"; relink them one by one."
                });
                continue;
            }

            var check = await CheckCoreAsync(project, asset.Id, candidate, ct);   // the one set of rules (D026 §3)
            entries.Add(new RelinkSearchEntry
            {
                AssetId = asset.Id, FileName = asset.FileName, CandidatePath = candidate, Check = check,
                Outcome = check.CanApply ? RelinkSearchOutcome.Found : RelinkSearchOutcome.Rejected,
                Message = check.CanApply ? "" : check.Message
            });
        }
        return new RelinkSearch { Folder = fullFolder, Entries = entries };
    }

    private static IReadOnlyList<string>? ListFiles(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).ToList() : null;

    // ---- Later incompatibility --------------------------------------------------------------------------------------

    private void LookForIncompatibleMedia()
    {
        var project = _projects.Current;
        foreach (var asset in project.MediaAssets)
        {
            if (asset.AnalysisStatus != MediaAnalysisStatus.Completed || asset.Metadata is not { } metadata) continue;
            if (_seenMetadata.TryGetValue(asset, out var seen) && ReferenceEquals(seen, metadata)) continue;
            _seenMetadata[asset] = metadata;

            var problem = StreamProblem(asset.Kind, metadata, asset.FileName) ?? TooShort(project, asset, metadata, asset.FileName);
            if (problem is null) continue;

            var message = $"{problem} If it was just relinked, Undo brings the previous file back.";
            _logger.LogWarning("Media found incompatible with its clips after analysis: {Path} — {Problem}", asset.FilePath, problem);
            RelinkedMediaFoundIncompatible?.Invoke(this, new RelinkedMediaIncompatibleEventArgs { Asset = asset, Message = message });
        }
    }
}
