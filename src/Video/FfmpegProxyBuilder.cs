using System.Diagnostics;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Video;

/// <summary>
/// D071 §3: the Preview's proxy made with the ffmpeg CLI — the source's first video stream, display-oriented, scaled to the
/// proxy's size (an HDR source through D033's conversion first: the proxy is SDR, BT.709), H.264 (libx264 veryfast, CRF 21,
/// no B-frames, a key frame every 0.5 s) in MP4:
/// <code>
/// ffmpeg [-hwaccel auto] -copyts -autorotate -i src -map 0:v:0 -an -sn -dn -vf showinfo,scale…,format=yuv420p
///        -fps_mode passthrough -enc_time_base demux -c:v libx264 … -f mp4 out
/// </code>
/// <c>-copyts</c>, <c>-fps_mode passthrough</c> and <c>-enc_time_base demux</c> keep every frame and its timestamp. That is
/// checked, never trusted: the frames' timestamps the encoding read (its <c>showinfo</c>) are compared exactly, frame for
/// frame, with those of the proxy decoded again; any difference fails the proxy. Runs below normal priority; a failed
/// hardware-decoded attempt is retried in software.
/// </summary>
public sealed class FfmpegProxyBuilder : IProxyBuilder
{
    /// <summary>The key frames' interval in seconds.</summary>
    public const double KeyFrameSeconds = 0.5;

    private readonly IFfmpegLocator _locator;
    private readonly IHdrLutStore? _luts;
    private readonly ILogger<FfmpegProxyBuilder> _logger;

    public FfmpegProxyBuilder(IFfmpegLocator locator, ILogger<FfmpegProxyBuilder> logger, IHdrLutStore? luts = null)
    {
        _locator = locator;
        _logger = logger;
        _luts = luts;
    }

    public async Task BuildAsync(ProxyBuildRequest request, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var ffmpeg = await _locator.GetFfmpegPathAsync(ct) ?? throw new ProxyBuildException("ffmpeg could not be found.");
        if (!File.Exists(request.SourcePath)) throw new ProxyBuildException($"File not found: {request.SourcePath}");
        string? lut = null;
        if (request.Hdr is { } hdr)
        {
            if (_luts is null) throw new ProxyBuildException("The source is HDR, and no HDR → SDR conversion is available.");
            lut = await _luts.GetLutPathAsync(hdr, ct);
        }

        try
        {
            List<SourceTimestamp> source;
            try
            {
                source = await EncodeAsync(ffmpeg, request, lut, "auto", progress, ct);
            }
            catch (ProxyBuildException ex)
            {
                _logger.LogWarning(ex, "The proxy of '{Path}' failed with hardware decoding; retrying in software.", request.SourcePath);
                source = await EncodeAsync(ffmpeg, request, lut, null, progress, ct);
            }
            var proxy = await ReadTimestampsAsync(ffmpeg, request.OutputPath, ct);
            if (Mismatch(source, proxy) is { } difference)
                throw new ProxyBuildException($"The proxy's frames differ from the source's: {difference}.");
            progress?.Report(1);
        }
        catch
        {
            TryDelete(request.OutputPath);
            throw;
        }
    }

    /// <summary>The first difference of two frame lists (count or a timestamp, compared exactly), or null.</summary>
    internal static string? Mismatch(IReadOnlyList<SourceTimestamp> source, IReadOnlyList<SourceTimestamp> proxy)
    {
        if (source.Count == 0) return "the source gave no frame";
        if (source.Count != proxy.Count) return $"{source.Count} frames in the source, {proxy.Count} in the proxy";
        for (var i = 0; i < source.Count; i++)
        {
            if (!SameTime(source[i], proxy[i]))
                return $"frame {i}: {source[i].Pts}×{source[i].TimeBase} in the source, {proxy[i].Pts}×{proxy[i].TimeBase} in the proxy";
        }
        return null;
    }

    private static bool SameTime(SourceTimestamp a, SourceTimestamp b) =>
        (Int128)a.Pts * a.TimeBase.Numerator * b.TimeBase.Denominator ==
        (Int128)b.Pts * b.TimeBase.Numerator * a.TimeBase.Denominator;

