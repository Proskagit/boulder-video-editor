using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Export;

public enum ExportIssueSeverity
{
    /// <summary>The export cannot start.</summary>
    Error,
    /// <summary>The export can start; the user is told about the difference.</summary>
    Warning
}

public enum ExportIssueKind
{
    /// <summary>The timeline has no content (duration 0).</summary>
    EmptyTimeline,
    /// <summary>The canvas can't be encoded (H.264 4:2:0 needs even width and height).</summary>
    InvalidCanvas,
    /// <summary>No output file, not a full path, not <c>.mp4</c>, or an existing folder.</summary>
    InvalidOutputPath,
    OutputFolderMissing,
    /// <summary>The output would overwrite a media file of the project.</summary>
    OutputIsProjectMedia,
    /// <summary>ffmpeg is not available.</summary>
    EncoderUnavailable,
    /// <summary>The media is not in the project, marked missing, or its file is gone now.</summary>
    MediaOffline,
    /// <summary>The media's analysis has not completed (pending, running or failed).</summary>
    MediaNotAnalyzed,
    /// <summary>The clip can't play this media (e.g. a video clip on an image).</summary>
    MediaUnsupported,
    /// <summary>A text clip's font is not installed; the Preview's fallback font is used (warning).</summary>
    FontMissing
}

/// <summary>One problem found by <see cref="ExportPreflight"/>. Media and font problems name every
/// affected clip (<see cref="ClipIds"/>, in timeline order) under one issue per media file / font.</summary>
public sealed record ExportIssue(ExportIssueSeverity Severity, ExportIssueKind Kind, string Message)
{
    public Guid? AssetId { get; init; }

    /// <summary>Media file path or font family the issue is about, if any.</summary>
    public string? Subject { get; init; }

    public ImmutableArray<Guid> ClipIds { get; init; } = ImmutableArray<Guid>.Empty;
}

/// <summary>What the preflight needs from outside the model; <see cref="Default"/> uses the file system.</summary>
public sealed record ExportPreflightEnvironment(
    bool EncoderAvailable,
    Func<string, bool> IsFontInstalled,
    Func<string, bool> FileExists,
    Func<string, bool> DirectoryExists)
{
    public static ExportPreflightEnvironment Default(bool encoderAvailable, Func<string, bool> isFontInstalled) =>
        new(encoderAvailable, isFontInstalled, File.Exists, Directory.Exists);
}

/// <summary>Result of <see cref="ExportPreflight.Check"/>: every problem found, and the job when
/// nothing blocks the export.</summary>
public sealed record ExportPreflightResult(ImmutableArray<ExportIssue> Issues, ExportJob? Job)
{
    public bool CanExport => Job is not null;
    public IEnumerable<ExportIssue> Errors => Issues.Where(i => i.Severity == ExportIssueSeverity.Error);
    public IEnumerable<ExportIssue> Warnings => Issues.Where(i => i.Severity == ExportIssueSeverity.Warning);
}

/// <summary>
/// Checks, before an export starts, everything that would make it fail or differ from the Preview
/// (D023), and collects all problems at once. Must run on the thread that owns the project (UI thread):
/// it builds the export's <see cref="PlaybackSnapshot"/> itself, so the checks and the job describe the
/// same state.
/// <para>
/// Only clips that reach the output are checked — exactly the ones the snapshot plays: picture clips on
/// visible video tracks with opacity above 0; audio of clips on unmuted tracks that are not muted and have
/// a volume above 0; text clips on visible tracks with opacity above 0 and non-blank text. Media problems
/// (offline, not analysed, unsupported) block; a missing font is a warning.
/// </para>
/// </summary>
public static class ExportPreflight
{
    public static ExportPreflightResult Check(Project project, string? outputPath, ExportPreflightEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(environment);

        var snapshot = PlaybackSnapshotBuilder.Build(project, version: 0);
        var issues = new List<ExportIssue>();

        if (snapshot.Duration <= MediaTime.Zero)
            issues.Add(Error(ExportIssueKind.EmptyTimeline, "The timeline is empty."));

        var canvas = snapshot.Canvas;
        if (canvas.Width % 2 != 0 || canvas.Height % 2 != 0)
            issues.Add(Error(ExportIssueKind.InvalidCanvas,
                $"The frame size {canvas.Width} × {canvas.Height} can't be exported: width and height must be even."));

        var fullOutputPath = CheckOutputPath(project, outputPath, environment, issues);

        if (!environment.EncoderAvailable)
            issues.Add(Error(ExportIssueKind.EncoderUnavailable, "ffmpeg could not be found, so the video can't be encoded."));

        CheckMedia(project, snapshot, environment, issues);
        CheckFonts(snapshot, environment, issues);

        var ordered = issues.OrderBy(i => i.Severity).ToImmutableArray();
        var job = ordered.Any(i => i.Severity == ExportIssueSeverity.Error) ? null : new ExportJob(snapshot, fullOutputPath!);
        return new ExportPreflightResult(ordered, job);
    }

