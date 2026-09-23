using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Two real layers with real ffmpeg (Phase 7 Step 6): both decoded at the same timeline frame,
/// the lower one's ffmpeg process is started when opacity uncovers it and ended when it is covered
/// again, and nothing is left after Dispose.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class MultiLayerIntegrationTests
{
    private readonly TestMedia _media;
    private readonly ITestOutputHelper _output;

    public MultiLayerIntegrationTests(TestMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    [FfmpegFact]
    public async Task Two_real_layers_open_and_close_ffmpeg_processes_with_the_visible_layer_set()
    {
        var lower = _media.Get("cfr25.mp4");
        var upper = _media.Get("video.ts");
        Assert.NotNull(lower.Metadata.DisplayWidth); // probed with orientation

        var project = new Project { Settings = { FrameRate = FrameRate.Fps25, IsFrameRateLocked = true, FrameWidth = 256, FrameHeight = 144 } };
        var v1 = new Track { Type = TrackType.Video, Name = "V1", Order = 0 };
        var v2 = new Track { Type = TrackType.Video, Name = "V2", Order = 1 };
        project.Timeline.VideoTracks.AddRange(new[] { v1, v2 });
        var length = MediaTime.FromFrame(100, FrameRate.Fps25);
        VideoClip Clip(Track track, MediaFile file)
        {
            var asset = new MediaAsset { FilePath = file.Path, Kind = MediaKind.Video, Metadata = file.Metadata };
            project.MediaAssets.Add(asset);
            var clip = new VideoClip { MediaAssetId = asset.Id, Duration = length, SourceOut = length };
            track.Clips.Add(clip);
            return clip;
        }
        var bottom = Clip(v1, lower);
        var top = Clip(v2, upper);

        var baseline = FfmpegProcess.LiveProcesses;
        var service = new PlaybackService(new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance),
            new FakeReferenceClock(), NullLogger<PlaybackService>.Instance, new PlaybackSettings { Hardware = HardwareDecoding.Disabled });
        long version = 0;
        void Publish() => service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(project, ++version));

        async Task<PlaybackFrame> Settle(int layers)
        {
            PlaybackFrame frame = default;
            await WaitFor(() =>
            {
                frame = service.Update();
                return !frame.IsBuffering && frame.Layers.Length == layers &&
                       frame.Layers.All(l => l.State == LayerPictureState.Frame && l.IsCurrent);
            }, $"{layers} settled layer(s); last: {string.Join(", ", frame.Layers.Select(l => l.State + (l.IsCurrent ? "" : "(late)")))} buffering={frame.IsBuffering}");
            return frame;
        }


        try
        {
            Publish();
            var seek = service.SeekAsync(MediaTime.FromFrame(40, FrameRate.Fps25));
            await WaitFor(() => { service.Update(); return seek.IsCompleted; }, "seek");   // the Preview ticks while seeking
            Assert.True(await seek);
            var single = await Settle(1);
            Assert.Equal(top.Id, single.Layers[0].Layer.ClipId); // the full-canvas top video hides the lower one
            await WaitFor(() => { service.Update(); return FfmpegProcess.LiveProcesses - baseline == 1; }, "retired pipeline gone");

            top.Opacity = 0.5;
            Publish();
            var both = await Settle(2);
            Assert.Equal(new[] { bottom.Id, top.Id }, both.Layers.Select(l => l.Layer.ClipId));
            Assert.Equal(2, FfmpegProcess.LiveProcesses - baseline);   // one process per visible layer
            _output.WriteLine($"live ffmpeg processes with two layers: {FfmpegProcess.LiveProcesses - baseline}");

            top.Opacity = 1;
            Publish();
            await Settle(1);
            await WaitFor(() => { service.Update(); return FfmpegProcess.LiveProcesses - baseline == 1; }, "lower process ended");
        }
        finally
        {
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        }
        Assert.Equal(baseline, FfmpegProcess.LiveProcesses);
    }

    private static async Task WaitFor(Func<bool> condition, string what = "condition")
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(what);
            await Task.Delay(1);
        }
    }
}
