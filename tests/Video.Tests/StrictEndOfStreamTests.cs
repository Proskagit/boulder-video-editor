using System.Collections.Immutable;
using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Export;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Follow-up to Phase 8 Step 4 (D023): how the end of an ffmpeg stream is judged. The "ffmpeg" here is a script
/// that runs the real ffmpeg with fixed arguments — real frames/samples on stdout, real showinfo/ashowinfo lines on
/// stderr — and then exits with a chosen code, i.e. a decoder process that fails after it delivered data. A strict end
/// (the export) turns that into <see cref="VideoDecodeError.DecoderFailed"/> / <see cref="ExportFailure.DecodeFailed"/>;
/// a normal exit stays a normal end; without a strict end (playback) nothing changes; cancellation stays cancellation;
/// no process is left behind.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class StrictEndOfStreamTests : IDisposable
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const int VideoFrames = 10;          // 0.4 s at 25 fps
    private const int AudioSamples = 24_000;     // 0.5 s at 48 kHz

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-strict-end", Guid.NewGuid().ToString("N"));

    public StrictEndOfStreamTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A fake ffmpeg: a script running the real ffmpeg (ignoring the decoder's arguments), optionally waiting,
    /// then exiting with <paramref name="exitCode"/>.</summary>
    private sealed class ScriptedFfmpeg(string script) : IFfmpegLocator
    {
        public string Script { get; } = script;
        public DateTime Created { get; } = DateTime.Now;
        public Task<string?> GetFfmpegPathAsync(CancellationToken ct = default) => Task.FromResult<string?>(Script);
    }

    private ScriptedFfmpeg Script(bool video, int exitCode, int waitSeconds = 0)
    {
        var ffmpeg = FfmpegTools.Ffmpeg!;
        var run = video
            ? $"\"{ffmpeg}\" -hide_banner -nostdin -nostats -loglevel info -f lavfi -i \"testsrc2=size=64x36:rate=25:duration=0.4\" " +
              "-vf \"format=bgra,showinfo=checksum=0\" -fps_mode passthrough -pix_fmt bgra -f rawvideo pipe:1"
            : $"\"{ffmpeg}\" -hide_banner -nostdin -nostats -loglevel info -f lavfi -i \"sine=frequency=440:sample_rate=48000:duration=0.5\" " +
              "-af \"aresample=48000,aformat=sample_fmts=flt:channel_layouts=stereo,ashowinfo\" -f f32le pipe:1";
        var path = Path.Combine(_dir, $"ffmpeg-{(video ? "video" : "audio")}-{exitCode}-{waitSeconds}-{Guid.NewGuid():N}.cmd");
        var wait = waitSeconds > 0 ? $"ping -n {waitSeconds + 1} 127.0.0.1 >nul\r\n" : "";
        File.WriteAllText(path, $"@echo off\r\n{run}\r\n{wait}exit {exitCode}\r\n");
        return new ScriptedFfmpeg(path);
    }

    private static FfmpegVideoDecoder VideoDecoder(ScriptedFfmpeg ffmpeg) => new(ffmpeg, NullLogger<FfmpegVideoDecoder>.Instance);
    private static FfmpegAudioDecoder AudioDecoder(ScriptedFfmpeg ffmpeg) => new(ffmpeg, NullLogger<FfmpegAudioDecoder>.Instance);

    private static VideoDecodeRequest VideoRequest(string path, bool strict) => new()
    {
        FilePath = path, StartTime = MediaTime.Zero, NominalFrameRate = Rate, Hardware = HardwareDecoding.Disabled, StrictEnd = strict,
        FirstSamplePoint = SourceFrameSelector.SamplePoint(MediaTime.Zero, MediaTime.Zero, 0, Rate, Rate)
    };

    private static AudioDecodeRequest AudioRequest(string path, bool strict) => new()
    {
        FilePath = path, StartTime = MediaTime.Zero, SourcePosition = MediaTime.Zero, StrictEnd = strict
    };

    /// <summary>No ffmpeg or ping started by the script (i.e. since it was written) is still alive.</summary>
    private static void AssertNothingRunning(ScriptedFfmpeg ffmpeg)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var alive = Process.GetProcessesByName("ffmpeg").Concat(Process.GetProcessesByName("PING"))
                .Where(p => { try { return !p.HasExited && p.StartTime >= ffmpeg.Created.AddSeconds(-1); } catch { return false; } })
                .Select(p => $"{p.ProcessName}#{p.Id}").ToList();
            if (alive.Count == 0) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail($"still running after '{Path.GetFileName(ffmpeg.Script)}': {string.Join(", ", alive)}");
            Thread.Sleep(50);
        }
    }

    // --- decoder streams ------------------------------------------------------------------------------------

    [FfmpegTheory]
    [InlineData(3, true, true)]     // failed after its frames, strict (export): an error
    [InlineData(3, false, false)]   // failed after its frames, playback: the stream just ends (unchanged)
    [InlineData(0, true, false)]    // normal exit, strict: a normal end
    [InlineData(0, false, false)]
    public async Task Video_stream_end(int exitCode, bool strict, bool fails)
    {
        var ffmpeg = Script(video: true, exitCode);
        var baseline = FfmpegProcess.LiveProcesses;
        await using (var stream = await VideoDecoder(ffmpeg).OpenAsync(VideoRequest(ffmpeg.Script, strict)))
        {
            for (var i = 0; i < VideoFrames; i++)
                Assert.Equal(i, (await stream.ReadFrameAsync())!.Timestamp.Pts);

            if (fails)
            {
                var error = await Assert.ThrowsAsync<VideoDecodeException>(async () => await stream.ReadFrameAsync());
                Assert.Equal(VideoDecodeError.DecoderFailed, error.Error);
                Assert.Contains("exited with code 3", error.Message);
                Assert.Contains($"after {VideoFrames} frames", error.Message);
            }
            else
            {
                Assert.Null(await stream.ReadFrameAsync());
            }
        }
        Assert.Equal(baseline, FfmpegProcess.LiveProcesses);
        AssertNothingRunning(ffmpeg);
    }

    [FfmpegTheory]
    [InlineData(3, true, true)]
    [InlineData(3, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, false)]
    public async Task Audio_stream_end(int exitCode, bool strict, bool fails)
    {
        var ffmpeg = Script(video: false, exitCode);
        var baseline = FfmpegProcess.LiveProcesses;
        await using (var stream = await AudioDecoder(ffmpeg).OpenAsync(AudioRequest(ffmpeg.Script, strict)))
        {
            var buffer = new float[4096];
            long frames = 0;
            AudioDecodeException? error = null;
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0) frames += read / 2;
            }
            catch (AudioDecodeException ex)
            {
                error = ex;
            }

            Assert.Equal(AudioSamples, frames);                                 // everything ffmpeg wrote was delivered first
            Assert.Equal(fails, error is not null);
            if (fails)
            {
                Assert.Equal(VideoDecodeError.DecoderFailed, error!.Error);
                Assert.Contains("exited with code 3", error.Message);
            }
        }
        Assert.Equal(baseline, FfmpegProcess.LiveProcesses);
        AssertNothingRunning(ffmpeg);
    }

    [FfmpegTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelling_while_the_strict_end_is_judged_is_cancellation_and_leaves_no_process(bool video)
    {
        // The script keeps stdout open for 10 s after ffmpeg's data, then exits with 3: the end is still pending.
        var ffmpeg = Script(video, exitCode: 3, waitSeconds: 10);
        var baseline = FfmpegProcess.LiveProcesses;
        using var cts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();

        if (video)
        {
            await using var stream = await VideoDecoder(ffmpeg).OpenAsync(VideoRequest(ffmpeg.Script, strict: true));
            for (var i = 0; i < VideoFrames; i++) await stream.ReadFrameAsync();
            cts.CancelAfter(300);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadFrameAsync(cts.Token));
        }
        else
        {
            await using var stream = await AudioDecoder(ffmpeg).OpenAsync(AudioRequest(ffmpeg.Script, strict: true));
            var buffer = new float[2 * AudioSamples];
            var read = 0;
            while (read < 2 * AudioSamples) read += await stream.ReadAsync(buffer.AsMemory(read));
            cts.CancelAfter(300);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadAsync(buffer, cts.Token));
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed}");   // not the script's 10 s
        Assert.Equal(baseline, FfmpegProcess.LiveProcesses);
        AssertNothingRunning(ffmpeg);                                                    // the whole tree was killed
    }

    // --- the export: strict ------------------------------------------------------------------------------------

    private static PlaybackSnapshot Snapshot(string path, long frames, bool audio)
    {
        var asset = Guid.NewGuid();
        var clip = Guid.NewGuid();
        var picture = new PictureSpan(clip, asset, SpanStatus.Video, MediaTime.Zero, MediaTime.FromFrame(frames, Rate), MediaTime.Zero)
            { SourceSize = new FrameSize(64, 36) };
        var sound = new AudioSpan(clip, asset, SpanStatus.Audio, MediaTime.Zero, MediaTime.FromFrame(frames, Rate), MediaTime.Zero, 1);
        return new PlaybackSnapshot(1, Rate, MediaTime.FromFrame(frames, Rate),
            audio ? ImmutableArray<VideoLayer>.Empty : ImmutableArray.Create(new VideoLayer(Guid.NewGuid(), ImmutableArray.Create(picture))),
            audio ? ImmutableArray.Create(sound) : ImmutableArray<AudioSpan>.Empty,
            ImmutableDictionary<Guid, PlaybackAsset>.Empty.Add(asset, new PlaybackAsset(asset, path, MediaKind.Video, MediaTime.Zero, Rate)),
            new FrameSize(64, 36));
    }

    [FfmpegTheory]
    [InlineData(3)]
    [InlineData(0)]
    public async Task The_export_fails_on_a_video_decoder_that_crashed_after_its_frames_and_holds_the_last_frame_after_a_normal_end(int exitCode)
    {
        var ffmpeg = Script(video: true, exitCode);
        var baseline = FfmpegProcess.LiveProcesses;
        await using (var source = new ExportFrameSource(Snapshot(ffmpeg.Script, 20, audio: false), VideoDecoder(ffmpeg)))
        {
            for (var n = 0; n < VideoFrames - 1; n++)
                Assert.Equal(n, Assert.Single((await source.GetFrameAsync(n)).Layers).Frame!.Timestamp.Pts);

            if (exitCode != 0)
            {
                // Frame 9 is only certain once the stream's end is known — and that end is a crash.
                var error = await Assert.ThrowsAsync<ExportException>(() => source.GetFrameAsync(VideoFrames - 1));
                Assert.Equal(ExportFailure.DecodeFailed, error.Failure);
                Assert.Contains("exited with code 3", error.Message);
            }
            else
            {
                for (var n = VideoFrames - 1; n < 20; n++)
                    Assert.Equal(VideoFrames - 1, Assert.Single((await source.GetFrameAsync(n)).Layers).Frame!.Timestamp.Pts);   // hold-last
            }
        }
        Assert.Equal(baseline, FfmpegProcess.LiveProcesses);
        AssertNothingRunning(ffmpeg);
    }

    [FfmpegTheory]
    [InlineData(3)]
    [InlineData(0)]
    public async Task The_export_fails_on_an_audio_decoder_that_crashed_after_its_samples_and_is_silent_after_a_normal_end(int exitCode)
    {
        var ffmpeg = Script(video: false, exitCode);
        var baseline = FfmpegProcess.LiveProcesses;
        await using (var source = new ExportAudioSource(Snapshot(ffmpeg.Script, 25, audio: true), AudioDecoder(ffmpeg)))   // 1 s, the source has 0.5 s
        {
            var buffer = new float[2 * 12_000];
            Assert.Equal(buffer.Length, await source.ReadAsync(buffer));             // 0 – 0.25 s
            Assert.Equal(buffer.Length, await source.ReadAsync(buffer));             // 0.25 – 0.5 s
            Assert.Contains(buffer, s => s != 0);

            if (exitCode != 0)
            {
                var error = await Assert.ThrowsAsync<ExportException>(async () => await source.ReadAsync(buffer));
                Assert.Equal(ExportFailure.DecodeFailed, error.Failure);
                Assert.Contains("exited with code 3", error.Message);
            }
            else
            {
                Assert.Equal(buffer.Length, await source.ReadAsync(buffer));         // 0.5 – 0.75 s: the source ended normally
                Assert.All(buffer, s => Assert.Equal(0f, s));
            }
        }
        Assert.Equal(baseline, FfmpegProcess.LiveProcesses);
        AssertNothingRunning(ffmpeg);
    }

    // --- the Preview: unchanged ---------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task The_preview_still_holds_the_last_frame_and_plays_silence_after_a_crashed_decoder()
    {
        var video = Script(video: true, exitCode: 3);
        await using (var pipeline = new VideoPipeline(Snapshot(video.Script, 20, audio: false), 1, 0, VideoDecoder(video),
                         new PlaybackSettings { Hardware = HardwareDecoding.Disabled }, NullLogger.Instance))
        {
            foreach (var n in new long[] { 0, 5, VideoFrames - 1, 15, 19 })
            {
                var watch = Stopwatch.StartNew();
                LayerPicture layer;
                while ((layer = Assert.Single(pipeline.GetFrame(n).Layers)) is not { State: LayerPictureState.Frame, IsCurrent: true })
                {
                    Assert.False(layer.IsPlaceholder, $"frame {n}: {layer.State} {layer.Message}");
                    if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"frame {n}");
                    await Task.Delay(5);
                }
                Assert.Equal(Math.Min(n, VideoFrames - 1), layer.Frame!.Timestamp.Pts);        // no DecodeError: the last frame is held
            }
        }
        AssertNothingRunning(video);

        var audio = Script(video: false, exitCode: 3);
        var mixer = new AudioMixer();
        mixer.Reset(0);
        var samples = new List<float>();
        await using (var pipeline = new AudioPipeline(Snapshot(audio.Script, 25, audio: true), 1, 0, mixer, AudioDecoder(audio),
                         new PlaybackSettings(), NullLogger.Instance))
        {
            for (long from = 0; from < 48_000; from += 4_800)
            {
                pipeline.Maintain(from);
                var watch = Stopwatch.StartNew();
                while (!pipeline.HasData(from, from + 4_800))
                {
                    if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"audio {from}");
                    await Task.Delay(1);
                }
                var buffer = new float[2 * 4_800];
                mixer.Read(buffer);
                samples.AddRange(buffer);
            }
        }
        Assert.Contains(samples.Take(2 * AudioSamples), s => s != 0);                    // the decoded half second plays
        Assert.All(samples.Skip(2 * AudioSamples), s => Assert.Equal(0f, s));            // then silence, as before
        AssertNothingRunning(audio);
    }
}
