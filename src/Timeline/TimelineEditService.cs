using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Timeline.Commands;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline;

/// <summary>
/// Implements <see cref="ITimelineEditService"/>. Each operation builds an
/// <see cref="EditPlan"/> in frame indices, validates every affected track with
/// <see cref="TimelineValidator"/>, and only then executes one undoable command.
/// Must be called on the UI thread (the project model is not thread-safe).
/// </summary>
public sealed class TimelineEditService : ITimelineEditService
{
    /// <summary>Length of a newly added image clip.</summary>
    public static readonly MediaTime DefaultImageDuration = MediaTime.FromSeconds(5);

    /// <summary>Source frame rates outside this range are treated as unknown.</summary>
    public const int MinSourceFps = 1;
    public const int MaxSourceFps = 240;

    private readonly IProjectService _projectService;
    private readonly IUndoRedoService _undoRedo;
    private readonly ILogger<TimelineEditService> _logger;

    public TimelineEditService(IProjectService projectService, IUndoRedoService undoRedo, ILogger<TimelineEditService> logger)
    {
        _projectService = projectService;
        _undoRedo = undoRedo;
        _logger = logger;
    }

    private Core.Entities.Project Project => _projectService.Current;
    private Sequence Sequence => Project.Timeline;
    private ProjectSettings Settings => Project.Settings;

    public FrameRate FrameRate => Settings.FrameRate;

    // --- Add -------------------------------------------------------------------

    public string? GetAddBlockReason(MediaAsset asset)
    {
        if (asset.IsMissing)
            return $"{asset.FileName} is missing.";

        if (asset.Kind == MediaKind.Image)
            return null;

        return asset.AnalysisStatus switch
        {
            MediaAnalysisStatus.Pending or MediaAnalysisStatus.Analyzing =>
                $"{asset.FileName} is still being analyzed. It can be added once its duration is known.",
            MediaAnalysisStatus.Failed =>
                $"{asset.FileName} can't be added: its duration is unknown ({asset.AnalysisError ?? "analysis failed"}).",
            _ when asset.Metadata is not { } m || m.Duration <= MediaTime.Zero =>
                $"{asset.FileName} can't be added: its duration is unknown.",
            _ => null
        };
    }

    public TimelineEditResult AddClip(Guid mediaAssetId, Guid? trackId = null, MediaTime? start = null)
    {
        if (FindAsset(mediaAssetId) is not { } asset)
            return TimelineEditResult.Fail("That media is not in the project.");

        if (GetAddBlockReason(asset) is { } blocked)
            return TimelineEditResult.Fail(blocked);

        var plan = new EditPlan(Sequence, Settings);
        string? info = null;

        if (asset.Kind == MediaKind.Video && !Settings.IsFrameRateLocked)
        {
            var (rate, fromSource) = ResolveSourceFrameRate(asset);
            plan.SetFrameRate(rate, locked: true);

            if (rate != Settings.FrameRate && FrameRateRegrid.Plan(plan, rate, FindAsset) is { } regridError)
                return TimelineEditResult.Fail($"Can't add {asset.FileName}: {regridError}");

            info = fromSource
                ? $"Project frame rate set to {FormatRate(rate)} FPS from {asset.FileName}."
                : $"Source frame rate of {asset.FileName} is unknown — project frame rate set to {FormatRate(rate)} FPS (fallback, not the file's actual frame rate).";
        }

        var rateForAdd = plan.Rate;
        var kind = asset.Kind == MediaKind.Audio ? TrackType.Audio : TrackType.Video;

        Track? track;
        if (trackId is { } id)
        {
            track = FindTrack(id);
            if (track is null) return TimelineEditResult.Fail("That track no longer exists.");
            if (track.Type != kind)
                return TimelineEditResult.Fail(kind == TrackType.Audio ? "Audio can only go on an audio track." : "Video and images can only go on a video track.");
        }
        else
        {
            track = (kind == TrackType.Video ? Sequence.VideoTracks : Sequence.AudioTracks).FirstOrDefault();
            if (track is null) return TimelineEditResult.Fail($"The timeline has no {(kind == TrackType.Video ? "video" : "audio")} track.");
        }

        if (track.IsLocked)
            return TimelineEditResult.Fail($"Track {track.Name} is locked.");

        long frames;
        if (asset.Kind == MediaKind.Image)
        {
            frames = Math.Max(1, DefaultImageDuration.ToNearestFrame(rateForAdd));
        }
        else
        {
            frames = FrameMath.MaxWholeFrames(asset.Metadata!.Duration, rateForAdd);
            if (frames < 1)
                return TimelineEditResult.Fail($"{asset.FileName} is shorter than one frame.");
        }

        long startFrame;
        if (start is { } requested)
        {
            startFrame = Math.Max(0, requested.ToNearestFrame(rateForAdd));
        }
        else
        {
            var end = plan.EffectiveClips(track).Select(c => c.State.End).DefaultIfEmpty(MediaTime.Zero).Max();
            startFrame = end.ToNearestFrame(rateForAdd);
        }

        var clip = CreateClip(asset, ClipState.FromFrames(startFrame, startFrame + frames, MediaTime.Zero, rateForAdd));
        plan.Insert(track, clip);

        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail($"Can't add {asset.FileName}: {error}");

        Commit(plan, "Add Clip");
        if (info is not null) _logger.LogInformation("{Message}", info);
        return TimelineEditResult.Ok(new[] { clip.Id }, info);
    }

