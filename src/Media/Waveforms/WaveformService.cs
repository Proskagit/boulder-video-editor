using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Media.Caching;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Media.Waveforms;

/// <summary>
/// Timeline waveforms (D024 Step 9.5), cached in a folder the caller chooses.
/// <list type="bullet">
/// <item>Data: the file's audio decoded once from its start by the app's <see cref="IAudioDecoder"/> (48 kHz stereo,
/// 1×, the same samples playback plays) and reduced to one peak per <see cref="SamplesPerPeak"/> source samples —
/// <c>max(|L|, |R|)</c>, linear (<see cref="Waveform.ToPeak"/>). Samples are placed by the stream's
/// <see cref="IAudioSampleStream.FirstSampleIndex"/>: any before the file's start time are dropped, a stream starting
/// later leaves silent peaks before it. The waveform ends where the audio ends. Nothing about volume, mute or speed —
/// that is the display's.</item>
/// <item>Which media: video with an audio stream and audio files, analysed successfully and online. Images, video
/// without sound, pending or failed analysis get none.</item>
/// <item>Cache: as the thumbnails' (<see cref="SourceFileCache"/>: asset id, source size and last-write time,
/// <see cref="RuleVersion"/>; atomic write; a missing, damaged or unreadable file is a miss and the waveform is made
/// again). A decode that fails, also midway, caches nothing.</item>
/// <item>Offline media (marked missing, or the file is gone now) is never decoded: the last cached waveform, or none.</item>
/// </list>
/// No ffmpeg specifics here and no concurrency limit — the caller owns scheduling.
/// </summary>
public sealed class WaveformService : IWaveformService
{
    /// <summary>Version of the waveform rules (peak definition, resolution, file format). Changing any rule changes it,
    /// which makes every cached waveform a miss.</summary>
    public const int CurrentRuleVersion = 1;

    /// <summary>256 samples at 48 kHz: 187.5 peaks per second, one per 5.3 ms.</summary>
    public const int DefaultSamplesPerPeak = 256;

    private const string Extension = ".peaks";
    private const int ReadFrames = AudioFormat.SampleRate / 4;

    private readonly IAudioDecoder _decoder;
    private readonly ILogger<WaveformService> _logger;

    public WaveformService(IAudioDecoder decoder, ILogger<WaveformService> logger)
    {
        _decoder = decoder;
        _logger = logger;
    }

    /// <summary>Rule version in cache file names (tests set another one to simulate a rule change).</summary>
    internal int RuleVersion { get; init; } = CurrentRuleVersion;

    public int SamplesPerPeak { get; init; } = DefaultSamplesPerPeak;

    public Waveform? TryGetCached(MediaAsset asset, string cacheFolder)
    {
        if (SourceFileCache.Identity(asset) is { } identity)
            return WaveformCacheFile.TryRead(Path.Combine(cacheFolder, FileName(asset, identity)));

        // Offline: the last waveform cached for the asset, whatever file (or rule version) it was made from.
        return SourceFileCache.LastCached(asset, cacheFolder, Extension, WaveformCacheFile.TryRead);
    }

    public async Task<Waveform?> GetOrCreateAsync(MediaAsset asset, string cacheFolder, CancellationToken ct = default)
    {
        if (SourceFileCache.Identity(asset) is not { } identity)
            return TryGetCached(asset, cacheFolder); // offline: never decoded
        if (!HasSound(asset))
            return null;

        var path = Path.Combine(cacheFolder, FileName(asset, identity));
        if (WaveformCacheFile.TryRead(path) is { } cached)
            return cached;

        Waveform waveform;
        try
        {
            waveform = await DecodeAsync(asset, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No waveform for '{Path}': its audio could not be decoded.", asset.FilePath);
            return null;
        }

        Write(asset, cacheFolder, path, waveform);
        return waveform;
    }

    /// <summary>Audio files and video with an audio stream, analysed successfully (the audible sources of the
    /// timeline, <c>PlaybackSnapshotBuilder</c>).</summary>
    private static bool HasSound(MediaAsset asset) =>
        asset.AnalysisStatus == MediaAnalysisStatus.Completed && asset.Metadata is { } metadata &&
        (asset.Kind == MediaKind.Audio || asset.Kind == MediaKind.Video && metadata.AudioCodec is not null);

    private async Task<Waveform> DecodeAsync(MediaAsset asset, CancellationToken ct)
    {
        await using var stream = await _decoder.OpenAsync(new AudioDecodeRequest
        {
            FilePath = asset.FilePath,
            StartTime = asset.Metadata!.StartTime ?? MediaTime.Zero,
            SourcePosition = MediaTime.Zero,
            Speed = ClipSpeed.Normal,
            StrictEnd = true // a decoder that fails midway would leave a truncated waveform
        }, ct);

        var peaks = new PeakBuilder(SamplesPerPeak);
        var buffer = new float[ReadFrames * AudioFormat.Channels];
        var index = stream.FirstSampleIndex; // source sample of the next frame read
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            for (var i = 0; i + 1 < read; i += AudioFormat.Channels, index++)
            {
                if (index >= 0)
                    peaks.Add(index, Math.Max(Math.Abs(buffer[i]), Math.Abs(buffer[i + 1])));
            }
        }
        return peaks.Build(Math.Max(index, 0));
    }

    /// <summary>Writes atomically; a failed write only costs the cache — the waveform is still returned.</summary>
    private void Write(MediaAsset asset, string cacheFolder, string path, Waveform waveform)
    {
        try
        {
            SourceFileCache.Write(asset, cacheFolder, path, Extension, WaveformCacheFile.Encode(waveform));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The waveform of '{Path}' could not be cached in '{Folder}'.", asset.FilePath, cacheFolder);
        }
    }

    private string FileName(MediaAsset asset, (long Size, long LastWriteTicks) identity) =>
        SourceFileCache.FileName(asset, identity, RuleVersion, Extension);

    /// <summary>Peaks of samples added in increasing source order; groups before the first sample stay silent.</summary>
    private sealed class PeakBuilder(int samplesPerPeak)
    {
        private byte[] _peaks = new byte[1024];

        public void Add(long sampleIndex, float amplitude)
        {
            var group = sampleIndex / samplesPerPeak;
            if (group >= _peaks.Length)
                Array.Resize(ref _peaks, (int)Math.Max(group + 1, Math.Min((long)_peaks.Length * 2, int.MaxValue)));
            var peak = Waveform.ToPeak(amplitude);
            if (peak > _peaks[group])
                _peaks[group] = peak;
        }

        public Waveform Build(long sampleCount)
        {
            var count = (int)Waveform.PeakCountFor(sampleCount, samplesPerPeak);
            var peaks = new byte[count];
            Array.Copy(_peaks, peaks, Math.Min(count, _peaks.Length));
            return new Waveform(samplesPerPeak, sampleCount, peaks);
        }
    }
}
