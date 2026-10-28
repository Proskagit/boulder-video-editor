namespace AiVideoEditor.Core.Telemetry;

/// <summary>Why a training file was refused. The UI words each reason for the user; the exception's
/// <see cref="Exception.Message"/> is an English detail for the log.</summary>
public enum TrainingFileError
{
    /// <summary>Not a FIT file at all (no header or signature).</summary>
    NotAFitFile,

    /// <summary>The file ends before the data its header promises.</summary>
    Truncated,

    /// <summary>A checksum does not match: the file was damaged.</summary>
    Corrupted,

    /// <summary>The FIT structure is invalid (an undefined local message, a timestamp chain without a start).</summary>
    Malformed,

    /// <summary>A FIT file, but not a recorded activity (e.g. a course, settings or a workout plan).</summary>
    NotAnActivity,

    /// <summary>An activity recorded by a device of another manufacturer than COROS.</summary>
    NotCoros,

    /// <summary>The activity has no session summary, so its start and end are unknown.</summary>
    NoSession,

    /// <summary>The activity has no heart-rate samples.</summary>
    NoHeartRate,

    /// <summary>The watch's clock was not set: its times are relative, not absolute, so no video can be matched.</summary>
    ClockNotSet,

    /// <summary>The file does not exist (moved or deleted after it was picked).</summary>
    FileNotFound,

    /// <summary>The file could not be read: locked by another program, no access, or a disk error.</summary>
    Unreadable,

    /// <summary>The file is larger than any training export can be: most likely another file was picked.</summary>
    TooLarge,
}

/// <summary>A training file could not be imported. <see cref="Error"/> is the reason the UI shows.</summary>
public sealed class TrainingFileException : Exception
{
    public TrainingFileException(TrainingFileError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public TrainingFileError Error { get; }
}
