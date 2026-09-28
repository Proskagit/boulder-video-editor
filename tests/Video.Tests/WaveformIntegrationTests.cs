using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Media.Waveforms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D024 Step 9.5a: <see cref="WaveformService"/> with the real ffmpeg audio decoder on generated sound whose loudness
/// is known per second (silence, then tones of a known amplitude on the left and on the right): the peaks follow it
/// in source time — exactly for PCM, within a few groups for AAC —, the louder channel counts, a container start time
/// is the origin, video with sound works, video without sound is never decoded, and a cache hit starts no ffmpeg.
/// </summary>
public sealed class WaveformIntegrationTests : IDisposable
{
    private const int PerPeak = WaveformService.DefaultSamplesPerPeak;

    // Seconds 0–1 silent; 1–2 a 440 Hz tone of 0.5 on the left, 0.2 on the right; 2–3 0.8 on the right only.
    private const string Sound =
        @"aevalsrc=exprs='if(between(t\,1\,2)\,0.5*sin(2*PI*440*t)\,0)|if(lt(t\,2)\,if(gte(t\,1)\,0.2*sin(2*PI*440*t)\,0)\,0.8*sin(2*PI*440*t))':s=48000:d=3";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-waveform-e2e", Guid.NewGuid().ToString("N"));
    private readonly CountingDecoder _decoder = new(new FfmpegAudioDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegAudioDecoder>.Instance));

    public WaveformIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class CountingDecoder(IAudioDecoder inner) : IAudioDecoder
    {
        public int Opens;
        public Task<IAudioSampleStream> OpenAsync(AudioDecodeRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opens);
            return inner.OpenAsync(request, ct);
        }
    }

    private WaveformService Service() => new(_decoder, NullLogger<WaveformService>.Instance);
    private string Cache => Path.Combine(_dir, "cache", "waveforms");

    private string Generate(string name, params string[] args)
    {
        var path = Path.Combine(_dir, name);
        TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-v", "error", "-y" }.Concat(args).Append(path));
        return path;
    }

    private static MediaAsset Analysed(string path, MediaKind kind)
    {
        var service = new FfprobeMediaAnalysisService(new FfprobeLocator(Options.Create(new FfmpegOptions()), NullLogger<FfprobeLocator>.Instance),
            NullLogger<FfprobeMediaAnalysisService>.Instance);
        var result = service.AnalyzeAsync(path).GetAwaiter().GetResult();
        Assert.True(result.Metadata is not null, $"analysis failed for {path}: {result.ErrorMessage}");
        return new MediaAsset { FilePath = path, Kind = kind, Metadata = result.Metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
    }

    private static int Group(double seconds) => (int)(seconds * AudioFormat.SampleRate / PerPeak);

    [FfmpegFact]
    public async Task Pcm_peaks_follow_the_sound_exactly_and_the_louder_channel_counts()
    {
        var asset = Analysed(Generate("tones.wav", "-f", "lavfi", "-i", Sound, "-c:a", "pcm_s16le"), MediaKind.Audio);

        var waveform = (await Service().GetOrCreateAsync(asset, Cache))!;

        Assert.Equal(3L * AudioFormat.SampleRate, waveform.SampleCount);
        var peaks = waveform.Peaks.ToArray();
        Assert.All(peaks[..Group(1)], p => Assert.Equal(0, p));                          // silence
        Assert.All(peaks[(Group(1) + 1)..Group(2)], p => Assert.InRange(p, 125, 128));   // left 0.5 (right 0.2)
        Assert.All(peaks[(Group(2) + 1)..], p => Assert.InRange(p, 200, 204));           // right 0.8 (left silent)
        Assert.Equal(1, _decoder.Opens);
    }

    [FfmpegFact]
    public async Task A_container_start_time_is_the_origin_of_the_peaks()
    {
        var asset = Analysed(Generate("tones.ts", "-f", "lavfi", "-i", Sound, "-c:a", "aac", "-output_ts_offset", "10"), MediaKind.Audio);
        Assert.True(asset.Metadata!.StartTime!.Value.Ticks > 9 * TimeSpan.TicksPerSecond, "the file should start at about 10 s");

        var peaks = (await Service().GetOrCreateAsync(asset, Cache))!.Peaks.ToArray();

        var onset = Array.FindIndex(peaks, p => p > 64);
        Assert.InRange(onset, Group(1) - 8, Group(1) + 8);                                // AAC: within ~40 ms
        Assert.InRange(peaks.Length, Group(3) - 8, Group(3) + 16);
        Assert.InRange(peaks[Group(2.5)], 190, 215);
    }

    [FfmpegFact]
    public async Task Video_with_sound_gets_its_audio_waveform_and_a_hit_starts_no_ffmpeg()
    {
        var path = Generate("clip.mp4", "-f", "lavfi", "-i", "color=c=gray:s=64x36:r=25:d=3", "-f", "lavfi", "-i", Sound,
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
        var asset = Analysed(path, MediaKind.Video);

        var peaks = (await Service().GetOrCreateAsync(asset, Cache))!.Peaks.ToArray();
        Assert.All(peaks[..(Group(1) - 8)], p => Assert.True(p <= 2, $"silence expected, got {p}"));
        Assert.InRange(peaks[Group(1.5)], 115, 140);
        Assert.InRange(peaks[Group(2.5)], 190, 215);

        Assert.NotNull(await Service().GetOrCreateAsync(asset, Cache));
        Assert.Equal(1, _decoder.Opens);                                                  // the hit decoded nothing
    }

    [FfmpegFact]
    public async Task Video_without_sound_is_never_decoded()
    {
        var asset = Analysed(Generate("silent.mp4", "-f", "lavfi", "-i", "color=c=gray:s=64x36:r=25:d=1", "-c:v", "libx264",
            "-pix_fmt", "yuv420p"), MediaKind.Video);
        Assert.Null(asset.Metadata!.AudioCodec);

        Assert.Null(await Service().GetOrCreateAsync(asset, Cache));

        Assert.Equal(0, _decoder.Opens);
    }
}
