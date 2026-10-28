using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>
/// Builds a <see cref="ClimbingWorkout"/> from a COROS watch's FIT activity export (Phase Coros). Pure: bytes in, a
/// workout or a <see cref="TrainingFileException"/> out.
/// </summary>
/// <remarks>
/// Established on the product owner's export of 2026-09-27 (COROS APEX 4 42mm, FIT protocol 2.0, profile 21.158):
/// <c>file_id</c>, <c>device_info</c>, one <c>session</c> / <c>lap</c> / <c>activity</c>, timer <c>event</c>s, 1 s
/// <c>record</c>s with heart rate and altitude, and the climbing <c>split</c>s (message 312: type 9 climb, 10 rest; fields
/// 15 / 16 the split's average / maximum heart rate, verified against the records). The route's grade and result are not
/// read: the grade is not in the file and the meaning of split field 71 is not confirmed.
/// </remarks>
public static class CorosWorkoutParser
{
    public const int CorosManufacturer = 294;

    private const ushort FileIdMessage = 0;
    private const ushort SessionMessage = 18;
    private const ushort RecordMessage = 20;
    private const ushort EventMessage = 21;
    private const ushort DeviceInfoMessage = 23;
    private const ushort ActivityMessage = 34;
    private const ushort SplitMessage = 312;

    private const int ActivityFileType = 4;
    private const int TimerEvent = 0;
    private const int ClimbActiveSplit = 9;
    private const int ClimbRestSplit = 10;

    /// <summary>FIT <c>date_time</c> values below this are seconds since the device powered on, not absolute time.</summary>
    private const long MinimumAbsoluteFitTime = 0x10000000;

    /// <summary>Plausible heart rate in bpm; other values are left out with a warning.</summary>
    public const int MinimumHeartRate = 25;

    public const int MaximumHeartRate = 250;

    private static readonly DateTimeOffset FitEpoch = new(1989, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public static ClimbingWorkout Parse(ReadOnlySpan<byte> file)
    {
        var (_, messages) = FitFile.Read(file);
        return Parse(messages);
    }

    public static ClimbingWorkout Parse(IReadOnlyList<FitMessage> messages)
    {
        var fileId = messages.FirstOrDefault(m => m.GlobalNumber == FileIdMessage)
            ?? throw new TrainingFileException(TrainingFileError.Malformed, "The FIT file has no file_id message.");

        if (fileId.Get(0) != ActivityFileType)
            throw new TrainingFileException(TrainingFileError.NotAnActivity, $"The FIT file type is {fileId.Get(0)?.ToString() ?? "unknown"}, not an activity.");

        if (fileId.Get(1) != CorosManufacturer)
            throw new TrainingFileException(TrainingFileError.NotCoros, $"The FIT file was written by manufacturer {fileId.Get(1)?.ToString() ?? "unknown"}, not COROS.");

        var session = messages.FirstOrDefault(m => m.GlobalNumber == SessionMessage)
            ?? throw new TrainingFileException(TrainingFileError.NoSession, "The activity has no session message.");

        var start = session.Get(2) ?? throw new TrainingFileException(TrainingFileError.NoSession, "The session has no start time.");
        var end = session.Get(FitFile.TimestampField) ?? throw new TrainingFileException(TrainingFileError.NoSession, "The session has no end time.");
        if (start < MinimumAbsoluteFitTime || end < MinimumAbsoluteFitTime)
            throw new TrainingFileException(TrainingFileError.ClockNotSet, "The session's times are relative to the device's power-on.");
        if (end < start)
            throw new TrainingFileException(TrainingFileError.Malformed, "The session ends before it starts.");

        var warnings = new List<TrainingFileWarning>();
        var heartRate = ReadHeartRate(messages, warnings);
        if (heartRate.Count == 0)
            throw new TrainingFileException(TrainingFileError.NoHeartRate, "The activity has no heart-rate samples.");

        return new ClimbingWorkout
        {
            DeviceName = fileId.GetText(8) ?? messages.FirstOrDefault(m => m.GlobalNumber == DeviceInfoMessage)?.GetText(27),
            Manufacturer = CorosManufacturer,
            Product = (int?)fileId.Get(2),
            StartUtc = ToUtc(start),
            EndUtc = ToUtc(end),
            UtcOffset = ReadUtcOffset(messages, warnings),
            ElapsedTime = FromMilliseconds(session.Get(7)),
            TimerTime = FromMilliseconds(session.Get(8)),
            Sport = (int?)session.Get(5),
            SubSport = (int?)session.Get(6),
            AverageHeartRate = (int?)session.Get(16),
            MaxHeartRate = (int?)session.Get(17),
            HeartRate = heartRate,
            Pauses = ReadPauses(messages),
            Segments = ReadSegments(messages, warnings),
            Warnings = warnings,
        };
    }

    /// <summary>A FIT <c>date_time</c> (seconds since 1989-12-31 00:00 UTC) as an absolute time.</summary>
    public static DateTimeOffset ToUtc(long fitSeconds) => FitEpoch.AddTicks(checked(fitSeconds * TimeSpan.TicksPerSecond));

    private static MediaTime FromMilliseconds(long? milliseconds) =>
        new(checked((milliseconds ?? 0) * TimeSpan.TicksPerMillisecond));

    private static List<HeartRateSample> ReadHeartRate(IReadOnlyList<FitMessage> messages, List<TrainingFileWarning> warnings)
    {
        var samples = new List<HeartRateSample>();
        long? last = null;
        int nonMonotonic = 0, implausible = 0;

        foreach (var message in messages)
        {
            if (message.GlobalNumber != RecordMessage) continue;
            if (message.Get(3) is not { } bpm || message.Get(FitFile.TimestampField) is not { } time) continue;

            if (time < MinimumAbsoluteFitTime || (last is { } previous && time <= previous))
            {
                nonMonotonic++;
                continue;
            }

            if (bpm < MinimumHeartRate || bpm > MaximumHeartRate)
            {
                implausible++;
                continue;
            }

            samples.Add(new HeartRateSample(ToUtc(time), (int)bpm));
            last = time;
        }

        if (nonMonotonic > 0) warnings.Add(new TrainingFileWarning(TrainingFileWarningKind.NonMonotonicSamples, nonMonotonic));
        if (implausible > 0) warnings.Add(new TrainingFileWarning(TrainingFileWarningKind.ImplausibleHeartRate, implausible));
        return samples;
    }

    /// <summary>The activity's local timestamp minus its UTC timestamp, when it is a whole quarter-hour within ±14 h.</summary>
    private static TimeSpan? ReadUtcOffset(IReadOnlyList<FitMessage> messages, List<TrainingFileWarning> warnings)
    {
        var activity = messages.FirstOrDefault(m => m.GlobalNumber == ActivityMessage);
        if (activity?.Get(FitFile.TimestampField) is not { } utc || activity.Get(5) is not { } local) return null;

        var seconds = local - utc;
        if (Math.Abs(seconds) > 14 * 3600 || seconds % 900 != 0)
        {
            warnings.Add(new TrainingFileWarning(TrainingFileWarningKind.UnusableUtcOffset, 1));
            return null;
        }

        return TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond);
    }

