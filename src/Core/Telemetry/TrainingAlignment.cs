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

/// <summary>
/// The video ↔ training mapping of D049 §6, in ticks: timeline frame → source time <c>s(n)</c> (the clip's exact rule, D009)
/// → camera time <c>c = Z + s</c> → watch time <c>w = c + ClockCorrection</c>. <c>Z</c> is the media file's recording zero
/// in camera time C: its override (<see cref="MediaAsset.RecordingZeroOverrideUtc"/>); an automatic zero exists only once
/// Step 35.3's rule is approved (D050 amendment B1), so until then a file without an override is «unknown». Never reads
/// the system clock or time zone.
/// </summary>
public static class TrainingAlignment
{
    /// <summary>The source time a timeline frame of the clip shows.</summary>
    public static MediaTime SourceTimeAt(VideoClip clip, long frame, FrameRate rate) =>
        HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, frame, rate);

    /// <summary>The media file's recording zero in C, or null when unknown (D049 §5).</summary>
    public static DateTimeOffset? RecordingZero(MediaAsset asset) =>
        asset.Kind == MediaKind.Video ? asset.RecordingZeroOverrideUtc : null;

    /// <summary><c>w = Z + s + ClockCorrection</c>.</summary>
    public static DateTimeOffset WatchTime(DateTimeOffset zero, MediaTime sourceTime, MediaTime clockCorrection) =>
        zero.ToUniversalTime().AddTicks(checked(sourceTime.Ticks + clockCorrection.Ticks));

    /// <summary>The override that puts the frame showing <paramref name="sourceTime"/> on the watch instant
    /// <paramref name="splitStart"/>: <c>Z = W_split − ClockCorrection − s(n)</c> (D050 amendment B2).</summary>
    public static DateTimeOffset ZeroForSplit(DateTimeOffset splitStart, MediaTime clockCorrection, MediaTime sourceTime) =>
        splitStart.ToUniversalTime().AddTicks(checked(-clockCorrection.Ticks - sourceTime.Ticks));

    /// <summary>Whether the timeline frame lies inside the clip.</summary>
    public static bool Covers(VideoClip clip, long frame, FrameRate rate)
    {
        var t = MediaTime.FromFrame(frame, rate);
        return t >= clip.TimelineStart && t < clip.TimelineStart + clip.Duration;
    }

    /// <summary>The watch time and heart rate shown by the clip at the timeline frame, or why there is none.</summary>
    public static TrainingReadout ReadoutAt(TrainingReference? training, VideoClip clip, MediaAsset asset, long frame, FrameRate rate)
    {
        if (training is null) return new(null, null, TrainingReadoutReason.NoTraining);
        if (!training.IsUsable) return new(null, null, TrainingReadoutReason.TrainingNotAvailable);
        if (!Covers(clip, frame, rate)) return new(null, null, TrainingReadoutReason.OutsideClip);
        if (RecordingZero(asset) is not { } zero) return new(null, null, TrainingReadoutReason.RecordingStartUnknown);

        var w = WatchTime(zero, SourceTimeAt(clip, frame, rate), training.ClockCorrection);
        var workout = training.Workout!;
        if (!workout.Recording.Contains(w)) return new(w, null, TrainingReadoutReason.OutsideRecording);
        return WorkoutTimeline.HeartRateAt(workout, w) is { } bpm
            ? new(w, bpm, null)
            : new(w, null, TrainingReadoutReason.NoSample);
    }
}
