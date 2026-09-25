using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Export;

/// <summary>
/// One export (D023): an immutable <see cref="PlaybackSnapshot"/> of the project taken on the UI
/// thread — the same input the Preview plays — and the output file. The export never reads the live
/// project. Created by <see cref="ExportPreflight.Check"/> only when nothing blocks the export.
/// </summary>
public sealed class ExportJob
{
    public ExportJob(PlaybackSnapshot snapshot, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        Snapshot = snapshot;
        OutputPath = outputPath;
        Output = ExportOutput.For(snapshot);
    }

    public PlaybackSnapshot Snapshot { get; }

    /// <summary>Full path of the MP4 to write; an existing file is replaced only when the export succeeds.</summary>
    public string OutputPath { get; }

    public ExportOutput Output { get; }
}

public enum ExportStage
{
    /// <summary>Checking the encoder and preparing temporary files.</summary>
    Preparing,
    /// <summary>Mixing and encoding the audio track.</summary>
    Audio,
    /// <summary>Rendering and encoding the frames.</summary>
    Video,
    /// <summary>Writing the output file.</summary>
    Finalizing
}

/// <summary>Progress of a running export: <see cref="Done"/> of <see cref="Total"/> units (audio
/// samples or frames) of the current <see cref="Stage"/>.</summary>
public readonly record struct ExportProgress(ExportStage Stage, long Done, long Total);

public enum ExportFailure
{
    /// <summary>No encoder backend (ffmpeg) is available.</summary>
    EncoderUnavailable,
    /// <summary>A source could not be decoded (D023: the export stops, no substitute frames or silence).</summary>
    DecodeFailed,
    /// <summary>The encoder failed.</summary>
    EncodeFailed,
    /// <summary>The output file could not be written.</summary>
    OutputFailed
}

/// <summary>An export that failed. No output file is left behind; an existing file at the output
/// path stays as it was. Cancellation is reported as <see cref="OperationCanceledException"/> with
/// the same cleanup.</summary>
public sealed class ExportException : Exception
{
    public ExportException(ExportFailure failure, string message, Exception? inner = null)
        : base(message, inner) => Failure = failure;

    public ExportFailure Failure { get; }
}
