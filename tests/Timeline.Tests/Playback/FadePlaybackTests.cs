using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>
/// Phase 10 Step 10.4 (D025 §2): in the Preview a fading layer shows its fade and stops hiding the layers below; a
/// lower layer that a fade out uncovers is opened ahead (prefetch), like one appearing at a clip edge; a fade change
/// keeps every decoder.
/// </summary>
public sealed class FadePlaybackTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _service;
    private long _version;

    public FadePlaybackTests() =>
        _service = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private (Clip Clip, MediaAsset Asset) Video(Track track, string name, int frames = 250)
    {
        var asset = _f.Video(frames / 25.0, Rate, name);
        asset.Metadata!.AvgFrameRate = Rate;
        (asset.Metadata.DisplayRotation, asset.Metadata.DisplayWidth, asset.Metadata.DisplayHeight) = (0, 1920, 1080);
        _decoder.Add(asset.FilePath, new FakeSource(Rate, frames));
        var result = _f.Service.AddClip(asset.Id, track.Id, F(0));
        Assert.True(result.Success, result.Message);
        return (track.Clips.Single(c => c.Id == result.ClipIds[0]), asset);
    }

    private Track V2()
    {
        if (_f.Project.Timeline.VideoTracks.Count < 2) Assert.True(_f.Service.AddTrack(TrackType.Video).Success);
        return _f.Project.Timeline.VideoTracks[1];
    }

    private void Publish() => _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, ++_version));

    private void SetFades(Clip clip, long fadeIn, long fadeOut)
    {
        Assert.True(_f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Fade = new FadeProperties(F(fadeIn), F(fadeOut)) }).Success);
        Publish();
    }

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
        !f.IsBuffering && f.Layers.Length > 0 && f.Layers.All(l => l.IsCurrent && l.State != LayerPictureState.Pending);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task A_layer_that_a_fade_out_uncovers_is_prefetched_before_the_ramp()
    {
        var (_, bottomAsset) = Video(_f.V1, "bottom.mp4");
        var (top, _) = Video(V2(), "top.mp4");
        SetFades(top, 0, 10);                                   // the ramp starts at frame 240

        Assert.True(await _service.SeekAsync(F(225)));          // 15 frames before it: inside the 1 s prefetch window
        var frame = await Until(AllCurrent, "top layer");
        Assert.Equal(new[] { top.Id }, frame.Layers.Select(l => l.Layer.ClipId));   // still occluded here

        await Eventually(() => { _service.Update(); return _decoder.OpenCount(bottomAsset.FilePath) == 1; }, "not prefetched");
    }

    [Fact]
    public async Task In_the_ramp_both_layers_are_shown_with_the_fade_on_the_top_one()
    {
        var (bottom, _) = Video(_f.V1, "bottom.mp4");
        var (top, _) = Video(V2(), "top.mp4");
        SetFades(top, 0, 10);

        Assert.True(await _service.SeekAsync(F(245)));          // ramp step 5 of 10 from the end: (249 − 245 + 1) / 11
        var frame = await Until(f => AllCurrent(f) && f.Layers.Length == 2, "both layers");

        Assert.Equal(new[] { bottom.Id, top.Id }, frame.Layers.Select(l => l.Layer.ClipId));
        Assert.Equal(5.0 / 11, frame.Layers[1].Layer.Opacity);
        Assert.Equal(1.0, frame.Layers[0].Layer.Opacity);
    }

    [Fact]
    public async Task A_fade_change_keeps_the_decoders()
    {
        var (top, topAsset) = Video(_f.V1, "top.mp4");
        Publish();
        Assert.True(await _service.SeekAsync(F(100)));
        await Until(AllCurrent, "initial");
        var opened = _decoder.OpenCount(topAsset.FilePath);

        SetFades(top, 30, 30);
        await Until(AllCurrent, "after the fade change");

        Assert.Equal(opened, _decoder.OpenCount(topAsset.FilePath));
    }
}
