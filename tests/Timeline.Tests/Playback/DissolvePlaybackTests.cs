using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>
/// Phase 10 Step 10.7 (D025 §3): in the Preview B's decoder opens ahead of the dissolve zone (its start is a picture
/// change for the prefetch), both clips are decoded in the zone — A past its end, B before its start, from the handles
/// — and both layers become current without a placeholder.
/// </summary>
public sealed class DissolvePlaybackTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _decoder = new();
    private readonly PlaybackService _service;
    private MediaAsset _asset = null!;
    private Clip _a = null!, _b = null!;

    public DissolvePlaybackTests() =>
        _service = new PlaybackService(_decoder, new FakeReferenceClock(), NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });

    public Task InitializeAsync()
    {
        _asset = _f.Video(10, Rate, "cut.mp4");
        _asset.Metadata!.AvgFrameRate = Rate;
        (_asset.Metadata.DisplayRotation, _asset.Metadata.DisplayWidth, _asset.Metadata.DisplayHeight) = (0, 1920, 1080);
        _decoder.Add(_asset.FilePath, new FakeSource(Rate, 250));
        Assert.True(_f.Service.AddClip(_asset.Id).Success);
        Assert.True(_f.Service.Split(F(125)).Success);
        (_a, _b) = (_f.V1.Clips[0], _f.V1.Clips[1]);
        Assert.True(_f.Service.AddTransition(_a.Id, _b.Id, F(80)).Success);        // zone [85, 165): longer than the 1 s prefetch window
        _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, 1));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _service.DisposeAsync();

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private async Task<PlaybackFrame> Until(Func<PlaybackFrame, bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var frame = _service.Update();
            if (condition(frame)) return frame;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(1);
        }
    }

    private static bool AllCurrent(PlaybackFrame f) =>
        !f.IsBuffering && f.Layers.Length > 0 && f.Layers.All(l => l.IsCurrent && l.State == LayerPictureState.Frame);

    [Fact]
    public async Task B_is_opened_before_the_zone_starts()
    {
        Assert.True(await _service.SeekAsync(F(70)));                               // 15 frames before the zone, 55 before the cut
        var frame = await Until(AllCurrent, "A");
        Assert.Equal(new[] { _a.Id }, frame.Layers.Select(l => l.Layer.ClipId));

        var watch = Stopwatch.StartNew();
        while (!_service.VideoReaderClipIds.Contains(_b.Id))                         // B's reader exists before B is shown
        {
            _service.Update();
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("B not prefetched");
            await Task.Delay(1);
        }
        Assert.Single(_service.Update().Layers);
    }

    [Fact]
    public async Task In_the_zone_both_clips_show_the_same_source_frame_from_their_handles()
    {
        foreach (var n in new long[] { 85, 124, 125, 164 })
        {
            Assert.True(await _service.SeekAsync(F(n)));
            var frame = await Until(f => AllCurrent(f) && f.Layers.Length == 2, $"both layers at {n}");

            Assert.Equal(new[] { _a.Id, _b.Id }, frame.Layers.Select(l => l.Layer.ClipId));
            Assert.Equal(n, FakeVideoDecoder.Number(frame.Layers[0].Frame!));        // A past its end (from 125): its handle
            Assert.Equal(n, FakeVideoDecoder.Number(frame.Layers[1].Frame!));        // B before its start (to 124): its handle
            Assert.Equal((n - 85 + 1) / 81.0, frame.Layers[1].Layer.Opacity);
        }
    }
}
