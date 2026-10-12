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
    private async Task<float[]> PreviewAudio(PlaybackSnapshot snapshot, long count, int chunkFrames = 4800, long start = 0,
        TimeSpan? lookahead = null)
    {
        var mixer = new AudioMixer();
        mixer.Reset(start);
        var settings = lookahead is { } ahead ? new PlaybackSettings { AudioLookahead = ahead } : new PlaybackSettings();
        await using var pipeline = new AudioPipeline(snapshot, 1, start, mixer, _previewDecoder, settings, NullLogger.Instance);
        var all = new float[count * 2];
        for (long from = start; from < count; from += chunkFrames)
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

    // --- fades (Phase 10 Step 10.4, D025 §2) ------------------------------------------------------------------------

    private void Fades(Clip clip, long fadeIn, long fadeOut) =>
        Ok(_f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Fade = new FadeProperties(F(fadeIn), F(fadeOut)) }));

    [Theory]
    [InlineData(20)]
    [InlineData(5)]
    [InlineData(40)]
    public async Task Fades_mix_every_sample_like_the_preview(int speedSteps)
    {
        var video = Video(FrameRate.Ntsc30, 12, "video.mp4");
        Ok(_f.Service.AddClip(video.Id));                                               // locks 29.97
        var videoClip = (VideoClip)_f.V1.Clips.Single();
        Ok(_f.Service.TrimClip(videoClip.Id, ClipEdge.End, F(150)));
        Ok(_f.Service.SetClipProperties(videoClip.Id, new ClipPropertyChange { Audio = new AudioProperties(2.0, false) }));   // 200 %
        Fades(videoClip, 20, 33);
        Ok(_f.Service.Split(F(10), new[] { videoClip.Id }));                             // inside the fade in

        var music = Music(speedSteps == 5 ? 3 : 10, "music.wav");
        var main = Add(music, _f.A1, 7);
        Ok(_f.Service.SetClipSpeed(main.Id, ClipSpeed.FromSteps(speedSteps)));
        Ok(_f.Service.TrimClip(main.Id, ClipEdge.End, F(7 + 60)));
        Fades(main, 45, 45);                                                             // overlapping ramps

        var muted = Add(Music(4, "muted.wav", constant: 0.4f), AudioTrack(), 30);
        Ok(_f.Service.SetClipProperties(muted.Id, new ClipPropertyChange { Audio = new AudioProperties(1, true) }));
        Fades(muted, 10, 10);
        _f.AssertValid();

        await AssertMatchesPreview(Snapshot());
    }

    [Fact]
    public async Task A_fading_constant_follows_the_envelope_sample_by_sample()
    {
        var video = _f.Video(4, FrameRate.Fps25, "picture.mp4");                        // locks 25 fps, no sound
        Ok(_f.Service.AddClip(video.Id));
        var music = Music(4, "tone.wav", constant: 0.5f);
        var clip = Add(music, _f.A1, 0);
        Ok(_f.Service.TrimClip(clip.Id, ClipEdge.End, F(50)));
        Fades(clip, 10, 20);
        var snapshot = Snapshot();
        var envelope = AudioFadeEnvelope.Of(snapshot.AudioSpans.Single(), snapshot.FrameRate);
        Assert.Equal((0L, 19_200L, 57_600L, 96_000L), (envelope.FirstSample, envelope.FadeInEnd, envelope.FadeOutStart, envelope.EndSample));

        var samples = await AssertMatchesPreview(snapshot);

        for (long k = 0; k < 96_000; k++)
            Assert.Equal(0.5f * (float)(1f * envelope.Gain(k)), samples[2 * k]);
        Assert.Equal(0.5f / 19_201, samples[0], 1e-9f);                                  // the first sample of the ramp
        Assert.Equal(0.5f, samples[2 * 19_200]);                                         // the ramp is over
        Assert.Equal(0f, samples[2 * 96_000]);                                           // after the clip: silence
    }

    // --- dissolves (Phase 10 Step 10.7, D025 PO-5) ------------------------------------------------------------------

    [Fact]
    public async Task A_dissolve_leaves_the_sound_as_a_hard_cut()
    {
        var video = Video(FrameRate.Ntsc30, 12, "video.mp4");
        Ok(_f.Service.AddClip(video.Id));
        Ok(_f.Service.TrimClip(_f.V1.Clips.Single().Id, ClipEdge.End, F(200)));
        Ok(_f.Service.Split(F(100)));
        var (a, b) = (_f.V1.Clips[0], _f.V1.Clips[1]);
        var without = await ExportAudio(Snapshot());

        Ok(_f.Service.AddTransition(a.Id, b.Id, F(30)));
        var with = await AssertMatchesPreview(Snapshot());

        Assert.Equal(without, with);
    }

    // --- crossfades (Phase 17 Step 17.6, D032 R9) -------------------------------------------------------------------

    /// <summary>25 fps (a silent picture locks it); A1: A = a.wav [0, 50) from its start, B = b.wav [50, 100) from its
    /// frame 20 — a crossfade of <paramref name="frames"/> on the cut at frame 50.</summary>
    private (AudioClip A, AudioClip B) Crossfaded(long frames, float? constantA = null, float? constantB = null)
    {
        Ok(_f.Service.AddClip(_f.Video(8, FrameRate.Fps25, "picture.mp4").Id));
        var a = (AudioClip)Add(Music(8, "a.wav", constantA), _f.A1, 0);
        Ok(_f.Service.TrimClip(a.Id, ClipEdge.End, F(50)));
        var b = (AudioClip)Add(Music(8, "b.wav", constantB), _f.A1, 50);
        Ok(_f.Service.TrimClip(b.Id, ClipEdge.Start, F(70)));                          // source from its frame 20
        Ok(_f.Service.MoveClips(new[] { b.Id }, -20));
        Ok(_f.Service.TrimClip(b.Id, ClipEdge.End, F(100)));
        Ok(_f.Service.AddTransition(a.Id, b.Id, F(frames)));
        Assert.Equal(TransitionRules.Crossfade, _f.A1.Transitions.Single().TransitionTypeId);
        _f.AssertValid();
        return (a, b);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(30)]
    public async Task A_crossfade_mixes_every_sample_like_the_preview(long frames)
    {
        Crossfaded(frames);
        await AssertMatchesPreview(Snapshot());
    }

    [Fact]
    public async Task A_crossfade_of_two_constants_is_linear_and_sums_to_one_across_the_zone()
    {
        Crossfaded(10, constantA: 0.5f, constantB: 0.25f);
        var snapshot = Snapshot();
        var (spanA, spanB) = (snapshot.AudioSpans[0], snapshot.AudioSpans[1]);
        var (envA, envB) = (AudioFadeEnvelope.Of(spanA, snapshot.FrameRate), AudioFadeEnvelope.Of(spanB, snapshot.FrameRate));
        Assert.Equal((86_400L, 105_600L), (envB.CrossfadeInStart, envB.CrossfadeInEnd));     // the zone [45, 55) of 25 fps
        Assert.Equal((86_400L, 105_600L), (envA.CrossfadeOutStart, envA.CrossfadeOutEnd));
        Assert.Equal((86_400L, 105_600L), (AudioPlacement.Of(spanB).FirstSample, AudioPlacement.Of(spanA).EndSample));

        var samples = await AssertMatchesPreview(snapshot);

        for (long k = 0; k < 192_000; k++)
        {
            var expected = 0f;
            if (k < 105_600) expected += 0.5f * (float)envA.Gain(k);
            if (k >= 86_400) expected += 0.25f * (float)envB.Gain(k);
            Assert.True(expected == samples[2 * k], $"sample {k}: {samples[2 * k]} ≠ {expected}");
        }
        for (long j = 0; j < 19_200; j++)
        {
            var b = (j + 1) / 19_201.0;
            Assert.Equal(b, envB.Gain(86_400 + j), 12);
            Assert.Equal(1 - b, envA.Gain(86_400 + j), 12);
        }
        Assert.Equal(0.5f, samples[2 * 86_399]);                                         // before the zone: A alone
        Assert.Equal(0.25f, samples[2 * 105_600]);                                       // after it: B alone
    }

    [Fact]
    public async Task The_crossfade_plays_the_source_beyond_the_clips_by_their_own_mapping()
    {
        var (a, b) = Crossfaded(10);
        Ok(_f.Service.SetClipProperties(b.Id, new ClipPropertyChange { Audio = new AudioProperties(1, true) }));   // A alone
        var snapshot = Snapshot();
        var envA = AudioFadeEnvelope.Of(snapshot.AudioSpans[0], snapshot.FrameRate);
        var samples = await AssertMatchesPreview(snapshot);
        for (long k = 86_400; k < 105_600; k++)                                            // A's own source sample k, past its end too
            Assert.Equal(k * FakeAudioSource.Unit * (float)envA.Gain(k), samples[2 * k]);

        Ok(_f.Service.SetClipProperties(b.Id, new ClipPropertyChange { Audio = new AudioProperties(1, false) }));
        Ok(_f.Service.SetClipProperties(a.Id, new ClipPropertyChange { Audio = new AudioProperties(1, true) }));    // B alone
        snapshot = Snapshot();
        var envB = AudioFadeEnvelope.Of(snapshot.AudioSpans[1], snapshot.FrameRate);
        samples = await AssertMatchesPreview(snapshot);
        for (long k = 86_400; k < 105_600; k++)                                            // B's source: k − 30 frames (its start 50, SourceIn 20)
            Assert.Equal((k - 57_600) * FakeAudioSource.Unit * (float)envB.Gain(k), samples[2 * k]);
    }

    [Fact]
    public async Task Playback_started_inside_the_zone_plays_both_extensions_like_the_export()
    {
        Crossfaded(20);                                                                   // zone [40, 60): 76 800 … 115 200
        var snapshot = Snapshot();
        var export = await ExportAudio(snapshot);
        foreach (var start in new[] { 0L, 80_000L, 100_000L, 110_000L })                 // before the cut, after A's end
        {
            // A look-ahead shorter than B's extension: B's reader must open for the zone, not for its own start.
            var preview = await PreviewAudio(snapshot, export.Length / 2, chunkFrames: 960, start: start, lookahead: TimeSpan.FromMilliseconds(50));
            for (var i = (int)(2 * start); i < export.Length; i++)
                Assert.True(export[i] == preview[i], $"from {start}: sample {i / 2} ch {i % 2}: preview {preview[i]} ≠ export {export[i]}");
        }
    }

    [Fact]
    public async Task A_dissolve_on_a_video_track_still_cuts_the_sound_hard_and_its_spans_have_no_crossfade()
    {
        var video = Video(FrameRate.Ntsc30, 12, "video.mp4");
        Ok(_f.Service.AddClip(video.Id));
        Ok(_f.Service.TrimClip(_f.V1.Clips.Single().Id, ClipEdge.End, F(200)));
        Ok(_f.Service.Split(F(100)));
        Ok(_f.Service.AddTransition(_f.V1.Clips[0].Id, _f.V1.Clips[1].Id, F(30)));
        Assert.All(Snapshot().AudioSpans, s => Assert.True(s.CrossfadeIn is null && s.CrossfadeOut is null));
        await AssertMatchesPreview(Snapshot());
    }
}
