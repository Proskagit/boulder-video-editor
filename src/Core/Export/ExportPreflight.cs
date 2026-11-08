using System.Collections.Immutable;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Motion;
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
    /// <summary>The canvas can't be exported: it breaks <see cref="ProjectSettingsRules.CanvasError"/> (even sides,
    /// 64–4096 px each, at most the H.264 level 5.1 frame size; D028).</summary>
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
    FontMissing,
    /// <summary>The In / Out range to export has no part of the timeline in it (D030 §8).</summary>
    EmptyRange,
    /// <summary>The media's metadata states HDR in a combination the HDR → SDR rule does not convert (D033 §3, OQ1): its
    /// picture is exported unconverted, as the Preview shows it (warning).</summary>
    HdrNotConverted,
    /// <summary>The camera motion of a clip with holds (hold tracking) is still being analysed: the export waits for it, so
    /// its contours follow the wall exactly as in the Preview.</summary>
    HoldTrackingPending,
    /// <summary>The camera-motion analysis of a clip with holds failed (R2 → E, K2): its contours would be exported where
    /// they were marked, without the camera's motion — a warning, so the user cancels or exports knowingly.</summary>
    HoldTrackingFailed,
    /// <summary>On some or most frames of a clip with holds the camera's position is not reliably known (R2 → E, K2): its
    /// contours are left out there — a warning with the share of such frames.</summary>
    HoldTrackingUnreliable,
    /// <summary>A heart-rate overlay (Phase 35.5) has no data where it is exported — no usable training, or a video below it
    /// without a recording zero: it shows nothing there (warning, D049 §10).</summary>
    HeartRateUnavailable
}

/// <summary>The camera-motion tracks the export draws the hold contours with (the Preview's own), and the clips whose
/// analysis has not finished (hold tracking).</summary>
public sealed record ExportCameraMotion(IReadOnlyDictionary<Guid, CameraMotionTrack> Tracks, IReadOnlySet<Guid> Pending)
{
    /// <summary>The clips whose analysis failed (their contours are drawn as marked).</summary>
    public IReadOnlySet<Guid> Failed { get; init; } = new HashSet<Guid>();

    /// <summary>The clips with unreliable frames, with the share of such frames (their contours are hidden there).</summary>
    public IReadOnlyDictionary<Guid, double> Unreliable { get; init; } = new Dictionary<Guid, double>();

