using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>Where the time a video is bound to the FIT by comes from (D054 §2).</summary>
public enum FitBindingTimeSource
{
    /// <summary>The video's recording zero <c>Z</c> (D049 §5) — set by the user; exact.</summary>
    RecordingZero,

    /// <summary>The file's recording-time tag read as the end of the recording (<c>tag − duration</c>); approximate.</summary>
    RecordingTag,

    /// <summary>The file-name time under the watch's UTC offset (<c>L − h</c>); approximate.</summary>
    FileName,
}

/// <summary>The instant of a video's first source frame on the camera's clock that the FIT binding uses, and its source.
/// Never a recording zero: an approximate time is used by the binding only (D054 §2, D050 B1 unchanged).</summary>
public readonly record struct FitBindingTime(DateTimeOffset Zero, FitBindingTimeSource Source)
{
    public bool IsApproximate => Source != FitBindingTimeSource.RecordingZero;
}

/// <summary>A climb a video clip shows (D054 §3): the climb and the length of its estimated interval inside the clip's
/// source range.</summary>
public readonly record struct ShownClimb(WorkoutSegment Climb, MediaTime Shown);

/// <summary>
/// The automatic binding of the FIT's climbs to the video clips of the timeline (D054) — pure: the time a video is bound
/// by and the climbs a clip shows. The route areas themselves are made by the edit service (D053 §7's rule).
/// </summary>
public static class FitRouteBinding
{
    /// <summary>How far the file's two times may disagree and still be one time (D054 §2): both have whole seconds and the
    /// duration's rounding; the 2026-09-27 files agree within 2 s but for an edited one.</summary>
    public static readonly MediaTime FileTimesTolerance = MediaTime.FromSeconds(5);

    /// <summary>
    /// The time a video is bound to the FIT by (D054 §2): its recording zero when known (set by the user); else the file's
    /// recording-time tag read as the end of the recording (<c>tag − duration</c>); else the file-name time under the
    /// watch's UTC offset; else null — the video is not bound (never a guess). Null too when the tag and the file name
    /// both give a time and they disagree by more than <see cref="FileTimesTolerance"/> (<see cref="FileTimesConflict"/>):
    /// a file edited after the recording, its start unknown.
    /// </summary>
    public static FitBindingTime? TimeOf(MediaAsset asset, ClimbingWorkout workout)
    {
        if (asset.Kind != MediaKind.Video) return null;
        if (RecordingZeroEstimator.Resolve(asset).Utc is { } zero) return new FitBindingTime(zero, FitBindingTimeSource.RecordingZero);
        var candidates = RecordingZeroEstimator.Candidates(asset, workout.UtcOffset);
        if (Conflict(candidates)) return null;
        if (candidates.TagMarksEnd is { } tag) return new FitBindingTime(tag, FitBindingTimeSource.RecordingTag);
        if (candidates.FromFileName is { } name) return new FitBindingTime(name, FitBindingTimeSource.FileName);
        return null;
    }

    /// <summary>Whether a video without a <c>Z</c> is unbound because its tag and its file name disagree (D054 §2).</summary>
    public static bool FileTimesConflict(MediaAsset asset, ClimbingWorkout workout) =>
        asset.Kind == MediaKind.Video && RecordingZeroEstimator.Resolve(asset).Utc is null &&
        Conflict(RecordingZeroEstimator.Candidates(asset, workout.UtcOffset));

    private static bool Conflict(RecordingZeroCandidates candidates) =>
        candidates is { TagMarksEnd: { } tag, FromFileName: { } name } && Math.Abs((tag - name).Ticks) > FileTimesTolerance.Ticks;

    /// <summary>
    /// The climbs a video clip shows (D054 §3): those of which at least half of the estimated climb lies inside the clip's
    /// source range <c>[SourceIn, SourceOut)</c> by <paramref name="time"/> and the clock correction, in file order. The
    /// estimated climb is the one the route area is laid out by: from <c>W_auto = W_FITStart − StartCorrection</c> (D051
    /// §5.5) to the actual end <c>W_end − ClimbEndLag</c> (D050); the FIT interval when that is empty.
    /// </summary>
    public static IReadOnlyList<ShownClimb> ClimbsShown(ClimbingWorkout workout, FitBindingTime time, MediaTime clockCorrection,
        long startCorrectionTicks, VideoClip clip)
    {
        var shown = new List<ShownClimb>();
        foreach (var climb in workout.Climbs)
        {
            var start = TrainingAlignment.SourceTimeOf(time.Zero, climb.StartUtc.AddTicks(-startCorrectionTicks), clockCorrection);
            var end = TrainingAlignment.SourceTimeOf(time.Zero, TrainingAlignment.AnchorTime(climb, ClimbAnchor.End), clockCorrection);
            if (end <= start)
            {
                start = TrainingAlignment.SourceTimeOf(time.Zero, climb.StartUtc, clockCorrection);
                end = TrainingAlignment.SourceTimeOf(time.Zero, climb.End, clockCorrection);
            }
            var length = end - start;
            var from = start > clip.SourceIn ? start : clip.SourceIn;
            var to = end < clip.SourceOut ? end : clip.SourceOut;
            var inside = to - from;
            if (inside <= MediaTime.Zero) continue;
            // At least half of the climb: a climb cut by the clip's edge to less than half belongs to the clip that shows more.
            if (inside.Ticks * 2 >= length.Ticks) shown.Add(new ShownClimb(climb, inside));
        }
        return shown;
    }
}
