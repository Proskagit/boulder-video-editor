using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry.Calibration;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>A video a FIT mark can be on (D051 §5.5.1): its recording zero <c>Z</c>, its frame rate and whole frames.
/// Its recording in watch time is <c>[Z + CC, Z + CC + frames / rate)</c>.</summary>
public sealed record FitVideo(Guid AssetId, DateTimeOffset Zero, FrameRate Rate, long Frames)
{
    public MediaTime Length => MediaTime.FromFrame(Frames, Rate);
}

/// <summary>Where a mark's effective start lies (D051 §4.2, §5.6): a video and a source time on it — or, for a
/// <c>proposed</c> mark whose video has no <c>Z</c>, only its watch time <c>W_auto</c>.</summary>
public readonly record struct FitEffectiveStart(Guid AssetId, MediaTime? SourceTime, DateTimeOffset? AutoStartUtc);

/// <summary>
/// The rules of the FIT marks (D051 §4–§5) — pure: the choice of the video and the position of a proposal (§5.5.1), the
/// effective start and its watch time with every conversion made once (§4.5), the comparison by the source frame (§5.6,
/// О5), the observation of a usable mark (§5.8) and the link following its markup (§5.4).
/// </summary>
public static class FitMarkRules
{
    // ---- Proposals (§5.5, §5.5.1) -------------------------------------------------------------------------------------

    /// <summary>The project's videos that can hold a mark: a known <c>Z</c> and at least one whole frame. The rate is the
    /// file's own, else <paramref name="fallbackRate"/>. <paramref name="zeroOf"/> overrides one video's zero (the value a
    /// change of <c>Z</c> is about to set).</summary>
    public static IReadOnlyList<FitVideo> Videos(IEnumerable<MediaAsset> assets, FrameRate fallbackRate,
        (Guid AssetId, DateTimeOffset? Zero)? zeroOf = null)
    {
        var videos = new List<FitVideo>();
        foreach (var asset in assets)
        {
            if (asset.Kind != MediaKind.Video || asset.Metadata is not { } metadata) continue;
            var resolved = zeroOf is { } o && o.AssetId == asset.Id ? o.Zero : RecordingZeroEstimator.Resolve(asset).Utc;
            if (resolved is not { } zero) continue;
            var rate = RateOf(asset, fallbackRate);
            var frames = WholeFrames(metadata.Duration, rate);
            if (frames >= 1) videos.Add(new FitVideo(asset.Id, zero, rate, frames));
        }
        return videos;
    }

    /// <summary>The rate a video's source frames are counted in.</summary>
    public static FrameRate RateOf(MediaAsset asset, FrameRate fallbackRate) =>
        asset.Metadata?.FrameRate is { IsValid: true } own ? own : fallbackRate;

    private static long WholeFrames(MediaTime duration, FrameRate rate) => duration.Ticks <= 0
        ? 0
        : (long)((Int128)duration.Ticks * rate.Numerator / ((Int128)TimeSpan.TicksPerSecond * rate.Denominator));

    /// <summary>
    /// The video and the own position of a new mark (D051 §5.5.1): the video whose recording holds <c>W_auto</c> →
    /// <c>proposed</c>; else the one holding <c>W_FITStart</c> → <c>clamped</c> to the first frame (W_auto before the
    /// recording) or to the start of the last frame (after it); else none. Several candidates: the whole
    /// <c>[min(W_auto, W_s), W_e)</c> inside, then <c>W_s</c> inside, then the earlier start, then the longer recording,
    /// then the smaller media id.
    /// </summary>
    public static (Guid AssetId, FitMarkPosition Position)? ChooseVideo(WorkoutSegment climb, DateTimeOffset autoStart,
        MediaTime clockCorrection, IReadOnlyList<FitVideo> videos)
    {
        var start = climb.StartUtc;
        var end = climb.End;
        bool Holds(FitVideo v, DateTimeOffset w) => w >= Begin(v) && w < Finish(v);
        DateTimeOffset Begin(FitVideo v) => v.Zero.AddTicks(clockCorrection.Ticks);
        DateTimeOffset Finish(FitVideo v) => Begin(v).AddTicks(v.Length.Ticks);
        FitVideo Pick(IEnumerable<FitVideo> candidates)
        {
            var from = autoStart < start ? autoStart : start;
            return candidates
                .OrderByDescending(v => Holds(v, from) && end <= Finish(v))
                .ThenByDescending(v => Holds(v, start))
                .ThenBy(v => Begin(v).UtcTicks)
                .ThenByDescending(v => v.Length.Ticks)
                .ThenBy(v => v.AssetId)
                .First();
        }

        var a = videos.Where(v => Holds(v, autoStart)).ToList();
        if (a.Count > 0)
            return (Pick(a).AssetId, FitMarkPosition.Proposed);

        var b = videos.Where(v => Holds(v, start)).ToList();
        if (b.Count == 0) return null;
        var video = Pick(b);
        var edge = autoStart < Begin(video) ? MediaTime.Zero : MediaTime.FromFrame(video.Frames - 1, video.Rate);
        return (video.AssetId, FitMarkPosition.Clamped(edge));
    }

