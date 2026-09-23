using System.Globalization;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Video;

/// <summary>Limits of <see cref="FfmpegVideoDecoder"/>. Defaults suit preview playback.</summary>
public sealed class FfmpegVideoDecoderSettings
{
    /// <summary>First preroll, in nominal source frames.</summary>
    public int InitialPrerollFrames { get; init; } = 2;

    /// <summary>Each failed attempt multiplies the preroll by this factor.</summary>
    public int PrerollGrowthFactor { get; init; } = 4;

    /// <summary>Upper bound of the preroll. If the needed frame is still not reached
    /// with this preroll, decoding fails with <see cref="VideoDecodeError.FrameNotReached"/>.</summary>
    public TimeSpan MaxPreroll { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Longest wait for any single frame before failing with <see cref="VideoDecodeError.Timeout"/>.</summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Value passed to <c>-hwaccel</c> for <see cref="HardwareDecoding.Auto"/>.</summary>
    public string HardwareAccelerator { get; init; } = "auto";
}

/// <summary>
/// <see cref="IVideoDecoder"/> backed by the ffmpeg CLI (DECISIONS D009/D010):
/// <code>
/// ffmpeg [-hwaccel auto] -copyts [-ss seek] -i file -map 0:v:0 -an -sn -dn
///        -vf scale=…,format=bgra,showinfo=checksum=0 -fps_mode passthrough -pix_fmt bgra -f rawvideo pipe:1
/// </code>
/// No <c>fps</c> filter: every decoded frame is delivered with its own PTS and the
/// caller chooses frames with <see cref="SourceFrameSelector"/>.
/// <para>
/// Seeking: <c>-ss</c> alone is not trusted (accurate seek drops the frame containing the
/// seek point; MPEG-TS seeks can land on a keyframe after the target). The seek target is
/// placed a preroll before the first sample point; if the first decoded frame is still after
/// that point, the attempt is discarded and retried with a larger preroll, up to
/// <see cref="FfmpegVideoDecoderSettings.MaxPreroll"/>. A seek target at or before the
/// start decodes from the beginning of the file, which always succeeds (hold-first).
/// </para>
/// <para>
/// Hardware decoding is never trusted to fall back by itself: if an attempt with
/// <c>-hwaccel</c> fails, times out or yields no frames, the same attempt is relaunched
/// without <c>-hwaccel</c>, and the rest of this open stays in software.
/// </para>
/// <para>
/// Known limitations (not handled): HDR / 10-bit tone mapping, interlaced content
/// (no deinterlacing), non-square pixels (SAR ignored), rotation metadata (ffmpeg's
/// autorotate applies, dimensions may swap), resolution changes mid-stream (each frame
/// carries its own size, untested), and phone-specific VFR quirks beyond the tested cases.
/// A hardware failure after the first frame is not retried here; the caller reopens.
/// </para>
/// </summary>
public sealed class FfmpegVideoDecoder : IVideoDecoder
{
    private static readonly long DefaultFrameTicks = TimeSpan.TicksPerSecond / 30;

    private readonly IFfmpegLocator _locator;
    private readonly ILogger<FfmpegVideoDecoder> _logger;
    private readonly FfmpegVideoDecoderSettings _settings;

    public FfmpegVideoDecoder(IFfmpegLocator locator, ILogger<FfmpegVideoDecoder> logger, FfmpegVideoDecoderSettings? settings = null)
    {
        _locator = locator;
        _logger = logger;
        _settings = settings ?? new FfmpegVideoDecoderSettings();
    }

    public async Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default) =>
        await OpenStreamAsync(request, ct);