    public static readonly ExportCameraMotion None =
        new(new Dictionary<Guid, CameraMotionTrack>(), new HashSet<Guid>());
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
    /// <summary>Hold tracking: the tracks and the clips still analysed; none — contours where they were marked.</summary>
    public ExportCameraMotion CameraMotion { get; init; } = ExportCameraMotion.None;

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
/// (offline, not analysed, unsupported) block; a missing font is a warning. A range export (D030 §8, Q16) checks
/// only what reaches its frames: a picture shown in it (the clip with its dissolve zones), a sound placed in its
/// samples, a text in it — media used only outside the range don't block it; every other check is unchanged.
/// </para>
/// </summary>
public static class ExportPreflight
{
    /// <param name="range">Only these timeline frames (the In / Out range, clamped to the sequence here); null = the
    /// whole sequence, exactly as before.</param>
    public static ExportPreflightResult Check(Project project, string? outputPath, ExportPreflightEnvironment environment,
        ExportRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(environment);

        var snapshot = PlaybackSnapshotBuilder.Build(project, version: 0, environment.CameraMotion.Tracks);
        var issues = new List<ExportIssue>();

        if (snapshot.Duration <= MediaTime.Zero)
            issues.Add(Error(ExportIssueKind.EmptyTimeline, Messages.Preflight_EmptyTimeline));

        // The canvas rules of D028 (Step 13.4, P-1): a file may carry any positive size (written by hand), but only a
        // size the user could choose is exported; ProjectSettingsRules is the one source of these limits.
        var canvas = snapshot.Canvas;
        if (ProjectSettingsRules.CanvasError(canvas.Width, canvas.Height) is { } canvasError)
            issues.Add(Error(ExportIssueKind.InvalidCanvas,
                string.Format(Messages.Preflight_CanvasError, canvas.Width, canvas.Height, canvasError)));

        var fullOutputPath = CheckOutputPath(project, outputPath, environment, issues);

        if (!environment.EncoderAvailable)
            issues.Add(Error(ExportIssueKind.EncoderUnavailable, Messages.Preflight_NoFfmpeg));

        // The range within the sequence; the window of timeline time and samples the output covers.
        ExportRange? exported = null;
        var window = Window.Everything;
        if (range is { } r && snapshot.Duration > MediaTime.Zero)
        {
            var total = ExportOutput.For(snapshot).FrameCount;
            var clamped = new ExportRange(Math.Max(0, r.FirstFrame), Math.Min(r.EndFrame, total));
            if (clamped.FrameCount <= 0)
            {
                issues.Add(Error(ExportIssueKind.EmptyRange, Messages.Preflight_EmptyRange));
            }
            else
            {
                exported = clamped;
                var output = ExportOutput.For(snapshot, clamped);
                window = new Window(MediaTime.FromFrame(clamped.FirstFrame, snapshot.FrameRate), MediaTime.FromFrame(clamped.EndFrame, snapshot.FrameRate),
                    output.FirstSample, output.FirstSample + output.AudioSampleCount);
            }
        }

        CheckMedia(project, snapshot, environment, issues, window);
        CheckHoldTracking(project, snapshot, environment, issues, window);
        CheckFonts(snapshot, environment, issues, window);
        CheckHeartRate(project, snapshot, issues, window);

        var ordered = issues.OrderBy(i => i.Severity).ToImmutableArray();
        var job = ordered.Any(i => i.Severity == ExportIssueSeverity.Error)
            ? null
            : new ExportJob(snapshot, fullOutputPath!, project.Settings.Export, exported);
        return new ExportPreflightResult(ordered, job);
    }

    /// <summary>The part of the timeline the output covers: <c>[Start, End)</c> in time, <c>[FirstSample, EndSample)</c>
    /// in timeline samples.</summary>
    private readonly record struct Window(MediaTime Start, MediaTime End, long FirstSample, long EndSample)
    {
        public static readonly Window Everything = new(new MediaTime(long.MinValue), new MediaTime(long.MaxValue), long.MinValue, long.MaxValue);
        public bool Shows(MediaTime start, MediaTime end) => start < End && end > Start;
        public bool Plays(AudioPlacement placement) => placement.FirstSample < EndSample && placement.EndSample > FirstSample;
    }

    private static string? CheckOutputPath(Project project, string? outputPath, ExportPreflightEnvironment env, List<ExportIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            issues.Add(Error(ExportIssueKind.InvalidOutputPath, Messages.Preflight_NoOutput));
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
            issues.Add(Error(ExportIssueKind.InvalidOutputPath, string.Format(Messages.Preflight_NotValidPath, outputPath)));
            return null;
        }

        if (!string.Equals(Path.GetExtension(full), ExportFormat.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error(ExportIssueKind.InvalidOutputPath, string.Format(Messages.Preflight_Extension, ExportFormat.FileExtension)));
            return null;
        }

        if (env.DirectoryExists(full))
        {
            issues.Add(Error(ExportIssueKind.InvalidOutputPath, string.Format(Messages.Preflight_IsFolder, full)));
            return null;
        }

        var folder = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(folder) || !env.DirectoryExists(folder))
            issues.Add(Error(ExportIssueKind.OutputFolderMissing, string.Format(Messages.Preflight_NoFolder, folder)));

        if (project.MediaAssets.FirstOrDefault(a => SamePath(a.FilePath, full)) is { } media)
            issues.Add(Error(ExportIssueKind.OutputIsProjectMedia, string.Format(Messages.Preflight_OverwritesMedia, media.FileName)) with
                { AssetId = media.Id, Subject = media.FilePath });

