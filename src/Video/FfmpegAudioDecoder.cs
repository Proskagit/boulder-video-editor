using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Video;

/// <summary>Limits of <see cref="FfmpegAudioDecoder"/>.</summary>
public sealed class FfmpegAudioDecoderSettings
{
    public TimeSpan InitialPreroll { get; init; } = TimeSpan.FromMilliseconds(200);
    public int PrerollGrowthFactor { get; init; } = 4;
    public TimeSpan MaxPreroll { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Longest wait for the first decoded audio frame.</summary>
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// <see cref="IAudioDecoder"/> over the ffmpeg CLI:
/// <code>
/// ffmpeg -copyts [-ss seek] -i file -map 0:a:0 -vn -sn -dn
///        -af aresample=48000:async=1,aformat=sample_fmts=flt:channel_layouts=stereo,ashowinfo
///        -f f32le pipe:1
/// </code>
/// The first sample's position comes from the PTS <c>ashowinfo</c> reports (time base 1/48000
/// after <c>aresample</c>), never from <c>-ss</c>: for AAC in MP4, ffmpeg starts at a codec frame
/// boundary after the requested time. If the stream starts after the requested sample, the
/// attempt is repeated with a larger preroll (up to <see cref="FfmpegAudioDecoderSettings.MaxPreroll"/>,
/// then from the beginning of the file); a stream that really starts later is accepted and the
/// caller fills the gap with silence. <c>async=1</c> keeps the output contiguous across timestamp gaps.
/// </summary>
public sealed partial class FfmpegAudioDecoder : IAudioDecoder
{
    private readonly IFfmpegLocator _locator;
    private readonly ILogger<FfmpegAudioDecoder> _logger;
    private readonly FfmpegAudioDecoderSettings _settings;

    public FfmpegAudioDecoder(IFfmpegLocator locator, ILogger<FfmpegAudioDecoder> logger, FfmpegAudioDecoderSettings? settings = null)
    {
        _locator = locator;
        _logger = logger;
        _settings = settings ?? new FfmpegAudioDecoderSettings();
    }

    [GeneratedRegex(@"^\[Parsed_ashowinfo_\d+ @ [^\]]+\] n:\s*(\d+)\s+pts:\s*(-?\d+)\s+pts_time:\s*(-?[0-9.]+)")]
    private static partial Regex FrameRegex();

    public async Task<IAudioSampleStream> OpenAsync(AudioDecodeRequest request, CancellationToken ct = default) =>
        await OpenStreamAsync(request, ct);

    internal async Task<FfmpegAudioStream> OpenStreamAsync(AudioDecodeRequest request, CancellationToken ct)
    {
        var ffmpeg = await _locator.GetFfmpegPathAsync(ct)
            ?? throw new AudioDecodeException(VideoDecodeError.DecoderUnavailable, "ffmpeg could not be found.");
        if (!File.Exists(request.FilePath))
            throw new AudioDecodeException(VideoDecodeError.FileNotFound, $"File not found: {request.FilePath}");

        var requestedSample = AudioTiming.NearestSample(request.SourcePosition);
        var originSamples = AudioTiming.NearestSample(request.StartTime);
        var preroll = _settings.InitialPreroll.Ticks;

        for (var attempt = 1; ; attempt++)
        {
            var seekTicks = request.SourcePosition.Ticks - preroll;
            var fromStart = seekTicks <= 0;
            var arguments = BuildArguments(request.FilePath, fromStart ? null : seekTicks);
            var stream = await StartAsync(ffmpeg, arguments, originSamples, requestedSample, ct);

            if (fromStart || stream.FirstSampleIndex <= requestedSample || preroll >= _settings.MaxPreroll.Ticks)
            {
                stream.Attempts = attempt;
                return stream;
            }

            _logger.LogDebug("Audio of '{Path}' starts at sample {First}, after {Requested}; increasing preroll.",
                request.FilePath, stream.FirstSampleIndex, requestedSample);
            await stream.DisposeAsync();
            preroll = Math.Min(_settings.MaxPreroll.Ticks, preroll * _settings.PrerollGrowthFactor);
        }
    }

    private async Task<FfmpegAudioStream> StartAsync(string ffmpeg, List<string> arguments, long originSamples,
        long requestedSample, CancellationToken ct)
    {
        var firstPts = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        FfmpegProcess process;
        try
        {
            process = FfmpegProcess.Start(ffmpeg, arguments,
                line =>
                {
                    var match = FrameRegex().Match(line);
                    if (!match.Success) return false;
                    if (!firstPts.Task.IsCompleted)
                        firstPts.TrySetResult(ParseFirstPts(match));
                    return true;
                },
                error =>
                {
                    if (error is not null) firstPts.TrySetException(error);
                    else firstPts.TrySetResult(null);
                },
                _logger);
        }
        catch (Exception ex)
        {
            throw new AudioDecodeException(VideoDecodeError.DecoderUnavailable, "ffmpeg could not be started.", ex);
        }

        try
        {
            var pts = await firstPts.Task.WaitAsync(_settings.FirstFrameTimeout, ct);
            if (pts is null)
            {
                // No audio frame at all: a failure (e.g. no audio stream) or simply nothing left.
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0)
                    throw new AudioDecodeException(VideoDecodeError.DecoderFailed,
                        $"ffmpeg exited with code {process.ExitCode}. {process.StderrTail()}");
                return new FfmpegAudioStream(process, requestedSample) { Arguments = arguments };
            }
            return new FfmpegAudioStream(process, pts.Value - originSamples) { Arguments = arguments };
        }
        catch (TimeoutException ex)
        {
            await process.DisposeAsync();
            throw new AudioDecodeException(VideoDecodeError.Timeout, $"ffmpeg produced no audio within {_settings.FirstFrameTimeout.TotalSeconds:0.#} s.", ex);
        }
        catch (FormatException ex)
        {
            await process.DisposeAsync();
            throw new AudioDecodeException(VideoDecodeError.DecoderFailed, $"Unexpected ffmpeg output: {ex.Message}", ex);
        }
        catch
        {
            await process.DisposeAsync();
            throw;
        }
    }

    /// <summary>PTS of the first frame in 1/48000 units; cross-checked against pts_time so a
    /// different time base cannot go unnoticed.</summary>
    private static long ParseFirstPts(Match match)
    {
        var pts = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = decimal.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (Math.Abs(pts / (decimal)AudioFormat.SampleRate - seconds) > 0.0005m)
            throw new FormatException($"ashowinfo pts {pts} does not match pts_time {seconds} at 1/{AudioFormat.SampleRate}.");
        return pts;
    }

    internal static List<string> BuildArguments(string filePath, long? seekTicks)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-copyts" };
        if (seekTicks is { } seek)
            args.AddRange(new[] { "-ss", (seek / (decimal)TimeSpan.TicksPerSecond).ToString("0.0######", CultureInfo.InvariantCulture) });
        args.AddRange(new[]
        {
            "-i", filePath, "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-af", $"aresample={AudioFormat.SampleRate}:async=1,aformat=sample_fmts=flt:channel_layouts=stereo,ashowinfo",
            "-f", "f32le", "pipe:1"
        });
        return args;
    }
}

