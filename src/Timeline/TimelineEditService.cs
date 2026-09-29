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

    /// <summary>Length of a newly added text clip.</summary>
    public static readonly MediaTime DefaultTextDuration = MediaTime.FromSeconds(5);

    /// <summary>Content of a newly added text clip (empty text would not be visible).</summary>
    public const string DefaultText = "Text";

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
        return TimelineEditResult.Ok(new[] { clip.Id }, Join(info, TransitionNote(plan)));
    }

    public TimelineEditResult AddTextClip(MediaTime start)
    {
        // Titles go over the picture: the topmost video track, never a new one.
        var track = Sequence.VideoTracks.OrderByDescending(t => t.Order).FirstOrDefault();
        if (track is null) return TimelineEditResult.Fail("The timeline has no video track.");
        if (track.IsLocked) return TimelineEditResult.Fail($"Track {track.Name} is locked.");

        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var startFrame = Math.Max(0, start.ToNearestFrame(rate));
        var frames = Math.Max(1, DefaultTextDuration.ToNearestFrame(rate));

        // Text defaults are the model's (Segoe UI, 48, #FFFFFF, centred); only the content is set.
        var clip = new TextClip { Text = DefaultText };
        ClipState.FromFrames(startFrame, startFrame + frames, MediaTime.Zero, rate).ApplyTo(clip);
        plan.Insert(track, clip);

        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail($"Can't add text on {track.Name}: {error}");

        Commit(plan, "Add Text");
        return TimelineEditResult.Ok(new[] { clip.Id });
    }

    // --- Move ------------------------------------------------------------------

    public TimelineEditResult MoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null)
    {
        var (plan, clips, error) = PlanMove(clipIds, frameDelta, targetTrackId);
        if (error is not null) return TimelineEditResult.Fail(error);
        if (plan is null) return TimelineEditResult.Unchanged();

        Commit(plan, clips.Count == 1 ? "Move Clip" : "Move Clips");
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList(), TransitionNote(plan));
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

            if (clip is MediaBackedClip { Speed.IsNormal: false } fast)
            {
                // D022: the source range and speed move along unchanged; so does the frame count.
                plan.Update(clip, toTrack, ClipState.FromFrames(startFrame, endFrame, fast.SourceIn, fast.SourceOut, fast.Speed, rate));
                continue;
            }

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
        return TimelineEditResult.Ok(new[] { clipId }, TransitionNote(plan));
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

        // D025 §5: a trim of the far edge keeps the part of the dissolve on the other edge inside the clip (the trimmed
        // edge's own dissolve, if any, goes when its cut opens).
        var (partAtStart, partAtEnd) = DissolveParts(track, clip, rate);
        var minFrames = Math.Max(1, edge == ClipEdge.End ? partAtStart : partAtEnd);

        ClipState after;
        if (clip is MediaBackedClip { Speed.IsNormal: false } fast)
        {
            after = PlanTrimAtSpeed(fast, edge, startFrame, endFrame, targetFrame, others, sourceDuration, rate, minFrames);
        }
        else if (edge == ClipEdge.End)
        {
            var hi = others.Where(c => c.TimelineStart >= clip.TimelineEnd)
                .Select(c => c.TimelineStart.ToNearestFrame(rate))
                .DefaultIfEmpty(long.MaxValue).Min();
            if (sourceDuration is { } duration)
                hi = Math.Min(hi, startFrame + FrameMath.MaxWholeFrames(duration - sourceIn, rate));

            var newEnd = Math.Clamp(targetFrame, startFrame + minFrames, Math.Max(startFrame + minFrames, hi));
            after = ClipState.FromFrames(startFrame, newEnd, sourceIn, rate);
        }
        else
        {
            var lo = others.Where(c => c.TimelineEnd <= clip.TimelineStart)
                .Select(c => c.TimelineEnd.ToNearestFrame(rate))
                .DefaultIfEmpty(0).Max();
            if (sourceDuration is not null)
                lo = Math.Max(lo, FrameMath.CeilingFrame(clip.TimelineStart - sourceIn, rate)); // keeps SourceIn ≥ 0

            var newStart = Math.Clamp(targetFrame, Math.Min(lo, endFrame - minFrames), endFrame - minFrames);
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

    /// <summary>
    /// Trim of a clip at a speed other than 1× (D022), in the same one rounding rule
    /// (<see cref="SpeedTiming.SourceLength"/>): the end edge sets SourceOut = SourceIn +
    /// SourceLength(N); the start edge moves SourceIn by SourceLength(Δ) frames' worth (so the content
    /// under the playhead stays) and keeps SourceOut, normalized only if the invariant is missed by
    /// the rounding. Limits: neighbours, <paramref name="minFrames"/> (one frame, or a dissolve's part), SourceIn ≥ 0
    /// and the end of the source.
    /// </summary>
    private static ClipState PlanTrimAtSpeed(MediaBackedClip clip, ClipEdge edge, long startFrame, long endFrame, long targetFrame,
        List<Clip> others, MediaTime? sourceDuration, FrameRate rate, long minFrames)
    {
        var speed = clip.Speed;
        if (edge == ClipEdge.End)
        {
            var hi = others.Where(c => c.TimelineStart >= clip.TimelineEnd)
                .Select(c => c.TimelineStart.ToNearestFrame(rate))
                .DefaultIfEmpty(long.MaxValue).Min();
            if (sourceDuration is { } duration)
                hi = Math.Min(hi, startFrame + SpeedTiming.FramesFor(duration - clip.SourceIn, speed, rate));

            var newEnd = Math.Clamp(targetFrame, startFrame + minFrames, Math.Max(startFrame + minFrames, hi));
            var sourceOut = clip.SourceIn + SpeedTiming.SourceLength(newEnd - startFrame, speed, rate);
            return ClipState.FromFrames(startFrame, newEnd, clip.SourceIn, sourceOut, speed, rate);
        }

        var lo = others.Where(c => c.TimelineEnd <= clip.TimelineStart)
            .Select(c => c.TimelineEnd.ToNearestFrame(rate))
            .DefaultIfEmpty(0).Max();
        lo = Math.Max(lo, startFrame - SpeedTiming.FramesFor(clip.SourceIn, speed, rate)); // keeps SourceIn ≥ 0

        var newStart = Math.Clamp(targetFrame, Math.Min(lo, endFrame - minFrames), endFrame - minFrames);
        var delta = newStart - startFrame;
        var sourceIn = delta >= 0
            ? clip.SourceIn + SpeedTiming.SourceLength(delta, speed, rate)
            : clip.SourceIn - SpeedTiming.SourceLength(-delta, speed, rate);
        var (normalizedIn, normalizedOut) = ClipState.Normalize(endFrame - newStart, sourceIn, clip.SourceOut, speed, rate);
        return ClipState.FromFrames(newStart, endFrame, normalizedIn, normalizedOut, speed, rate);
    }

    // --- Speed (D022) ---------------------------------------------------------------

    public TimelineEditResult SetClipSpeed(Guid clipId, ClipSpeed speed)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        if (clips[0] is not (VideoClip or AudioClip)) return TimelineEditResult.Fail("Only video and audio clips have a speed.");
        if (CheckEditable(plan, clips) is { } editError) return TimelineEditResult.Fail(editError);

        var clip = (MediaBackedClip)clips[0];
        if (clip.Speed == speed) return TimelineEditResult.Unchanged();

        // The speed never changes the source range: the frame count follows from it.
        var rate = plan.Rate;
        var frames = SpeedTiming.FramesFor(clip.SourceOut - clip.SourceIn, speed, rate);
        if (frames < 1) return TimelineEditResult.Fail($"At {speed} the clip would be shorter than one frame.");

        var startFrame = clip.TimelineStart.ToNearestFrame(rate);
        var before = ClipState.Capture(clip);
        var after = ClipState.FromFrames(startFrame, startFrame + frames, clip.SourceIn, clip.SourceOut, speed, rate);
        plan.Update(clip, plan.TrackOf(clip), after);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail($"Can't change the speed: {error}");

        // A speed change of A moves its end: its dissolve goes (D025 §5) — then one composite step, which no longer
        // merges with the next speed change.
        IUndoableCommand command = new SetClipSpeedCommand(clip, before, after);
        if (plan.ChangesTransitions)
            command = new CompositeCommand(command.Description, new[] { command }.Concat(plan.BuildTransitionCommands()));
        _undoRedo.Execute(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { clip.Id }, TransitionNote(plan));
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

            ClipState left, right;
            if (clip is MediaBackedClip { Speed.IsNormal: false } fast)
            {
                // D022: the cut in the source is SourceIn + SourceLength(frames left of the split);
                // the right half keeps the original SourceOut (normalized only if rounding misses).
                var cut = fast.SourceIn + SpeedTiming.SourceLength(atFrame - startFrame, fast.Speed, rate);
                left = ClipState.FromFrames(startFrame, atFrame, fast.SourceIn, cut, fast.Speed, rate);
                var (rightIn, rightOut) = ClipState.Normalize(endFrame - atFrame, cut, fast.SourceOut, fast.Speed, rate);
                right = ClipState.FromFrames(atFrame, endFrame, rightIn, rightOut, fast.Speed, rate);
            }
            else
            {
                left = ClipState.FromFrames(startFrame, atFrame, sourceIn, rate);
                right = ClipState.FromFrames(atFrame, endFrame, hasSource ? left.SourceOut : sourceIn, rate);
            }

            var rightClip = CloneClip(clip);
            right.ApplyTo(rightClip);

            // D025 §2: the fades stay with the outer edges — the right part keeps the fade out, the left part the
            // fade in; the new inner edges have none.
            rightClip.FadeIn = MediaTime.Zero;
            var track = plan.TrackOf(clip);
            if (clip.FadeOut != MediaTime.Zero)
                plan.SetProperties(clip, new ClipPropertyValues(null, null, null, new FadeProperties(clip.FadeIn, MediaTime.Zero)));

            // D025 §5: never inside a dissolve's zone; a dissolve at the clip's end moves to the right part (the left
            // part keeps the clip's id and its dissolve at the start).
            foreach (var transition in track.Transitions)
            {
                var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(transition.Duration, rate));
                if ((transition.LeftClipId == clip.Id && atFrame > endFrame - beforeCut) ||
                    (transition.RightClipId == clip.Id && atFrame < startFrame + afterCut))
                    return TimelineEditResult.Fail("Can't split inside a dissolve.");
                if (transition.LeftClipId == clip.Id)
                    plan.UpdateTransition(transition, plan.StateOf(transition) with { LeftClipId = rightClip.Id });
            }

            plan.Update(clip, track, left);
            plan.Insert(track, rightClip);
            ids.Add(clip.Id);
            ids.Add(rightClip.Id);
        }

        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail($"Can't split: {error}");

        Commit(plan, toSplit.Count == 1 ? "Split Clip" : "Split Clips");
        return TimelineEditResult.Ok(ids, TransitionNote(plan));
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
        plan.ReconcileTransitions();   // a deleted clip's dissolves go with it (D025 §5)

        Commit(plan, clips.Count == 1 ? "Delete Clip" : "Delete Clips");
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList(), TransitionNote(plan));
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

    // --- Clip properties -------------------------------------------------------

    public TimelineEditResult SetClipProperties(Guid clipId, ClipPropertyChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);

        var clip = clips[0];
        var track = plan.TrackOf(clip);
        if (track.IsLocked) return TimelineEditResult.Fail($"Track {track.Name} is locked.");

        if (ClipPropertyValidator.Validate(clip, change) is { } error)
            return TimelineEditResult.Fail(error);

        FadeProperties? fade = null;
        if (change.Fade is { } requested)
        {
            if (NormalizeFades(clip, requested, plan.Rate, out var normalized) is { } fadeError)
                return TimelineEditResult.Fail(fadeError);
            fade = normalized;
        }

        // Timing is untouched, so no timeline validation is needed: the clip's placement,
        // grid alignment and source range stay exactly as they are.
        var before = ClipPropertyValues.Capture(clip);
        var after = new ClipPropertyValues(
            change.Visual ?? before.Visual,
            change.Audio ?? before.Audio,
            change.Text ?? before.Text,
            fade ?? before.Fade);

        var fields = ClipPropertyValues.Diff(before, after);
        if (fields == ClipPropertyFields.None) return TimelineEditResult.Unchanged();

        var command = new SetClipPropertiesCommand(clip, before, after, fields);
        _undoRedo.Execute(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { clip.Id });
    }

    /// <summary>
    /// Checks and normalizes a fade change (D025 §2): each fade is a whole number of frames of the project rate
    /// (nearest, ties up), from 0 to the clip's length, stored as exactly that many frames. A fade that is not
    /// changed is kept as stored (it may exceed a clip trimmed shorter since: clamped when rendered).
    /// </summary>
    private static string? NormalizeFades(Clip clip, FadeProperties requested, FrameRate rate, out FadeProperties normalized)
    {
        normalized = FadeProperties.Of(clip);
        if (requested.FadeIn < MediaTime.Zero || requested.FadeOut < MediaTime.Zero) return "A fade can't be negative.";

        var frames = TransitionRules.ClipFrames(clip, rate);
        var fadeIn = requested.FadeIn == clip.FadeIn ? clip.FadeIn : Normalize(requested.FadeIn);
        var fadeOut = requested.FadeOut == clip.FadeOut ? clip.FadeOut : Normalize(requested.FadeOut);
        if (fadeIn != clip.FadeIn && TransitionRules.Frames(fadeIn, rate) > frames) return "The fade in can't be longer than the clip.";
        if (fadeOut != clip.FadeOut && TransitionRules.Frames(fadeOut, rate) > frames) return "The fade out can't be longer than the clip.";

        normalized = new FadeProperties(fadeIn, fadeOut);
        return null;

        MediaTime Normalize(MediaTime value) => MediaTime.FromFrame(TransitionRules.Frames(value, rate), rate);
    }

    // --- Dissolves (D025 §3–§5) ------------------------------------------------------

    public TimelineEditResult AddTransition(Guid leftClipId, Guid rightClipId, MediaTime duration)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (ResolveCut(plan, leftClipId, rightClipId, out var track, out var left, out var right) is { } cutError)
            return TimelineEditResult.Fail(cutError);
        if (track.Transitions.Any(t => t.LeftClipId == left.Id))
            return TimelineEditResult.Fail("This cut already has a dissolve.");

        var rate = plan.Rate;
        var frames = TransitionRules.Frames(duration, rate);
        if (frames < TransitionRules.MinFrames)
            return TimelineEditResult.Fail($"A dissolve must be at least {TransitionRules.MinFrames} frames long.");
        if (TooLong(frames, MaxFrames(track, left, right, rate, except: null)) is { } tooLong)
            return TimelineEditResult.Fail(tooLong);

        var transition = new Transition
        {
            TransitionTypeId = TransitionRules.CrossDissolve, Duration = MediaTime.FromFrame(frames, rate),
            LeftClipId = left.Id, RightClipId = right.Id
        };
        plan.AddTransition(track, transition);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail($"Can't add the dissolve: {error}");

        _undoRedo.Execute(new NotifyingCommand(new AddTransitionCommand(track, transition), _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { left.Id, right.Id }, TransitionId = transition.Id };
    }

    public TimelineEditResult RemoveTransition(Guid transitionId)
    {
        if (FindTransition(transitionId) is not { } found) return TimelineEditResult.Fail("That dissolve no longer exists.");
        var (track, transition) = found;
        if (track.IsLocked) return TimelineEditResult.Fail($"Track {track.Name} is locked.");

        _undoRedo.Execute(new NotifyingCommand(new RemoveTransitionCommand(track, transition), _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { transition.LeftClipId, transition.RightClipId } };
    }

    public TimelineEditResult SetTransitionDuration(Guid transitionId, MediaTime duration)
    {
        if (FindTransition(transitionId) is not { } found) return TimelineEditResult.Fail("That dissolve no longer exists.");
        var (track, transition) = found;
        if (track.IsLocked) return TimelineEditResult.Fail($"Track {track.Name} is locked.");

        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var frames = TransitionRules.Frames(duration, rate);
        if (frames < TransitionRules.MinFrames)
            return TimelineEditResult.Fail($"A dissolve must be at least {TransitionRules.MinFrames} frames long.");
        var stored = MediaTime.FromFrame(frames, rate);
        if (stored == transition.Duration) return TimelineEditResult.Unchanged();

        var left = track.Clips.First(c => c.Id == transition.LeftClipId);
        var right = track.Clips.First(c => c.Id == transition.RightClipId);
        if (TooLong(frames, MaxFrames(track, left, right, rate, except: transition)) is { } tooLong)
            return TimelineEditResult.Fail(tooLong);

        var before = TransitionState.Capture(track, transition);
        var after = before with { Duration = stored };
        plan.UpdateTransition(transition, after);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail($"Can't change the dissolve: {error}");

        _undoRedo.Execute(new NotifyingCommand(new UpdateTransitionCommand(transition, before, after), _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { left.Id, right.Id }, TransitionId = transition.Id };
    }

    public long? MaxTransitionFrames(Guid leftClipId, Guid rightClipId)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (ResolveCut(plan, leftClipId, rightClipId, out var track, out var left, out var right, allowLocked: true) is not null)
            return null;
        var own = track.Transitions.FirstOrDefault(t => t.LeftClipId == left.Id);
        return MaxFrames(track, left, right, plan.Rate, except: own);
    }

    /// <summary>Two touching clips of one (unlocked) video track, A left of B.</summary>
    private static string? ResolveCut(EditPlan plan, Guid leftClipId, Guid rightClipId, out Track track, out Clip left, out Clip right,
        bool allowLocked = false)
    {
        track = null!;
        left = right = null!;
        if (Resolve(plan, new[] { leftClipId, rightClipId }, out var clips) is { } resolveError) return resolveError;
        if (clips.Count != 2) return "A dissolve needs two different clips.";
        (left, right) = (clips[0], clips[1]);
        track = plan.TrackOf(left);
        if (plan.TrackOf(right) != track) return "A dissolve joins two clips of the same track.";
        if (track.Type != TrackType.Video) return "Only clips on a video track can have a dissolve.";
        if (!allowLocked && track.IsLocked) return $"Track {track.Name} is locked.";
        if (left.TimelineEnd != right.TimelineStart) return "A dissolve needs two clips that meet: the first must end where the second starts.";
        return null;
    }

    /// <summary>The longest dissolve the cut A | B can take (D025 §3–§4): the frames of each clip not used by its
    /// dissolve on the other edge, and the source handles. <paramref name="except"/> is the cut's own dissolve.</summary>
    private long MaxFrames(Track track, Clip left, Clip right, FrameRate rate, Transition? except)
    {
        var others = track.Transitions.Where(t => t != except).ToList();
        long PartAtStart(Clip clip) => others.Where(t => t.RightClipId == clip.Id)
            .Select(t => TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate)).AfterCut).DefaultIfEmpty(0).Max();
        long PartAtEnd(Clip clip) => others.Where(t => t.LeftClipId == clip.Id)
            .Select(t => TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate)).BeforeCut).DefaultIfEmpty(0).Max();

        var leftState = ClipState.Capture(left);
        var rightState = ClipState.Capture(right);
        return TransitionRules.MaxFrames(
            roomInLeft: DissolveHandles.Frames(leftState, rate) - PartAtStart(left),
            roomInRight: DissolveHandles.Frames(rightState, rate) - PartAtEnd(right),
            handleAfterLeft: DissolveHandles.After(left, leftState, AssetOf(left), rate),
            handleBeforeRight: DissolveHandles.Before(right, rightState, AssetOf(right), rate));
    }

    private static string? TooLong(long frames, long max) =>
        frames <= max ? null
        : max < TransitionRules.MinFrames ? NotEnoughMedia
        : $"A dissolve of {frames} frames doesn't fit here; the longest that fits is {max} frames.";

    private (Track Track, Transition Transition)? FindTransition(Guid id)
    {
        foreach (var track in Sequence.VideoTracks.Concat(Sequence.AudioTracks))
            if (track.Transitions.FirstOrDefault(t => t.Id == id) is { } transition)
                return (track, transition);
        return null;
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
        }
        return null;
    }

    /// <summary>Validates the plan: the dissolves are first brought in line with the planned clips (D025 §5: removed
    /// where their cut is gone, moved with their clips), then every affected track's clips and dissolves are checked —
    /// zones against the clips, and the source handles of every dissolve the edit creates or keeps (§4).</summary>
    private string? Validate(EditPlan plan)
    {
        plan.ReconcileTransitions();
        foreach (var track in plan.AffectedTracks)
        {
            if (TimelineValidator.ValidateTrack(track, plan.EffectiveClips(track), plan.Rate, FindAsset) is { } error)
                return error;
        }
        foreach (var track in plan.TransitionTracks)
        {
            if (ValidateTransitions(plan, track) is { } error)
                return error;
        }
        return null;
    }

    public const string NotEnoughMedia = "There is not enough media beyond the clips for the dissolve.";

    private string? ValidateTransitions(EditPlan plan, Track track)
    {
        var transitions = plan.EffectiveTransitions(track).ToList();
        if (transitions.Count == 0) return null;

        var clips = plan.EffectiveClips(track).ToDictionary(c => c.Clip.Id);
        var specs = transitions.Select(t =>
            new TransitionSpec(t.Transition.Id, t.Transition.TransitionTypeId, t.State.Duration, t.State.LeftClipId, t.State.RightClipId));
        if (TransitionRules.Validate(track.Type, track.Name, specs,
                id => clips.TryGetValue(id, out var c) ? (c.State.Start, c.State.End) : null, plan.Rate) is { } error)
            return error;

        foreach (var (transition, state) in transitions)
        {
            if (!plan.IsTouched(transition)) continue;
            var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(state.Duration, plan.Rate));
            var left = clips[state.LeftClipId];
            var right = clips[state.RightClipId];
            if (afterCut > DissolveHandles.After(left.Clip, left.State, AssetOf(left.Clip), plan.Rate) ||
                beforeCut > DissolveHandles.Before(right.Clip, right.State, AssetOf(right.Clip), plan.Rate))
                return NotEnoughMedia;
        }
        return null;
    }

    private MediaAsset? AssetOf(Clip clip) => clip is MediaBackedClip m ? FindAsset(m.MediaAssetId) : null;

    /// <summary>The frames of <paramref name="clip"/> inside the dissolve zones at its start and its end (0 without one).</summary>
    private static (long AtStart, long AtEnd) DissolveParts(Track track, Clip clip, FrameRate rate)
    {
        long atStart = 0, atEnd = 0;
        foreach (var t in track.Transitions)
        {
            var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate));
            if (t.RightClipId == clip.Id) atStart = afterCut;
            if (t.LeftClipId == clip.Id) atEnd = beforeCut;
        }
        return (atStart, atEnd);
    }

    /// <summary>The status note for dissolves an edit removed because their cut is gone (D025 §5), or null.</summary>
    private static string? TransitionNote(EditPlan plan) => plan.AutoRemovedTransitions switch
    {
        0 => null,
        1 => "A dissolve was removed: its clips no longer meet.",
        var n => $"{n} dissolves were removed: their clips no longer meet."
    };

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : $"{a} {b}";

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
                Scale = v.Scale, RotationDegrees = v.RotationDegrees, Opacity = v.Opacity, Volume = v.Volume, IsMuted = v.IsMuted,
                Crop = v.Crop
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
        (copy.FadeIn, copy.FadeOut) = (source.FadeIn, source.FadeOut);
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
