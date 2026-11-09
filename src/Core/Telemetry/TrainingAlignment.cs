using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>Why the readout at a frame has no value (D049 §5, D050 amendment B4).</summary>
public enum TrainingReadoutReason
{
    NoTraining,
    TrainingNotAvailable,
    /// <summary>D055 §1: the video's file gives no time (no tag, no time in its name).</summary>
    NoFileTime,
    /// <summary>D055 §1: the file's tag and its name disagree by more than 5 s (an edited file).</summary>
    FileTimesDisagree,
    OutsideClip,
    OutsideRecording,
    NoSample,
}

/// <summary>The mapping at one frame: the watch time and the heart rate, or the reason there is none.</summary>
public readonly record struct TrainingReadout(DateTimeOffset? WatchTime, int? Bpm, TrainingReadoutReason? Reason);

/// <summary>A climb of the training as a video shows it: the climb and its interval <c>[SourceStart, SourceEnd)</c> in the
/// video's source time — not clipped to the range that was asked for (may begin before 0 or end past the video).</summary>
public readonly record struct SourceClimb(WorkoutSegment Climb, MediaTime SourceStart, MediaTime SourceEnd);

/// <summary>The point of a climb that a video frame is matched to (D050 amendment of 2026-11-02 to B2): its end — the
/// default, the watch records it more steadily than the start — or its start.</summary>
public enum ClimbAnchor
{
    End,
    Start,
}