    /// <summary>
    /// The new marks for the climbs that are neither active nor declined and get a video (D051 §5.5), each with the
    /// estimate frozen into it and <c>positionChangedAt</c> = <paramref name="now"/>. With <paramref name="onlyVideo"/>,
    /// only the marks whose chosen video is that one (a <c>Z</c> set on it).
    /// </summary>
    public static IReadOnlyList<FitMark> Proposals(TrainingReference training, IReadOnlyList<FitVideo> videos,
        long startCorrectionTicks, int calibrationVersion, DateTimeOffset now, Guid? onlyVideo = null)
    {
        if (training.Workout is not { } workout || !training.IsUsable) return [];
        var taken = training.Marks.Select(m => m.Climb).Concat(training.DeclinedClimbs.Select(d => d.Climb)).ToHashSet();
        var marks = new List<FitMark>();
        foreach (var climb in workout.Climbs)
        {
            var identity = IdentityOf(climb);
            if (taken.Contains(identity)) continue;
            var autoStart = climb.StartUtc.AddTicks(-startCorrectionTicks);
            if (ChooseVideo(climb, autoStart, training.ClockCorrection, videos) is not { } chosen) continue;
            if (onlyVideo is { } only && chosen.AssetId != only) continue;
            marks.Add(new FitMark(identity, autoStart, startCorrectionTicks, calibrationVersion, chosen.AssetId, chosen.Position, now));
        }
        return marks;
    }

    /// <summary>A climb split's full identity (D050 B8).</summary>
    public static ClimbIdentity IdentityOf(WorkoutSegment climb) => new(climb.Index, climb.ClimbNumber ?? 0, climb.StartUtc);

    /// <summary>The climb of the workout a mark names, or null (another FIT, or no FIT read).</summary>
    public static WorkoutSegment? ClimbOf(ClimbingWorkout? workout, ClimbIdentity identity) =>
        workout?.Climbs.FirstOrDefault(c => IdentityOf(c) == identity);

    // ---- The effective start (§4.2, §4.5) -------------------------------------------------------------------------------

