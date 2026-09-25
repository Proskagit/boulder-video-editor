using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests;
using AiVideoEditor.Timeline.Tests.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Export.Tests;

/// <summary>
/// Phase 8 Step 4 (D023): the export's audio is the Preview's. For the same snapshot and the same (fake) decoder,
/// every sample of <see cref="ExportAudioSource"/> must equal what the Preview's <see cref="AudioPipeline"/> +
/// <see cref="AudioMixer"/> produce when playing from the start — speeds, trim, split, gaps, volume, clip/track
/// mute, a hidden video track, overlapping clips and clipping included. The fake source makes sample i of a file
/// (i·2⁻²⁴, −i·2⁻²⁴), so every value identifies the source sample it came from.
/// </summary>
public sealed class ExportAudioContractTests
{
    private readonly TimelineFixture _f = new();
    private readonly FakeAudioDecoder _previewDecoder = new();
    private readonly FakeAudioDecoder _exportDecoder = new();

    private MediaTime F(long frame) => MediaTime.FromFrame(frame, _f.Rate);
    private static void Ok(TimelineEditResult result) => Assert.True(result.Success, result.Message);

    private void Source(MediaAsset asset, FakeAudioSource source)
    {
        _previewDecoder.Add(asset.FilePath, source);
        _exportDecoder.Add(asset.FilePath, source);
    }

    private MediaAsset Video(FrameRate rate, double seconds, string name, float? constant = null)
    {
        var asset = _f.Video(seconds, rate, name);
        asset.Metadata!.AudioCodec = "aac";
        Source(asset, new FakeAudioSource((long)(seconds * 48_000), Constant: constant));
        return asset;
    }

    private MediaAsset Music(double seconds, string name, float? constant = null, long streamStart = 0)
    {
        var asset = _f.Audio(seconds, name);
        Source(asset, new FakeAudioSource((long)(seconds * 48_000), streamStart, constant));
        return asset;
    }

    private Track AudioTrack()
    {
        Ok(_f.Service.AddTrack(TrackType.Audio));
        return _f.Project.Timeline.AudioTracks.OrderBy(t => t.Order).Last();
    }

    private Clip Add(MediaAsset asset, Track track, long startFrame)
    {
        var result = _f.Service.AddClip(asset.Id, track.Id, F(startFrame));
        Ok(result);
        return track.Clips.Single(c => c.Id == result.ClipIds[0]);
    }

    private PlaybackSnapshot Snapshot() => PlaybackSnapshotBuilder.Build(_f.Project, 1);

    // --- the two sides ----------------------------------------------------------------------------------------