/// <summary>
/// The video ↔ training mapping of D049 §6, in ticks — the only implementation of it (D050 amendment B8): timeline frame
/// → source time <c>s(n)</c> (the clip's exact rule, D009) → camera time <c>c = Z + s</c> → watch time
/// <c>w = c + ClockCorrection</c>, and back. <c>Z</c> is the media file's recording zero in camera time C, resolved by
/// <see cref="RecordingZeroEstimator"/>. Exact tick arithmetic, checked against overflow; never reads the system clock or
/// time zone; every instant it returns has the offset 0.
/// </summary>
public static class TrainingAlignment
{
    /// <summary>The source time a timeline frame of the clip shows.</summary>
    public static MediaTime SourceTimeAt(VideoClip clip, long frame, FrameRate rate) =>
        HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, frame, rate);

    /// <summary><c>w = Z + s + ClockCorrection</c>.</summary>
    public static DateTimeOffset WatchTime(DateTimeOffset zero, MediaTime sourceTime, MediaTime clockCorrection) =>
        Utc(checked(zero.UtcTicks + sourceTime.Ticks + clockCorrection.Ticks));

    /// <summary>The inverse: the source time that shows the watch instant <paramref name="watchTime"/>,
    /// <c>s = w − ClockCorrection − Z</c> (may be negative or past the video's end).</summary>
    public static MediaTime SourceTimeOf(DateTimeOffset zero, DateTimeOffset watchTime, MediaTime clockCorrection) =>
        new(checked(watchTime.UtcTicks - clockCorrection.Ticks - zero.UtcTicks));

    /// <summary>The override that puts the frame showing <paramref name="sourceTime"/> on the watch instant
    /// <paramref name="splitStart"/>: <c>Z = W_split − ClockCorrection − s(n)</c> (D050 amendment B2).</summary>
    public static DateTimeOffset ZeroForSplit(DateTimeOffset splitStart, MediaTime clockCorrection, MediaTime sourceTime) =>
        Utc(checked(splitStart.UtcTicks - clockCorrection.Ticks - sourceTime.Ticks));

    /// <summary>
    /// How much later than the end seen on a video the watch records a climb's end: 4.6 s, exactly 46 000 000 ticks (D050
    /// amendment of 2026-11-02 to B2) — the mean of the median end lags of the owner's two trainings (4.4 s on 2026-08-08,
    /// 4.8 s on 2026-09-27, measured with no clock correction). Used only when a frame is matched to a climb's end; never in
    /// the clock correction, a climb's boundaries (<see cref="WorkoutSegment.Interval"/>) or the readout.
    /// </summary>
    public static readonly MediaTime ClimbEndLag = new(46_000_000);

    /// <summary>The watch instant of the frame that shows <paramref name="anchor"/> of the climb: its start
    /// <c>W_start</c>, or its end by the one rule for a climb's boundaries less the lag, <c>W_end − ClimbEndLag</c>.</summary>
    public static DateTimeOffset AnchorTime(WorkoutSegment climb, ClimbAnchor anchor) => anchor == ClimbAnchor.Start
        ? Utc(climb.StartUtc.UtcTicks)
        : Utc(checked(climb.End.UtcTicks - ClimbEndLag.Ticks));

    /// <summary>The override that puts the frame showing <paramref name="sourceTime"/> on the climb's anchor:
    /// <c>Z = W_end − ClimbEndLag − ClockCorrection − s(n)</c> for the end, <c>Z = W_start − ClockCorrection − s(n)</c> for
    /// the start. The lag enters only here, once; the zero it gives is an ordinary override.</summary>
    public static DateTimeOffset ZeroForClimb(WorkoutSegment climb, ClimbAnchor anchor, MediaTime clockCorrection, MediaTime sourceTime) =>
        ZeroForSplit(AnchorTime(climb, anchor), clockCorrection, sourceTime);

    /// <summary>Whether the timeline frame lies inside the clip.</summary>
    public static bool Covers(VideoClip clip, long frame, FrameRate rate)
    {
        var t = MediaTime.FromFrame(frame, rate);
        return t >= clip.TimelineStart && t < clip.TimelineStart + clip.Duration;
    }

    /// <summary>
    /// The climbs whose interval (<see cref="WorkoutSegment.Interval"/>) overlaps the source range
    /// <c>[sourceStart, sourceEnd)</c> of a video with the recording zero <paramref name="zero"/>, in file order, each with
    /// its interval in source time. The one query for «which climbs does this piece of video show».
    /// </summary>
    public static IReadOnlyList<SourceClimb> ClimbsInSource(
        ClimbingWorkout workout, DateTimeOffset zero, MediaTime clockCorrection, MediaTime sourceStart, MediaTime sourceEnd)
    {
        var start = WatchTime(zero, sourceStart, clockCorrection);
        var end = WatchTime(zero, sourceEnd, clockCorrection);
        return WorkoutTimeline.SegmentsOverlapping(workout, start, end)
            .Where(s => s.Kind == WorkoutSegmentKind.Climb)
            .Select(s => new SourceClimb(s,
                SourceTimeOf(zero, s.Interval.Start, clockCorrection),
                SourceTimeOf(zero, s.Interval.End, clockCorrection)))
            .ToList();
    }

    /// <summary>The watch time and heart rate shown by the clip at the timeline frame, or why there is none — by the file's
    /// time (D055 §1, <see cref="FitRouteBinding.FileTimeOf"/>), never by the video's recording zero.</summary>
    public static TrainingReadout ReadoutAt(TrainingReference? training, VideoClip clip, MediaAsset asset, long frame, FrameRate rate)
    {
        if (training is null) return new(null, null, TrainingReadoutReason.NoTraining);
        if (!training.IsUsable) return new(null, null, TrainingReadoutReason.TrainingNotAvailable);
        if (!Covers(clip, frame, rate)) return new(null, null, TrainingReadoutReason.OutsideClip);
        if (FitRouteBinding.FileTimeOf(asset, training.Workout!) is not { Zero: var zero })
            return new(null, null, FitRouteBinding.FileTimesDisagree(asset, training.Workout!)
                ? TrainingReadoutReason.FileTimesDisagree : TrainingReadoutReason.NoFileTime);

        var w = WatchTime(zero, SourceTimeAt(clip, frame, rate), training.ClockCorrection);
        var workout = training.Workout!;
        if (!workout.Recording.Contains(w)) return new(w, null, TrainingReadoutReason.OutsideRecording);
        return HeartRateAtWatch(workout, w) is { } bpm
            ? new(w, bpm, null)
            : new(w, null, TrainingReadoutReason.NoSample);
    }

    /// <summary>
    /// The heart rate a video with the recording zero <paramref name="zero"/> shows at <paramref name="sourceTime"/>: the
    /// watch instant <c>w = Z + s + ClockCorrection</c>, then the readout's rule — inside the recording, the latest sample at
    /// most 2 s old, outside the timer's pauses (<see cref="WorkoutTimeline.HeartRateAt"/>). Null otherwise — never an
    /// interpolated value. The heart-rate overlay (Phase 35.5) and <see cref="ReadoutAt"/> read the same value.
    /// </summary>
    public static int? HeartRateAtSource(ClimbingWorkout workout, DateTimeOffset zero, MediaTime clockCorrection, MediaTime sourceTime) =>
        HeartRateAtWatch(workout, WatchTime(zero, sourceTime, clockCorrection));

    private static int? HeartRateAtWatch(ClimbingWorkout workout, DateTimeOffset w) =>
        workout.Recording.Contains(w) ? WorkoutTimeline.HeartRateAt(workout, w) : null;

    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);
}
