namespace AiVideoEditor.Project.Persistence;

/// <summary>What a recovery file says about itself besides the project: the folder of the
/// project it belongs to (null if never saved), when it was written, and by which process
/// (so a file that belongs to a still-running instance isn't offered for recovery).</summary>
public sealed record RecoveryInfo(string? ProjectFolderPath, DateTimeOffset AutosavedAt, int ProcessId, DateTimeOffset? ProcessStartTime);
