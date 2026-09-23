using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>PlaybackService with audio: fake video/audio decoders and a fake output device whose
/// played-frames clock the test advances. The Stopwatch fallback is the manual FakeReferenceClock.</summary>
public sealed class AudioPlaybackServiceTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _video = new();
    private readonly FakeAudioDecoder _audio = new();
    private readonly FakeAudioOutput _output = new();
    private readonly FakeReferenceClock _stopwatch = new();
    private readonly PlaybackService _service;
    private long _version;

    public AudioPlaybackServiceTests() =>
        _service = new PlaybackService(_video, _stopwatch, NullLogger<PlaybackService>.Instance, new PlaybackSettings(), _audio, _output);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private static long S(double seconds) => (long)Math.Round(seconds * AudioFormat.SampleRate);

    /// <summary>A 25 fps video with sound: audio sample i has value i·2⁻²⁴.</summary>
    private VideoClip AddVideoWithSound(string name, double seconds)
    {
        var asset = _f.Video(seconds, Rate, name);
        asset.Metadata!.AudioCodec = "aac";
        asset.Metadata.AvgFrameRate = Rate;
        _video.Add(asset.FilePath, new FakeSource(Rate, (int)(seconds * 25)));
        _audio.Add(asset.FilePath, new FakeAudioSource(S(seconds)));
        var result = _f.Service.AddClip(asset.Id);
        Assert.True(result.Success, result.Message);
        return (VideoClip)_f.V1.Clips.Single(c => c.Id == result.ClipIds[0]);
    }

    private void Publish() => _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, ++_version));

    /// <summary>Ticks the service until the audio for the next <paramref name="frames"/> is decoded,
    /// then lets the device play them.</summary>
    private async Task<float[]> PlayAudio(int frames)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            _service.Update();
            var from = WritePosition();
            if (_service.AudioHasData(from, from + frames)) break;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("audio not decoded");
            await Task.Delay(1);
        }
        return _output.Play(frames);
    }

    private long WritePosition() => (long)Math.Round(_service.Position.TotalSeconds * AudioFormat.SampleRate); // no device latency in the fake

    private static long FirstIndex(float[] buffer) => FakeAudioSource.IndexOf(buffer[0]);
    private static long LastIndex(float[] buffer) => FakeAudioSource.IndexOf(buffer[^2]);

    [Fact]
    public async Task Playing_TheDeviceIsTheMasterClock_AndVideoFollowsIt()
    {
        AddVideoWithSound("a.mp4", 10);
        Publish();
        _service.Play();
        Assert.Equal(1, _output.StartCount);
        Assert.True(_service.IsAudioAvailable);

        _stopwatch.Advance(5.0); // the Stopwatch is not the master now
        Assert.Equal(MediaTime.Zero, _service.Position);

        var audio = await PlayAudio(S(0.1) is var n ? (int)n : 0);
        Assert.Equal(MediaTime.FromSeconds(0.1), _service.Position);
        Assert.Equal((0L, n - 1), (FirstIndex(audio), LastIndex(audio)));
        Assert.Equal(2, _service.Update().TimelineFrame); // 0.1 s at 25 fps
    }

    [Fact]
    public async Task Pause_StopsTheDevice_AndResumeContinuesAtTheNextSample()
    {
        AddVideoWithSound("a.mp4", 10);
        Publish();
        _service.Play();
        var before = await PlayAudio(9_600);

        _service.Pause();
        Assert.False(_output.IsRunning);
        Assert.Equal(MediaTime.FromSeconds(0.2), _service.Position);
        _stopwatch.Advance(3.0);
        Assert.Equal(MediaTime.FromSeconds(0.2), _service.Position);

        _service.Play();
        Assert.Equal(2, _output.StartCount);
        var after = await PlayAudio(1_000);
        Assert.Equal(LastIndex(before) + 1, FirstIndex(after));
    }

    [Fact]
    public async Task SeekWhilePlaying_FlushesTheDevice_AndPlaysFromTheNewPosition()
    {
        AddVideoWithSound("a.mp4", 10);
        Publish();
        _service.Play();
        await PlayAudio(4_800);

        _ = _service.SeekAsync(MediaTime.FromSeconds(2));
        Assert.Equal((1, 2), (_output.StopCount, _output.StartCount));
        Assert.Equal(PlaybackState.Playing, _service.State);

        var audio = await PlayAudio(1_000);
        Assert.Equal(S(2), FirstIndex(audio));
        Assert.Equal(MediaTime.FromSeconds(2) + new MediaTime(1_000 * 10_000_000L / 48_000), _service.Position);
    }

    [Fact]
    public async Task SnapshotUpdateWhilePlaying_KeepsTheDeviceAndReader_AndAppliesTheNewGain()
    {
        var clip = AddVideoWithSound("a.mp4", 10);
        Publish();
        _service.Play();
        var before = await PlayAudio(4_800);
        var opens = _audio.Requests.Count;

        clip.Volume = 0.5;
        Publish();
        var after = await PlayAudio(1_000);

        Assert.Equal((0, 1), (_output.StopCount, _output.StartCount)); // device never stopped
        Assert.Equal(opens, _audio.Requests.Count);                    // reader reused
        Assert.Equal((LastIndex(before) + 1) * FakeAudioSource.Unit * 0.5f, after[0]); // contiguous, new gain
        Assert.Equal(PlaybackState.Playing, _service.State);
    }

    [Fact]
    public async Task DeviceFailure_FallsBackToTheStopwatch_WithoutAJump()
    {
        AddVideoWithSound("a.mp4", 10);
        Publish();
        _service.Play();
        await PlayAudio(4_800);

        _output.Fail();
        _service.Update();
        Assert.False(_service.IsAudioAvailable);
        Assert.Equal(MediaTime.FromSeconds(0.1), _service.Position);

        _stopwatch.Advance(0.5);
        Assert.Equal(MediaTime.FromSeconds(0.6), _service.Position);
        Assert.Equal(PlaybackState.Playing, _service.State);
    }

    [Fact]
    public void NoDevice_PlaysOnTheStopwatch()
    {
        _output.Available = false;
        AddVideoWithSound("a.mp4", 10);
        Publish();
        _service.Play();

        Assert.False(_service.IsAudioAvailable);
        _stopwatch.Advance(0.3);
        Assert.Equal(MediaTime.FromSeconds(0.3), _service.Position);
    }

    [Fact]
    public async Task ReachingTheEnd_PausesAndStopsTheDevice_StopReturnsToZero()
    {
        AddVideoWithSound("a.mp4", 0.4);
        Publish();
        _service.Play();
        await PlayAudio(S(0.2) is var n ? (int)n : 0);
        _output.Play((int)S(0.3));                            // past the end (silence after it)

        var frame = _service.Update();
        Assert.Equal((PlaybackState.Paused, MediaTime.FromSeconds(0.4)), (frame.State, frame.Position));
        Assert.False(_output.IsRunning);

        _service.Play();                                      // at the end → from 0 on the device
        Assert.True(_output.IsRunning);
        _service.Stop();
        Assert.False(_output.IsRunning);
        Assert.Equal(MediaTime.Zero, _service.Position);
    }

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
    public async Task PlayPausePlay_Cycles_AudioIsContinuous_PositionNeverDecreases_NoReaderPileUp()
    {
        AddVideoWithSound("a.mp4", 20);
        Publish();
        long? last = null;
        var position = MediaTime.Zero;

        for (var cycle = 1; cycle <= 6; cycle++)
        {
            _service.Play();
            var audio = await PlayAudio(2_400 + 97 * cycle); // odd block sizes
            if (last is { } previous)
                Assert.Equal(previous + 1, FirstIndex(audio)); // resumes on the next sample
            last = LastIndex(audio);
            Assert.True(_service.Position >= position);
            position = _service.Position;

            _service.Pause();
            Assert.False(_output.IsRunning);
            Assert.Equal(position, _service.Position);             // paused on what was played
            _stopwatch.Advance(1.0);
            Assert.Equal(position, _service.Position);
            await Eventually(() => _audio.LiveStreams <= 1, $"audio readers piled up after pause {cycle}");
        }

        Assert.Equal((6, 6), (_output.StartCount, _output.StopCount));
    }

    [Fact]
    public async Task SeekBackwardAndForward_WhilePlaying_PlaysEachTarget_AndReleasesOldReaders()
    {
        AddVideoWithSound("a.mp4", 20);
        Publish();
        _service.Play();
        await PlayAudio(2_400);

        foreach (var seconds in new[] { 12.0, 3.0, 17.5, 0.5, 9.25 })
        {
            _ = _service.SeekAsync(MediaTime.FromSeconds(seconds));
            Assert.Equal(PlaybackState.Playing, _service.State);
            var audio = await PlayAudio(1_200);
            Assert.Equal(S(seconds), FirstIndex(audio));
            Assert.Equal(MediaTime.FromSeconds(seconds) + new MediaTime(1_200 * 10_000_000L / 48_000), _service.Position);
            await Eventually(() => _audio.LiveStreams <= 1, $"old audio readers still open after seeking to {seconds}");
        }
        Assert.Equal(6, _output.StartCount); // play + one restart per seek
    }

    [Fact]
    public async Task RepeatedSnapshotUpdates_WhilePlaying_KeepAudioContinuous_AndDoNotLeakReaders()
    {
        var clip = AddVideoWithSound("a.mp4", 20);
        var music = _f.Audio(20, "music.wav");
        _audio.Add(music.FilePath, new FakeAudioSource(S(20), Constant: 0));
        Assert.True(_f.Service.AddClip(music.Id, _f.A1.Id, MediaTime.FromFrame(10, Rate)).Success); // room to move ±1 frame
        var musicClip = _f.A1.Clips[0];
        Publish();
        _service.Play();
        var previous = LastIndex(await PlayAudio(1_000));

        for (var update = 1; update <= 20; update++)
        {
            if (update % 2 == 0)
                Assert.True(_f.Service.MoveClips(TimelineFixture.Ids(musicClip), update % 4 == 0 ? 1 : -1).Success); // changed span
            else
                clip.Volume = 1.0;                                                                           // unchanged span
            Publish();

            var audio = await PlayAudio(500);
            Assert.Equal(previous + 1, FirstIndex(audio)); // the video's audio never skips or repeats
            previous = LastIndex(audio);
            await Eventually(() => _audio.LiveStreams <= 2, $"readers leaked after update {update}");
        }

        Assert.Equal((0, 1), (_output.StopCount, _output.StartCount));
        Assert.Equal(1, _audio.OpenCount(_f.FindAsset(clip.MediaAssetId)!.FilePath)); // the unchanged clip was never reopened
        Assert.Equal(2, _service.AudioReaderCount);
    }

    [Fact]
    public async Task ReachingTheEnd_ReleasesAudioReaders()
    {
        AddVideoWithSound("a.mp4", 0.4);
        Publish();
        _service.Play();
        await PlayAudio((int)S(0.3));
        _output.Play((int)S(0.2));

        Assert.Equal(PlaybackState.Paused, _service.Update().State);
        await Eventually(() => _audio.LiveStreams == 0 && _service.AudioReaderCount == 0, "audio readers left after the end");
    }

    [Fact]
    public async Task Dispose_ReleasesEveryReaderAndStream_EvenWhileOpensArePending()
    {
        var clip = AddVideoWithSound("a.mp4", 20);
        var slow = _f.Audio(20, "slow.wav");
        _audio.Add(slow.FilePath, new FakeAudioSource(S(20)));
        Assert.True(_f.Service.AddClip(slow.Id, _f.A1.Id, MediaTime.Zero).Success);
        var gate = _audio.Gate(slow.FilePath); // one reader stuck opening its decoder
        Publish();
        _service.Play();
        await Eventually(() => { _service.Update(); return _audio.LiveStreams >= 1; }, "the video's audio stream did not open");
        _output.Play(1_000);
        _audio.StreamDisposeDelay = TimeSpan.FromMilliseconds(150); // closing streams is slow from now on

        // A pipeline whose reader has an open (slow-closing) stream is superseded right before Dispose.
        var videoAudio = _f.FindAsset(clip.MediaAssetId)!.FilePath;
        _ = _service.SeekAsync(MediaTime.FromSeconds(5));
        await Eventually(() => { _service.Update(); return _audio.OpenCount(videoAudio) >= 2; }, "reader of the seek did not open");
        await Task.Delay(50);
        Assert.True(_audio.LiveStreams >= 1);
        _ = _service.SeekAsync(_service.Duration);                 // retires it (stream closes in 150 ms); nothing to play here
        Assert.Equal(0, _service.AudioReaderCount);

        await _service.DisposeAsync();

        Assert.Equal(0, _audio.LiveStreams);   // deterministic: DisposeAsync awaited every retiring pipeline
        Assert.Equal(0, _video.LiveStreams);
        Assert.False(_output.IsRunning);
        gate.SetResult();                       // a late gate release opens nothing any more
        await Task.Delay(20);
        Assert.Equal(0, _audio.LiveStreams);
        Assert.Equal(clip.Id, _f.V1.Clips[0].Id);
    }

    [Fact]
    public async Task AudioClipsAndHiddenTrackVideo_AreMixed_MutedTracksAreNot()
    {
        var clip = AddVideoWithSound("v.mp4", 4);
        _f.V1.IsHidden = true;
        var music = _f.Audio(4, "music.wav");
        _audio.Add(music.FilePath, new FakeAudioSource(S(4), Constant: 0.25f));
        Assert.True(_f.Service.AddClip(music.Id, _f.A1.Id, MediaTime.Zero).Success);
        Publish();
        _service.Play();

        var mixed = await PlayAudio(100);
        Assert.Equal(0.25f, mixed[0]);                          // sample 0 of the video is 0
        Assert.Equal(0.25f + 50 * FakeAudioSource.Unit, mixed[100]);

        _f.A1.IsMuted = true;
        Publish();
        var muted = await PlayAudio(100);
        Assert.Equal(100 * FakeAudioSource.Unit, muted[0]);     // only the (hidden) video's audio
        Assert.Equal(clip.Id, _f.V1.Clips[0].Id);
    }
}
