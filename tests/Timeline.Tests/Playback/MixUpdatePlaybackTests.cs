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
/// Volume and mute (Phase 7 Step 4) reach playback as a mix-only snapshot update: the same video
/// pipeline, audio pipeline, readers and seek generation keep running, only the gains change. Edits
/// go through the real edit service; the fake device plays the mix sample by sample.
/// </summary>
public sealed class MixUpdatePlaybackTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const float Music = 0.25f;

    private readonly TimelineFixture _f = new();
    private readonly FakeVideoDecoder _video = new();
    private readonly FakeAudioDecoder _audio = new();
    private readonly FakeAudioOutput _output = new();
    private readonly FakeReferenceClock _stopwatch = new();
    private readonly PlaybackService _service;
    private long _version;

    public MixUpdatePlaybackTests() =>
        _service = new PlaybackService(_video, _stopwatch, NullLogger<PlaybackService>.Instance, new PlaybackSettings(), _audio, _output);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _service.DisposeAsync();

    private static long S(double seconds) => (long)Math.Round(seconds * AudioFormat.SampleRate);

    /// <summary>A 25 fps video with sound on V1: audio sample i is i·2⁻²⁴.</summary>
    private VideoClip AddVideoWithSound(double seconds)
    {
        var asset = _f.Video(seconds, Rate, "v.mp4");
        asset.Metadata!.AudioCodec = "aac";
        asset.Metadata.AvgFrameRate = Rate;
        _video.Add(asset.FilePath, new FakeSource(Rate, (int)(seconds * 25)));
        _audio.Add(asset.FilePath, new FakeAudioSource(S(seconds)));
        var result = _f.Service.AddClip(asset.Id);
        Assert.True(result.Success, result.Message);
        return (VideoClip)_f.V1.Clips.Single(c => c.Id == result.ClipIds[0]);
    }

    /// <summary>Music on A1 from 0: every sample is <see cref="Music"/>.</summary>
    private AudioClip AddMusic(double seconds)
    {
        var asset = _f.Audio(seconds, "music.wav");
        _audio.Add(asset.FilePath, new FakeAudioSource(S(seconds), Constant: Music));
        Assert.True(_f.Service.AddClip(asset.Id, _f.A1.Id, MediaTime.Zero).Success);
        return (AudioClip)_f.A1.Clips[0];
    }

    private void Publish() => _service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(_f.Project, ++_version));

    private void SetAudio(Clip clip, double volume, bool muted)
    {
        var result = _f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = new AudioProperties(volume, muted) });
        Assert.True(result.Success, result.Message);
        Publish(); // what PreviewViewModel does on TimelineChanged
    }

    private long WritePosition() => (long)Math.Round(_service.Position.TotalSeconds * AudioFormat.SampleRate);

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

    private async Task<PlaybackFrame> SettlePicture()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var frame = _service.Update();
            if (!frame.IsBuffering && frame.IsPictureCurrent && frame.Picture?.Kind == PictureKind.Frame) return frame;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException($"no settled picture: {frame}");
            await Task.Delay(1);
        }
    }

    /// <summary>Everything a mix-only update must leave untouched.</summary>
    private sealed record Decoding(object? VideoPipeline, object? AudioPipeline, long SeekGeneration, int VideoOpens, int AudioOpens, int AudioReaders);

    /// <summary>Decoding state once no retired pipeline can still open a decoder (their readers
    /// run on background tasks; a late open would otherwise be counted after the capture).</summary>
    private async Task<Decoding> Capture()
    {
        await _service.RetiringSettledAsync();
        return new(_service.VideoPipelineInstance, _service.AudioPipelineInstance, _service.SeekGeneration,
            _video.Requests.Count, _audio.Requests.Count, _service.AudioReaderCount);
    }

    private async Task AssertUntouched(Decoding before, string step)
    {
        var now = await Capture();
        Assert.True(ReferenceEquals(before.VideoPipeline, now.VideoPipeline), $"{step}: new video pipeline");
        Assert.True(ReferenceEquals(before.AudioPipeline, now.AudioPipeline), $"{step}: new audio pipeline");
        Assert.True(before.SeekGeneration == now.SeekGeneration, $"{step}: new seek generation");
        Assert.True(before.VideoOpens == now.VideoOpens, $"{step}: video reader reopened");
        Assert.True(before.AudioOpens == now.AudioOpens, $"{step}: audio reader reopened");
        Assert.True(before.AudioReaders == now.AudioReaders, $"{step}: audio readers changed");

        // While playing, the frame for the new position may be momentarily late (IsPictureCurrent
        // false, D012) — that is not buffering. Buffering (a reset pipeline) must never show up.
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var frame = _service.Update();
            Assert.False(frame.IsBuffering, $"{step}: buffering");
            Assert.Equal(PictureKind.Frame, frame.Picture?.Kind);
            if (frame.IsPictureCurrent) break;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException($"{step}: picture never current");
            await Task.Delay(1);
        }
    }

    // --- Regression: no reader/pipeline recreation --------------------------------------------------

    [Fact]
    public async Task VolumeAndMuteWhilePlaying_KeepPipelinesReadersAndSeekGeneration()
    {
        var video = AddVideoWithSound(10);
        var music = AddMusic(10);
        Publish();
        _service.Play();
        await PlayAudio(4_800);
        await SettlePicture();
        var before = await Capture();
        Assert.Equal(2, before.AudioReaders);

        var steps = new (string Name, Action Edit)[]
        {
            ("video volume", () => SetAudio(video, 0.5, false)),
            ("video mute", () => SetAudio(video, 0.5, true)),
            ("video unmute", () => SetAudio(video, 0.5, false)),
            ("music volume 0", () => SetAudio(music, 0, false)),
            ("music mute", () => SetAudio(music, 0, true)),
            ("video volume 200 %", () => SetAudio(video, 2, false)),
            ("undo", () => { _f.UndoRedo.Undo(); Publish(); }),
            ("redo", () => { _f.UndoRedo.Redo(); Publish(); }),
            ("video opacity", () =>
            {
                Assert.True(_f.Service.SetClipProperties(video.Id, new ClipPropertyChange
                {
                    Visual = VisualProperties.Of(video)!.Value with { Opacity = 0.5 }
                }).Success);
                Publish();
            }),
        };

        foreach (var (name, edit) in steps)
        {
            edit();
            await AssertUntouched(before, name);
            await PlayAudio(480);
            await AssertUntouched(before, name + " (after playing on)");
        }

        Assert.Equal((0, 1), (_output.StopCount, _output.StartCount)); // the device never stopped
        Assert.Equal(PlaybackState.Playing, _service.State);
    }

    [Fact]
    public async Task VolumeAndMuteWhilePaused_KeepThePictureAndSeekGeneration()
    {
        var video = AddVideoWithSound(10);
        Publish();
        Assert.True(await _service.SeekAsync(MediaTime.FromSeconds(2)));
        var shown = await SettlePicture();
        // The seek also replaced the audio reader, which opens its decoder on a background task:
        // wait until it has data, or its (first) open could be counted after the edit as a reopen.
        var at = AudioTiming.NearestSample(_service.Position);
        var watch = Stopwatch.StartNew();
        while (!_service.AudioHasData(at, at + 1))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("audio reader did not open");
            _service.Update();
            await Task.Delay(1);
        }
        var before = await Capture();

        SetAudio(video, 0.3, true);

        await AssertUntouched(before, "paused mute");
        Assert.Equal(50, FakeVideoDecoder.Number(shown.Picture!.Frame!));
        Assert.Equal(50, FakeVideoDecoder.Number(_service.Update().Picture!.Frame!));
    }

    [Fact]
    public async Task SeekAfterAMixOnlyUpdate_ShowsACurrentPicture()
    {
        // The video pipeline was built from an older snapshot version than the service now holds;
        // a later seek builds from the newer one and must count as current.
        var video = AddVideoWithSound(10);
        Publish();
        await SettlePicture();
        SetAudio(video, 0.5, false);

        Assert.True(await _service.SeekAsync(MediaTime.FromSeconds(3)));
        var frame = await SettlePicture();
        Assert.Equal(75, FakeVideoDecoder.Number(frame.Picture!.Frame!));
    }

    // --- What the mix sounds like ---------------------------------------------------------------------

    [Fact]
    public async Task Mute_SilencesOnlyThatClipsOwnAudio_AndUnmuteRestoresItsVolume()
    {
        var video = AddVideoWithSound(10);
        AddMusic(10);
        Publish();
        _service.Play();
        await PlayAudio(4_800);

        static float Mix(long sample, float videoGain) => Music + videoGain * (sample * FakeAudioSource.Unit);

        SetAudio(video, 0.5, false);
        var k = WritePosition();
        var half = await PlayAudio(100);
        Assert.Equal(Mix(k, 0.5f), half[0]);
        Assert.Equal(Mix(k + 99, 0.5f), half[198]);

        SetAudio(video, 0.5, true);                     // mute: the video's embedded audio goes silent…
        var muted = await PlayAudio(100);
        Assert.All(Enumerable.Range(0, 100), j => Assert.Equal(Music, muted[2 * j])); // …the music keeps playing
        Assert.Equal(0.5, video.Volume);                  // mute never touched the volume

        SetAudio(video, 0.5, false);                    // unmute: back at 50 %, contiguous
        k = WritePosition();
        var back = await PlayAudio(100);
        Assert.Equal(Mix(k, 0.5f), back[0]);
        Assert.Equal(S(0.1) + 200, k);                    // 4 800 + 2 × 100 samples played, nothing skipped
    }

    [Fact]
    public async Task VolumeZero_IsSilent_ButNotMuted()
    {
        var video = AddVideoWithSound(10);
        AddMusic(10);
        Publish();
        _service.Play();
        await PlayAudio(4_800);

        SetAudio(video, 0, false);
        var silent = await PlayAudio(100);
        Assert.Equal(Music, silent[0]);
        Assert.False(video.IsMuted);
        Assert.False(PlaybackSnapshotBuilder.Build(_f.Project, 99).AudioSpans.Single(s => s.ClipId == video.Id).IsMuted);

        SetAudio(video, 1, false);
        var k = WritePosition();
        var full = await PlayAudio(100);
        Assert.Equal(Music + k * FakeAudioSource.Unit, full[0]);
    }

    [Fact]
    public async Task ClipStartingMuted_PlaysAfterUnmute_WithoutOpeningAReader()
    {
        var video = AddVideoWithSound(10);
        SetAudio(video, 1, true); // muted before playback starts: its reader still opens
        _service.Play();
        var muted = await PlayAudio(4_800);
        Assert.All(Enumerable.Range(0, 4_800), j => Assert.Equal(0f, muted[2 * j]));
        var opens = _audio.Requests.Count;

        SetAudio(video, 1, false);
        var k = WritePosition();
        var audible = await PlayAudio(100);

        Assert.Equal(k * FakeAudioSource.Unit, audible[0]);
        Assert.Equal(opens, _audio.Requests.Count);
    }

    // --- Picture changes keep unchanged audio readers --------------------------------------------------

    [Fact]
    public async Task PictureChangeWhilePlaying_RebuildsVideo_ButReusesEveryAudioReader()
    {
        var video = AddVideoWithSound(10);
        var music = AddMusic(10);
        Publish();
        _service.Play();
        await PlayAudio(4_800);
        await SettlePicture();
        var before = await Capture();
        var videoFile = _f.FindAsset(video.MediaAssetId)!.FilePath;
        var musicFile = _f.FindAsset(music.MediaAssetId)!.FilePath;

        _f.V1.IsHidden = true; // picture-relevant (the video's audio still plays: hidden ≠ muted)
        Assert.True(_f.Service.AddTrack(TrackType.Video).Success);
        Publish();

        Assert.NotSame(before.VideoPipeline, _service.VideoPipelineInstance); // a real resync of the picture…
        var next = await PlayAudio(100);
        Assert.Equal((1, 1), (_audio.OpenCount(videoFile), _audio.OpenCount(musicFile))); // …but no audio reader reopened
        Assert.Equal(2, _service.AudioReaderCount);
        Assert.Equal(Music + (4_800) * FakeAudioSource.Unit, next[0]); // contiguous
        Assert.Equal((0, 1), (_output.StopCount, _output.StartCount));
    }
}
