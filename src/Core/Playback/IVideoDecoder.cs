using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Playback;

/// <summary>Whether a decoder may use hardware decoding. Hardware is never assumed:
/// <see cref="Auto"/> falls back to software when it is unavailable or fails.</summary>
public enum HardwareDecoding
{
    Auto,
    Disabled
}

/// <summary>What to decode: one source file's first video stream, starting so that the
/// frame <see cref="SourceFrameSelector"/> picks for <see cref="FirstSamplePoint"/> is
/// included.</summary>
public sealed record VideoDecodeRequest
{
    public required string FilePath { get; init; }

    /// <summary>The file's start time (<c>MediaMetadata.StartTime</c>): origin of source time.</summary>
    public MediaTime StartTime { get; init; }

    /// <summary>Sample point of the first timeline frame that will be shown.</summary>
    public required SourceSamplePoint FirstSamplePoint { get; init; }

    /// <summary>Nominal source rate (<see cref="SourceFrameSelector.NominalRate"/>); sizes the
    /// initial preroll only. Null when unknown.</summary>
    public FrameRate? NominalFrameRate { get; init; }

    /// <summary>Frames larger than this are scaled down, keeping the aspect ratio.</summary>
    public int MaxWidth { get; init; } = 1280;
    public int MaxHeight { get; init; } = 720;

    public HardwareDecoding Hardware { get; init; } = HardwareDecoding.Auto;
}

/// <summary>
/// Decodes source video into <see cref="DecodedFrame"/>s with exact PTS. Which frame a
/// timeline frame shows is decided by <see cref="SourceFrameSelector"/>, never by the decoder.
/// </summary>
public interface IVideoDecoder
{
    /// <summary>
    /// Starts decoding. The returned stream yields frames in presentation order; its first
    /// frame is at or before <see cref="VideoDecodeRequest.FirstSamplePoint"/>, or is the
    /// stream's very first frame (hold-first). Throws <see cref="VideoDecodeException"/>
    /// when that cannot be achieved within the decoder's limits.
    /// </summary>
    Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default);
}

/// <summary>A running decode. Dispose to stop it.</summary>
public interface IVideoFrameStream : IAsyncDisposable
{
    /// <summary>The next frame in presentation order, or null at the end of the stream.</summary>
    ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default);
}

public enum VideoDecodeError
{
    /// <summary>No decoder backend is available (e.g. ffmpeg not found).</summary>
    DecoderUnavailable,
    FileNotFound,
    /// <summary>The frame for the requested position could not be reached within the preroll limit.</summary>
    FrameNotReached,
    /// <summary>The file has no decodable video frames.</summary>
    NoVideo,
    DecoderFailed,
    Timeout
}

/// <summary>A controlled decoder failure for one source; playback treats the segment as offline.</summary>
public sealed class VideoDecodeException : Exception
{
    public VideoDecodeException(VideoDecodeError error, string message, Exception? inner = null)
        : base(message, inner) => Error = error;

    public VideoDecodeError Error { get; }
}