    // --- Move ------------------------------------------------------------------

    public TimelineEditResult MoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null)
    {
        var (plan, clips, error) = PlanMove(clipIds, frameDelta, targetTrackId);
        if (error is not null) return TimelineEditResult.Fail(error);
        if (plan is null) return TimelineEditResult.Unchanged();

        Commit(plan, clips.Count == 1 ? "Move Clip" : "Move Clips");
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList());
    }

    public string? CanMoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null) =>
        PlanMove(clipIds, frameDelta, targetTrackId).Error;

    /// <summary>Plan + validation for a move. Plan is null (and Error null) when nothing would change.</summary>
    private (EditPlan? Plan, List<Clip> Clips, string? Error) PlanMove(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId)
    {
        if (clipIds.Count == 0) return (null, new List<Clip>(), null);

        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var clips) is { } resolveError) return (null, clips, resolveError);
        if (CheckEditable(plan, clips) is { } editError) return (null, clips, editError);

        Track? target = null;
        if (targetTrackId is { } tid)
        {
            target = FindTrack(tid);
            if (target is null) return (null, clips, "That track no longer exists.");
            if (clips.Select(plan.TrackOf).Distinct().Count() != 1)
                return (null, clips, "Clips from several tracks can only be moved in time, not to another track.");
            if (target.IsLocked) return (null, clips, $"Track {target.Name} is locked.");
            if (target == plan.TrackOf(clips[0])) target = null;
        }

        if (frameDelta == 0 && target is null) return (null, clips, null);

        var rate = plan.Rate;
        foreach (var clip in clips)
        {
            var toTrack = target ?? plan.TrackOf(clip);
            if (!TimelineValidator.IsCompatible(clip, toTrack.Type))
                return (null, clips, clip is AudioClip ? "Audio can only go on an audio track." : "Video and images can only go on a video track.");

            var startFrame = clip.TimelineStart.ToNearestFrame(rate) + frameDelta;
            var endFrame = clip.TimelineEnd.ToNearestFrame(rate) + frameDelta;
            if (startFrame < 0)
                return (null, clips, "Clips can't be moved before the beginning of the timeline.");

            var sourceIn = clip is MediaBackedClip m ? m.SourceIn : MediaTime.Zero;
            var after = ClipState.FromFrames(startFrame, endFrame, sourceIn, rate);

            // The tick length of the same frame count can differ by one tick between
            // positions. If that would push SourceOut one tick past the source end
            // (possible only for the right half of a split), slip SourceIn back by
            // the excess — a sub-frame, invisible adjustment — instead of failing.
            if (SourceDuration(clip) is { } sourceDuration && after.SourceOut > sourceDuration)
            {
                var excess = after.SourceOut - sourceDuration;
                if (after.SourceIn < excess)
                    return (null, clips, "A clip can't extend past the end of its source media.");
                after = after with { SourceIn = after.SourceIn - excess, SourceOut = sourceDuration };
            }

            plan.Update(clip, toTrack, after);
        }

        if (Validate(plan) is { } error)
            return (null, clips, $"Can't move: {error}");

        return (plan, clips, null);
    }

    // --- Trim ------------------------------------------------------------------

    public TimelineEditResult TrimClip(Guid clipId, ClipEdge edge, MediaTime edgeTime)
    {
        var (plan, _, error) = PlanTrim(clipId, edge, edgeTime);
        if (error is not null) return TimelineEditResult.Fail(error);
        if (plan is null) return TimelineEditResult.Unchanged();

        Commit(plan, "Trim Clip");
        return TimelineEditResult.Ok(new[] { clipId });
    }

    public (MediaTime Start, MediaTime End)? PreviewTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime)
    {
        var (_, after, error) = PlanTrim(clipId, edge, edgeTime);
        return error is null && after is { } state ? (state.Start, state.End) : null;
    }

    /// <summary>Clamped trim result. Plan is null when nothing would change; After is
    /// the resulting timing either way (null on error).</summary>
    private (EditPlan? Plan, ClipState? After, string? Error) PlanTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return (null, null, resolveError);
        if (CheckEditable(plan, clips) is { } editError) return (null, null, editError);

        var clip = clips[0];
        var track = plan.TrackOf(clip);
        var rate = plan.Rate;

        var startFrame = clip.TimelineStart.ToNearestFrame(rate);
        var endFrame = clip.TimelineEnd.ToNearestFrame(rate);
        var targetFrame = edgeTime.ToNearestFrame(rate);
        var others = track.Clips.Where(c => c != clip).ToList();
        var sourceIn = clip is MediaBackedClip m ? m.SourceIn : MediaTime.Zero;
        var sourceDuration = SourceDuration(clip);

        ClipState after;
        if (edge == ClipEdge.End)
        {
            var hi = others.Where(c => c.TimelineStart >= clip.TimelineEnd)
                .Select(c => c.TimelineStart.ToNearestFrame(rate))
                .DefaultIfEmpty(long.MaxValue).Min();
            if (sourceDuration is { } duration)
                hi = Math.Min(hi, startFrame + FrameMath.MaxWholeFrames(duration - sourceIn, rate));

            var newEnd = Math.Clamp(targetFrame, startFrame + 1, Math.Max(startFrame + 1, hi));
            after = ClipState.FromFrames(startFrame, newEnd, sourceIn, rate);
        }
        else
        {
            var lo = others.Where(c => c.TimelineEnd <= clip.TimelineStart)
                .Select(c => c.TimelineEnd.ToNearestFrame(rate))
                .DefaultIfEmpty(0).Max();
            if (sourceDuration is not null)
                lo = Math.Max(lo, FrameMath.CeilingFrame(clip.TimelineStart - sourceIn, rate)); // keeps SourceIn ≥ 0

            var newStart = Math.Clamp(targetFrame, Math.Min(lo, endFrame - 1), endFrame - 1);
            var newStartTime = MediaTime.FromFrame(newStart, rate);

            // Video/audio: the in-point follows the edge so the content under the
            // playhead doesn't shift. Images have no source timing: SourceIn stays 0.
            var newSourceIn = sourceDuration is not null ? sourceIn + (newStartTime - clip.TimelineStart) : sourceIn;
            after = ClipState.FromFrames(newStart, endFrame, newSourceIn, rate);
        }

        if (after == ClipState.Capture(clip)) return (null, after, null);

        plan.Update(clip, track, after);
        if (Validate(plan) is { } error)
            return (null, null, $"Can't trim: {error}");

        return (plan, after, null);
    }

    // --- Split -----------------------------------------------------------------

    public TimelineEditResult Split(MediaTime at, IReadOnlyCollection<Guid>? clipIds = null)
    {
        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var atFrame = at.ToNearestFrame(rate);

        List<Clip> candidates;
        var explicitSelection = clipIds is { Count: > 0 };
        if (explicitSelection)
        {
            if (Resolve(plan, clipIds!, out var selected) is { } resolveError) return TimelineEditResult.Fail(resolveError);
            candidates = selected;
        }
        else
        {
            candidates = plan.AllTracks.Where(t => !t.IsLocked).SelectMany(t => t.Clips).ToList();
        }

        var toSplit = candidates
            .Where(c => c.TimelineStart.ToNearestFrame(rate) < atFrame && atFrame < c.TimelineEnd.ToNearestFrame(rate))
            .ToList();

        if (toSplit.Count == 0)
            return TimelineEditResult.Fail(explicitSelection
                ? "The playhead is not inside the selected clip(s)."
                : "There is no clip under the playhead to split.");

        if (CheckEditable(plan, toSplit) is { } editError) return TimelineEditResult.Fail(editError);

        var ids = new List<Guid>();
        foreach (var clip in toSplit)
        {
            var startFrame = clip.TimelineStart.ToNearestFrame(rate);
            var endFrame = clip.TimelineEnd.ToNearestFrame(rate);
            var sourceIn = clip is MediaBackedClip m ? m.SourceIn : MediaTime.Zero;
            var hasSource = SourceDuration(clip) is not null;

            var left = ClipState.FromFrames(startFrame, atFrame, sourceIn, rate);
            var right = ClipState.FromFrames(atFrame, endFrame, hasSource ? left.SourceOut : sourceIn, rate);

            var rightClip = CloneClip(clip);
            right.ApplyTo(rightClip);

            var track = plan.TrackOf(clip);
            plan.Update(clip, track, left);
            plan.Insert(track, rightClip);
            ids.Add(clip.Id);
            ids.Add(rightClip.Id);
        }

        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail($"Can't split: {error}");

        Commit(plan, toSplit.Count == 1 ? "Split Clip" : "Split Clips");
        return TimelineEditResult.Ok(ids);
    }

    // --- Delete / tracks -------------------------------------------------------

    public TimelineEditResult DeleteClips(IReadOnlyCollection<Guid> clipIds)
    {
        if (clipIds.Count == 0) return TimelineEditResult.Unchanged();

        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);

        if (clips.Select(plan.TrackOf).FirstOrDefault(t => t.IsLocked) is { } locked)
            return TimelineEditResult.Fail($"Track {locked.Name} is locked.");

        foreach (var clip in clips) plan.Remove(clip);

        Commit(plan, clips.Count == 1 ? "Delete Clip" : "Delete Clips");
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList());
    }

    public TimelineEditResult AddTrack(TrackType type)
    {
        var list = type == TrackType.Video ? Sequence.VideoTracks : Sequence.AudioTracks;
        var prefix = type == TrackType.Video ? "V" : "A";
        var number = list.Count + 1;
        while (list.Any(t => t.Name == $"{prefix}{number}")) number++;

        var track = new Track
        {
            Type = type,
            Name = $"{prefix}{number}",
            Order = list.Count == 0 ? 0 : list.Max(t => t.Order) + 1
        };

        _undoRedo.Execute(new NotifyingCommand(new AddTrackCommand(Sequence, track), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    // --- Snapping --------------------------------------------------------------

    public SnapResult Snap(IReadOnlyList<MediaTime> candidates, MediaTime tolerance, IReadOnlyCollection<Guid> excludedClipIds)
    {
        var excluded = excludedClipIds as ISet<Guid> ?? excludedClipIds.ToHashSet();
        var targets = new List<MediaTime> { MediaTime.Zero, Sequence.PlayheadPosition };
        foreach (var track in Sequence.VideoTracks.Concat(Sequence.AudioTracks))
        {
            foreach (var clip in track.Clips)
            {
                if (excluded.Contains(clip.Id)) continue;
                targets.Add(clip.TimelineStart);
                targets.Add(clip.TimelineEnd);
            }
        }

        return SnapEngine.Find(candidates, targets, tolerance);
    }

    // --- Helpers ---------------------------------------------------------------

    /// <summary>The asset's rate if ffprobe reported a usable one, else the 30 FPS
    /// fallback — with a flag so the caller never presents the fallback as the
    /// file's real rate.</summary>
    public static (FrameRate Rate, bool FromSource) ResolveSourceFrameRate(MediaAsset asset)
    {
        if (asset.Metadata?.FrameRate is { IsValid: true } rate &&
            rate.Numerator >= (long)MinSourceFps * rate.Denominator &&
            rate.Numerator <= (long)MaxSourceFps * rate.Denominator)
        {
            return (rate, true);
        }

        return (FrameRate.Default, false);
    }

    public static string FormatRate(FrameRate rate) => rate.Denominator == 1
        ? rate.Numerator.ToString(CultureInfo.InvariantCulture)
        : Math.Round(rate.ToDouble(), 3).ToString("0.###", CultureInfo.InvariantCulture);

    private MediaAsset? FindAsset(Guid id) => Project.MediaAssets.FirstOrDefault(a => a.Id == id);

    private Track? FindTrack(Guid id) =>
        Sequence.VideoTracks.Concat(Sequence.AudioTracks).FirstOrDefault(t => t.Id == id);

    /// <summary>Source length limit for video/audio clips; null for clips without one (images, text).</summary>
    private MediaTime? SourceDuration(Clip clip) =>
        clip is VideoClip or AudioClip ? FindAsset(((MediaBackedClip)clip).MediaAssetId)?.Metadata?.Duration : null;

    private static string? Resolve(EditPlan plan, IEnumerable<Guid> ids, out List<Clip> clips)
    {
        var byId = plan.AllTracks.SelectMany(t => t.Clips).ToDictionary(c => c.Id);
        clips = new List<Clip>();
        foreach (var id in ids.Distinct())
        {
            if (!byId.TryGetValue(id, out var clip))
                return "A selected clip no longer exists.";
            clips.Add(clip);
        }
        return null;
    }

    private static string? CheckEditable(EditPlan plan, IEnumerable<Clip> clips)
    {
        foreach (var clip in clips)
        {
            if (plan.TrackOf(clip).IsLocked)
                return $"Track {plan.TrackOf(clip).Name} is locked.";
            if (clip is MediaBackedClip { Speed: not 1.0 })
                return "Clips with a speed other than 100% can't be edited on the timeline yet.";
        }
        return null;
    }

    private string? Validate(EditPlan plan)
    {
        foreach (var track in plan.AffectedTracks)
        {
            if (TimelineValidator.ValidateTrack(track, plan.EffectiveClips(track), plan.Rate, FindAsset) is { } error)
                return error;
        }
        return null;
    }

    private void Commit(EditPlan plan, string description)
    {
        var command = new NotifyingCommand(plan.BuildCommand(description), _projectService.NotifyTimelineChanged);
        _undoRedo.Execute(command);
    }

    private static Clip CreateClip(MediaAsset asset, ClipState state)
    {
        Clip clip = asset.Kind switch
        {
            MediaKind.Video => new VideoClip { MediaAssetId = asset.Id },
            MediaKind.Audio => new AudioClip { MediaAssetId = asset.Id },
            _ => new ImageClip { MediaAssetId = asset.Id }
        };
        state.ApplyTo(clip);
        return clip;
    }

    /// <summary>Copy of every clip property with a new Id (used for the right half of a split).</summary>
    private static Clip CloneClip(Clip source)
    {
        Clip copy = source switch
        {
            VideoClip v => new VideoClip
            {
                MediaAssetId = v.MediaAssetId, Speed = v.Speed, PositionX = v.PositionX, PositionY = v.PositionY,
                Scale = v.Scale, RotationDegrees = v.RotationDegrees, Opacity = v.Opacity, Volume = v.Volume, Crop = v.Crop
            },
            AudioClip a => new AudioClip { MediaAssetId = a.MediaAssetId, Speed = a.Speed, Volume = a.Volume, IsMuted = a.IsMuted },
            ImageClip i => new ImageClip
            {
                MediaAssetId = i.MediaAssetId, Speed = i.Speed, PositionX = i.PositionX, PositionY = i.PositionY,
                Scale = i.Scale, RotationDegrees = i.RotationDegrees, Opacity = i.Opacity, Crop = i.Crop
            },
            TextClip t => new TextClip
            {
                Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, ColorHex = t.ColorHex, Alignment = t.Alignment,
                PositionX = t.PositionX, PositionY = t.PositionY, Scale = t.Scale, RotationDegrees = t.RotationDegrees, Opacity = t.Opacity
            },
            _ => throw new NotSupportedException($"Unknown clip type {source.GetType().Name}.")
        };

        ClipState.Capture(source).ApplyTo(copy);
        foreach (var effect in source.Effects)
        {
            copy.Effects.Add(new Effect
            {
                EffectTypeId = effect.EffectTypeId,
                DisplayName = effect.DisplayName,
                IsEnabled = effect.IsEnabled,
                Parameters = new Dictionary<string, object?>(effect.Parameters)
            });
        }
        return copy;
    }
}
