using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>Why the readout at a frame has no value (D049 §5, D050 amendment B4).</summary>
public enum TrainingReadoutReason
{
    NoTraining,
    TrainingNotAvailable,
    RecordingStartUnknown,
    OutsideClip,
    OutsideRecording,
    NoSample,
}

/// <summary>The mapping at one frame: the watch time and the heart rate, or the reason there is none.</summary>
public readonly record struct TrainingReadout(DateTimeOffset? WatchTime, int? Bpm, TrainingReadoutReason? Reason);

/// <summary>A climb of the training as a video shows it: the climb and its interval <c>[SourceStart, SourceEnd)</c> in the
/// video's source time — not clipped to the range that was asked for (may begin before 0 or end past the video).</summary>
public readonly record struct SourceClimb(WorkoutSegment Climb, MediaTime SourceStart, MediaTime SourceEnd);

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

    /// <summary>The watch time and heart rate shown by the clip at the timeline frame, or why there is none.</summary>
    public static TrainingReadout ReadoutAt(TrainingReference? training, VideoClip clip, MediaAsset asset, long frame, FrameRate rate)
    {
        if (training is null) return new(null, null, TrainingReadoutReason.NoTraining);
        if (!training.IsUsable) return new(null, null, TrainingReadoutReason.TrainingNotAvailable);
        if (!Covers(clip, frame, rate)) return new(null, null, TrainingReadoutReason.OutsideClip);
        if (RecordingZeroEstimator.Resolve(asset).Utc is not { } zero) return new(null, null, TrainingReadoutReason.RecordingStartUnknown);

        var w = WatchTime(zero, SourceTimeAt(clip, frame, rate), training.ClockCorrection);
        var workout = training.Workout!;
        if (!workout.Recording.Contains(w)) return new(w, null, TrainingReadoutReason.OutsideRecording);
        return WorkoutTimeline.HeartRateAt(workout, w) is { } bpm
            ? new(w, bpm, null)
            : new(w, null, TrainingReadoutReason.NoSample);
    }

    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);
}