        return full;
    }

    private static void CheckMedia(Project project, PlaybackSnapshot snapshot, ExportPreflightEnvironment env, List<ExportIssue> issues,
        Window window)
    {
        var assets = project.MediaAssets.ToDictionary(a => a.Id);

        // (clip, asset, span status, timeline start) of every clip whose picture or sound reaches the output — in the
        // range: the picture where it is shown (with its dissolve zones), the sound where it is placed.
        var used = snapshot.VideoLayers.SelectMany(l => l.Spans)
            .Where(s => s.Visual.Opacity > 0 && window.Shows(s.ShownStart, s.ShownEnd))
            .Select(s => (s.ClipId, s.AssetId, s.Status, s.TimelineStart))
            .Concat(snapshot.AudioSpans
                .Where(s => s.EffectiveGain > 0 && window.Plays(AudioPlacement.Of(s)))
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
            var name = asset?.FileName ?? Messages.Preflight_MediaNotInProject;
            var count = Plural.Format(clips.Count, Messages.Plural_Clips);
            var first = clips.Min(c => c.Start);
            var message = kind switch
            {
                ExportIssueKind.MediaOffline => Messages.Preflight_MediaOffline,
                ExportIssueKind.MediaNotAnalyzed => asset?.AnalysisStatus == MediaAnalysisStatus.Failed
                    ? Messages.Preflight_AnalysisFailed : Messages.Preflight_NotAnalysedYet,
                _ => Messages.Preflight_Unsupported
            };
            issues.Add(Error(kind, string.Format(message, name, count, first)) with
            {
                AssetId = assetId,
                Subject = asset?.FilePath,
                ClipIds = clips.OrderBy(c => c.Start).Select(c => c.ClipId).ToImmutableArray()
            });
        }

        // D033 OQ1: a picture whose metadata states HDR in an unsupported combination is exported unconverted — a
        // warning naming the file and the reason; the export is allowed. SDR and untagged media are never warned about.
        var unconverted = snapshot.VideoLayers.SelectMany(l => l.Spans)
            .Where(s => s.Status == SpanStatus.Video && s.Visual.Opacity > 0 && window.Shows(s.ShownStart, s.ShownEnd))
            .Where(s => !problems.Keys.Any(k => k.AssetId == s.AssetId))
            .Select(s => (Span: s, Asset: assets.GetValueOrDefault(s.AssetId)))
            .Where(p => p.Asset is not null && HdrColor.Classify(p.Asset.Kind, p.Asset.Metadata).Kind == HdrKind.UnsupportedHdr)
            .GroupBy(p => p.Asset!.Id)
            .OrderBy(g => g.Min(p => p.Span.TimelineStart));
        foreach (var group in unconverted)
        {
            var asset = group.First().Asset!;
            var clips = group.OrderBy(p => p.Span.TimelineStart).Select(p => p.Span.ClipId).Distinct().ToImmutableArray();
            var count = Plural.Format(clips.Length, Messages.Plural_Clips);
            var reason = HdrColor.Classify(asset.Kind, asset.Metadata).Reason;
            issues.Add(new ExportIssue(ExportIssueSeverity.Warning, ExportIssueKind.HdrNotConverted,
                string.Format(Messages.Preflight_HdrNotConverted, asset.FileName, reason, count, group.Min(p => p.Span.TimelineStart)))
            {
                AssetId = asset.Id,
                Subject = asset.FilePath,
                ClipIds = clips
            });
        }
    }

    /// <summary>
    /// D049 §10 (Phase 35.5): a heart-rate overlay that reaches the output without data is a warning to confirm — no training
    /// or a training not usable (one issue naming the overlays), or a video right below an overlay whose recording zero is
    /// unknown (one issue per file, naming its clips). The picture below an overlay is resolved exactly as the composition
    /// does (<see cref="PlaybackSnapshot.PictureBelow"/>) on every stretch between the clip edges below it. A project without
    /// a heart-rate overlay is never checked: the training never affects its export.
    /// </summary>
    private static void CheckHeartRate(Project project, PlaybackSnapshot snapshot, List<ExportIssue> issues, Window window)
    {
        var overlays = snapshot.VideoLayers
            .SelectMany((layer, index) => layer.Templates.Select(t => (Template: t, Index: index)))
            .Where(o => o.Template.Kind == TemplateKind.HeartRate && window.Shows(o.Template.TimelineStart, o.Template.TimelineEnd))
            .ToList();
        if (overlays.Count == 0) return;

        var overlayIds = overlays.OrderBy(o => o.Template.TimelineStart).Select(o => o.Template.ClipId).ToImmutableArray();
        if (project.Training is null)
        {
            issues.Add(new ExportIssue(ExportIssueSeverity.Warning, ExportIssueKind.HeartRateUnavailable, Messages.Preflight_HeartRateNoTraining)
                { ClipIds = overlayIds });
            return;
        }
        if (!project.Training.IsUsable)
        {
            issues.Add(new ExportIssue(ExportIssueSeverity.Warning, ExportIssueKind.HeartRateUnavailable,
                Messages.Preflight_HeartRateTrainingUnavailable) { Subject = project.Training.FilePath, ClipIds = overlayIds });
            return;
        }

        var unknown = new Dictionary<Guid, List<PictureSpan>>();
        foreach (var (template, index) in overlays)
        {
            var start = Max(template.TimelineStart, window.Start);
            var end = Min(template.TimelineEnd, window.End);
            // The picture below is constant between two clip edges of the tracks below.
            var edges = snapshot.VideoLayers.Skip(index + 1).SelectMany(l => l.Spans)
                .SelectMany(s => new[] { s.TimelineStart, s.TimelineEnd })
                .Where(t => t > start && t < end)
                .Append(start).Distinct().OrderBy(t => t);
            foreach (var at in edges)
            {
                if (snapshot.PictureBelow(index, at) is not { Status: SpanStatus.Video, HeartRate: null } picture) continue;
                if (!unknown.TryGetValue(picture.AssetId, out var spans)) unknown[picture.AssetId] = spans = new();
                if (!spans.Contains(picture)) spans.Add(picture);
            }
        }

        var assets = project.MediaAssets.ToDictionary(a => a.Id);
        foreach (var (assetId, spans) in unknown.OrderBy(p => p.Value.Min(s => s.TimelineStart)))
        {
            var asset = assets.GetValueOrDefault(assetId);
            var clips = spans.OrderBy(s => s.TimelineStart).Select(s => s.ClipId).Distinct().ToImmutableArray();
            issues.Add(new ExportIssue(ExportIssueSeverity.Warning, ExportIssueKind.HeartRateUnavailable,
                string.Format(Messages.Preflight_HeartRateUnknownZero, asset?.FileName ?? Messages.Preflight_MediaNotInProject,
                    Plural.Format(clips.Length, Messages.Plural_Clips), spans.Min(s => s.TimelineStart)))
            {
                AssetId = assetId,
                Subject = asset?.FilePath,
                ClipIds = clips
            });
        }

        static MediaTime Max(MediaTime a, MediaTime b) => a > b ? a : b;
        static MediaTime Min(MediaTime a, MediaTime b) => a < b ? a : b;
    }

    private static ExportIssueKind? Classify(MediaAsset? asset, SpanStatus status, ExportPreflightEnvironment env)
    {
        if (asset is null || asset.IsMissing || !env.FileExists(asset.FilePath)) return ExportIssueKind.MediaOffline;
        if (status == SpanStatus.Unsupported) return ExportIssueKind.MediaUnsupported;
        if (asset.AnalysisStatus != MediaAnalysisStatus.Completed || asset.Metadata is null) return ExportIssueKind.MediaNotAnalyzed;
        // D033 T3: a video whose saved metadata predates the colour probe is waited for while that probe is pending or
        // running, so an export never mixes the SDR and the HDR path; once the probe is over (or failed) never again.
        if (asset.Kind == MediaKind.Video && asset.IsMetadataRefreshing && asset.Metadata.NeedsColorProbe)
            return ExportIssueKind.MediaNotAnalyzed;
        return status is SpanStatus.Video or SpanStatus.StillImage or SpanStatus.Audio ? null : ExportIssueKind.MediaOffline;
    }

    /// <summary>Hold tracking: a clip whose contours reach the output while its camera motion is still analysed blocks the
    /// export (one issue per media file), so the export never draws contours the Preview would draw elsewhere.</summary>
    /// <para>R2 → E, K2: a clip whose analysis failed, or whose camera position is unreliable on some frames, is a warning
    /// (one per media file) — never a silent export of contours off their holds, never a block: the user decides.</para>
    private static void CheckHoldTracking(Project project, PlaybackSnapshot snapshot, ExportPreflightEnvironment env, List<ExportIssue> issues,
        Window window)
    {
        var motion = env.CameraMotion;
        if (motion.Pending.Count == 0 && motion.Failed.Count == 0 && motion.Unreliable.Count == 0) return;
        var assets = project.MediaAssets.ToDictionary(a => a.Id);
        var shown = snapshot.VideoLayers.SelectMany(l => l.Spans)
            .Where(s => s.Outline is not null && s.Visual.Opacity > 0 && window.Shows(s.ShownStart, s.ShownEnd))
            .ToList();

        IEnumerable<IGrouping<Guid, PictureSpan>> Of(Func<Guid, bool> which) =>
            shown.Where(s => which(s.ClipId)).GroupBy(s => s.AssetId).OrderBy(g => g.Min(s => s.TimelineStart));
        ExportIssue With(ExportIssue issue, IGrouping<Guid, PictureSpan> group) => issue with
        {
            AssetId = group.Key,
            Subject = assets.GetValueOrDefault(group.Key)?.FilePath,
            ClipIds = group.OrderBy(s => s.TimelineStart).Select(s => s.ClipId).Distinct().ToImmutableArray()
        };
        string Name(Guid assetId) => assets.GetValueOrDefault(assetId)?.FileName ?? Messages.Preflight_MediaNotInProject;
        string Clips(IGrouping<Guid, PictureSpan> group) => Plural.Format(group.Select(s => s.ClipId).Distinct().Count(), Messages.Plural_Clips);

        foreach (var group in Of(motion.Pending.Contains))
            issues.Add(With(Error(ExportIssueKind.HoldTrackingPending,
                string.Format(Messages.Preflight_HoldTrackingPending, Name(group.Key), Clips(group), group.Min(s => s.TimelineStart))), group));
        foreach (var group in Of(motion.Failed.Contains))
            issues.Add(With(Warning(ExportIssueKind.HoldTrackingFailed,
                string.Format(Messages.Preflight_HoldTrackingFailed, Name(group.Key), Clips(group), group.Min(s => s.TimelineStart))), group));
        foreach (var group in Of(motion.Unreliable.ContainsKey))
        {
            var share = group.Select(s => s.ClipId).Distinct().Max(id => motion.Unreliable[id]);
            issues.Add(With(Warning(ExportIssueKind.HoldTrackingUnreliable,
                string.Format(Messages.Preflight_HoldTrackingUnreliable, Name(group.Key), Clips(group), group.Min(s => s.TimelineStart),
                    Math.Max(1, (int)Math.Round(share * 100)))), group));
        }
    }

    private static void CheckFonts(PlaybackSnapshot snapshot, ExportPreflightEnvironment env, List<ExportIssue> issues, Window window)
    {
        var missing = snapshot.VideoLayers.SelectMany(l => l.Texts)
            .Where(t => t.Visual.Opacity > 0 && !string.IsNullOrWhiteSpace(t.Text.Text) && window.Shows(t.TimelineStart, t.TimelineEnd))
            .GroupBy(t => t.Text.FontFamily, StringComparer.OrdinalIgnoreCase)
            .Where(g => !env.IsFontInstalled(g.Key))
            .OrderBy(g => g.Min(t => t.TimelineStart));

        foreach (var font in missing)
        {
            var clips = font.OrderBy(t => t.TimelineStart).Select(t => t.ClipId).Distinct().ToImmutableArray();
            var count = Plural.Format(clips.Length, Messages.Plural_TextClips);
            issues.Add(new ExportIssue(ExportIssueSeverity.Warning, ExportIssueKind.FontMissing,
                string.Format(Messages.Preflight_FontMissing, font.Key, count))
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

    private static ExportIssue Warning(ExportIssueKind kind, string message) => new(ExportIssueSeverity.Warning, kind, message);
}
