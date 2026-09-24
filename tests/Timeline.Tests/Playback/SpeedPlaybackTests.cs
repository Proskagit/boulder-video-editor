using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>
/// Phase 7 Step 9 (D022): playback of clips at other speeds with fake decoders — which source frame
/// each timeline frame shows, and which source sample each timeline sample plays (the fake audio
/// decoder models an ideal tempo-changed stream: output sample j is source sample first + ⌊j·s⌋).
/// </summary>
public sealed class SpeedPlaybackTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _video = new();
    private readonly FakeAudioDecoder _audio = new();
    private readonly FakeAudioOutput _output = new();
    private readonly FakeReferenceClock _stopwatch = new();
    private readonly PlaybackService _service;
    private long _version;

    public SpeedPlaybackTests() =>
        _service = new PlaybackService(_video, _stopwatch, NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 8 }, _audio, _output);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);
    private static long Samples(double seconds) => (long)Math.Round(seconds * AudioFormat.SampleRate);
    private static ClipSpeed X(decimal value) => ClipSpeed.TryFromDecimal(value, out var s) ? s : throw new ArgumentException();

    /// <summary>A 25 fps, 20 s video with sound at the start of V1, set to <paramref name="speed"/>.</summary>
    private VideoClip Clip(decimal speed)
    {
        var asset = _f.Video(20, Rate, "v.mp4");
        asset.Metadata!.AudioCodec = "aac";
        asset.Metadata.AvgFrameRate = Rate;
        _video.Add(asset.FilePath, new FakeSource(Rate, 500));
        _audio.Add(asset.FilePath, new FakeAudioSource(Samples(20)));
        var result = _f.Service.AddClip(asset.Id);
        Assert.True(result.Success, result.Message);
        var clip = (VideoClip)_f.V1.Clips.Single();
        if (speed != 1m) Assert.True(_f.Service.SetClipSpeed(clip.Id, X(speed)).Success);
        Publish();
        return clip;
    }

    private void Publish() => _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, ++_version));

    private async Task<int> FrameAt(long timelineFrame)
    {
        await _service.SeekAsync(F(timelineFrame));
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var frame = _service.Update();
            if (!frame.IsBuffering && frame.IsPictureCurrent && frame.Picture is { Kind: PictureKind.Frame } picture)
                return FakeVideoDecoder.Number(picture.Frame!);
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException($"frame {timelineFrame}: {frame}");
            await Task.Delay(1);
        }
    }

    // --- Video ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.35)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public async Task Each_timeline_frame_shows_the_source_frame_of_its_sample_point(double speedValue)
    {
        var speed = (decimal)speedValue;
        var clip = Clip(speed);
        var frames = clip.TimelineEnd.ToFrameFloor(Rate);

        // Equal rates: t(n) = n·s frames, δ = ½·min(s, 1) frame → source frame ⌊n·s + ½·min(s, 1)⌋.
        foreach (var n in new long[] { 0, 1, 2, 3, 7, 25, frames / 2, frames - 1 })
        {
            var expected = (int)Math.Floor(n * speed + Math.Min(speed, 1m) / 2);
            Assert.Equal(expected, await FrameAt(n));
        }
    }

    [Fact]
    public async Task A_speed_change_while_paused_shows_the_new_source_frame()
    {
        var clip = Clip(1m);
        Assert.Equal(40, await FrameAt(40));

        Assert.True(_f.Service.SetClipSpeed(clip.Id, X(2m)).Success);
        Publish();

        Assert.Equal(80, await FrameAt(40));
    }

    // --- Audio ---------------------------------------------------------------------------------------

    private async Task<float[]> PlayAudio(int frames)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            _service.Update();
            var from = (long)Math.Round(_service.Position.TotalSeconds * AudioFormat.SampleRate);
            if (_service.AudioHasData(from, from + frames)) break;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("audio not decoded");
            await Task.Delay(1);
        }
        return _output.Play(frames);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.35)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public async Task Timeline_sample_k_plays_the_source_sample_at_k_times_the_speed(double speedValue)
    {
        var speed = (decimal)speedValue;
        Clip(speed);
        _service.Play();

        var played = await PlayAudio(4_800);   // 0.1 s of timeline

        Assert.Contains(_audio.Requests, r => r.Speed == X(speed));
        for (var k = 0; k < 4_800; k += 97)
        {
            var source = FakeAudioSource.IndexOf(played[2 * k]);
            Assert.InRange(source - (double)(k * speed), -1.0, 1.0);
        }
    }

    [Fact]
    public async Task After_a_seek_at_2x_the_first_sample_is_the_source_at_twice_the_position()
    {
        Clip(2m);
        await _service.SeekAsync(MediaTime.FromSeconds(1.5));
        _service.Play();

        var played = await PlayAudio(1_000);

        Assert.InRange(FakeAudioSource.IndexOf(played[0]) - Samples(3.0), -1, 1);
        Assert.InRange(FakeAudioSource.IndexOf(played[2 * 999]) - (Samples(3.0) + 2 * 999), -1, 1);
    }
}
