using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>
/// Builds a <see cref="ClimbingWorkout"/> from a watch's FIT activity export — COROS, Garmin or any other maker (D062;
/// formerly <c>CorosWorkoutParser</c>, Phase 35). Pure: bytes in, a workout or a <see cref="TrainingFileException"/> out.
/// </summary>
/// <remarks>
/// <para>One reader by the FIT standard for every maker: it uses what the file contains and refuses it only when nothing a
/// video can be matched with is there (no absolute times, or neither heart rate nor splits). The maker
/// (<see cref="FitVendor"/>) is recognised for the summary and the log; the two makers' known differences are rules on the
/// data, not on the maker's name.</para>
/// <para>COROS (APEX 4, the owner's exports of 2026-08-08 and 2026-09-27, protocol 2.0, profile 21.158): <c>file_id</c>
/// with the device's name, one <c>session</c> / <c>lap</c> / <c>activity</c>, timer <c>event</c>s, 1 s <c>record</c>s,
/// the climbing <c>split</c>s (message 312: type 9 climb, 10 rest; 15 / 16 the split's average / maximum heart rate).</para>
/// <para>Garmin (Venu X1, export of 2026-11-14, protocol 1.0, profile 21.213): the same splits; the <c>session</c> and the
/// <c>activity</c> stamped with the training's start, not its end — the end is the start plus the session's elapsed time;
/// records only when a value changes («smart recording», 1–19 s apart) — a sample is held longer
/// (<see cref="SmartRecordingHold"/>); no device name in the file.</para>
/// <para>Both write a climb's route in split fields 69–73 (grading scale, grade value, status, falls, send — the Garmin
/// FIT SDK profile 21.221), read into <see cref="WorkoutSegment.Route"/>; verified on the Garmin export against Garmin
/// Connect (16 routes, 6b / 6c, 2 attempted, 14 completed, 9 sends, 12 falls).</para>
/// </remarks>
public static class FitWorkoutParser
{
    public const int GarminManufacturer = 1;
    public const int CorosManufacturer = 294;

    private const ushort FileIdMessage = 0;
    private const ushort SportMessage = 12;
    private const ushort SessionMessage = 18;
    private const ushort LapMessage = 19;
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

    /// <summary>How long a sample is held when the watch records only on a change (D062 §4): the longest silence of the
    /// Garmin export of 2026-11-14 is 19 s; a longer one is a gap.</summary>
    public static readonly TimeSpan SmartRecordingHold = TimeSpan.FromSeconds(20);

    /// <summary>A recording is «smart» when more than this share of the steps between heart-rate samples (pauses aside)
    /// is longer than a second; a 1 s recording has a handful of such steps at most.</summary>
    private const double SmartRecordingShare = 0.05;

    private static readonly DateTimeOffset FitEpoch = new(1989, 12, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The maker a FIT manufacturer id names.</summary>
    public static FitVendor VendorOf(int manufacturer) => manufacturer switch
    {
        CorosManufacturer => FitVendor.Coros,
        GarminManufacturer => FitVendor.Garmin,
        _ => FitVendor.Other,
    };

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

        var manufacturer = (int)(fileId.Get(1) ?? 0);
        var session = messages.FirstOrDefault(m => m.GlobalNumber == SessionMessage);
        var (start, end) = ReadRecording(messages, session);

        var warnings = new List<TrainingFileWarning>();
        var pauses = ReadPauses(messages);
        var heartRate = ReadHeartRate(messages, warnings);
        var segments = ReadSegments(messages, warnings);
        if (heartRate.Count == 0 && !segments.Any(s => s.Kind == WorkoutSegmentKind.Climb))
            throw new TrainingFileException(TrainingFileError.NoTrainingData, "The activity has neither heart-rate samples nor climbs.");

        var lap = messages.FirstOrDefault(m => m.GlobalNumber == LapMessage);
        var sport = messages.FirstOrDefault(m => m.GlobalNumber == SportMessage);
        var elapsed = session?.Get(7) is { } elapsedMs ? FromMilliseconds(elapsedMs) : new MediaTime((end - ToUtc(start)).Ticks);
        var timer = session?.Get(8) is { } timerMs
            ? FromMilliseconds(timerMs)
            : new MediaTime(Math.Max(0, elapsed.Ticks - pauses.Sum(p => p.Duration.Ticks)));

        return new ClimbingWorkout
        {
            DeviceName = fileId.GetText(8)
                ?? messages.Where(m => m.GlobalNumber == DeviceInfoMessage).Select(m => m.GetText(27)).FirstOrDefault(n => n is not null)
                ?? VendorName(VendorOf(manufacturer)),
            Manufacturer = manufacturer,
            Product = (int?)fileId.Get(2),
            StartUtc = ToUtc(start),
            EndUtc = end,
            UtcOffset = ReadUtcOffset(messages, warnings),
            ElapsedTime = elapsed,
            TimerTime = timer,
            Sport = (int?)(session?.Get(5) ?? sport?.Get(0) ?? lap?.Get(25)),
            SubSport = (int?)(session?.Get(6) ?? sport?.Get(1) ?? lap?.Get(39)),
            AverageHeartRate = (int?)session?.Get(16) ?? (heartRate.Count > 0 ? (int)Math.Round(heartRate.Average(s => s.Bpm)) : null),
            MaxHeartRate = (int?)session?.Get(17) ?? (heartRate.Count > 0 ? heartRate.Max(s => s.Bpm) : null),
            HeartRate = heartRate,
            HeartRateHold = HoldOf(heartRate, pauses),
            Pauses = pauses,
            Segments = segments,
            Warnings = warnings,
        };
    }

