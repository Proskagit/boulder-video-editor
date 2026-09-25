using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Export;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Phase 8 Step 4 with the real ffmpeg decoder (atempo chain and its latency compensation, D022): the export's
/// offline audio equals the Preview's pipeline + mixer on the same snapshot, sample for sample, at 0.25×, 1× and
/// 4×; the 1 kHz bursts of the source land within 10 ms of the exact timeline mapping without drift; every
/// decoder stream is closed afterwards.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class ExportAudioIntegrationTests : IDisposable
{
    private const int Rate = AudioFormat.SampleRate;
    private static readonly FrameRate Fps = FrameRate.Fps25;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-export-audio-tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public ExportAudioIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A 20 s MP4 whose AAC audio has 250 ms bursts of 1 kHz at every whole second.</summary>
    private string Bursts()
    {
        var path = Path.Combine(_dir, "bursts.mp4");
        TestMedia.Run(FfmpegTools.Ffmpeg!, new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=25:duration=20",
            "-f", "lavfi", "-i", @"aevalsrc=sin(2*PI*1000*t)*0.8*lt(mod(t\,1)\,0.25):s=48000:d=20",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest", path
        });
        return path;
    }

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Fps);

    /// <summary>Energy centroids (in samples) of the bursts: runs above 0.05 separated by ≥ 150 ms.</summary>
    private static List<double> Centroids(float[] left)
    {
        var result = new List<double>();
        var gap = (int)(0.15 * Rate);
        for (var i = 0; i < left.Length;)
        {
            if (Math.Abs(left[i]) <= 0.05) { i++; continue; }
            int j = i, last = i;
            while (j < left.Length && j - last < gap) { if (Math.Abs(left[j]) > 0.05) last = j; j++; }
            double sum = 0, weighted = 0;
            for (var k = i; k <= last; k++) { var e = (double)left[k] * left[k]; sum += e; weighted += e * k; }
            result.Add(weighted / sum);
            i = j;
        }
        return result;
    }

    [FfmpegTheory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(80)]
    public async Task Export_audio_is_the_preview_mix_and_lands_on_the_exact_mapping(int steps)
    {
        var speed = ClipSpeed.FromSteps(steps);
        var s = (double)speed.ToDecimal();
        var path = Bursts();

        // A video clip with sound from timeline 1 s, SourceIn 2.3 s, at the speed; the sequence runs 1 s longer.
        var project = new Project { Settings = { FrameRate = Fps, IsFrameRateLocked = true } };
        var v1 = new Track { Type = TrackType.Video, Name = "V1", Order = 0, IsHidden = true };   // hidden: the sound stays
        project.Timeline.VideoTracks.Add(v1);
        var asset = new MediaAsset
        {
            FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(20), FrameRate = Fps, StartTime = MediaTime.Zero, AudioCodec = "aac" }
        };
        project.MediaAssets.Add(asset);
        var frames = steps switch { 80 => 100L, 5 => 400L, _ => 150L };                    // ≥ 4 bursts, inside the 20 s source
        var sourceIn = MediaTime.FromSeconds(2.3);
        v1.Clips.Add(new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = F(25), Duration = F(25 + frames) - F(25), Speed = speed, Volume = 0.75,
            SourceIn = sourceIn, SourceOut = sourceIn + (speed.IsNormal ? F(25 + frames) - F(25) : SpeedTiming.SourceLength(frames, speed, Fps))
        });
        var a1 = new Track { Type = TrackType.Audio, Name = "A1", Order = 0 };
        project.Timeline.AudioTracks.Add(a1);
        var snapshot = PlaybackSnapshotBuilder.Build(project, 1);
        var count = ExportOutput.For(snapshot).AudioSampleCount;

        var decoder = new CountingAudioDecoder(new FfmpegAudioDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegAudioDecoder>.Instance));
        var export = new float[count * 2];
        await using (var source = new ExportAudioSource(snapshot, decoder))
        {
            var at = 0;
            int read;
            var buffer = new float[2 * 5_000];
            while ((read = await source.ReadAsync(buffer)) > 0) { Array.Copy(buffer, 0, export, at, read); at += read; }
            Assert.Equal(export.Length, at);
        }
        Assert.Equal(1, decoder.Opened);
        Assert.Equal(0, decoder.Live);

        // The Preview's pipeline + mixer on the same snapshot, playing from the start.
        var preview = new float[count * 2];
        var mixer = new AudioMixer();
        mixer.Reset(0);
        await using (var pipeline = new AudioPipeline(snapshot, 1, 0, mixer, decoder, new PlaybackSettings(), NullLogger.Instance))
        {
            for (long from = 0; from < count; from += 4_800)
            {
                var n = (int)Math.Min(4_800, count - from);
                pipeline.Maintain(from);
                var watch = Stopwatch.StartNew();
                while (!pipeline.HasData(from, from + n))
                {
                    if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException($"preview audio at {from}");
                    await Task.Delay(1);
                }
                var chunk = new float[n * 2];
                mixer.Read(chunk);
                Array.Copy(chunk, 0, preview, from * 2, chunk.Length);
            }
        }
        Assert.Equal(0, decoder.Live);
        var differing = Enumerable.Range(0, export.Length).Count(i => export[i] != preview[i]);
        Assert.True(differing == 0, $"{differing} of {export.Length} samples differ from the Preview");

        // Burst centroids on the timeline → source seconds; burst k is centred at source second k + 0.125.
        var left = Enumerable.Range(0, (int)count).Select(i => export[2 * i]).ToArray();
        var start = AudioTiming.CeilingSample(F(25));
        var errorsMs = new List<double>();
        foreach (var centroid in Centroids(left))
        {
            var source = sourceIn.TotalSeconds + (centroid - start) / Rate * s;
            var burst = Math.Round(source - 0.125);
            errorsMs.Add((source - (burst + 0.125)) / s * 1000);                            // timeline milliseconds
        }
        _output.WriteLine($"{speed}: {errorsMs.Count} bursts, errors ms: {string.Join(" ", errorsMs.Select(e => e.ToString("0.00")))}");
        Assert.True(errorsMs.Count >= 3, $"only {errorsMs.Count} bursts");
        Assert.All(errorsMs, e => Assert.InRange(e, -10.0, 10.0));
        Assert.InRange(errorsMs[^1] - errorsMs[0], -10.0, 10.0);                            // no drift
        Assert.All(left.Take((int)start), v => Assert.Equal(0f, v));                          // silence before the clip
        // 0.8 × volume 0.75, with the decoder's mono → stereo upmix at −3 dB (as in the Preview): ≈ 0.42
        Assert.InRange(left.Where(v => v != 0).Max(Math.Abs), 0.38f, 0.47f);
    }

    /// <summary>Counts this test's audio streams (the process-wide ffmpeg counter is shared with parallel tests).</summary>
    private sealed class CountingAudioDecoder(IAudioDecoder inner) : IAudioDecoder
    {
        private int _live, _opened;
        public int Live => Volatile.Read(ref _live);
        public int Opened => Volatile.Read(ref _opened);

        public async Task<IAudioSampleStream> OpenAsync(AudioDecodeRequest request, CancellationToken ct = default)
        {
            var stream = await inner.OpenAsync(request, ct);
            Interlocked.Increment(ref _opened);
            Interlocked.Increment(ref _live);
            return new Stream(stream, () => Interlocked.Decrement(ref _live));
        }

        private sealed class Stream(IAudioSampleStream inner, Action disposed) : IAudioSampleStream
        {
            private int _done;
            public long FirstSampleIndex => inner.FirstSampleIndex;
            public ValueTask<int> ReadAsync(Memory<float> interleaved, CancellationToken ct = default) => inner.ReadAsync(interleaved, ct);
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                if (Interlocked.Exchange(ref _done, 1) == 0) disposed();
            }
        }
    }
}
