using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Reserved for later media utilities (thumbnails, waveform extraction; Phase 9); not implemented.
/// FFmpeg is reached through narrower interfaces instead: <see cref="IMediaAnalysisService"/> (probe),
/// <c>IVideoDecoder</c> / <c>IAudioDecoder</c> (playback and export decoding) and
/// <see cref="IExportService"/> (export, D023).
/// </summary>
public interface IVideoEngine
{
    /// <summary>Verifies FFmpeg is available and returns its reported version, or null if not found.</summary>
    Task<string?> GetFfmpegVersionAsync(CancellationToken ct = default);

    Task<MediaMetadata> ProbeAsync(string filePath, CancellationToken ct = default);

    /// <summary>Extracts a single thumbnail frame near <paramref name="at"/> and writes it to <paramref name="outputPngPath"/>.</summary>
    Task ExtractThumbnailAsync(string filePath, Common.MediaTime at, string outputPngPath, CancellationToken ct = default);

    /// <summary>Extracts the audio track as a standalone file, e.g. for waveform generation.</summary>
    Task ExtractAudioAsync(string filePath, string outputWavPath, CancellationToken ct = default);
}

/// <summary>Reports incremental progress for a long-running FFmpeg operation of <see cref="IVideoEngine"/>
/// (the export reports <see cref="Export.ExportProgress"/>).</summary>
public sealed class EngineProgress
{
    public double? PercentComplete { get; init; }
    public string? CurrentStage { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
}
