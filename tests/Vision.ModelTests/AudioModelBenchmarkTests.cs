using System.Diagnostics;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Media.Audio;
using AiVideoEditor.Video;
using AiVideoEditor.Vision.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// D064 §4–§5, local only (the real models in <c>%LOCALAPPDATA%\BoulderVideoEditor\models\audio-v1</c>, ffmpeg on PATH, the test
/// videos in <c>VideosForTest</c>): the pinned models load and verify; DeepFilterNet3 lowers the noise of a camera recording and
/// keeps the length; Silero finds speech in it; and the benchmark of docs/PHASE45_AUDIO.md — a 30-minute recording through the
/// noise reduction, the voice enhancement and the speech analysis, with the time and the process's peak memory.
/// </summary>
public sealed class AudioModelBenchmarkTests(ITestOutputHelper output)
{
    private static readonly string ModelDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BoulderVideoEditor", "models", AudioModel.FolderName);

    private static string? Video(string name)
    {
        var dir = Environment.GetEnvironmentVariable("AIVE_TEST_VIDEOS") ?? FindUp("VideosForTest");
        var path = dir is null ? null : Directory.GetFiles(dir, "*.mp4").FirstOrDefault(f => Path.GetFileName(f).Contains(name, StringComparison.OrdinalIgnoreCase));
        return path;
    }

    private static string? FindUp(string folder)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, folder))) return Path.Combine(dir.FullName, folder);
        return null;
    }

    private static FfmpegAudioDecoder Decoder()
    {
        var options = Options.Create(new FfmpegOptions());
        return new FfmpegAudioDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance),
            NullLogger<FfmpegAudioDecoder>.Instance);
    }

    private bool Ready(out DeepFilterNetEnhancer voice, out SileroSpeechModel speech)
    {
        voice = new DeepFilterNetEnhancer(ModelDir);
        speech = new SileroSpeechModel(ModelDir);
        var a = voice.CheckAsync(default).GetAwaiter().GetResult();
        var b = speech.CheckAsync(default).GetAwaiter().GetResult();
        if (a is null && b is null) return true;
        output.WriteLine($"SKIPPED: {a ?? b}");
        return false;
    }

    private static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var x = new float[(bytes.Length - 44) / 4];
        Buffer.BlockCopy(bytes, 44, x, 0, x.Length * 4);
        return x;
    }

    private static double Rms(float[] x, int from, int until)
    {
        double s = 0;
        for (var i = from; i < until; i++) s += x[i] * (double)x[i];
        return Math.Sqrt(s / Math.Max(1, until - from));
    }

    [Fact]
    public async Task The_voice_model_cleans_a_camera_recording_and_finds_its_speech()
    {
        var source = Video("человек бар");
        if (source is null || !Ready(out var voice, out var speech)) return;
        using (voice) using (speech)
        {
            var processor = new AudioCleanupProcessor(Decoder(), voice, speech);
            var dir = Directory.CreateTempSubdirectory("aive-audio-model-").FullName;
            try
            {
                var duration = MediaTime.FromSeconds(36.3);
                var plain = Path.Combine(dir, "plain.wav");
                var cleaned = Path.Combine(dir, "voice.wav");
                await processor.ProduceAsync(source, MediaTime.Zero, duration, new AudioCleanup(1, 0), plain, null, default);   // NR at its mildest
                var watch = Stopwatch.StartNew();
                await processor.ProduceAsync(source, MediaTime.Zero, duration, new AudioCleanup(0, 100), cleaned, null, default);
                output.WriteLine($"voice 36 s: {watch.Elapsed.TotalSeconds:F2} s");

                var a = ReadWav(plain);
                var b = ReadWav(cleaned);
                Assert.Equal(a.Length, b.Length);
                Assert.All(b, v => Assert.True(float.IsFinite(v)));
                output.WriteLine($"rms before {Rms(a, 0, a.Length):F4}, after {Rms(b, 0, b.Length):F4}");
                Assert.True(Rms(b, 0, b.Length) < Rms(a, 0, a.Length));

                var activity = await processor.AnalyseSpeechAsync(source, MediaTime.Zero, duration, null, default);
                var speechShare = activity.Intervals(DuckingSettings.Default(Guid.NewGuid()).SpeechThreshold).Sum(i => i.End - i.Start) / (36.3 * 48_000);
                output.WriteLine($"speech share {speechShare:P0}, windows {activity.Probabilities.Length}");
                Assert.Equal((int)Math.Ceiling(a.Length / 2.0 / SpeechActivity.WindowSamples), activity.Probabilities.Length);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Benchmark_a_thirty_minute_recording()
    {
        var clip = Video("боулдеринг нежные");
        if (Environment.GetEnvironmentVariable("AIVE_AUDIO_BENCHMARK") != "1")
        {
            output.WriteLine("SKIPPED: set AIVE_AUDIO_BENCHMARK=1");
            return;
        }
        if (clip is null || !Ready(out var voice, out var speech)) return;
        using (voice) using (speech)
        {
            var dir = Directory.CreateTempSubdirectory("aive-audio-bench-").FullName;
            try
            {
                // 30 minutes of the bouldering recording's sound, looped.
                var source = Path.Combine(dir, "long.m4a");
                var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", $"-hide_banner -loglevel error -y -stream_loop 23 -i \"{clip}\" -vn -t 1800 -c:a aac -b:a 160k \"{source}\"")
                    { UseShellExecute = false })!;
                await ffmpeg.WaitForExitAsync();
                Assert.Equal(0, ffmpeg.ExitCode);
                var duration = MediaTime.FromSeconds(1800);
                var processor = new AudioCleanupProcessor(Decoder(), voice, speech);

                foreach (var (name, run) in new (string, Func<Task>)[]
                {
                    ("noise reduction 50", () => processor.ProduceAsync(source, MediaTime.Zero, duration, new AudioCleanup(50, 0), Path.Combine(dir, "nr.wav"), null, default)),
                    ("voice enhancement 100", () => processor.ProduceAsync(source, MediaTime.Zero, duration, new AudioCleanup(0, 100), Path.Combine(dir, "ve.wav"), null, default)),
                    ("both 50 / 70", () => processor.ProduceAsync(source, MediaTime.Zero, duration, new AudioCleanup(50, 70), Path.Combine(dir, "both.wav"), null, default)),
                    ("speech analysis", () => processor.AnalyseSpeechAsync(source, MediaTime.Zero, duration, null, default))
                })
                {
                    using var process = Process.GetCurrentProcess();
                    process.Refresh();
                    var watch = Stopwatch.StartNew();
                    await run();
                    process.Refresh();
                    output.WriteLine($"{name}: {watch.Elapsed.TotalSeconds:F1} s for 30 min (×{1800 / watch.Elapsed.TotalSeconds:F0} real time), " +
                                     $"peak working set {process.PeakWorkingSet64 / 1024 / 1024} MB");
                }
                Assert.InRange(new FileInfo(Path.Combine(dir, "ve.wav")).Length, 44 + 1799L * 48_000 * 8, 44 + 1801L * 48_000 * 8);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