    /// <summary>A FIT <c>date_time</c> (seconds since 1989-12-31 00:00 UTC) as an absolute time.</summary>
    public static DateTimeOffset ToUtc(long fitSeconds) => FitEpoch.AddTicks(checked(fitSeconds * TimeSpan.TicksPerSecond));

    /// <summary>"COROS", "Garmin"; null for another maker.</summary>
    public static string? VendorName(FitVendor vendor) => vendor switch
    {
        FitVendor.Coros => "COROS",
        FitVendor.Garmin => "Garmin",
        _ => null,
    };

    private static MediaTime FromMilliseconds(long? milliseconds) =>
        new(checked((milliseconds ?? 0) * TimeSpan.TicksPerMillisecond));

    /// <summary>
    /// The recording's start (FIT seconds) and end (D062 §2). With a session: its start time; its end is the session's
    /// timestamp when that lies after the start (COROS), else the start plus the session's elapsed milliseconds (Garmin
    /// stamps the session with its start). Without a session — or with one that states no start — what the file has:
    /// the earliest record, timer event or split time to the latest.
    /// </summary>
    private static (long Start, DateTimeOffset End) ReadRecording(IReadOnlyList<FitMessage> messages, FitMessage? session)
    {
        long? start = session?.Get(2);
        DateTimeOffset? end = null;
        if (start is { } s)
        {
            if (session!.Get(FitFile.TimestampField) is { } stamp && stamp > s) end = ToUtcChecked(stamp);
            else if (session.Get(7) is { } elapsed && elapsed > 0 && s >= MinimumAbsoluteFitTime)
                end = ToUtc(s).AddTicks(checked(elapsed * TimeSpan.TicksPerMillisecond));
            else if (session.Get(FitFile.TimestampField) is { } same) end = ToUtcChecked(same);
        }

        if (start is null || end is null)
        {
            var times = messages
                .Where(m => m.GlobalNumber is RecordMessage || (m.GlobalNumber == EventMessage && m.Get(0) == TimerEvent))
                .Select(m => m.Get(FitFile.TimestampField))
                .Concat(messages.Where(m => m.GlobalNumber == SplitMessage).SelectMany(m => new[] { m.Get(9), m.Get(27) }))
                .OfType<long>()
                .ToList();
            if (times.Count == 0)
                throw new TrainingFileException(TrainingFileError.NoSession, "The activity has no session and no timed records, events or splits.");
            start ??= times.Min();
            end ??= ToUtcChecked(times.Max());
        }

        if (start < MinimumAbsoluteFitTime || end < ToUtc(MinimumAbsoluteFitTime))
            throw new TrainingFileException(TrainingFileError.ClockNotSet, "The session's times are relative to the device's power-on.");
        if (end < ToUtc(start.Value))
            throw new TrainingFileException(TrainingFileError.Malformed, "The session ends before it starts.");
        return (start.Value, end.Value);

        static DateTimeOffset ToUtcChecked(long fitSeconds) => ToUtc(Math.Max(0, fitSeconds));
    }

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

    /// <summary>The heart rate's hold (D062 §4): <see cref="SmartRecordingHold"/> when more than 5 % of the steps between
    /// samples outside the pauses are longer than a second, else the 1 s recording's 2 s.</summary>
    private static TimeSpan HoldOf(IReadOnlyList<HeartRateSample> samples, IReadOnlyList<WorkoutInterval> pauses)
    {
        int steps = 0, longSteps = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            var (from, to) = (samples[i - 1].Utc, samples[i].Utc);
            if (pauses.Any(p => p.Overlaps(from, to))) continue;
            steps++;
            if (to - from > TimeSpan.FromSeconds(1)) longSteps++;
        }

        return steps > 0 && longSteps > SmartRecordingShare * steps ? SmartRecordingHold : WorkoutTimeline.DefaultMaxSampleAge;
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
                (int?)split.Get(16))
            {
                Route = kind == WorkoutSegmentKind.Climb ? RouteOf(split) : null,
            });
        }

        if (invalid > 0) warnings.Add(new TrainingFileWarning(TrainingFileWarningKind.InvalidSegments, invalid));
        return segments;
    }

    /// <summary>A climb's route (split fields 69–73); null when the split states none of them. A value outside its enum is
    /// left out, never mapped to a neighbour.</summary>
    private static ClimbRoute? RouteOf(FitMessage split)
    {
        var scale = split.Get(69) is { } s && Enum.IsDefined(typeof(ClimbGradingScale), (int)s) ? (ClimbGradingScale?)(int)s : null;
        var grade = split.Get(70) is { } g && g is >= 0 and <= int.MaxValue ? (int?)g : null;
        var result = split.Get(71) is { } r && Enum.IsDefined(typeof(ClimbResult), (int)r) ? (ClimbResult?)(int)r : null;
        var falls = split.Get(72) is { } f && f is >= 0 and <= ushort.MaxValue ? (int?)f : null;
        var send = split.Get(73) is { } b && b is 0 or 1 ? (bool?)(b == 1) : null;
        var route = new ClimbRoute(scale, grade, result, falls, send);
        return route.IsEmpty ? null : route;
    }
}
