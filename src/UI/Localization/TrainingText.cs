using System.Globalization;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Telemetry;

namespace AiVideoEditor.UI.Localization;

/// <summary>An imported training as the user reads it (Phase Coros C1): its refusal reasons, its warnings and a one-line
/// summary. Never the enums' names.</summary>
public static class TrainingText
{
    /// <summary>Why the file was refused, as the end of a sentence ("the file was not found.").</summary>
    public static string Error(TrainingFileError error) => error switch
    {
        TrainingFileError.NotAFitFile => Strings.TrainingError_NotAFitFile,
        TrainingFileError.Truncated => Strings.TrainingError_Truncated,
        TrainingFileError.Corrupted => Strings.TrainingError_Corrupted,
        TrainingFileError.Malformed => Strings.TrainingError_Malformed,
        TrainingFileError.NotAnActivity => Strings.TrainingError_NotAnActivity,
        TrainingFileError.NotCoros => Strings.TrainingError_NotCoros,
        TrainingFileError.NoSession => Strings.TrainingError_NoSession,
        TrainingFileError.NoHeartRate => Strings.TrainingError_NoHeartRate,
        TrainingFileError.ClockNotSet => Strings.TrainingError_ClockNotSet,
        TrainingFileError.FileNotFound => Strings.TrainingError_FileNotFound,
        TrainingFileError.Unreadable => Strings.TrainingError_Unreadable,
        TrainingFileError.TooLarge => Strings.TrainingError_TooLarge,
        _ => Strings.TrainingError_Malformed
    };

    /// <summary>The training file's state for the tooltip (D049 §9).</summary>
    public static string State(TrainingAvailability availability) => availability switch
    {
        TrainingAvailability.Available => Strings.TrainingState_Available,
        TrainingAvailability.Changed => Strings.TrainingState_Changed,
        TrainingAvailability.Missing => Strings.TrainingState_Missing,
        TrainingAvailability.OtherActivity => Strings.TrainingState_OtherActivity,
        TrainingAvailability.Unreadable => Strings.TrainingState_Unreadable,
        _ => Strings.TrainingState_Unresolved
    };

    /// <summary>What was left out ("2 implausible heart-rate values left out").</summary>
    public static string Warning(TrainingFileWarning warning) => warning.Kind switch
    {
        TrainingFileWarningKind.NonMonotonicSamples => Plural.Format(warning.Count, Strings.TrainingWarning_NonMonotonicSamples),
        TrainingFileWarningKind.ImplausibleHeartRate => Plural.Format(warning.Count, Strings.TrainingWarning_ImplausibleHeartRate),
        TrainingFileWarningKind.InvalidSegments => Plural.Format(warning.Count, Strings.TrainingWarning_InvalidSegments),
        TrainingFileWarningKind.UnusableUtcOffset => Strings.TrainingWarning_UnusableUtcOffset,
        _ => warning.Kind.ToString()
    };

    /// <summary>"COROS APEX 4 42mm, 27.09.2026 10:02–11:39 (UTC+03:00), 14 climbs": the device, the date and the times of
    /// the recording in the watch's local time (in UTC when the file states no usable offset) and the number of climbs.</summary>
    public static string Summary(ClimbingWorkout workout)
    {
        var offset = workout.UtcOffset ?? TimeSpan.Zero;
        var start = workout.StartUtc.ToOffset(offset);
        var end = workout.EndUtc.ToOffset(offset);
        var culture = CultureInfo.CurrentCulture;
        return string.Format(Strings.Training_Summary,
            string.IsNullOrWhiteSpace(workout.DeviceName) ? "COROS" : workout.DeviceName,
            start.ToString("d", culture),
            start.ToString("t", culture),
            end.ToString("t", culture),
            ZoneName(workout.UtcOffset),
            Plural.Format(workout.Climbs.Count(), Strings.Training_Climbs));
    }

    /// <summary>"UTC", "UTC+03:00", "UTC−05:30".</summary>
    public static string ZoneName(TimeSpan? offset) => offset is not { } o || o == TimeSpan.Zero
        ? "UTC"
        : $"UTC{(o < TimeSpan.Zero ? "−" : "+")}{o.Duration():hh\\:mm}";

    /// <summary>The status after an import: the summary, then what was left out, if anything.</summary>
    public static string Imported(ClimbingWorkout workout)
    {
        var text = string.Format(Strings.Training_Imported, Summary(workout));
        return workout.Warnings.Count == 0
            ? text
            : text + "; " + string.Join("; ", workout.Warnings.Select(Warning));
    }
}
