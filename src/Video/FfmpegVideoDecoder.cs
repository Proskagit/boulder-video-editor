using System.Globalization;
using AiVideoEditor.Core.Color;
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
/// HDR (D033): a request with <see cref="VideoDecodeRequest.Hdr"/> (supported HLG / PQ) takes the HDR graph — the
/// application's LUT of the HDR → SDR rule — and fails rather than decode without it; every other request takes the
/// SDR graph above, unchanged.
/// </para>
/// <para>
/// Known limitations (not handled): HDR variants other than D033's (decoded as SDR), interlaced content
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
    private readonly IHdrLutStore? _luts;

    public FfmpegVideoDecoder(IFfmpegLocator locator, ILogger<FfmpegVideoDecoder> logger, FfmpegVideoDecoderSettings? settings = null,
        IHdrLutStore? luts = null)
    {
        _locator = locator;
        _logger = logger;
        _settings = settings ?? new FfmpegVideoDecoderSettings();
        _luts = luts;
    }

    public async Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default) =>
        await OpenStreamAsync(request, ct);

    internal async Task<FfmpegVideoFrameStream> OpenStreamAsync(VideoDecodeRequest request, CancellationToken ct)
    {
        var ffmpeg = await _locator.GetFfmpegPathAsync(ct)
            ?? throw new VideoDecodeException(VideoDecodeError.DecoderUnavailable, "ffmpeg could not be found.");
        if (!File.Exists(request.FilePath))
            throw new VideoDecodeException(VideoDecodeError.FileNotFound, $"File not found: {request.FilePath}");
        var lut = request.Hdr is { } hdr ? await LutPathAsync(hdr, request.FilePath, ct) : null;

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
                hardware == HardwareDecoding.Auto ? _settings.HardwareAccelerator : null, lut);
            _logger.LogDebug("Decode attempt {Attempt} for '{Path}' at {Point}.", attempt, request.FilePath, point); // command line: FfmpegProcess

            var stream = FfmpegVideoFrameStream.Start(ffmpeg, arguments, _settings.FrameTimeout, _logger, request.StrictEnd);
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

    /// <summary>The LUT of an HDR request (D033). Any failure is the open's failure — never a fallback to the SDR path.</summary>
    private async Task<string> LutPathAsync(HdrConversion hdr, string filePath, CancellationToken ct)
    {
        if (_luts is null)
            throw new VideoDecodeException(VideoDecodeError.DecoderFailed,
                $"'{filePath}' is HDR, but no HDR → SDR conversion is available to decode it.");
        try
        {
            return await _luts.GetLutPathAsync(hdr, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The HDR → SDR table for '{Path}' could not be prepared.", filePath);
            throw new VideoDecodeException(VideoDecodeError.DecoderFailed,
                $"'{filePath}' is HDR, and its HDR → SDR conversion could not be prepared: {ex.Message}", ex);
        }
    }

    internal static List<string> BuildArguments(VideoDecodeRequest request, long? seekTicks, string? hardwareAccelerator,
        string? lutPath = null)
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
        var filters = request.Hdr is { } hdr
            ? HdrFilters(hdr, w, h, lutPath ?? throw new ArgumentNullException(nameof(lutPath), "An HDR request needs its LUT."))
            : $"scale=w='min(iw,{w})':h='min(ih,{h})':force_original_aspect_ratio=decrease,format=bgra,showinfo=checksum=0";
        args.AddRange(new[]
        {
            "-vf", filters,
            "-fps_mode", "passthrough",
            "-pix_fmt", "bgra",
            "-f", "rawvideo",
            "pipe:1"
        });
        return args;
    }

    /// <summary>
    /// The HDR graph (D033 §3 "Graph"): scale to the bound first, both sizes even (zscale refuses odd sizes of 4:2:0
    /// frames, D033 "Context"); <c>zscale</c> only turns Y'CbCr into R'G'B' float (BT.2020 NCL matrix, the file's range →
    /// full; transfer and primaries unchanged — step 0); the LUT does steps 1–9; nothing else converts colour.
    /// </summary>
    private static string HdrFilters(HdrConversion hdr, string w, string h, string lutPath)
    {
        var transfer = hdr.Transfer == HdrTransfer.Hlg ? HdrColor.HlgTransfer : HdrColor.PqTransfer;
        var range = hdr.FullRange ? "full" : "limited";
        return $"scale=w='min(iw,{w})':h='min(ih,{h})':force_original_aspect_ratio=decrease:force_divisible_by=2," +
               $"zscale=min={HdrColor.Bt2020NclMatrix}:pin={HdrColor.Bt2020Primaries}:tin={transfer}:rin={range}:" +
               $"p={HdrColor.Bt2020Primaries}:t={transfer}:r=full,format=gbrpf32le," +
               $"lut3d=file={FilterValue(lutPath)}:interp=tetrahedral,format=bgra,showinfo=checksum=0";
    }

    /// <summary>
    /// A file path as a filter option value inside a <c>-vf</c> graph: forward slashes, then ffmpeg's two levels of
    /// escaping — the option value (<c>\ ' :</c>) and the graph (<c>\ ' [ ] , ;</c>) — so any folder name works (a drive's
    /// colon, spaces, apostrophes, commas, brackets, non-Latin letters).
    /// </summary>
    internal static string FilterValue(string path)
    {
        var value = Escape(path.Replace('\\', '/'), @"\':");
        return Escape(value, @"\'[],;");

        static string Escape(string text, string special)
        {
            var escaped = new System.Text.StringBuilder(text.Length + 8);
            foreach (var c in text)
            {
                if (special.Contains(c)) escaped.Append('\\');
                escaped.Append(c);
            }
            return escaped.ToString();
        }
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
