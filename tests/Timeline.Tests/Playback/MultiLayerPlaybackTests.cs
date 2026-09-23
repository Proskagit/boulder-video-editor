using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>
/// Multi-layer playback (Phase 7 Step 6): every visible layer is decoded and reported bottom to top
/// with its own state; layers under an opaque full-canvas video get no reader; presentation changes
/// open/close readers without a new seek generation or buffering; placeholders never cull.
/// </summary>
public sealed class MultiLayerPlaybackTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _service;
    private long _version;

    public MultiLayerPlaybackTests() =>
        _service = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    /// <summary>A 25 fps, 1920 × 1080 (display) video of <paramref name="frames"/> frames on <paramref name="track"/>.</summary>
    private (Clip Clip, MediaAsset Asset) Video(Track track, string name, int frames = 250, long start = 0, bool displaySize = true)
    {
        var asset = _f.Video(frames / 25.0, Rate, name);
        asset.Metadata!.AvgFrameRate = Rate;
        if (displaySize) (asset.Metadata.DisplayRotation, asset.Metadata.DisplayWidth, asset.Metadata.DisplayHeight) = (0, 1920, 1080);
        _decoder.Add(asset.FilePath, new FakeSource(Rate, frames));
        var result = _f.Service.AddClip(asset.Id, track.Id, F(start));
        Assert.True(result.Success, result.Message);
        return (track.Clips.Single(c => c.Id == result.ClipIds[0]), asset);
    }

    private Track V2()
    {
        if (_f.Project.Timeline.VideoTracks.Count < 2) Assert.True(_f.Service.AddTrack(TrackType.Video).Success);
        return _f.Project.Timeline.VideoTracks[1];
    }

    private void Publish() => _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, ++_version));

    private void SetOpacity(Clip clip, double opacity)
    {
        Assert.True(_f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Visual = VisualProperties.Of(clip)!.Value with { Opacity = opacity }
        }).Success);
        Publish();
    }

    private async Task<PlaybackFrame> Until(Func<PlaybackFrame, bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var frame = _service.Update();
            if (condition(frame)) return frame;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException($"{what}; last: {Describe(frame)}");
            await Task.Delay(1);
        }
    }

    private static bool AllCurrent(PlaybackFrame f) =>
        !f.IsBuffering && f.Layers.Length > 0 && f.Layers.All(l => l.IsCurrent && l.State != LayerPictureState.Pending);

    private static string Describe(PlaybackFrame f) =>
        string.Join(", ", f.Layers.Select(l => $"{l.State}{(l.IsCurrent ? "" : "(late)")}")) + $" buffering={f.IsBuffering}";

    private static int Number(LayerPicture layer) => FakeVideoDecoder.Number(layer.Frame!);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(what);
            await Task.Delay(1);
        }
    }

    // --- Layers and their order -------------------------------------------------------------------

    [Fact]
    public async Task Visible_layers_are_all_decoded_and_reported_bottom_to_top()
    {
        var (bottom, _) = Video(_f.V1, "bottom.mp4");
        var (top, _) = Video(V2(), "top.mp4");
        Assert.True(_f.Service.SetClipProperties(top.Id, new ClipPropertyChange { Visual = VisualProperties.Default with { Opacity = 0.5 } }).Success);
        Publish();

        Assert.True(await _service.SeekAsync(F(10)));
        var frame = await Until(AllCurrent, "both layers");

        Assert.Equal(new[] { bottom.Id, top.Id }, frame.Layers.Select(l => l.Layer.ClipId));
        Assert.All(frame.Layers, l => Assert.Equal((LayerPictureState.Frame, 10), (l.State, Number(l))));
        Assert.Equal(0.5, frame.Layers[1].Layer.Opacity);
        Assert.Equal(new FrameSize(1920, 1080), frame.Canvas);
        Assert.Equal(top.Id, frame.Picture!.ClipId);             // compatibility picture: the topmost layer
        Assert.True(frame.IsPictureCurrent);
        Assert.Equal(2, _service.VideoReaderCount);
    }

    [Fact]
    public async Task Layer_under_an_opaque_full_canvas_video_gets_no_reader()
    {
        var (_, bottomAsset) = Video(_f.V1, "bottom.mp4");
        var (top, _) = Video(V2(), "top.mp4");
        Publish();

        Assert.True(await _service.SeekAsync(F(10)));
        var frame = await Until(AllCurrent, "top layer");

        Assert.Equal(new[] { top.Id }, frame.Layers.Select(l => l.Layer.ClipId));
        Assert.Equal(new[] { top.Id }, _service.VideoReaderClipIds);
        Assert.Equal(0, _decoder.OpenCount(bottomAsset.FilePath));
    }

    [Fact]
    public async Task Text_layers_are_reported_without_decoding()
    {
        var (clip, _) = Video(_f.V1, "a.mp4");
        var text = new TextClip { Text = "Title", TimelineStart = F(0), Duration = F(50) };
        V2().Clips.Add(text);
        Publish();

        var frame = await Until(AllCurrent, "layers");

        Assert.Equal(new[] { clip.Id, text.Id }, frame.Layers.Select(l => l.Layer.ClipId));
        Assert.Equal(LayerPictureState.Text, frame.Layers[1].State);
        Assert.Equal(clip.Id, frame.Picture!.ClipId); // text is not the compatibility picture
        Assert.Equal(1, _service.VideoReaderCount);
    }

    // --- Presentation changes: no seek, no buffering ---------------------------------------------------

    [Fact]
    public async Task Opacity_1_to_09_while_playing_opens_the_lower_reader_without_a_seek_or_buffering()
    {
        var (bottom, bottomAsset) = Video(_f.V1, "bottom.mp4");
        var (top, _) = Video(V2(), "top.mp4");
        Publish();
        _service.Play();
        await Until(AllCurrent, "playing");
        var pipeline = _service.VideoPipelineInstance;
        var generation = _service.SeekGeneration;
        Assert.Equal(0, _decoder.OpenCount(bottomAsset.FilePath));

        var gate = _decoder.Gate(bottomAsset.FilePath);   // the new layer's decoder is slow
        SetOpacity(top, 0.9);
        var frame = _service.Update();

        Assert.Same(pipeline, _service.VideoPipelineInstance);
        Assert.Equal(generation, _service.SeekGeneration);
        Assert.False(frame.IsBuffering);
        Assert.Equal(new[] { bottom.Id, top.Id }, frame.Layers.Select(l => l.Layer.ClipId));
        Assert.Equal(LayerPictureState.Pending, frame.Layers[0].State);  // not drawn until it has a frame
        Assert.Equal(LayerPictureState.Frame, frame.Layers[1].State);
        Assert.Equal(0.9, frame.Layers[1].Layer.Opacity);
        Assert.Equal(top.Id, frame.Picture!.ClipId);
        await Eventually(() => _decoder.OpenCount(bottomAsset.FilePath) == 1, "lower reader not opened");

        gate.SetResult();
        _clock.Advance(0.2);
        frame = await Until(AllCurrent, "lower layer ready");
        Assert.Equal(LayerPictureState.Frame, frame.Layers[0].State);
        Assert.Equal(Number(frame.Layers[1]), Number(frame.Layers[0]));   // both at the same timeline frame
        Assert.Same(pipeline, _service.VideoPipelineInstance);
        Assert.Equal(generation, _service.SeekGeneration);
    }

    [Fact]
    public async Task Opacity_back_to_1_closes_the_lower_reader_and_its_stream()
    {
        var (_, bottomAsset) = Video(_f.V1, "bottom.mp4");
        var (top, _) = Video(V2(), "top.mp4");
        SetOpacity(top, 0.5);
        await Until(f => AllCurrent(f) && f.Layers.Length == 2, "two layers");
        Assert.Equal(2, _decoder.LiveStreams);
        var generation = _service.SeekGeneration;

        SetOpacity(top, 1);
        var frame = await Until(AllCurrent, "top only");

        Assert.Equal(new[] { top.Id }, frame.Layers.Select(l => l.Layer.ClipId));
        Assert.Equal(new[] { top.Id }, _service.VideoReaderClipIds);
        Assert.Equal(generation, _service.SeekGeneration);
        await Eventually(() => _decoder.LiveStreams == 1, "lower stream left open");
        Assert.Equal(1, _decoder.OpenCount(bottomAsset.FilePath));
    }

    [Fact]
    public async Task Many_presentation_toggles_leave_no_reader_or_stream_behind()
    {
        Video(_f.V1, "bottom.mp4");
        var (top, _) = Video(V2(), "top.mp4");
        Publish();
        _service.Play();
        for (var i = 0; i < 20; i++)
        {
            SetOpacity(top, i % 2 == 0 ? 0.5 : 1);
            _clock.Advance(0.04);
            await Until(AllCurrent, $"toggle {i}");
        }

        await Eventually(() => _decoder.LiveStreams == _service.VideoReaderCount, "streams leaked");
        await _service.DisposeAsync();
        Assert.Equal(0, _decoder.LiveStreams);
    }

    [Fact]
    public async Task A_layer_whose_decoder_falls_behind_is_late_on_its_own()
    {
        var (bottom, bottomAsset) = Video(_f.V1, "bottom.mp4");
        var (top, _) = Video(V2(), "top.mp4");
        var release = _decoder.HoldAfter(bottomAsset.FilePath, frames: 3); // the lower decoder stalls after frames 0..2
        SetOpacity(top, 0.5);
        await Until(AllCurrent, "both at frame 0");
        _service.Play();

        _clock.Advance(0.2); // frame 5: the top layer keeps up, the bottom one has nothing after frame 2
        var frame = await Until(f => f.Layers.Length == 2 && f.Layers[1].IsCurrent && Number(f.Layers[1]) == 5, "top at frame 5");

        var late = frame.Layers[0];
        Assert.Equal(bottom.Id, late.Layer.ClipId);
        Assert.Equal(LayerPictureState.Frame, late.State);   // keeps showing its last frame…
        Assert.False(late.IsCurrent);                         // …flagged late (D012), per layer
        Assert.True(Number(late) < 5);
        Assert.True(frame.IsPictureCurrent);                 // the compatibility picture (top) is current
        Assert.False(frame.IsBuffering);

        release.SetResult();
        frame = await Until(AllCurrent, "caught up");
        Assert.Equal(Number(frame.Layers[1]), Number(frame.Layers[0]));
    }

    // --- Seek ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("bottom")]
    [InlineData("top")]
    public async Task Seek_is_ready_only_when_every_visible_layer_is(string slowLayer)
    {
        var (_, lowerAsset) = Video(_f.V1, "bottom.mp4");
        var (top, topAsset) = Video(V2(), "top.mp4");
        var slowAsset = slowLayer == "bottom" ? lowerAsset : topAsset;
        Assert.True(_f.Service.SetClipProperties(top.Id, new ClipPropertyChange { Visual = VisualProperties.Default with { Scale = 0.5 } }).Success);
        Publish();
        await Until(AllCurrent, "initial");

        var gate = _decoder.Gate(slowAsset.FilePath);
        var seek = _service.SeekAsync(F(100));
        await Task.Delay(50);
        Assert.False(seek.IsCompleted);
        Assert.True(_service.Update().IsBuffering);

        gate.SetResult();
        Assert.True(await seek);
        var frame = await Until(AllCurrent, "after seek");
        Assert.All(frame.Layers, l => Assert.Equal(100, Number(l)));
    }

    // --- Placeholders ------------------------------------------------------------------------------------

    [Fact]
    public async Task Offline_and_unsupported_layers_are_placeholders_with_the_clip_geometry_and_never_cull()
    {
        var (bottom, _) = Video(_f.V1, "bottom.mp4");
        var (top, topAsset) = Video(V2(), "top.mp4");
        Assert.True(_f.Service.SetClipProperties(top.Id, new ClipPropertyChange
        {
            Visual = VisualProperties.Default with { Scale = 0.5, PositionX = 100, RotationDegrees = 90 }
        }).Success);
        topAsset.IsMissing = true;
        Publish();

        var frame = await Until(AllCurrent, "layers");

        Assert.Equal(new[] { bottom.Id, top.Id }, frame.Layers.Select(l => l.Layer.ClipId));
        var placeholder = frame.Layers[1];
        Assert.Equal(LayerPictureState.Offline, placeholder.State);
        Assert.True(placeholder.IsPlaceholder);
        var geometry = ((PictureLayer)placeholder.Layer).Geometry!;
        Assert.Equal((geometry.Transform, 1920.0, 1080.0), placeholder.PlaceholderArea(frame.Canvas));
        Assert.Equal(new PointD(1060, 540), geometry.Transform.Apply(new PointD(960, 540))); // position/scale/rotation apply
        Assert.Equal(PictureKind.Offline, frame.Picture!.Kind);

        ((VideoClip)top).Speed = 2; // unsupported (can't be set through the edit service yet)
        topAsset.IsMissing = false;
        Publish();
        frame = await Until(f => AllCurrent(f) && f.Layers[^1].State == LayerPictureState.Unsupported, "unsupported");
        Assert.Equal(bottom.Id, frame.Layers[0].Layer.ClipId); // a full-canvas placeholder still doesn't cull
    }

    [Fact]
    public async Task Placeholder_of_unknown_size_covers_the_whole_canvas()
    {
        Video(_f.V1, "bottom.mp4");
        var (top, topAsset) = Video(V2(), "top.mp4", displaySize: false);
        topAsset.IsMissing = true;
        Publish();

        var frame = await Until(AllCurrent, "layers");

        Assert.Equal(top.Id, frame.Layers[^1].Layer.ClipId);
        Assert.Equal((Affine2D.Identity, 1920.0, 1080.0), frame.Layers[^1].PlaceholderArea(frame.Canvas));
        Assert.Equal(2, frame.Layers.Length);
    }

    [Fact]
    public async Task Occluding_video_that_fails_to_decode_uncovers_the_layers_below()
    {
        var (bottom, bottomAsset) = Video(_f.V1, "bottom.mp4");
        var (top, topAsset) = Video(V2(), "top.mp4");
        _decoder.FailOpen(topAsset.FilePath, VideoDecodeError.DecoderFailed);
        Publish();

        var frame = await Until(f => AllCurrent(f) && f.Layers.Length == 2, "lower layer after the failure");

        Assert.Equal(new[] { bottom.Id, top.Id }, frame.Layers.Select(l => l.Layer.ClipId));
        Assert.Equal(LayerPictureState.Frame, frame.Layers[0].State);
        Assert.Equal(LayerPictureState.DecodeError, frame.Layers[1].State);
        Assert.Equal(PictureKind.DecodeError, frame.Picture!.Kind);
        Assert.Equal(1, _decoder.OpenCount(bottomAsset.FilePath));
    }

    [Fact]
    public async Task File_vanishing_at_decode_time_is_an_offline_placeholder()
    {
        Video(_f.V1, "bottom.mp4");
        var (_, topAsset) = Video(V2(), "top.mp4");
        _decoder.FailOpen(topAsset.FilePath, VideoDecodeError.FileNotFound);
        Publish();

        var frame = await Until(f => AllCurrent(f) && f.Layers.Length == 2, "layers");

        Assert.Equal(LayerPictureState.Offline, frame.Layers[1].State);
        Assert.Equal(LayerPictureState.Frame, frame.Layers[0].State);
    }

    // --- Prefetch and clean-up ----------------------------------------------------------------------------

    [Fact]
    public async Task Layer_appearing_at_the_next_edge_is_prefetched()
    {
        Video(_f.V1, "bottom.mp4");
        var (_, laterAsset) = Video(V2(), "later.mp4", frames: 100, start: 12);
        Assert.True(_f.Service.SetClipProperties(V2().Clips[0].Id, new ClipPropertyChange { Visual = VisualProperties.Default with { Scale = 0.5 } }).Success);
        Publish();

        await Until(AllCurrent, "initial");
        await Eventually(() => { _service.Update(); return _decoder.OpenCount(laterAsset.FilePath) == 1; }, "not prefetched");

        Assert.Single(_service.Update().Layers); // the reader exists before its layer is visible
    }

    [Fact]
    public async Task Dispose_releases_every_layer_reader()
    {
        Video(_f.V1, "a.mp4");
        var (b, _) = Video(V2(), "b.mp4");
        SetOpacity(b, 0.5);
        await Until(f => AllCurrent(f) && f.Layers.Length == 2, "two layers");

        await _service.DisposeAsync();

        Assert.Equal(0, _decoder.LiveStreams);
    }
}