    internal async Task<FfmpegVideoFrameStream> OpenStreamAsync(VideoDecodeRequest request, CancellationToken ct)
    {
        var ffmpeg = await _locator.GetFfmpegPathAsync(ct)
            ?? throw new VideoDecodeException(VideoDecodeError.DecoderUnavailable, "ffmpeg could not be found.");
        if (!File.Exists(request.FilePath))
            throw new VideoDecodeException(VideoDecodeError.FileNotFound, $"File not found: {request.FilePath}");

        var point = request.FirstSamplePoint;
        var pointTicks = (long)Floor(point.TicksNumerator, point.TicksDenominator);
        var maxPreroll = _settings.MaxPreroll.Ticks;
        var preroll = Math.Min(maxPreroll, _settings.InitialPrerollFrames * NominalFrameTicks(request));
        var hardware = request.Hardware;

        for (var attempt = 1; ; attempt++)
        {
            var seekTicks = pointTicks - preroll;
            var fromStart = seekTicks <= 0;
            var arguments = BuildArguments(request, fromStart ? null : seekTicks,
                hardware == HardwareDecoding.Auto ? _settings.HardwareAccelerator : null);
            _logger.LogDebug("Decode attempt {Attempt} for '{Path}' at {Point}: {Arguments}",
                attempt, request.FilePath, point, string.Join(' ', arguments));

            var stream = FfmpegVideoFrameStream.Start(ffmpeg, arguments, _settings.FrameTimeout, _logger);
            DecodedFrame? first;
            try
            {
                first = await stream.ReadFrameAsync(ct);
            }
            catch (VideoDecodeException ex) when (hardware == HardwareDecoding.Auto &&
                                                  ex.Error is VideoDecodeError.DecoderFailed or VideoDecodeError.Timeout)
            {
                await stream.DisposeAsync();
                _logger.LogWarning(ex, "Hardware decoding of '{Path}' failed; relaunching without -hwaccel.", request.FilePath);
                hardware = HardwareDecoding.Disabled;
                attempt--;
                continue;
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }

            if (first is null && fromStart && hardware == HardwareDecoding.Auto)
            {
                await stream.DisposeAsync();
                _logger.LogWarning("Hardware decoding of '{Path}' produced no frames; relaunching without -hwaccel.", request.FilePath);
                hardware = HardwareDecoding.Disabled;
                attempt--;
                continue;
            }

            if (first is not null && (fromStart || SourceFrameSelector.IsAtOrBefore(first.Timestamp, request.StartTime, point)))
            {
                stream.PushBack(first);
                stream.Attempts = attempt;
                return stream;
            }

            await stream.DisposeAsync();

            if (first is null && fromStart)
                throw new VideoDecodeException(VideoDecodeError.NoVideo, $"'{request.FilePath}' has no decodable video frames.");

            if (preroll >= maxPreroll)
            {
                throw new VideoDecodeException(VideoDecodeError.FrameNotReached,
                    $"No frame at or before {point} in '{request.FilePath}' within a {TimeSpan.FromTicks(maxPreroll).TotalSeconds:0.###} s preroll.");
            }

            _logger.LogDebug("First frame {Pts} is after the sample point {Point}; increasing preroll.",
                first?.Timestamp.ToString() ?? "(none)", point);
            preroll = Math.Min(maxPreroll, preroll * _settings.PrerollGrowthFactor);
        }
    }

    private static long NominalFrameTicks(VideoDecodeRequest request) =>
        request.NominalFrameRate is { IsValid: true } rate
            ? (long)Ceiling((Int128)TimeSpan.TicksPerSecond * rate.Denominator, rate.Numerator)
            : DefaultFrameTicks;

    internal static List<string> BuildArguments(VideoDecodeRequest request, long? seekTicks, string? hardwareAccelerator)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info" };
        if (hardwareAccelerator is not null)
            args.AddRange(new[] { "-hwaccel", hardwareAccelerator });
        args.Add("-copyts");
        if (seekTicks is { } seek)
            args.AddRange(new[] { "-ss", FormatSeconds(seek) });
        // Frames come out display-oriented (display matrix / EXIF orientation applied); composition
        // sizes (MediaMetadata.DisplayWidth/Height) rely on it, so it is explicit, not a default.
        args.Add("-autorotate");
        args.AddRange(new[] { "-i", request.FilePath, "-map", "0:v:0", "-an", "-sn", "-dn" });

        var w = request.MaxWidth.ToString(CultureInfo.InvariantCulture);
        var h = request.MaxHeight.ToString(CultureInfo.InvariantCulture);
        args.AddRange(new[]
        {
            "-vf", $"scale=w='min(iw,{w})':h='min(ih,{h})':force_original_aspect_ratio=decrease,format=bgra,showinfo=checksum=0",
            "-fps_mode", "passthrough",
            "-pix_fmt", "bgra",
            "-f", "rawvideo",
            "pipe:1"
        });
        return args;
    }

    /// <summary>Exact decimal seconds for a tick count (ffmpeg rounds to microseconds).</summary>
    private static string FormatSeconds(long ticks) =>
        (ticks / (decimal)TimeSpan.TicksPerSecond).ToString("0.0######", CultureInfo.InvariantCulture);

    private static Int128 Floor(Int128 a, Int128 b)
    {
        var q = a / b;
        return (a % b != 0) && ((a < 0) != (b < 0)) ? q - 1 : q;
    }

    private static Int128 Ceiling(Int128 a, Int128 b) => -Floor(-a, b);
}
