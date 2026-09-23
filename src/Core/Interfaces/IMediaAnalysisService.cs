using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Analyzes a media file and extracts technical metadata. This is the only
/// Core-facing contract for "run ffprobe (or whatever) and tell me what's in this
/// file" — nothing outside its implementation (in the Video subsystem) knows
/// ffprobe's JSON shape or how it's invoked.
/// </summary>
public interface IMediaAnalysisService
{
    Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default);
}

/// <summary>Distinguishes every way analysis can fail so the caller can build an
/// accurate, honest status message instead of a generic "something went wrong".</summary>
public enum MediaAnalysisOutcome
{
    Success,
    FileNotFound,
    UnsupportedMedia,
    InvalidMedia,
    ProbeToolUnavailable,
    ProbeProcessFailed,
    InvalidOutput,
    Cancelled
}

/// <summary>Result of analyzing one file. Exactly one of <see cref="Metadata"/> or
/// <see cref="ErrorMessage"/> is meaningful, depending on <see cref="Outcome"/>.</summary>
public sealed class MediaAnalysisResult
{
    public required MediaAnalysisOutcome Outcome { get; init; }

    public MediaMetadata? Metadata { get; init; }

    /// <summary>Short, human-readable message safe to show directly in the UI.
    /// Never a raw exception message or stack trace — see the analysis service's
    /// error-translation for that.</summary>
    public string? ErrorMessage { get; init; }

    public static MediaAnalysisResult Success(MediaMetadata metadata) =>
        new() { Outcome = MediaAnalysisOutcome.Success, Metadata = metadata };

    public static MediaAnalysisResult Failure(MediaAnalysisOutcome outcome, string errorMessage) =>
        new() { Outcome = outcome, ErrorMessage = errorMessage };
}

/// <summary>
/// Resolves the path to the ffprobe executable: an explicit configured path if
/// one is set, otherwise a check for "ffprobe" being runnable via PATH. Never
/// throws — returns null when it can't be found, which callers must treat as a
/// normal, expected outcome (missing FFmpeg install), not a crash.
/// </summary>
public interface IFfprobeLocator
{
    Task<string?> GetFfprobePathAsync(CancellationToken ct = default);
}