    /// <summary>The source time on the clip's video of the frame <c>T</c> of its Duration animation — one conversion
    /// timeline → source by the clip's rule, <c>T</c> taken as the D048 strip takes it; null without one.</summary>
    public static MediaTime? AnimationEnd(VideoClip clip, HoldMarkup? route, FrameRate projectRate)
    {
        if (route?.Animation is not { Mode: HoldAnimationMode.Duration } animation) return null;
        var start = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, animation.StartSourceTime, projectRate);
        var t = start + HoldAnimationRule.DurationFrames(animation, projectRate);
        return HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, t, projectRate);
    }

    /// <summary>The effective start of <paramref name="mark"/>: the linked clip's <c>T</c> on that clip's video; else the
    /// own position on the mark's video — for <c>proposed</c> the source time of <c>W_auto</c> by that video's <c>Z</c>
    /// (only <c>W_auto</c> without one). Null when a linked clip is gone or has no Duration animation.</summary>
    public static FitEffectiveStart? EffectiveStart(FitMark mark, Project project)
    {
        if (mark.LinkedClipId is { } clipId)
        {
            if (FindVideoClip(project.Timeline, clipId) is not { } clip ||
                AnimationEnd(clip, LinkedRoute(clip, mark.LinkedRouteId), project.Settings.FrameRate) is not { } t)
                return null;
            return new FitEffectiveStart(clip.MediaAssetId, t, null);
        }
        if (mark.Position.SourceTime is { } own)
            return new FitEffectiveStart(mark.MediaAssetId, own, null);
        var asset = project.MediaAssets.FirstOrDefault(a => a.Id == mark.MediaAssetId);
        if (asset is not null && RecordingZeroEstimator.Resolve(asset).Utc is { } zero && project.Training is { } training)
            return new FitEffectiveStart(mark.MediaAssetId, TrainingAlignment.SourceTimeOf(zero, mark.AutoStartUtc, training.ClockCorrection), null);
        return new FitEffectiveStart(mark.MediaAssetId, null, mark.AutoStartUtc);
    }

    /// <summary>Whether two effective starts are the same (D051 §5.6, О5): the same video and the same source frame of it
    /// (its own rate, else the project's); without a source time, the same <c>W_auto</c>.</summary>
    public static bool SameStart(FitEffectiveStart? a, FitEffectiveStart? b, Project project)
    {
        if (a is not { } x || b is not { } y) return a is null && b is null;
        if (x.AssetId != y.AssetId) return false;
        if (x.SourceTime is not { } sx || y.SourceTime is not { } sy) return x.SourceTime is null && y.SourceTime is null && x.AutoStartUtc == y.AutoStartUtc;
        var asset = project.MediaAssets.FirstOrDefault(m => m.Id == x.AssetId);
        var rate = asset is null ? project.Settings.FrameRate : RateOf(asset, project.Settings.FrameRate);
        return sx.ToFrameFloor(rate) == sy.ToFrameFloor(rate);
    }

    /// <summary>
    /// The watch time of a mark's effective start (D051 §4.5): linked → <c>Z_u + s_T + CC</c> by the linked clip's video;
    /// unlinked <c>proposed</c> → <c>W_auto</c> exactly; <c>clamped</c> / <c>moved</c> → <c>Z_v + s + CC</c>. Null when
    /// the video of the effective start has no <c>Z</c> (or the linked clip is unusable).
    /// </summary>
    public static DateTimeOffset? WatchTimeOfStart(FitMark mark, Project project)
    {
        if (project.Training is not { } training) return null;
        if (mark.LinkedClipId is null && mark.Position.Kind == FitMarkPositionKind.Proposed)
            return ZeroOf(project, mark.MediaAssetId) is null ? null : mark.AutoStartUtc;
        if (EffectiveStart(mark, project) is not { SourceTime: { } s } start) return null;
        return ZeroOf(project, start.AssetId) is { } zero ? TrainingAlignment.WatchTime(zero, s, training.ClockCorrection) : null;
    }

    private static DateTimeOffset? ZeroOf(Project project, Guid assetId) =>
        project.MediaAssets.FirstOrDefault(a => a.Id == assetId) is { } asset ? RecordingZeroEstimator.Resolve(asset).Utc : null;

    // ---- Observations (§5.8, §5.9) -------------------------------------------------------------------------------------

    /// <summary>Whether a mark counts for the calibration (D051 §5.8): a <c>clamped</c> mark without a link does not.</summary>
    public static bool IsUsable(FitMark mark) => mark.LinkedClipId is not null || mark.Position.Kind != FitMarkPositionKind.Clamped;

    /// <summary>The observations of a save (D051 §5.8): one per usable mark whose climb is in the training's FIT —
    /// <c>c = W_FITStart − W(effective start)</c>, or no value when the video of its effective start has no <c>Z</c>
    /// (§5.9). Empty while the FIT is not usable (nothing is known of the climbs).</summary>
    public static IReadOnlyList<CalibrationObservation> Observations(Project project)
    {
        if (project.Training is not { IsUsable: true, Workout: { } workout } training) return [];
        var observations = new List<CalibrationObservation>();
        foreach (var mark in training.Marks)
        {
            if (!IsUsable(mark) || ClimbOf(workout, mark.Climb) is not { } climb) continue;
            var key = new CalibrationKey(training.Activity, mark.Climb);
            long? value = WatchTimeOfStart(mark, project) is { } w ? climb.StartUtc.UtcTicks - w.UtcTicks : null;
            observations.Add(new CalibrationObservation(key, value, mark.PositionChangedAt));
        }
        return observations;
    }

    // ---- The link following its markup (§5.4) and positionChangedAt (§5.6) -------------------------------------------

    /// <summary>What an edit needs to know of the marks before it runs: every mark's effective start, the hold identities
    /// of each linked clip's markup and the clips that existed.</summary>
    public sealed record Before(
        IReadOnlyDictionary<ClimbIdentity, FitEffectiveStart?> Starts,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> LinkedHolds,
        IReadOnlySet<Guid> Clips,
        IReadOnlySet<ClimbIdentity> Linked);

    public static Before Capture(Project project)
    {
        var marks = project.Training?.Marks ?? [];
        var linkedHolds = new Dictionary<Guid, IReadOnlySet<Guid>>();
        foreach (var mark in marks)
            if (mark.LinkedClipId is { } id && FindVideoClip(project.Timeline, id) is { } linked &&
                LinkedRoute(linked, mark.LinkedRouteId) is { } markup)
                linkedHolds[id] = markup.Holds.Select(h => h.Id).ToHashSet();
        return new Before(marks.ToDictionary(m => m.Climb, m => EffectiveStart(m, project)), linkedHolds,
            AllClips(project.Timeline).Select(c => c.Id).ToHashSet(),
            marks.Where(m => m.LinkedClipId is not null).Select(m => m.Climb).ToHashSet());
    }

    /// <summary>
    /// The marks after an edit (D051 §5.4, §5.6): a link whose clip lost its Duration markup moves to the one clip the edit
    /// created holding a Duration markup with the same holds (D048's Split / Insert / Overwrite), or is removed with the
    /// markup; <c>positionChangedAt</c> becomes <paramref name="now"/> exactly where the effective start changed (video and
    /// source frame) — except the marks of <paramref name="keep"/>, linked by «Разметить трассу от метки», which puts
    /// <c>T</c> on the mark's frame at any speed of the clip (D051 §5.6). A mark whose video left the project (media
    /// removed) leaves with it, and a declined climb keeps no former mark on such a video — no mark ever refers to a video
    /// that isn't in the project (D051 §5.3). Null when nothing changes.
    /// </summary>
    public static (IReadOnlyList<FitMark> Marks, IReadOnlyList<DeclinedClimb> Declined)? Follow(Project project, Before before,
        DateTimeOffset now, IReadOnlySet<ClimbIdentity>? keep = null)
    {
        if (project.Training is not { } training || (training.Marks.Count == 0 && training.DeclinedClimbs.Count == 0)) return null;
        var videos = project.MediaAssets.Select(a => a.Id).ToHashSet();
        var created = AllClips(project.Timeline).OfType<VideoClip>().Where(c => !before.Clips.Contains(c.Id)).ToList();
        var result = new List<FitMark>(training.Marks.Count);
        var changed = false;
        foreach (var mark in training.Marks)
        {
            if (!videos.Contains(mark.MediaAssetId))
            {
                changed = true;
                continue;
            }
            var next = mark;
            if (mark.LinkedClipId is { } id &&
                (FindVideoClip(project.Timeline, id) is not { } clip || !HasDuration(LinkedRoute(clip, mark.LinkedRouteId))))
            {
                var holds = before.LinkedHolds.TryGetValue(id, out var h) ? h : new HashSet<Guid>();
                var heirs = created.SelectMany(c => c.Routes.Where(r => HasDuration(r) && r.Holds.Any(x => holds.Contains(x.Id)))
                    .Select(r => (Clip: c, Route: r))).ToList();
                next = heirs.Count == 1
                    ? mark with { LinkedClipId = heirs[0].Clip.Id, LinkedRouteId = heirs[0].Route.Id }
                    : mark with { LinkedClipId = null, LinkedRouteId = null };
            }
            // A mark unlinked before and after keeps its time: its own moves set it themselves, a new Z or clock
            // correction never does (D051 §5.6).
            var linkedAround = before.Linked.Contains(mark.Climb) || next.LinkedClipId is not null;
            if (linkedAround && keep?.Contains(mark.Climb) != true && before.Starts.TryGetValue(mark.Climb, out var was) &&
                !SameStart(was, EffectiveStart(next, project), project))
                next = next with { PositionChangedAt = now };
            changed |= next != mark;
            result.Add(next);
        }
        var declined = training.DeclinedClimbs
            .Select(d => d.FormerMark is { } former && !videos.Contains(former.MediaAssetId) ? d with { FormerMark = null } : d)
            .ToList();
        changed |= !declined.SequenceEqual(training.DeclinedClimbs);
        return changed ? (result, declined) : null;
    }

    private static bool HasDuration(HoldMarkup? route) => route?.Animation is { Mode: HoldAnimationMode.Duration };

    /// <summary>D053: the route of <paramref name="clip"/> a mark is linked to — <paramref name="routeId"/>, or (a project
    /// before v15) the clip's single route.</summary>
    public static HoldMarkup? LinkedRoute(VideoClip clip, Guid? routeId) =>
        RouteOverlays.RouteIdOf(routeId, clip.Routes) is { } id ? RouteOverlays.Find(clip, id) : null;

    public static IEnumerable<Clip> AllClips(Sequence timeline) => timeline.VideoTracks.Concat(timeline.AudioTracks).SelectMany(t => t.Clips);

    public static VideoClip? FindVideoClip(Sequence timeline, Guid clipId) =>
        AllClips(timeline).OfType<VideoClip>().FirstOrDefault(c => c.Id == clipId);
}
