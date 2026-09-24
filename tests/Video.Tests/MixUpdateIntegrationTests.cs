using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Volume/mute with real ffmpeg decoders (Phase 7 Step 4): a mix-only snapshot update while playing
/// starts no ffmpeg process, keeps the video pipeline and seek generation, never buffers, and the
/// played samples follow the new gain immediately.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class MixUpdateIntegrationTests
{
    private readonly TestMedia _media;

    public MixUpdateIntegrationTests(TestMedia media) => _media = media;

    private sealed class CountingVideoDecoder(IVideoDecoder inner) : IVideoDecoder
    {
        public int Opens;
        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opens);
            return inner.OpenAsync(request, ct);
        }
    }

    private sealed class CountingAudioDecoder(IAudioDecoder inner) : IAudioDecoder
    {
        public int Opens;
        public Task<IAudioSampleStream> OpenAsync(AudioDecodeRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opens);
            return inner.OpenAsync(request, ct);
        }
    }

    [FfmpegFact]
    public async Task VolumeAndMuteWhilePlaying_StartNoFfmpegProcess_AndChangeTheSoundImmediately()
    {
        var file = _media.Get("longaudio.mp4"); // 25 fps video with a 440 Hz sine
        var project = new Project { Settings = { FrameRate = FrameRate.Fps25, IsFrameRateLocked = true } };
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(v1);
        var asset = new MediaAsset { FilePath = file.Path, Kind = MediaKind.Video, Metadata = file.Metadata };
        project.MediaAssets.Add(asset);
        var length = MediaTime.FromFrame(90, FrameRate.Fps25);
        var clip = new VideoClip { MediaAssetId = asset.Id, Duration = length, SourceOut = length };
        v1.Clips.Add(clip);

        var video = new CountingVideoDecoder(new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance));
        var audio = new CountingAudioDecoder(new FfmpegAudioDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegAudioDecoder>.Instance));
        var output = new FakeAudioOutput();
        await using var service = new PlaybackService(video, new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { Hardware = HardwareDecoding.Disabled }, audio, output);
        long version = 0;
        void Publish() => service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(project, ++version));

        async Task<float[]> PlayAudio(int frames)
        {
            var from = AudioTiming.NearestSample(service.Position);
            await WaitFor(() => { service.Update(); return service.AudioHasData(from, from + frames); });
            return output.Play(frames);
        }

        static float Peak(float[] buffer) => buffer.Max(Math.Abs);

        Publish();
        service.Play();
        var full = await PlayAudio(4_800);
        await WaitFor(() => { var f = service.Update(); return f.IsPictureCurrent && !f.IsBuffering && f.Picture?.Frame is not null; });
        Assert.True(Peak(full) > 0.05f, $"expected the sine (amplitude 1/8), peak {Peak(full)}");

        var pipeline = service.VideoPipelineInstance;
        var audioPipeline = service.AudioPipelineInstance;
        var generation = service.SeekGeneration;
        var (videoOpens, audioOpens, processes) = (video.Opens, audio.Opens, FfmpegProcess.LiveProcesses);

        void AssertKept(string step)
        {
            Assert.True(ReferenceEquals(pipeline, service.VideoPipelineInstance), $"{step}: new video pipeline");
            Assert.True(ReferenceEquals(audioPipeline, service.AudioPipelineInstance), $"{step}: new audio pipeline");
            Assert.Equal(generation, service.SeekGeneration);
            Assert.Equal((videoOpens, audioOpens), (video.Opens, audio.Opens));
            Assert.Equal(processes, FfmpegProcess.LiveProcesses);
            Assert.False(service.Update().IsBuffering, $"{step}: buffering");
        }

        clip.IsMuted = true;
        Publish();
        AssertKept("mute");
        var muted = await PlayAudio(2_400);
        Assert.Equal(0f, Peak(muted));                     // the clip's own audio is silent at once

        clip.IsMuted = false;
        clip.Volume = 0.5;
        Publish();
        AssertKept("unmute at 50 %");
        var half = await PlayAudio(2_400);
        Assert.InRange(Peak(half), 0.25f * Peak(full), 0.75f * Peak(full));

        clip.Volume = 1.0;
        Publish();
        AssertKept("back to 100 %");
        Assert.True(Peak(await PlayAudio(2_400)) > 0.05f);

        Assert.Equal((0, 1), (output.StopCount, output.StartCount));
        AssertKept("end");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
            await Task.Delay(1);
        }
    }
}
