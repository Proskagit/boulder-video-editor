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
            stdout = await RunFfprobeAsync(ffprobePath, filePath, ct);
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

        return MediaAnalysisResult.Success(BuildMetadata(parsed));
    }

    private static async Task<string> RunFfprobeAsync(string ffprobePath, string filePath, CancellationToken ct)
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
        process.StartInfo.ArgumentList.Add("-show_format");
        process.StartInfo.ArgumentList.Add("-show_streams");
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

    private static double? ParseFrameRate(string? rFrameRate)
    {
        if (string.IsNullOrWhiteSpace(rFrameRate))
            return null;

        var parts = rFrameRate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(rFrameRate, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct)
            ? direct
            : null;
    }
}