    private async Task<float[]> ExportAudio(PlaybackSnapshot snapshot, int chunkFrames = 3001)
    {
        await using var source = new ExportAudioSource(snapshot, _exportDecoder);
        Assert.Equal(ExportOutput.For(snapshot).AudioSampleCount, source.SampleCount);
        var all = new float[source.SampleCount * 2];
        var buffer = new float[chunkFrames * 2];
        var at = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            Array.Copy(buffer, 0, all, at, read);
            at += read;
        }
        Assert.Equal(all.Length, at);
        return all;
    }

    /// <summary>The Preview playing [0, count) from the start: pipeline + mixer, each chunk once decoded.</summary>
    private async Task<float[]> PreviewAudio(PlaybackSnapshot snapshot, long count, int chunkFrames = 4800)
    {
        var mixer = new AudioMixer();
        mixer.Reset(0);
        await using var pipeline = new AudioPipeline(snapshot, 1, 0, mixer, _previewDecoder, new PlaybackSettings(), NullLogger.Instance);
        var all = new float[count * 2];
        for (long from = 0; from < count; from += chunkFrames)
        {
            var frames = (int)Math.Min(chunkFrames, count - from);
            pipeline.Maintain(from);
            var watch = Stopwatch.StartNew();
            while (!pipeline.HasData(from, from + frames))
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"preview audio at {from}");
                await Task.Delay(1);
            }
            var buffer = new float[frames * 2];
            mixer.Read(buffer);
            Array.Copy(buffer, 0, all, from * 2, buffer.Length);
        }
        return all;
    }

    private async Task<float[]> AssertMatchesPreview(PlaybackSnapshot snapshot)
    {
        var export = await ExportAudio(snapshot);
        var preview = await PreviewAudio(snapshot, export.Length / 2);
        for (var i = 0; i < export.Length; i++)
            Assert.True(export[i] == preview[i], $"sample {i / 2} ch {i % 2}: preview {preview[i]} ≠ export {export[i]}");
        return export;
    }

    private static long? Index(float left) => left == 0 ? null : FakeAudioSource.IndexOf(left);

    // --- speeds, trim, split, gap, volume, mute, hidden track, overlap --------------------------------------------

    [Theory]
    [InlineData(20)]
    [InlineData(5)]
    [InlineData(80)]
    [InlineData(27)]
    public async Task Every_sample_equals_the_preview_mix(int speedSteps)
    {
        var video = Video(FrameRate.Ntsc30, 12, "video.mp4");
        Ok(_f.Service.AddClip(video.Id));                                               // V1 at 0, locks 29.97
        var v1 = _f.V1;
        v1.IsHidden = true;                                                              // hidden: its audio still plays
        var videoClip = (VideoClip)v1.Clips.Single();
        videoClip.Volume = 0.5;
        Ok(_f.Service.TrimClip(videoClip.Id, ClipEdge.End, F(90)));

        var music = Music(speedSteps == 5 ? 3 : 10, "music.wav");
        var main = Add(music, _f.A1, 20);
        Ok(_f.Service.SetClipSpeed(main.Id, ClipSpeed.FromSteps(speedSteps)));
        Ok(_f.Service.TrimClip(main.Id, ClipEdge.Start, F(25)));                        // trim start
        Ok(_f.Service.TrimClip(main.Id, ClipEdge.End, main.TimelineEnd - F(3)));        // trim end
        var splitAt = (main.TimelineStart.ToFrameFloor(_f.Rate) + main.TimelineEnd.ToFrameFloor(_f.Rate)) / 2;
        Ok(_f.Service.Split(F(splitAt), new[] { main.Id }));                            // split …
        var right = _f.A1.Clips.Single(c => c.TimelineStart == F(splitAt));
        Ok(_f.Service.MoveClips(new[] { right.Id }, 7));                                // … and a gap

        var a2 = AudioTrack();
        var loud = (AudioClip)Add(Music(4, "loud.wav"), a2, 40);                        // overlaps the others
        loud.Volume = 2;
        var muted = (AudioClip)Add(Music(4, "muted.wav"), a2, 200);
        muted.IsMuted = true;
        var silentTrack = AudioTrack();
        Add(Music(4, "muted-track.wav"), silentTrack, 0);
        silentTrack.IsMuted = true;
        _f.AssertValid();

        var samples = await AssertMatchesPreview(Snapshot());

        Assert.Contains(samples, s => s != 0);
        Assert.Equal(0, _exportDecoder.OpenCount(_f.FindAsset(muted.MediaAssetId)!.FilePath));        // gain 0: not decoded
        Assert.DoesNotContain(_exportDecoder.Requests, r => r.FilePath.EndsWith("muted-track.wav"));
        Assert.All(_exportDecoder.Requests.Where(r => r.FilePath.EndsWith("music.wav")), r => Assert.Equal(ClipSpeed.FromSteps(speedSteps), r.Speed));
    }

    [Fact]
    public async Task At_1x_each_sample_is_the_exact_source_sample_times_the_gain()
    {
        var music = Music(10, "music.wav");
        Ok(_f.Service.AddClip(music.Id));
        var clip = (AudioClip)_f.A1.Clips.Single();
        Ok(_f.Service.TrimClip(clip.Id, ClipEdge.Start, F(13)));
        clip.Volume = 0.5;

        var samples = await AssertMatchesPreview(Snapshot());

        var p = AudioPlacement.Of(PlaybackSnapshotBuilder.Build(_f.Project, 1).AudioSpans.Single());
        var d = AudioTiming.SourceOffset(clip.TimelineStart, clip.SourceIn);
        for (var k = 0L; k < samples.Length / 2; k += 997)
        {
            if (k < p.FirstSample || k >= p.EndSample) { Assert.Equal(0f, samples[2 * k]); continue; }
            Assert.Equal((k + d) * FakeAudioSource.Unit * 0.5f, samples[2 * k]);
            Assert.Equal(-(k + d) * FakeAudioSource.Unit * 0.5f, samples[2 * k + 1]);
        }
        Assert.Equal(0f, samples[2 * (p.FirstSample - 1)]);
        Assert.NotEqual(0f, samples[2 * p.FirstSample + 2]);
    }

    [Fact]
    public async Task A_split_plays_on_without_a_seam()
    {
        var music = Music(10, "music.wav");
        Ok(_f.Service.AddClip(music.Id));
        var clip = _f.A1.Clips.Single();
        Ok(_f.Service.Split(F(100), new[] { clip.Id }));

        var samples = await AssertMatchesPreview(Snapshot());

        var cut = AudioTiming.CeilingSample(F(100));
        for (var k = cut - 5; k < cut + 5; k++)
            Assert.Equal(k, Index(samples[2 * k]));                                     // source index = timeline index
    }

    [Fact]
    public async Task Sources_starting_late_or_ending_early_leave_silence_like_the_preview()
    {
        var late = Music(4, "late.wav", streamStart: 24_000);                            // no audio in the first 0.5 s of the file
        Ok(_f.Service.AddClip(late.Id));
        var shortSource = _f.Audio(6, "short.wav");                                     // 6 s in the metadata, 1 s of samples
        Source(shortSource, new FakeAudioSource(48_000));
        Add(shortSource, AudioTrack(), 0);

        var samples = await AssertMatchesPreview(Snapshot());

        Assert.Equal(12_000, Index(samples[2 * 12_000]));                               // late: not yet (silence) + short: 12 000
        Assert.Equal(60_000, Index(samples[2 * 30_000]));                               // late 30 000 + short 30 000
        Assert.Equal(100_000, Index(samples[2 * 100_000]));                             // short ended (silence) + late 100 000
        Assert.Equal(0f, samples[2 * 200_000]);                                          // both ended, the short clip still runs: silence
    }

    [Theory]
    [InlineData(20_000)]     // the decoder starts long before the request (ffmpeg's seek preroll): dropped
    [InlineData(-3_000)]     // it starts after the request (the file has no audio there yet): silence first
    [InlineData(0)]
    public async Task Whatever_the_decoder_delivers_is_placed_by_its_real_first_sample(long preroll)
    {
        _previewDecoder.PrerollSamples = preroll;
        _exportDecoder.PrerollSamples = preroll;
        var music = Music(10, "music.wav");
        Ok(_f.Service.AddClip(music.Id, _f.A1.Id, F(30)));
        var clip = _f.A1.Clips.Single();
        Ok(_f.Service.TrimClip(clip.Id, ClipEdge.Start, F(40)));                       // SourceIn 10 frames into the file
        Ok(_f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(20)));

        var samples = await AssertMatchesPreview(Snapshot());

        var first = AudioTiming.CeilingSample(F(40));
        var d = AudioTiming.SourceOffset(clip.TimelineStart, ((AudioClip)clip).SourceIn);
        Assert.Null(Index(samples[2 * (first - 1)]));
        var audible = preroll < 0 ? first - preroll : first;
        if (preroll < 0) Assert.Null(Index(samples[2 * (audible - 1)]));
        Assert.Equal(audible + d, Index(samples[2 * audible]));
        Assert.Equal(audible + 5_000 + d, Index(samples[2 * (audible + 5_000)]));
    }

    // --- clipping ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sum_is_clamped_to_plus_and_minus_one_after_mixing()
    {
        Ok(_f.Service.AddClip(Music(2, "a.wav", constant: 0.8f).Id));
        Add(Music(2, "b.wav", constant: 0.8f), AudioTrack(), 0);                         // 1.6 → 1
        Add(Music(1, "c.wav", constant: -0.7f), AudioTrack(), 45);                       // from 1.5 s: 1.6 − 0.7 = 0.9 (not clamped first)
        var negative = AudioTrack();
        var d = (AudioClip)Add(Music(1, "d.wav", constant: -0.9f), negative, 0);
        d.Volume = 2;                                                                    // 0.8 + 0.8 − 1.8 = −0.2

        var samples = await AssertMatchesPreview(Snapshot());

        Assert.Equal(-0.2f, samples[2 * 1_000], 5);
        Assert.Equal(1f, samples[2 * 60_000]);                                           // 1.25 s, after d: 1.6 → 1
        Assert.Equal(0.9f, samples[2 * AudioTiming.CeilingSample(F(47))], 5);             // a, b and c: 0.9
        Assert.Equal(-0.7f, samples[2 * 110_000], 5);                                     // 2.29 s: only c
        Assert.All(samples, s => Assert.InRange(s, -1f, 1f));
    }

    [Fact]
    public async Task Only_negative_sources_clip_at_minus_one()
    {
        Ok(_f.Service.AddClip(Music(1, "a.wav", constant: -0.6f).Id));
        Add(Music(1, "b.wav", constant: -0.6f), AudioTrack(), 0);

        var samples = await AssertMatchesPreview(Snapshot());

        Assert.Equal(-1f, samples[2 * 24_000]);
    }

    // --- silence ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_project_without_audio_is_silent_for_its_whole_duration()
    {
        var video = _f.Video(4, FrameRate.Fps25, "no-audio.mp4");                       // no audio stream
        Ok(_f.Service.AddClip(video.Id));
        Ok(_f.Service.AddTrack(TrackType.Video));
        Ok(_f.Service.AddTextClip(F(10)));                                              // text on the new top track

        var snapshot = Snapshot();
        var samples = await AssertMatchesPreview(snapshot);

        Assert.Equal(ExportOutput.For(snapshot).AudioSampleCount * 2, samples.Length);
        Assert.Equal(259_200 * 2, samples.Length);                                        // the text ends at 0.4 + 5 s: 5.4 s at 48 kHz
        Assert.All(samples, s => Assert.Equal(0f, s));
        Assert.Empty(_exportDecoder.Requests);
    }
}