    internal static List<string> EncodeArguments(ProxyBuildRequest request, string? lutPath, string? hardwareAccelerator)
    {
        var w = request.Size.Width.ToString(CultureInfo.InvariantCulture);
        var h = request.Size.Height.ToString(CultureInfo.InvariantCulture);
        var picture = request.Hdr is { } hdr
            // D033's graph to BGRA at the proxy's size, then BT.709 limited Y'CbCr — the SDR path decodes it back alike.
            ? FfmpegVideoDecoder.HdrFilters(hdr, w, h, lutPath ?? throw new ArgumentNullException(nameof(lutPath)))
                .Replace(",showinfo=checksum=0", string.Empty, StringComparison.Ordinal) +
              $",scale=w={w}:h={h}:out_color_matrix=bt709:out_range=tv,format=yuv420p"
            : $"scale=w={w}:h={h},format=yuv420p";

        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info" };
        if (hardwareAccelerator is not null) args.AddRange(new[] { "-hwaccel", hardwareAccelerator });
        args.AddRange(new[]
        {
            "-copyts", "-autorotate", "-i", request.SourcePath, "-map", "0:v:0", "-an", "-sn", "-dn",
            // showinfo first: the timestamps of the frames as the Preview's decoder reads them from the source.
            "-vf", "showinfo=checksum=0," + picture,
            "-fps_mode", "passthrough", "-enc_time_base", "demux",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-bf", "0", "-sc_threshold", "0",
            "-force_key_frames", FormattableString.Invariant($"expr:if(isnan(prev_forced_t),1,gte(t,prev_forced_t+{KeyFrameSeconds}))")
        });
        if (request.Hdr is not null)
            args.AddRange(new[] { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv" });
        args.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", "-y", request.OutputPath });
        return args;
    }

    private async Task<List<SourceTimestamp>> EncodeAsync(string ffmpeg, ProxyBuildRequest request, string? lut, string? hardware,
        IProgress<double>? progress, CancellationToken ct)
    {
        var frames = new List<SourceTimestamp>();
        var parser = new ShowInfoParser();
        var durationSeconds = request.Duration.Ticks / (double)TimeSpan.TicksPerSecond;
        double? firstSeconds = null;
        string? parseError = null;
        await RunAsync(ffmpeg, EncodeArguments(request, lut, hardware), line =>
        {
            ShowInfoFrame? frame;
            try { frame = parser.Parse(line); }
            catch (FormatException ex) { parseError ??= ex.Message; return true; }
            if (frame is not { } f) return false;
            if (f.Pts is not { } pts) { parseError ??= $"frame {f.Index} has no timestamp"; return true; }
            lock (frames) frames.Add(new SourceTimestamp(pts, f.TimeBase));
            if (progress is not null && durationSeconds > 0)
            {
                var seconds = pts * (double)f.TimeBase.Numerator / f.TimeBase.Denominator;
                firstSeconds ??= seconds;
                progress.Report(Math.Clamp((seconds - firstSeconds.Value) / durationSeconds, 0, 0.99));
            }
            return true;
        }, ct);
        if (parseError is not null) throw new ProxyBuildException($"The source's frames could not be read: {parseError}.");
        lock (frames) return frames.ToList();
    }

    /// <summary>The proxy's frames decoded again, as the Preview's decoder sees them.</summary>
    private async Task<List<SourceTimestamp>> ReadTimestampsAsync(string ffmpeg, string path, CancellationToken ct)
    {
        var frames = new List<SourceTimestamp>();
        var parser = new ShowInfoParser();
        await RunAsync(ffmpeg, new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-copyts", "-autorotate", "-i", path,
            "-map", "0:v:0", "-an", "-vf", "showinfo=checksum=0", "-fps_mode", "passthrough", "-f", "null", "-"
        }, line =>
        {
            if (parser.Parse(line) is not { } f) return false;
            lock (frames) frames.Add(new SourceTimestamp(f.Pts ?? long.MinValue, f.TimeBase));
            return true;
        }, ct);
        lock (frames) return frames.ToList();
    }

    private async Task RunAsync(string ffmpeg, IReadOnlyList<string> arguments, Func<string, bool> onLine, CancellationToken ct)
    {
        Exception? stderrFailure = null;
        var stderrEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FfmpegProcess process;
        try
        {
            process = FfmpegProcess.Start(ffmpeg, arguments, line =>
            {
                try { return onLine(line); }
                catch (FormatException) { return true; }
            }, ex => { stderrFailure = ex; stderrEnded.TrySetResult(); }, _logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ProxyBuildException($"ffmpeg could not be started: {ex.Message}", ex);
        }

        await using (process)
        {
            TryLowerPriority(process.Process);
            var failure = await process.AbnormalExitAsync(ct);
            await stderrEnded.Task.WaitAsync(ct);   // every frame line handled before the list is read
            if (failure is not null) throw new ProxyBuildException(failure);
            if (stderrFailure is not null) throw new ProxyBuildException($"ffmpeg's output could not be read: {stderrFailure.Message}", stderrFailure);
        }
    }

    private void TryLowerPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogDebug(ex, "The proxy's ffmpeg keeps its normal priority.");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
