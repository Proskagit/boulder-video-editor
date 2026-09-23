using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests.Playback;

public sealed class PlaybackServiceTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _decoder = new();
    private readonly FakeReferenceClock _clock = new();
    private readonly PlaybackService _service;
    private long _version;

    public PlaybackServiceTests() =>
        _service = new PlaybackService(_decoder, _clock, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    /// <summary>Adds an analyzed 25 fps video of <paramref name="frames"/> frames, decodable by the fake.</summary>
    private MediaAsset Video(string name, int frames)
    {
        var asset = _f.Video(frames / 25.0, Rate, name);
        asset.Metadata!.AvgFrameRate = Rate;
        _decoder.Add(asset.FilePath, new FakeSource(Rate, frames));
        return asset;
    }

    private Clip AddClip(MediaAsset asset, long? startFrame = null)
    {
        var result = _f.Service.AddClip(asset.Id, _f.V1.Id, startFrame is { } s ? F(s) : null);
        Assert.True(result.Success, result.Message);
        return _f.V1.Clips.Single(c => c.Id == result.ClipIds[0]);
    }

    private void Publish() => _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, ++_version));

    /// <summary>Calls Update until the picture for the current frame is available (the fake clock does not move meanwhile).</summary>
    private async Task<PlaybackFrame> SettleAsync(Func<PlaybackFrame, bool>? until = null)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var frame = _service.Update();
            if (!frame.IsBuffering && frame.IsPictureCurrent && (until?.Invoke(frame) ?? true))
                return frame;
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"No settled picture; last: {frame}");
            await Task.Delay(1);
        }
    }

    private static int Number(PlaybackFrame frame)
    {
        Assert.Equal(PictureKind.Frame, frame.Picture!.Kind);
        return FakeVideoDecoder.Number(frame.Picture.Frame!);
    }

    // --- Basics ----------------------------------------------------------------------------------

    [Fact]
    public void WithoutSnapshot_UpdateIsBlack_AndPlayDoesNothing()
    {
        _service.Play();
        var frame = _service.Update();
        Assert.Equal((PlaybackState.Paused, PictureKind.Black), (frame.State, frame.Picture!.Kind));
    }

    [Fact]
    public async Task Seek_ShowsTheFrameForThePosition()
    {
        AddClip(Video("a.mp4", 250));
        Publish();

        Assert.True(await _service.SeekAsync(F(37)));
        var frame = await SettleAsync();

        Assert.Equal((F(37), 37L, 37), (frame.Position, frame.TimelineFrame, Number(frame)));
    }

    [Fact]
    public async Task Play_FollowsTheClock_Pause_FreezesIt()
    {
        AddClip(Video("a.mp4", 250));
        Publish();
        var states = new List<PlaybackState>();
        _service.StateChanged += (_, _) => states.Add(_service.State);

        _service.Play();
        _clock.Advance(1.0);
        var playing = await SettleAsync(f => Number(f) == 25);
        Assert.Equal((MediaTime.FromSeconds(1), 25L), (playing.Position, playing.TimelineFrame));

        _service.Pause();
        _clock.Advance(5.0);
        Assert.Equal(MediaTime.FromSeconds(1), _service.Update().Position);
        Assert.Equal(new[] { PlaybackState.Playing, PlaybackState.Paused }, states);
    }

    [Fact]
    public async Task End_PausesAtDuration_ShowingTheLastFrame_PlayRestarts_StopReturnsToZero()
    {
        AddClip(Video("a.mp4", 50));
        Publish();
        _service.Play();
        _clock.Advance(3.0);

        var end = await SettleAsync();
        Assert.Equal((PlaybackState.Paused, F(50), 49), (end.State, end.Position, Number(end)));

        _service.Play();
        Assert.Equal((PlaybackState.Playing, MediaTime.Zero), (_service.State, _service.Position));
        _clock.Advance(0.4);
        Assert.Equal(10, Number(await SettleAsync(f => Number(f) == 10)));

        _service.Stop();
        Assert.Equal((PlaybackState.Paused, MediaTime.Zero), (_service.State, _service.Position));
        Assert.Equal(0, Number(await SettleAsync(f => Number(f) == 0)));
    }

    // --- Seek semantics -----------------------------------------------------------------------------

    [Fact]
    public async Task SeekWhileDecoding_ClockKeepsRunning_LatencyIsNotAddedToPosition()
    {
        var asset = Video("a.mp4", 500);
        AddClip(asset);
        Publish();
        _service.Play();

        var gate = _decoder.Gate(asset.FilePath);
        var seek = _service.SeekAsync(F(100));
        _clock.Advance(0.5); // decoder is "slow"

        var buffering = _service.Update();
        Assert.True(buffering.IsBuffering);
        Assert.Null(buffering.Picture);
        Assert.Equal(F(100) + MediaTime.FromSeconds(0.5), buffering.Position);

        gate.SetResult();
        Assert.True(await seek);
        var frame = await SettleAsync();
        Assert.Equal(112, Number(frame)); // 100 + 12.5 frames → frame 112, no extra delay
        Assert.Equal(PlaybackState.Playing, frame.State);
    }

    [Fact]
    public async Task RapidSeeks_OnlyTheLastOneCompletesAsCurrent()
    {
        var asset = Video("a.mp4", 500);
        AddClip(asset);
        Publish();

        var gate = _decoder.Gate(asset.FilePath);
        var seeks = Enumerable.Range(1, 10).Select(i => _service.SeekAsync(F(i * 30))).ToList();
        gate.SetResult();
        var results = await Task.WhenAll(seeks);

        Assert.Equal(Enumerable.Repeat(false, 9).Append(true), results);
        Assert.Equal(300, Number(await SettleAsync()));
    }

    [Fact]
    public async Task ResultOfASupersededSnapshot_IsNotPublished()
    {
        var asset = Video("a.mp4", 500);
        var clip = AddClip(asset);
        Publish();                                    // snapshot 1

        var gate = _decoder.Gate(asset.FilePath);
        var oldSeek = _service.SeekAsync(F(40));      // pipeline A (snapshot 1)
        Assert.True(_f.Service.MoveClips(TimelineFixture.Ids(clip), 10).Success);
        Publish();                                    // snapshot 2 → pipeline B at the same position
        gate.SetResult();

        Assert.False(await oldSeek);
        Assert.Equal(30, Number(await SettleAsync())); // clip now starts at frame 10: frame 40 → source 30
    }

    [Fact]
    public async Task OlderSnapshotVersion_IsIgnored()
    {
        AddClip(Video("a.mp4", 100));
        var older = PlaybackSnapshotBuilder.Build(_f.Project, 5);
        _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, 6));
        _service.UpdateSnapshot(older);
        Assert.Equal(F(100), _service.Duration);
        Assert.Equal(0, Number(await SettleAsync()));
    }

    // --- Clip boundaries, gaps, images ----------------------------------------------------------------

    [Fact]
    public async Task ClipBoundary_SwitchesSource_AfterPrefetchingTheNextClip()
    {
        var a = Video("a.mp4", 50);
        var b = Video("b.mp4", 50);
        AddClip(a);
        AddClip(b); // frames 50..99
        Publish();
        await _service.SeekAsync(F(20));
        _service.Play();

        _clock.Advance(F(10)); // frame 30: boundary 20 frames (0.8 s) ahead → within the prefetch window
        Assert.Equal(30, Number(await SettleAsync(f => Number(f) == 30)));
        Assert.Equal(1, _decoder.OpenCount(b.FilePath));

        _clock.Advance(F(19)); // frame 49
        Assert.Equal(49, Number(await SettleAsync(f => f.TimelineFrame == 49 && Number(f) == 49)));
        _clock.Advance(F(1));  // frame 50 → first frame of b
        var next = await SettleAsync(f => f.TimelineFrame == 50);
        Assert.Equal(0, Number(next));
        Assert.Equal(1, _decoder.OpenCount(b.FilePath)); // the prefetched reader was used
    }

    [Fact]
    public async Task Gap_IsBlack()
    {
        var a = Video("a.mp4", 25);
        AddClip(a);
        AddClip(Video("b.mp4", 25), startFrame: 50);
        Publish();

        await _service.SeekAsync(F(30));
        Assert.Equal(PictureKind.Black, (await SettleAsync()).Picture!.Kind);
    }

    [Fact]
    public async Task StillImage_IsDecodedOnce_AndHeldForTheWholeSpan()
    {
        var image = _f.Image("logo.png");
        _decoder.Add(image.FilePath, new FakeSource(Rate, 1));
        Assert.True(_f.Service.AddClip(image.Id).Success); // 5 s
        Publish();

        _service.Play();
        for (var i = 0; i < 8; i++)
        {
            _clock.Advance(0.5);
            Assert.Equal(0, Number(await SettleAsync()));
        }
        Assert.Equal(1, _decoder.OpenCount(image.FilePath));
    }

    // --- Unavailable media and decoder failures -------------------------------------------------------

    [Fact]
    public async Task Offline_Unsupported_DecodeError_AreDistinct_AndPlaybackContinues()
    {
        var missing = Video("missing.mp4", 25);
        var fast = Video("fast.mp4", 25);
        var broken = Video("broken.mp4", 25);
        var gone = Video("gone.mp4", 25);
        var ok = Video("ok.mp4", 25);
        AddClip(missing);
        var fastClip = (MediaBackedClip)AddClip(fast);
        AddClip(broken);
        AddClip(gone);
        AddClip(ok);
        missing.IsMissing = true;
        fastClip.Speed = 2.0;
        _decoder.FailOpen(broken.FilePath, VideoDecodeError.DecoderFailed);
        _decoder.FailOpen(gone.FilePath, VideoDecodeError.FileNotFound);
        Publish();

        async Task<PreviewPicture> At(long frame)
        {
            await _service.SeekAsync(F(frame));
            return (await SettleAsync()).Picture!;
        }

        Assert.Equal(PictureKind.Offline, (await At(5)).Kind);
        Assert.Equal(PictureKind.Unsupported, (await At(30)).Kind);
        var error = await At(55);
        Assert.Equal(PictureKind.DecodeError, error.Kind);
        Assert.Contains("fake", error.Message);
        Assert.Equal(PictureKind.Offline, (await At(80)).Kind); // file vanished at decode time
        var after = await At(105); // the clip after the failures plays normally
        Assert.Equal((PictureKind.Frame, 5), (after.Kind, FakeVideoDecoder.Number(after.Frame!)));
        Assert.True(_service.IsAvailable);
    }

    [Fact]
    public async Task MissingDecoderBackend_MakesPlaybackUnavailable()
    {
        var a = Video("a.mp4", 25);
        AddClip(a);
        _decoder.FailOpen(a.FilePath, VideoDecodeError.DecoderUnavailable);
        Publish();

        await _service.SeekAsync(F(3));
        Assert.Equal(PictureKind.DecodeError, (await SettleAsync()).Picture!.Kind);
        Assert.False(_service.IsAvailable);
    }

    [Fact]
    public async Task HardwareFailureMidStream_ReopensInSoftware()
    {
        var a = Video("a.mp4", 250);
        AddClip(a);
        _decoder.FailAfter(a.FilePath, 6); // only for HardwareDecoding.Auto
        Publish();
        _service.Play();

        for (var frame = 1; frame <= 40; frame++)
        {
            _clock.Advance(F(1));
            Assert.Equal(frame, Number(await SettleAsync(f => f.TimelineFrame == frame)));
        }
        Assert.Contains(_decoder.Requests, r => r.FilePath == a.FilePath && r.Hardware == HardwareDecoding.Disabled);
    }

    [Fact]
    public async Task HardwareFailureMidStream_KeepsDecodedFrames_ContinuesSameSpan_SameSeekGeneration()
    {
        // HW delivers source frames 0..5 and fails on the 7th read, which happens right after
        // frame 3 is shown (buffer of 4). The software reopen is held on a gate, so what is
        // shown meanwhile can only come from frames decoded before the failure.
        var a = Video("a.mp4", 250);
        AddClip(a);
        _decoder.FailAfter(a.FilePath, 6);
        var softwareGate = _decoder.GateSoftware(a.FilePath);
        Publish();
        _service.Play();
        var generation = _service.SeekGeneration;

        for (var frame = 1; frame <= 3; frame++)
        {
            _clock.Advance(F(1));
            Assert.Equal(frame, Number(await SettleAsync(f => f.TimelineFrame == frame)));
        }
        await UntilAsync(() => _decoder.Requests.Any(r => r.Hardware == HardwareDecoding.Disabled));

        // Reopened in software at the frame playback last asked for — not at the clip start.
        var reopen = Assert.Single(_decoder.Requests, r => r.Hardware == HardwareDecoding.Disabled);
        var clipStart = _f.V1.Clips[0];
        Assert.Equal(SourceFrameSelector.SamplePoint(clipStart.TimelineStart, MediaTime.Zero, 3, Rate, Rate), reopen.FirstSamplePoint);

        // Frame 4 is certain from the frames HW already decoded (4 and 5): shown at once.
        _clock.Advance(F(1));
        var fourth = _service.Update();
        Assert.True(fourth.IsPictureCurrent, "frames decoded before the failure must survive the fallback");
        Assert.Equal((4L, 4), (fourth.TimelineFrame, Number(fourth)));

        // Frame 5 needs the next frame to be certain: late (never wrong) until software resumes.
        _clock.Advance(F(1));
        var fifth = _service.Update();
        Assert.False(fifth.IsPictureCurrent);
        Assert.Equal(4, FakeVideoDecoder.Number(fifth.Picture!.Frame!));

        softwareGate.SetResult();
        for (var frame = 5; frame <= 40; frame++)
        {
            if (frame > 5) _clock.Advance(F(1));
            var shown = await SettleAsync(f => f.TimelineFrame == frame);
            Assert.Equal(frame, Number(shown)); // no frame repeated, skipped or re-sought
        }

        Assert.Equal(generation, _service.SeekGeneration); // recovery is not a new seek
        Assert.Equal(PlaybackState.Playing, _service.State);
        Assert.Equal(2, _decoder.OpenCount(a.FilePath));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException();
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task LowerClip_ResumesAfterTheTopClip_AtItsOwnSourceFrame()
    {
        // V1: one 250-frame clip; V2 (on top): a 25-frame clip at 50–75. The lower clip is not
        // split: after frame 75 it shows its own source frame 75 again.
        var low = Video("low.mp4", 250);
        var top = Video("top.mp4", 25);
        AddClip(low);
        Assert.True(_f.Service.AddTrack(TrackType.Video).Success);
        var v2 = _f.Project.Timeline.VideoTracks.Single(t => t.Id != _f.V1.Id);
        Assert.True(_f.Service.AddClip(top.Id, v2.Id, F(50)).Success);
        Publish();

        await _service.SeekAsync(F(45));
        _service.Play();
        var shown = new List<string>();
        for (var frame = 45; frame <= 80; frame++)
        {
            if (frame > 45) _clock.Advance(F(1));
            var picture = (await SettleAsync(f => f.TimelineFrame == frame)).Picture!;
            shown.Add($"{(picture.ClipId == _f.V1.Clips[0].Id ? "low" : "top")}{FakeVideoDecoder.Number(picture.Frame!)}");
        }

        var expected = Enumerable.Range(45, 36).Select(n => n is >= 50 and < 75 ? $"top{n - 50}" : $"low{n}");
        Assert.Equal(expected, shown);
    }

    [Fact]
    public async Task SupersededPipelines_ReleaseTheirDecoderStreams()
    {
        var a = Video("a.mp4", 100);
        var b = Video("b.mp4", 100);
        AddClip(a);
        AddClip(b);
        Publish();

        for (var i = 0; i < 10; i++)
            _ = _service.SeekAsync(F(i * 19));
        Publish(); // a newer snapshot supersedes the last seek's pipeline too
        await _service.SeekAsync(F(90)); // near the a|b boundary: current + prefetched reader
        await SettleAsync();

        // Only the current pipeline's readers may keep streams open.
        await UntilAsync(() => _decoder.LiveStreams <= 2);
        await _service.DisposeAsync();
        await UntilAsync(() => _decoder.LiveStreams == 0);
    }

    [Fact]
    public async Task UpdateSnapshotWhilePlaying_DoesNotReanchorTheClock()
    {
        // A reference that moves on every read exposes any re-anchoring: each one would drop
        // the time between reading the position and taking the new anchor.
        var reference = new AutoAdvancingReferenceClock();
        await using var service = new PlaybackService(_decoder, reference, NullLogger<PlaybackService>.Instance);
        AddClip(Video("a.mp4", 250));
        service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, 1));

        var readsBeforePlay = reference.Reads.Count;
        service.Play();
        var anchor = reference.Reads[readsBeforePlay];
        for (var version = 2; version <= 51; version++)
            service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, version));

        var position = service.Position;
        Assert.Equal(reference.Reads[^1] - anchor, position.Ticks);
        Assert.Equal(PlaybackState.Playing, service.State);
    }

    // --- Timeline changes during playback -----------------------------------------------------------------

    [Fact]
    public async Task UpdateSnapshotWhilePlaying_KeepsPlaying_AtTheSamePosition()
    {
        var a = Video("a.mp4", 250);
        var clip = AddClip(a);
        Publish();
        _service.Play();
        _clock.Advance(1.0);
        Assert.Equal(25, Number(await SettleAsync(f => Number(f) == 25)));

        Assert.True(_f.Service.MoveClips(TimelineFixture.Ids(clip), 10).Success);
        Publish();

        Assert.Equal((PlaybackState.Playing, MediaTime.FromSeconds(1)), (_service.State, _service.Position));
        Assert.Equal(15, Number(await SettleAsync(f => Number(f) == 15))); // same time, moved clip

        _f.UndoRedo.Undo();
        Publish();
        Assert.Equal(25, Number(await SettleAsync(f => Number(f) == 25)));

        _f.UndoRedo.Redo();
        Publish();
        _clock.Advance(F(5));
        Assert.Equal(20, Number(await SettleAsync(f => f.TimelineFrame == 30)));
        Assert.Equal(PlaybackState.Playing, _service.State);
    }

    [Fact]
    public async Task ShorteningTheTimeline_ClampsThePosition()
    {
        var a = Video("a.mp4", 250);
        var clip = AddClip(a);
        Publish();
        await _service.SeekAsync(F(200));

        Assert.True(_f.Service.TrimClip(clip.Id, ClipEdge.End, F(100)).Success);
        Publish();

        Assert.Equal(F(100), _service.Position);
        Assert.Equal(99, Number(await SettleAsync()));
    }
}
