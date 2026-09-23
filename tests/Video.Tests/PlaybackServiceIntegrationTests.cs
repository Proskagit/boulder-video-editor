using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// PlaybackService with the real ffmpeg decoder: snapshot → pipeline → D009 selection → pixels,
/// stepping a manual clock one timeline frame at a time across two clips and a gap.
/// </summary>
[Collection(MediaCollection.Name)]
public class PlaybackServiceIntegrationTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly TestMedia _media;

    public PlaybackServiceIntegrationTests(TestMedia media) => _media = media;

    private sealed class ManualClock : IReferenceClock
    {
        private long _ticks;
        public ReferenceTime Now => new(Interlocked.Read(ref _ticks), TimeSpan.TicksPerSecond);
        public void Advance(MediaTime by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    [FfmpegTheory]
    [InlineData(HardwareDecoding.Disabled)]
    [InlineData(HardwareDecoding.Auto)]
    public async Task TwoClipsAndAGap_EveryTimelineFrameShowsTheExpectedSourceFrame(HardwareDecoding hardware)
    {
        var a = _media.Get("cfr25.mp4");
        var b = _media.Get("cfr24.mp4");

        // V1: A frames 0–25 (SourceIn = source frame 50), gap 25–35, B frames 35–85 (24 fps source, SourceIn 0).
        var project = new Project { Settings = { FrameRate = Rate, IsFrameRateLocked = true } };
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(v1);
        var assetA = new MediaAsset { FilePath = a.Path, Kind = MediaKind.Video, Metadata = a.Metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
        var assetB = new MediaAsset { FilePath = b.Path, Kind = MediaKind.Video, Metadata = b.Metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
        project.MediaAssets.AddRange(new[] { assetA, assetB });
        v1.Clips.Add(new VideoClip { MediaAssetId = assetA.Id, TimelineStart = F(0), Duration = F(25), SourceIn = F(50), SourceOut = F(75) });
        v1.Clips.Add(new VideoClip { MediaAssetId = assetB.Id, TimelineStart = F(35), Duration = F(85) - F(35), SourceIn = MediaTime.Zero, SourceOut = F(85) - F(35) });

        var clock = new ManualClock();
        await using var service = new PlaybackService(
            new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance),
            clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { Hardware = hardware });
        service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(project, 1));
        service.Play();

        var shown = new List<string>();
        var expected = new List<string>();
        for (long n = 0; n < 85; n++)
        {
            if (n > 0) clock.Advance(F(n) - F(n - 1));
            var frame = await SettleAsync(service, n);
            shown.Add(frame.Picture!.Kind == PictureKind.Frame
                ? FfmpegVideoDecoderIntegrationTests.ReadNumber(frame.Picture.Frame!).ToString()
                : frame.Picture.Kind.ToString());
            expected.Add(n < 25 ? (50 + n).ToString()
                : n < 35 ? nameof(PictureKind.Black)
                : ((2 * (n - 35) + 1) * 24 / 50).ToString()); // 24 fps in 25: floor((m/25 + 1/50)·24)
        }

        Assert.Equal(expected, shown);
        Assert.Equal(PlaybackState.Playing, service.State);

        clock.Advance(MediaTime.FromSeconds(1));
        var end = await SettleAsync(service, 85);
        Assert.Equal((PlaybackState.Paused, F(85)), (end.State, end.Position));
    }

    [FfmpegFact]
    public async Task SeeksAndDispose_LeaveNoFfmpegProcessRunning()
    {
        var a = _media.Get("cfr25.mp4");
        var b = _media.Get("cfr24.mp4");
        var project = new Project { Settings = { FrameRate = Rate, IsFrameRateLocked = true } };
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(v1);
        var assetA = new MediaAsset { FilePath = a.Path, Kind = MediaKind.Video, Metadata = a.Metadata };
        var assetB = new MediaAsset { FilePath = b.Path, Kind = MediaKind.Video, Metadata = b.Metadata };
        project.MediaAssets.AddRange(new[] { assetA, assetB });
        v1.Clips.Add(new VideoClip { MediaAssetId = assetA.Id, TimelineStart = F(0), Duration = F(100), SourceIn = F(0), SourceOut = F(100) });
        v1.Clips.Add(new VideoClip { MediaAssetId = assetB.Id, TimelineStart = F(100), Duration = F(100), SourceIn = F(0), SourceOut = F(100) });

        var baseline = FfmpegVideoFrameStream.LiveProcesses;
        var clock = new ManualClock();
        var service = new PlaybackService(new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance),
            clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { Hardware = HardwareDecoding.Disabled });
        service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(project, 1));

        foreach (var frame in new long[] { 10, 150, 30, 95, 120, 5, 99 })
            _ = service.SeekAsync(F(frame));
        await SettleAsync(service, 99); // at the boundary: current reader + prefetched next

        await WaitUntil(() => FfmpegVideoFrameStream.LiveProcesses - baseline <= 2);
        await service.DisposeAsync();
        await WaitUntil(() => FfmpegVideoFrameStream.LiveProcesses == baseline);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"live ffmpeg processes: {FfmpegVideoFrameStream.LiveProcesses}");
            await Task.Delay(10);
        }
    }

    private static async Task<PlaybackFrame> SettleAsync(PlaybackService service, long timelineFrame)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var frame = service.Update();
            if (frame.TimelineFrame == timelineFrame && !frame.IsBuffering && frame.IsPictureCurrent)
                return frame;
            if (watch.Elapsed > TimeSpan.FromSeconds(20))
                throw new TimeoutException($"Frame {timelineFrame} not ready: {frame}");
            await Task.Delay(1);
        }
    }
}