    /// <summary>The timer's pauses: from a stop (stop, stop_all, stop_disable, stop_disable_all) to the next start.</summary>
    private static List<WorkoutInterval> ReadPauses(IReadOnlyList<FitMessage> messages)
    {
        var pauses = new List<WorkoutInterval>();
        long? stoppedAt = null;

        var timerEvents = messages
            .Where(m => m.GlobalNumber == EventMessage && m.Get(0) == TimerEvent && m.Get(FitFile.TimestampField) is >= MinimumAbsoluteFitTime)
            .OrderBy(m => m.Get(FitFile.TimestampField));

        foreach (var e in timerEvents)
        {
            var time = e.Get(FitFile.TimestampField)!.Value;
            switch (e.Get(1))
            {
                case 1 or 4 or 8 or 9:
                    stoppedAt ??= time;
                    break;
                case 0 when stoppedAt is { } stop:
                    if (time > stop) pauses.Add(new WorkoutInterval(ToUtc(stop), ToUtc(time)));
                    stoppedAt = null;
                    break;
            }
        }

        return pauses;
    }

    private static List<WorkoutSegment> ReadSegments(IReadOnlyList<FitMessage> messages, List<TrainingFileWarning> warnings)
    {
        var segments = new List<WorkoutSegment>();
        int climbs = 0, invalid = 0, index = 0;

        foreach (var split in messages)
        {
            if (split.GlobalNumber != SplitMessage) continue;
            var position = index++;

            var elapsed = FromMilliseconds(split.Get(1));
            if (split.Get(9) is not { } startSeconds || startSeconds < MinimumAbsoluteFitTime)
            {
                invalid++;
                continue;
            }

            var startUtc = ToUtc(startSeconds);
            var endUtc = split.Get(27) is { } endSeconds && endSeconds >= MinimumAbsoluteFitTime
                ? ToUtc(endSeconds)
                : startUtc.AddTicks(elapsed.Ticks);
            if (endUtc < startUtc)
            {
                invalid++;
                continue;
            }

            var kind = split.Get(0) switch
            {
                ClimbActiveSplit => WorkoutSegmentKind.Climb,
                ClimbRestSplit => WorkoutSegmentKind.Rest,
                _ => WorkoutSegmentKind.Other,
            };

            segments.Add(new WorkoutSegment(
                position,
                kind,
                kind == WorkoutSegmentKind.Climb ? (int?)++climbs : null,
                startUtc,
                endUtc,
                elapsed,
                (int?)split.Get(15),
                (int?)split.Get(16)));
        }

        if (invalid > 0) warnings.Add(new TrainingFileWarning(TrainingFileWarningKind.InvalidSegments, invalid));
        return segments;
    }
}