/// <summary>Raw little-endian float32 stereo from ffmpeg's stdout.</summary>
internal sealed class FfmpegAudioStream : IAudioSampleStream
{
    private readonly FfmpegProcess _process;
    private byte[] _scratch = Array.Empty<byte>();
    private int _pending; // bytes of an incomplete frame kept for the next read

    public FfmpegAudioStream(FfmpegProcess process, long firstSampleIndex)
    {
        _process = process;
        FirstSampleIndex = firstSampleIndex;
    }

    public long FirstSampleIndex { get; }
    public int Attempts { get; internal set; }
    public required IReadOnlyList<string> Arguments { get; init; }

    public async ValueTask<int> ReadAsync(Memory<float> interleaved, CancellationToken ct = default)
    {
        const int frameBytes = sizeof(float) * AudioFormat.Channels;
        var wantBytes = interleaved.Length / AudioFormat.Channels * frameBytes;
        if (wantBytes == 0) return 0;
        if (_scratch.Length < wantBytes) Array.Resize(ref _scratch, wantBytes);

        var read = _pending + await _process.Stdout.ReadAtLeastAsync(
            _scratch.AsMemory(_pending, wantBytes - _pending), Math.Max(1, frameBytes - _pending), throwOnEndOfStream: false, ct);
        var usable = read - read % frameBytes;
        if (usable == 0) return 0; // end of stream (a trailing partial frame is dropped)

        MemoryMarshal.Cast<byte, float>(_scratch.AsSpan(0, usable)).CopyTo(interleaved.Span);
        _pending = read - usable;
        if (_pending > 0) Buffer.BlockCopy(_scratch, usable, _scratch, 0, _pending);
        return usable / sizeof(float);
    }

    public ValueTask DisposeAsync() => _process.DisposeAsync();
}
