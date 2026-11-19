using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>
/// D071 §2, §4 (amendment 3: no proxies): the Preview decodes each asset at the quality's bound
/// (<see cref="PlaybackAsset.DecodeBound"/>) — a large one over the high-throughput transport; without one, the playback's own
/// bound; a new bound reopens the asset at the same frame.
/// </summary>
public sealed class PreviewBoundPlaybackTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _service;
    private long _version;

    public PreviewBoundPlaybackTests() =>
        _service = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private MediaAsset Video(string name, int frames)
    {
        var asset = _f.Video(frames / 25.0, Rate, name);
        asset.Metadata!.AvgFrameRate = Rate;
        _decoder.Add(asset.FilePath, new FakeSource(Rate, frames));
        var result = _f.Service.AddClip(asset.Id, _f.V1.Id, null);
        Assert.True(result.Success, result.Message);
        return asset;
    }

    private void Publish(Func<PlaybackAsset, PlaybackAsset> decoding) =>
        _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, ++_version).WithAssets(decoding));

    private async Task<int> SettledNumberAsync()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var frame = _service.Update();
            if (!frame.IsBuffering && frame.IsTopCurrent() && frame.TopPicture() is { State: LayerPictureState.Frame } top)
                return FakeVideoDecoder.Number(top.Frame!);
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException($"No settled picture; last: {frame}");
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task The_original_of_a_4K_source_decodes_unbounded_over_the_high_throughput_transport()
    {
        var asset = Video("clip.mp4", 50);
        Publish(a => a with { DecodeBound = new FrameSize(3840, 2160) });

        await _service.SeekAsync(MediaTime.FromFrame(10, Rate));
        Assert.Equal(10, await SettledNumberAsync());

        var request = _decoder.Requests.Last();
        Assert.Equal(asset.FilePath, request.FilePath);
        Assert.Equal((3840, 2160), (request.MaxWidth, request.MaxHeight));
        Assert.Equal(DecodeTransport.HighThroughput, request.Transport);
    }

    [Fact]
    public async Task Without_a_decoding_the_playback_keeps_its_own_bound_and_the_source()
    {
        var asset = Video("clip.mp4", 50);
        Publish(a => a);

        await _service.SeekAsync(MediaTime.FromFrame(5, Rate));
        Assert.Equal(5, await SettledNumberAsync());

        var request = _decoder.Requests.Last();
        Assert.Equal(asset.FilePath, request.FilePath);
        Assert.Equal((1280, 720), (request.MaxWidth, request.MaxHeight));
        Assert.Equal(DecodeTransport.Standard, request.Transport);
    }


    [Fact]
    public async Task A_new_quality_reopens_the_asset_at_the_same_frame_with_its_bound()
    {
        Video("clip.mp4", 100);
        Publish(a => a with { DecodeBound = new FrameSize(1920, 1080) });
        await _service.SeekAsync(MediaTime.FromFrame(20, Rate));
        Assert.Equal(20, await SettledNumberAsync());

        Publish(a => a with { DecodeBound = new FrameSize(1280, 720) });          // «Минимум» chosen: on the fly
        Assert.Equal(20, await SettledNumberAsync());
        Assert.Equal((1280, 720), (_decoder.Requests.Last().MaxWidth, _decoder.Requests.Last().MaxHeight));
        Assert.Equal(MediaTime.FromFrame(20, Rate), _service.Position);
    }
}
