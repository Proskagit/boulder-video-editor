using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests.Playback;

/// <summary>AudioPipeline + AudioSpanReader + AudioMixer with a fake decoder: every output sample
/// is checked against the exact source sample the timeline says it must play.</summary>
public sealed class AudioPipelineTests
{
    private readonly TimelineFixture _f = new();
    private readonly FakeAudioDecoder _decoder = new();
    private readonly AudioMixer _mixer = new();
    private readonly PlaybackSettings _settings = new();
    private long _version;

    private PlaybackSnapshot Snapshot() => PlaybackSnapshotBuilder.Build(_f.Project, ++_version);

    private AudioPipeline Pipeline(long start, AudioPipeline? previous = null)
    {
        var pipeline = new AudioPipeline(Snapshot(), 1, start, _mixer, _decoder, _settings, NullLogger.Instance, previous);
        _mixer.Reset(start);
        return pipeline;
    }

    private MediaAsset Music(string name, double seconds, float? constant = null)
    {
        var asset = _f.Audio(seconds, name);
        _decoder.Add(asset.FilePath, new FakeAudioSource((long)(seconds * 48_000), Constant: constant));
        return asset;
    }

    private AudioClip AddAudio(MediaAsset asset, Track track, long? startFrame = null)
    {
        var result = _f.Service.AddClip(asset.Id, track.Id, startFrame is { } s ? MediaTime.FromFrame(s, _f.Rate) : null);
        Assert.True(result.Success, result.Message);
        return (AudioClip)track.Clips.Single(c => c.Id == result.ClipIds[0]);
    }

