using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Media.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D064 §5 with the real FFmpeg: the cleaned sound is aligned to the source sample for sample (the noise reduction's constant
/// delay compensated), as long as the source, a valid float WAV the decoder reads back; the voice model's chunks join without a
/// seam (a stand-in model that only delays its input gives back exactly the input); the speech analysis has one value per 32 ms
/// window from the source's start.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class AudioCleanupIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-cleanup-tests", Guid.NewGuid().ToString("N"));

    public AudioCleanupIntegrationTests(TestMedia media)
    {
        _ = media;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static FfmpegAudioDecoder Decoder() => new(FfmpegTools.FfmpegLocator, NullLogger<FfmpegAudioDecoder>.Instance);

    /// <summary>Quiet noise with clicks of 0.9 at the given 48 kHz samples, <paramref name="seconds"/> long, as a WAV.</summary>
    private string Clicks(string name, double seconds, params long[] at)
    {
        var path = Path.Combine(_dir, name);
        var clicks = string.Join("+", at.Select(n => $"eq(n\\,{n})*0.9"));
        TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            $"aevalsrc=exprs='{clicks}+0.003*(random(0)-0.5)':s=48000:d={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            "-ac", "2", "-c:a", "pcm_f32le", path });
        return path;
    }

    private static async Task<float[]> ReadAll(string path)
    {
        await using var stream = await Decoder().OpenAsync(new AudioDecodeRequest { FilePath = path, StrictEnd = true });
        Assert.Equal(0, stream.FirstSampleIndex);
        var all = new List<float>();
        var buffer = new float[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0) all.AddRange(buffer.Take(read));
        return all.ToArray();
    }

    private static long Peak(float[] interleaved, long from, long until)
    {
        long best = from;
        for (var k = from; k < until; k++)
            if (Math.Abs(interleaved[2 * k]) > Math.Abs(interleaved[2 * best])) best = k;
        return best;
    }

    [Fact]
    public async Task The_noise_reduction_keeps_every_sample_in_its_place_and_the_length()
    {
        if (FfmpegTools.Ffmpeg is null) return;
        var source = Clicks("clicks.wav", 3, 24_000, 100_000, 140_000);
        var output = Path.Combine(_dir, "nr.wav");
        var processor = new AudioCleanupProcessor(Decoder());

        await processor.ProduceAsync(source, MediaTime.Zero, MediaTime.FromSeconds(3), new AudioCleanup(80, 0), output, null, CancellationToken.None);

        var original = await ReadAll(source);
        var cleaned = await ReadAll(output);
        Assert.Equal(original.Length, cleaned.Length);
        foreach (var click in new long[] { 24_000, 100_000, 140_000 })
            Assert.InRange(Peak(cleaned, click - 3_000, click + 3_000), click - 1, click + 1);
    }

    [Fact]
    public async Task The_voice_chunks_join_without_a_seam_and_keep_the_alignment()
    {
        if (FfmpegTools.Ffmpeg is null) return;
        // 65 s: two chunk boundaries (30 s, 60 s); clicks across them.
        var source = Clicks("long.wav", 65, 1_000, 1_440_000 - 3, 1_440_000, 2_880_001, 3_100_000);
        var output = Path.Combine(_dir, "ve.wav");
        var processor = new AudioCleanupProcessor(Decoder(), new DelayOnly(), parallelism: 3);

        await processor.ProduceAsync(source, MediaTime.Zero, MediaTime.FromSeconds(65), new AudioCleanup(0, 100), output, null, CancellationToken.None);

        var original = await ReadAll(source);
        var cleaned = await ReadAll(output);
        Assert.Equal(original.Length, cleaned.Length);
        // The stand-in "voice" is the mid of the channels (both equal here), delayed by its own latency: compensated, the output
        // is the input itself, across every chunk boundary.
        for (var i = 0; i < original.Length; i++)
            if (Math.Abs(original[i] - cleaned[i]) > 1e-6f) Assert.Fail($"sample {i / 2} differs: {original[i]} vs {cleaned[i]}");
    }

    [Fact]
    public async Task A_half_strength_blends_the_voice_and_the_dry_sound()
    {
        if (FfmpegTools.Ffmpeg is null) return;
        var source = Clicks("blend.wav", 2, 50_000);
        var output = Path.Combine(_dir, "half.wav");
        var processor = new AudioCleanupProcessor(Decoder(), new DelayOnly(gain: 0), parallelism: 1);   // the "voice" is silence

        await processor.ProduceAsync(source, MediaTime.Zero, MediaTime.FromSeconds(2), new AudioCleanup(0, 50), output, null, CancellationToken.None);

        var original = await ReadAll(source);
        var cleaned = await ReadAll(output);
        Assert.Equal(original[2 * 50_000] * 0.5f, cleaned[2 * 50_000], 6);
    }

    [Fact]
    public async Task The_speech_analysis_gives_one_value_per_window_from_the_start()
    {
        if (FfmpegTools.Ffmpeg is null) return;
        var source = Clicks("speech.wav", 2, 48_000);
        var processor = new AudioCleanupProcessor(Decoder(), speech: new LoudnessSpeech());

        var activity = await processor.AnalyseSpeechAsync(source, MediaTime.Zero, MediaTime.FromSeconds(2), null, CancellationToken.None);

        Assert.Equal((int)Math.Ceiling(96_000 / (double)SpeechActivity.WindowSamples), activity.Probabilities.Length);
        var loudest = Array.IndexOf(activity.Probabilities, activity.Probabilities.Max());
        Assert.Equal(48_000 / SpeechActivity.WindowSamples, loudest);
    }

    [Fact]
    public async Task Without_the_voice_model_the_clean_up_fails_with_the_reason()
    {
        if (FfmpegTools.Ffmpeg is null) return;
        var source = Clicks("none.wav", 1, 1_000);
        var processor = new AudioCleanupProcessor(Decoder());
        var error = await Assert.ThrowsAsync<AudioCleanupException>(() =>
            processor.ProduceAsync(source, MediaTime.Zero, MediaTime.FromSeconds(1), new AudioCleanup(0, 40), Path.Combine(_dir, "x.wav"), null, CancellationToken.None));
        Assert.Equal(Core.Localization.Messages.Audio_VoiceModelUnavailable, error.Message);
    }

    [Fact]
    public async Task A_cancelled_clean_up_stops()
    {
        if (FfmpegTools.Ffmpeg is null) return;
        var source = Clicks("cancel.wav", 40, 1_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var processor = new AudioCleanupProcessor(Decoder(), new DelayOnly());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProduceAsync(source, MediaTime.Zero, MediaTime.FromSeconds(40), new AudioCleanup(0, 100), Path.Combine(_dir, "c.wav"), null, cts.Token));
    }

    /// <summary>A stand-in for the voice model: its input × <c>gain</c>, 1 440 samples later (DeepFilterNet3's hop and delay).</summary>
    private sealed class DelayOnly(float gain = 1) : IVoiceEnhancer
    {
        public int HopSamples => 480;
        public int DelaySamples => 1_440;
        public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public IVoiceEnhancerStream Open() => new Stream(gain);

        private sealed class Stream(float gain) : IVoiceEnhancerStream
        {
            private readonly Queue<float> _line = new(Enumerable.Repeat(0f, 1_440));

            public void Process(ReadOnlySpan<float> input, Span<float> output)
            {
                for (var i = 0; i < input.Length; i++)
                {
                    _line.Enqueue(input[i] * gain);
                    output[i] = _line.Dequeue();
                }
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>A stand-in for the speech model: the window's peak.</summary>
    private sealed class LoudnessSpeech : ISpeechModel
    {
        public int WindowSamples => 512;
        public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public ISpeechModelStream Open() => new Stream();

        private sealed class Stream : ISpeechModelStream
        {
            public float Process(ReadOnlySpan<float> window16k)
            {
                var peak = 0f;
                foreach (var v in window16k) peak = Math.Max(peak, Math.Abs(v));
                return peak;
            }

            public void Dispose()
            {
            }
        }
    }
}