    private static string? CheckOutputPath(Project project, string? outputPath, ExportPreflightEnvironment env, List<ExportIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            issues.Add(Error(ExportIssueKind.InvalidOutputPath, "No output file was chosen."));
            return null;
        }

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(outputPath)) throw new ArgumentException("not a full path");
            full = Path.GetFullPath(outputPath);
            // .NET does not reject characters Windows can't store (e.g. '|') when normalizing a path.
            if (Path.GetFileName(full).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                (Path.GetDirectoryName(full) ?? "").IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ArgumentException("invalid characters");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(Error(ExportIssueKind.InvalidOutputPath, $"'{outputPath}' is not a valid output file path."));
            return null;
        }

        if (!string.Equals(Path.GetExtension(full), ExportFormat.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error(ExportIssueKind.InvalidOutputPath, $"The output file must be an {ExportFormat.FileExtension} file."));
            return null;
        }

        if (env.DirectoryExists(full))
        {
            issues.Add(Error(ExportIssueKind.InvalidOutputPath, $"'{full}' is a folder, not a file."));
            return null;
        }

        var folder = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(folder) || !env.DirectoryExists(folder))
            issues.Add(Error(ExportIssueKind.OutputFolderMissing, $"The folder '{folder}' does not exist."));

        if (project.MediaAssets.FirstOrDefault(a => SamePath(a.FilePath, full)) is { } media)
            issues.Add(Error(ExportIssueKind.OutputIsProjectMedia, $"The output would overwrite the project media '{media.FileName}'.") with
                { AssetId = media.Id, Subject = media.FilePath });

        return full;
    }

    private static void CheckMedia(Project project, PlaybackSnapshot snapshot, ExportPreflightEnvironment env, List<ExportIssue> issues)
    {
        var assets = project.MediaAssets.ToDictionary(a => a.Id);

        // (clip, asset, span status, timeline start) of every clip whose picture or sound reaches the output.
        var used = snapshot.VideoLayers.SelectMany(l => l.Spans)
            .Where(s => s.Visual.Opacity > 0)
            .Select(s => (s.ClipId, s.AssetId, s.Status, s.TimelineStart))
            .Concat(snapshot.AudioSpans
                .Where(s => s.EffectiveGain > 0)
                .Select(s => (s.ClipId, s.AssetId, s.Status, s.TimelineStart)));

        var problems = new Dictionary<(ExportIssueKind Kind, Guid AssetId), List<(Guid ClipId, MediaTime Start)>>();
        foreach (var (clipId, assetId, status, start) in used)
        {
            assets.TryGetValue(assetId, out var asset);
            var kind = Classify(asset, status, env);
            if (kind is null) continue;
            if (!problems.TryGetValue((kind.Value, assetId), out var clips))
                problems[(kind.Value, assetId)] = clips = new();
            if (!clips.Any(c => c.ClipId == clipId)) clips.Add((clipId, start));
        }

        foreach (var ((kind, assetId), clips) in problems
                     .OrderBy(p => p.Key.Kind).ThenBy(p => p.Value.Min(c => c.Start)))
        {
            assets.TryGetValue(assetId, out var asset);
            var name = asset?.FileName ?? "(media not in the project)";
            var count = clips.Count == 1 ? "1 clip" : $"{clips.Count} clips";
            var first = clips.Min(c => c.Start);
            var what = kind switch
            {
                ExportIssueKind.MediaOffline => "is offline",
                ExportIssueKind.MediaNotAnalyzed => asset?.AnalysisStatus == MediaAnalysisStatus.Failed
                    ? "could not be analysed" : "has not been analysed yet",
                _ => "is not supported by its clip"
            };
            issues.Add(Error(kind, $"'{name}' {what} ({count}, first at {first}).") with
            {
                AssetId = assetId,
                Subject = asset?.FilePath,
                ClipIds = clips.OrderBy(c => c.Start).Select(c => c.ClipId).ToImmutableArray()
            });
        }
    }

    private static ExportIssueKind? Classify(MediaAsset? asset, SpanStatus status, ExportPreflightEnvironment env)
    {
        if (asset is null || asset.IsMissing || !env.FileExists(asset.FilePath)) return ExportIssueKind.MediaOffline;
        if (status == SpanStatus.Unsupported) return ExportIssueKind.MediaUnsupported;
        if (asset.AnalysisStatus != MediaAnalysisStatus.Completed || asset.Metadata is null) return ExportIssueKind.MediaNotAnalyzed;
        return status is SpanStatus.Video or SpanStatus.StillImage or SpanStatus.Audio ? null : ExportIssueKind.MediaOffline;
    }

    private static void CheckFonts(PlaybackSnapshot snapshot, ExportPreflightEnvironment env, List<ExportIssue> issues)
    {
        var missing = snapshot.VideoLayers.SelectMany(l => l.Texts)
            .Where(t => t.Visual.Opacity > 0 && !string.IsNullOrWhiteSpace(t.Text.Text))
            .GroupBy(t => t.Text.FontFamily, StringComparer.OrdinalIgnoreCase)
            .Where(g => !env.IsFontInstalled(g.Key))
            .OrderBy(g => g.Min(t => t.TimelineStart));

        foreach (var font in missing)
        {
            var clips = font.OrderBy(t => t.TimelineStart).Select(t => t.ClipId).Distinct().ToImmutableArray();
            var count = clips.Length == 1 ? "1 text clip" : $"{clips.Length} text clips";
            issues.Add(new ExportIssue(ExportIssueSeverity.Warning, ExportIssueKind.FontMissing,
                $"The font '{font.Key}' is not installed ({count}); a fallback font is used, as in the Preview.")
            {
                Subject = font.Key,
                ClipIds = clips
            });
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static ExportIssue Error(ExportIssueKind kind, string message) => new(ExportIssueSeverity.Error, kind, message);
}