    /// <summary>Mixes [from, from + frames) once the pipeline has decoded it.</summary>
    private static async Task<float[]> Pull(AudioPipeline pipeline, AudioMixer mixer, long from, int frames)
    {
        Assert.Equal(from, mixer.WritePosition);
        pipeline.Maintain(from);
        var watch = Stopwatch.StartNew();
        while (!pipeline.HasData(from, from + frames))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException($"no audio data at {from}");
            await Task.Delay(1);
        }
        var buffer = new float[frames * 2];
        mixer.Read(buffer);
        return buffer;
    }

    /// <summary>Expected left-channel source index for timeline sample k, or null for silence.</summary>
    private static long? ExpectedIndex(long k, params Clip[] clips)
    {
        foreach (var clip in clips.OfType<MediaBackedClip>())
        {
            if (k >= AudioTiming.CeilingSample(clip.TimelineStart) && k < AudioTiming.CeilingSample(clip.TimelineEnd))
                return k + AudioTiming.SourceOffset(clip.TimelineStart, clip.SourceIn);
        }
        return null;
    }

    private static void AssertSamples(float[] buffer, long from, params Clip[] clips)
    {
        for (var i = 0; i < buffer.Length / 2; i++)
        {
            var k = from + i;
            var expected = ExpectedIndex(k, clips);
            if (expected is null)
            {
                Assert.True(buffer[2 * i] == 0 && buffer[2 * i + 1] == 0, $"sample {k} should be silent");
                continue;
            }
            Assert.True(FakeAudioSource.IndexOf(buffer[2 * i]) == expected, $"sample {k}: expected source {expected}, got {FakeAudioSource.IndexOf(buffer[2 * i])}");
            Assert.Equal(-buffer[2 * i], buffer[2 * i + 1]);
        }
    }

    [Fact]
    public async Task AdjacentAndTrimmedClips_AreSampleExact_WithSilentGaps()
    {
        // Lock 29.97 so clip edges fall between samples; A then B with B's start trimmed (gap + SourceIn).
        Assert.True(_f.Service.AddClip(_f.Video(1, FrameRate.Ntsc30, "lock.mp4").Id).Success);
        var a = AddAudio(Music("a.wav", 2), _f.A1);
        var b = AddAudio(Music("b.wav", 3), _f.A1);
        Assert.True(_f.Service.TrimClip(b.Id, ClipEdge.Start, b.TimelineStart + MediaTime.FromFrame(7, _f.Rate)).Success);
        Assert.NotEqual(MediaTime.Zero, b.SourceIn);

        await using var pipeline = Pipeline(0);
        var end = AudioTiming.CeilingSample(b.TimelineEnd) + 500;
        for (long from = 0; from < end; from += 1_000)
            AssertSamples(await Pull(pipeline, _mixer, from, 1_000), from, a, b);
    }

    [Fact]
    public async Task Gain_Sum_Clamp_AndHiddenTrackAudio()
    {
        var video = _f.Video(2, FrameRate.Fps25, "v.mp4");
        video.Metadata!.AudioCodec = "aac";
        _decoder.Add(video.FilePath, new FakeAudioSource(96_000, Constant: 0.5f));
        var added = _f.Service.AddClip(video.Id);
        Assert.True(added.Success, added.Message);
        var clip = (VideoClip)_f.V1.Clips.Single(c => c.Id == added.ClipIds[0]);
        clip.Volume = 0.5;                                   // 0.5 × 0.5 = 0.25
        _f.V1.IsHidden = true;                               // hidden video still sounds

        var a2 = AddTrack(TrackType.Audio);
        AddAudio(Music("loud.wav", 2, constant: 0.7f), a2);  // 0.25 + 0.7 = 0.95
        await using (var pipeline = Pipeline(1_000))
            Assert.All(await Pull(pipeline, _mixer, 1_000, 500), v => Assert.Equal(0.95f, v, 5));

        var a3 = AddTrack(TrackType.Audio);
        AddAudio(Music("louder.wav", 2, constant: 0.5f), a3); // 1.45 → clamped to 1
        await using (var pipeline = Pipeline(2_000))
            Assert.All(await Pull(pipeline, _mixer, 2_000, 500), v => Assert.Equal(1f, v));
    }

    [Fact]
    public async Task Offline_AndDecodeError_AreSilent_OtherSourcesStillPlay()
    {
        var missing = Music("missing.wav", 2, constant: 0.3f);
        AddAudio(missing, _f.A1);
        missing.IsMissing = true;                             // Offline in the snapshot
        var broken = Music("broken.wav", 2, constant: 0.3f);
        AddAudio(broken, AddTrack(TrackType.Audio));
        _decoder.Fail(broken.FilePath);                       // DecodeError at run time
        AddAudio(Music("ok.wav", 2, constant: 0.25f), AddTrack(TrackType.Audio));

        await using var pipeline = Pipeline(0);
        for (long from = 0; from < 48_000; from += 4_800)
            Assert.All(await Pull(pipeline, _mixer, from, 4_800), v => Assert.Equal(0.25f, v));
    }

    [Theory]
    [InlineData(-480)]   // stream starts 10 ms after the requested sample → leading silence
    [InlineData(5_000)]  // stream starts early (preroll) → the extra samples are dropped
    public async Task StreamStartingBeforeOrAfterTheRequest_IsAlignedByItsRealFirstSample(long preroll)
    {
        _decoder.PrerollSamples = preroll;
        var clip = AddAudio(Music("a.wav", 4), _f.A1);
        const long start = 30_000;

        await using var pipeline = Pipeline(start);
        var buffer = await Pull(pipeline, _mixer, start, 2_000);
        for (var i = 0; i < 2_000; i++)
        {
            if (preroll < 0 && i < -preroll)
                Assert.Equal(0f, buffer[2 * i]);
            else
                Assert.Equal(ExpectedIndex(start + i, clip), FakeAudioSource.IndexOf(buffer[2 * i]));
        }
    }

    [Fact]
    public async Task Underrun_IsSilence_ThenContinuesAtTheCorrectSample()
    {
        var asset = Music("slow.wav", 4);
        var clip = AddAudio(asset, _f.A1);
        var gate = _decoder.Gate(asset.FilePath);

        await using var pipeline = Pipeline(0);
        for (var block = 0; block < 3; block++)
        {
            pipeline.Maintain(_mixer.WritePosition);
            var silent = new float[2 * 1_000];
            _mixer.Read(silent);                              // decoder not ready: never waits
            Assert.All(silent, v => Assert.Equal(0f, v));
        }

        gate.SetResult();
        var from = _mixer.WritePosition;                       // 3000 — time did not stop
        Assert.Equal(3_000, from);
        AssertSamples(await Pull(pipeline, _mixer, from, 1_000), from, clip);
    }

    [Fact]
    public async Task SnapshotUpdate_ReusesReadersOfUnchangedClips_AndAppliesNewGain()
    {
        var music = Music("a.wav", 4);
        var clip = AddAudio(music, _f.A1);
        var other = Music("b.wav", 4, constant: 0.1f);
        var moved = AddAudio(other, AddTrack(TrackType.Audio), startFrame: 30); // 1 s: inside the look-ahead, after the checked range

        var first = Pipeline(0);
        AssertSamples(await Pull(first, _mixer, 0, 4_800), 0, clip);
        Assert.Equal(1, _decoder.OpenCount(music.FilePath));

        clip.Volume = 0.5;                                               // gain change only: reuse
        Assert.True(_f.Service.MoveClips(TimelineFixture.Ids(moved), 3).Success); // moved: new reader
        var next = new AudioPipeline(Snapshot(), 2, _mixer.WritePosition, _mixer, _decoder, _settings, NullLogger.Instance, first);
        await first.DisposeAsync();

        var buffer = await Pull(next, _mixer, 4_800, 1_000);
        Assert.Equal(1, _decoder.OpenCount(music.FilePath));
        Assert.Equal(2, _decoder.OpenCount(other.FilePath));
        for (var i = 0; i < 1_000; i++)
            Assert.Equal((4_800 + i) * FakeAudioSource.Unit * 0.5f, buffer[2 * i]); // exact: power-of-two scale
        await next.DisposeAsync();
    }

    private Track AddTrack(TrackType type)
    {
        Assert.True(_f.Service.AddTrack(type).Success);
        return (type == TrackType.Audio ? _f.Project.Timeline.AudioTracks : _f.Project.Timeline.VideoTracks)[^1];
    }
}
