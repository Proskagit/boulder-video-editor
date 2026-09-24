using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Video;

/// <summary>
/// Runs ffprobe as a separate process and parses its JSON output into a
/// <see cref="MediaMetadata"/>. This is the only place in the whole solution that
/// knows ffprobe exists or what its output looks like — everything upstream only
/// ever sees <see cref="IMediaAnalysisService"/> / <see cref="MediaAnalysisResult"/>.
/// Never touches the UI thread: this is plain async process I/O, always awaited
/// from a background task by the caller (see MediaAnalysisCoordinator in UI).
/// </summary>
public sealed class FfprobeMediaAnalysisService : IMediaAnalysisService
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    private readonly IFfprobeLocator _locator;
    private readonly ILogger<FfprobeMediaAnalysisService> _logger;

    public FfprobeMediaAnalysisService(IFfprobeLocator locator, ILogger<FfprobeMediaAnalysisService> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    public async Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return MediaAnalysisResult.Failure(MediaAnalysisOutcome.FileNotFound, "The file could not be found.");

        var ffprobePath = await _locator.GetFfprobePathAsync(ct);
        if (ffprobePath is null)
        {
            return MediaAnalysisResult.Failure(
                MediaAnalysisOutcome.ProbeToolUnavailable,
                "FFprobe could not be found. Install FFmpeg (with ffprobe on PATH) or configure its path in settings.");
        }

        string stdout;
        try
        {
            stdout = await RunFfprobeAsync(ffprobePath, new[] { "-show_format", "-show_streams" }, filePath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return MediaAnalysisResult.Failure(MediaAnalysisOutcome.Cancelled, "Analysis was cancelled.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ffprobe timed out analyzing '{Path}'.", filePath);
            return MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeProcessFailed, "FFprobe took too long to respond.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe process failed for '{Path}'.", filePath);
            return MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeProcessFailed, "FFprobe failed to analyze this file.");
        }

        FfprobeOutput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FfprobeOutput>(stdout);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse ffprobe output for '{Path}'.", filePath);
            return MediaAnalysisResult.Failure(MediaAnalysisOutcome.InvalidOutput, "FFprobe returned output that could not be understood.");
        }

        if (parsed?.Streams is null || parsed.Streams.Count == 0)
            return MediaAnalysisResult.Failure(MediaAnalysisOutcome.InvalidMedia, "This file doesn't appear to contain readable media streams.");

        var metadata = BuildMetadata(parsed);
        try
        {
            await ApplyOrientationAsync(metadata, parsed.Streams.FirstOrDefault(s => s.CodecType == "video"), ffprobePath, filePath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return MediaAnalysisResult.Failure(MediaAnalysisOutcome.Cancelled, "Analysis was cancelled.");
        }
        return MediaAnalysisResult.Success(metadata);
    }

    /// <summary>
    /// Display orientation and display size (the frames the decoder, with ffmpeg's automatic
    /// rotation, really delivers). Priority: the stream's display matrix (or a legacy "rotate" tag),
    /// then the first decoded frame's display matrix — where images carry their EXIF orientation —,
    /// otherwise 0°. A failed frame probe never fails the analysis.
    /// </summary>
    private async Task ApplyOrientationAsync(MediaMetadata metadata, FfprobeStream? videoStream, string ffprobePath, string filePath,
        CancellationToken ct)
    {
        if (videoStream is null || metadata.Width is not > 0 || metadata.Height is not > 0)
            return;

        var hint = StreamHint(videoStream) ?? await FirstFrameHintAsync(ffprobePath, filePath, ct);
        var result = DisplayOrientation.Resolve(metadata.Width.Value, metadata.Height.Value, hint);
        metadata.DisplayRotation = result.Rotation;
        metadata.DisplayWidth = result.DisplayWidth;
        metadata.DisplayHeight = result.DisplayHeight;

        if (result.Unsupported is { } reason)
        {
            _logger.LogWarning("'{Path}': {Reason}; orientation is not supported, frames are shown as the decoder delivers them ({Width}×{Height}).",
                filePath, reason, result.DisplayWidth, result.DisplayHeight);
        }
    }

    private static OrientationHint? StreamHint(FfprobeStream stream)
    {
        if (DisplayMatrixHint(stream.SideDataList) is { } fromMatrix)
            return fromMatrix;

        // Legacy containers: a "rotate" tag in clockwise degrees (the matrix convention is counter-clockwise).
        if (stream.Tags is { } tags && tags.TryGetValue("rotate", out var rotate) &&
            double.TryParse(rotate, NumberStyles.Float, CultureInfo.InvariantCulture, out var clockwise))
            return new OrientationHint(-clockwise, IsMirrored: false);

        return null;
    }

    private static OrientationHint? DisplayMatrixHint(IEnumerable<FfprobeSideData>? sideData)
    {
        var matrix = sideData?.FirstOrDefault(d =>
            d.SideDataType is { } type && type.Replace(" ", "").Contains("displaymatrix", StringComparison.OrdinalIgnoreCase));
        if (matrix is null) return null;
        return new OrientationHint(matrix.Rotation ?? 0, DisplayOrientation.IsMirrored(matrix.DisplayMatrix) ?? false);
    }

    private async Task<OrientationHint?> FirstFrameHintAsync(string ffprobePath, string filePath, CancellationToken ct)
    {
        try
        {
            var stdout = await RunFfprobeAsync(ffprobePath, new[]
            {
                "-select_streams", "v:0", "-read_intervals", "%+#1", "-show_frames",
                "-show_entries", "frame=width,height:frame_side_data=side_data_type,rotation,displaymatrix"
            }, filePath, ct);
            var frames = JsonSerializer.Deserialize<FfprobeFramesOutput>(stdout);
            return DisplayMatrixHint(frames?.Frames?.FirstOrDefault()?.SideDataList);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "First-frame orientation probe failed for '{Path}'; assuming 0°.", filePath);
            return null;
        }
    }

    private static async Task<string> RunFfprobeAsync(string ffprobePath, IReadOnlyList<string> arguments, string filePath, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("quiet");
        process.StartInfo.ArgumentList.Add("-print_format");
        process.StartInfo.ArgumentList.Add("json");
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.ArgumentList.Add(filePath);

        using var timeoutCts = new CancellationTokenSource(ProcessTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        await process.WaitForExitAsync(linked.Token);
        var stdout = await stdoutTask;

        // Drained (not surfaced) purely so a full stderr buffer can't deadlock the
        // process — ffprobe's stderr is technical noise, not something to show
        // the user; failures are reported through the exit code / outcome instead.
        _ = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe exited with code {process.ExitCode}.");

        return stdout;
    }

    private static MediaMetadata BuildMetadata(FfprobeOutput output)
    {
        var streams = output.Streams!;
        var videoStream = streams.FirstOrDefault(s => s.CodecType == "video");
        var audioStream = streams.FirstOrDefault(s => s.CodecType == "audio");

        var metadata = new MediaMetadata();

        if (output.Format?.Duration is { } durationText &&
            double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            metadata.Duration = MediaTime.FromSeconds(seconds);
        }

        metadata.StartTime = ParseStartTime(output.Format?.StartTime);

        if (output.Format?.BitRate is { } formatBitRateText &&
            long.TryParse(formatBitRateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var formatBitRate))
        {
            metadata.BitrateBps = formatBitRate;
        }

        if (videoStream is not null)
        {
            metadata.Width = videoStream.Width;
            metadata.Height = videoStream.Height;
            metadata.VideoCodec = videoStream.CodecName;
            metadata.FrameRate = ParseFrameRate(videoStream.RFrameRate);
            metadata.AvgFrameRate = ParseFrameRate(videoStream.AvgFrameRate);
        }

        if (audioStream is not null)
        {
            metadata.AudioCodec = audioStream.CodecName;
            metadata.AudioChannels = audioStream.Channels;

            if (audioStream.SampleRate is { } sampleRateText &&
                int.TryParse(sampleRateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleRate))
            {
                metadata.AudioSampleRate = sampleRate;
            }
        }

        return metadata;
    }

    /// <summary>Keeps ffprobe's rational form (e.g. "30000/1001") exact. "0/0" and
    /// anything unparseable mean "unknown" and yield null.</summary>
    private static FrameRate? ParseFrameRate(string? text) =>
        FrameRate.TryParse(text, out var rate) ? rate : null;

    /// <summary>ffprobe prints start_time in whole microseconds ("1.400000"); decimal
    /// parsing keeps it exact in ticks. "N/A" or anything unparseable yields null.</summary>
    private static MediaTime? ParseStartTime(string? text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var seconds))
            return null;

        try
        {
            return new MediaTime((long)Math.Round(seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero));
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
