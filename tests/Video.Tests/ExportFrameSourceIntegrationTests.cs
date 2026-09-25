using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Export;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Phase 8 Step 2 with the real ffmpeg decoder: the export selects the same source frames as the
/// Preview's pipeline (1×, 0.25×, 4×, a trimmed start) but decodes them at the source's full 1920 × 1080,
/// where the Preview decodes at ≤ 1280 × 720; every ffmpeg process ends with the frame source.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class ExportFrameSourceIntegrationTests
{
    private static readonly FrameRate Rate = FrameRate.Ntsc30;
    private readonly TestMedia _media;

    public ExportFrameSourceIntegrationTests(TestMedia media) => _media = media;

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    [FfmpegFact]
    public async Task Export_frames_are_the_previews_frames_at_full_resolution()
    {
        var file = _media.Get("hd2997.mp4");
        var project = new Project { Settings = { FrameRate = Rate, IsFrameRateLocked = true } };
        var v1 = new Track { Type = TrackType.Video, Name = "V1", Order = 0 };
        project.Timeline.VideoTracks.Add(v1);
        var asset = new MediaAsset { FilePath = file.Path, Kind = MediaKind.Video, Metadata = file.Metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
        project.MediaAssets.Add(asset);

        // [0, 20) 1× from source frame 5 · [20, 40) 0.25× from source frame 40 · [40, 50) 4× from source frame 50
        void Clip(long start, long end, long sourceInFrame, int speedSteps)
        {
            var speed = ClipSpeed.FromSteps(speedSteps);
            var sourceIn = F(sourceInFrame);
            v1.Clips.Add(new VideoClip
            {
                MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(end) - F(start), Speed = speed,
                SourceIn = sourceIn, SourceOut = sourceIn + SpeedTiming.SourceLength(end - start, speed, Rate)
            });
        }
        Clip(0, 20, 5, 20);
        Clip(20, 40, 40, 5);
        Clip(40, 50, 50, 80);
        var snapshot = PlaybackSnapshotBuilder.Build(project, 1);

        var decoder = new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance);
        var counting = new CountingDecoder(decoder);   // FfmpegProcess.LiveProcesses is global: other test classes run in parallel
        var exported = new List<long>();
        await using (var source = new ExportFrameSource(snapshot, counting))
        {
            Assert.Equal(50, source.FrameCount);
            for (var n = 0L; n < source.FrameCount; n++)
            {
                var frame = Assert.Single((await source.GetFrameAsync(n)).Layers).Frame!;
                Assert.Equal((1920, 1080), (frame.Width, frame.Height));
                exported.Add(FfmpegVideoDecoderIntegrationTests.ReadNumber(frame));
            }
        }
        Assert.Equal(3, counting.Opened);                                         // one stream per clip
        Assert.Equal(0, counting.Live);

        // The Preview's pipeline, playing the same snapshot from the start.
        var preview = new List<long>();
        await using (var pipeline = new VideoPipeline(snapshot, 1, 0, decoder,
                         new PlaybackSettings { Hardware = HardwareDecoding.Disabled }, NullLogger.Instance))
        {
            for (var n = 0L; n < 50; n++)
            {
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    var layer = Assert.Single(pipeline.GetFrame(n).Layers);
                    Assert.False(layer.IsPlaceholder, layer.Message);
                    if (layer is { State: LayerPictureState.Frame, IsCurrent: true })
                    {
                        Assert.True(layer.Frame!.Width <= 1280 && layer.Frame.Height <= 720);
                        preview.Add(FfmpegVideoDecoderIntegrationTests.ReadNumber(layer.Frame));
                        break;
                    }
                    if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException($"preview frame {n}");
                    await Task.Delay(1);
                }
            }
        }

        Assert.Equal(preview, exported);
        // Equal rates: frame n of a clip shows source ⌊in + (n − start)·s + ½·min(s, 1)⌋.
        Assert.Equal(Enumerable.Range(5, 20).Select(i => (long)i), exported.Take(20));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => 40 + (long)Math.Floor(i * 0.25 + 0.125)), exported.Skip(20).Take(20));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => 50 + 4L * i), exported.Skip(40));
    }

    /// <summary>Counts the streams this test opens and has not disposed.</summary>
    private sealed class CountingDecoder(IVideoDecoder inner) : IVideoDecoder
    {
        private int _live, _opened;
        public int Live => Volatile.Read(ref _live);
        public int Opened => Volatile.Read(ref _opened);

        public async Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            var stream = await inner.OpenAsync(request, ct);
            Interlocked.Increment(ref _opened);
            Interlocked.Increment(ref _live);
            return new Stream(stream, () => Interlocked.Decrement(ref _live));
        }

        private sealed class Stream(IVideoFrameStream inner, Action disposed) : IVideoFrameStream
        {
            private int _done;
            public ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default) => inner.ReadFrameAsync(ct);
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                if (Interlocked.Exchange(ref _done, 1) == 0) disposed();
            }
        }
    }
}
