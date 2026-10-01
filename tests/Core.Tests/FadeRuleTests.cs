using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 10 Step 10.4 (D025 §2, PO-3, PO-8): the fade ramps in frames and samples, the clamp to the clip, the audio
/// envelope and mix, and how the snapshot applies them (layer opacity, occlusion, prefetch edges, presentation-only).
/// </summary>
public class FadeRuleTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n, FrameRate? rate = null) => MediaTime.FromFrame(n, rate ?? Rate);

    // --- ramps --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_ramp_of_F_steps_is_k_plus_1_over_F_plus_1_and_exactly_1_outside()
    {
        Assert.Equal(1.0 / 5, FadeRule.Ramp(0, 4));
        Assert.Equal(4.0 / 5, FadeRule.Ramp(3, 4));
        Assert.Equal(1.0, FadeRule.Ramp(4, 4));
        Assert.Equal(1.0, FadeRule.Ramp(-1, 4));
        Assert.Equal(1.0, FadeRule.Ramp(0, 0));
    }

    [Fact]
    public void The_picture_factor_ramps_in_over_the_first_and_out_over_the_last_frames()
    {
        // Clip [10, 30): fade in 4 frames, fade out 5 frames.
        double At(long n) => FadeRule.PictureFactor(n, 10, 30, 4, 5);

        Assert.Equal(new[] { 1.0 / 5, 2.0 / 5, 3.0 / 5, 4.0 / 5, 1.0 }, new[] { At(10), At(11), At(12), At(13), At(14) });
        Assert.Equal(1.0, At(24));
        Assert.Equal(new[] { 5.0 / 6, 4.0 / 6, 3.0 / 6, 2.0 / 6, 1.0 / 6 }, new[] { At(25), At(26), At(27), At(28), At(29) });
    }

    [Fact]
    public void Overlapping_ramps_multiply()
    {
        // Clip of 4 frames with fade in 4 and fade out 4: frame i gets (i+1)/5 · (4−i)/5.
        for (long i = 0; i < 4; i++)
            Assert.Equal((i + 1) / 5.0 * ((4 - i) / 5.0), FadeRule.PictureFactor(i, 0, 4, 4, 4));
    }

    [Fact]
    public void Effective_fades_are_whole_frames_clamped_to_the_clip_and_off_on_an_edge_with_a_dissolve()
    {
        var clip = new TextClip { TimelineStart = F(10), Duration = F(20), FadeIn = F(25), FadeOut = new MediaTime(F(3).Ticks + 100) };

        Assert.Equal((20L, 3L), FadeRule.EffectiveFrames(clip, Rate));
        Assert.Equal((0L, 3L), FadeRule.EffectiveFrames(clip, Rate, fadeInSuppressed: true));
        Assert.Equal((20L, 0L), FadeRule.EffectiveFrames(clip, Rate, fadeOutSuppressed: true));
    }

    [Fact]
    public void Ramp_edges_are_where_the_factor_reaches_or_leaves_1()
    {
        Assert.Equal(new[] { F(14), F(25) }, FadeRule.RampEdges(F(10), F(30), 4, 5, Rate));
        Assert.Empty(FadeRule.RampEdges(F(10), F(30), 0, 0, Rate));
        Assert.Empty(FadeRule.RampEdges(F(10), F(30), 20, 20, Rate));   // the ramps span the whole clip
    }

    // --- audio envelope and mix -----------------------------------------------------------------------------------------

    private static AudioSpan Sound(long start, long end, long fadeIn, long fadeOut, FrameRate rate) =>
        new(Guid.NewGuid(), Guid.NewGuid(), SpanStatus.Audio, F(start, rate), F(end, rate), MediaTime.Zero, 1.0)
            { FadeInFrames = fadeIn, FadeOutFrames = fadeOut };

    [Theory]
    [InlineData(25, 1)]
    [InlineData(30, 1)]
    [InlineData(30000, 1001)]
    [InlineData(24000, 1001)]
    public void The_sound_ramps_own_the_samples_of_the_ramp_frames(int num, int den)
    {
        var rate = new FrameRate(num, den);
        var envelope = AudioFadeEnvelope.Of(Sound(7, 57, 10, 15, rate), rate);

        Assert.Equal(AudioTiming.CeilingSample(F(7, rate)), envelope.FirstSample);
        Assert.Equal(AudioTiming.CeilingSample(F(17, rate)), envelope.FadeInEnd);
        Assert.Equal(AudioTiming.CeilingSample(F(42, rate)), envelope.FadeOutStart);
        Assert.Equal(AudioTiming.CeilingSample(F(57, rate)), envelope.EndSample);

        var nIn = envelope.FadeInEnd - envelope.FirstSample;
        var nOut = envelope.EndSample - envelope.FadeOutStart;
        Assert.Equal(1.0 / (nIn + 1), envelope.Gain(envelope.FirstSample));
        Assert.Equal((double)nIn / (nIn + 1), envelope.Gain(envelope.FadeInEnd - 1));
        Assert.Equal(1.0, envelope.Gain(envelope.FadeInEnd));
        Assert.Equal(1.0, envelope.Gain(envelope.FadeOutStart - 1));
        Assert.Equal((double)nOut / (nOut + 1), envelope.Gain(envelope.FadeOutStart));
        Assert.Equal(1.0 / (nOut + 1), envelope.Gain(envelope.EndSample - 1));
    }

    [Fact]
    public void Without_fades_the_envelope_is_none()
    {
        var envelope = AudioFadeEnvelope.Of(Sound(0, 50, 0, 0, Rate), Rate);
        Assert.Equal(AudioFadeEnvelope.None, envelope);
        Assert.False(envelope.Affects(0, long.MaxValue));
        Assert.Equal(1.0, envelope.Gain(123));
    }

    [Fact]
    public void Mixing_outside_the_ramps_is_bit_identical_to_the_plain_mix()
    {
        var envelope = AudioFadeEnvelope.Of(Sound(0, 50, 10, 10, Rate), Rate);   // 25 fps: 1920 samples per frame
        var samples = Enumerable.Range(0, 2000).Select(i => MathF.Sin(i * 0.37f) * 0.8f).ToArray();
        var plain = new float[2000];
        var faded = new float[2000];
        AudioMix.Add(samples, plain, 0.7f);
        AudioMix.Add(samples, faded, 0.7f, envelope, firstSample: envelope.FadeInEnd);   // 1000 frames after the fade in

        Assert.Equal(plain, faded);
    }

    [Fact]
    public void Mixing_inside_a_ramp_scales_each_frame_by_its_gain()
    {
        var envelope = AudioFadeEnvelope.Of(Sound(0, 50, 10, 10, Rate), Rate);
        var samples = Enumerable.Repeat(0.5f, 20).ToArray();   // 10 stereo frames
        var mix = new float[20];
        AudioMix.Add(samples, mix, 0.8f, envelope, firstSample: 100);

        for (var f = 0; f < 10; f++)
        {
            var expected = 0.5f * (float)(0.8f * envelope.Gain(100 + f));
            Assert.Equal(expected, mix[2 * f]);
            Assert.Equal(expected, mix[2 * f + 1]);
        }
    }

    // --- the snapshot -------------------------------------------------------------------------------------------------

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };   // canvas 1920 × 1080
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly Track _v2 = new() { Type = TrackType.Video, Name = "V2", Order = 1 };
    private readonly Track _a1 = new() { Type = TrackType.Audio, Name = "A1" };

    public FadeRuleTests()
    {
        _project.Timeline.VideoTracks.AddRange(new[] { _v1, _v2 });
        _project.Timeline.AudioTracks.Add(_a1);
    }

    private VideoClip Video(Track track, long start, long end, double opacity = 1)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(60), FrameRate = Rate, AudioCodec = "aac",
                Width = 1920, Height = 1080, DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080
            }
        };
        _project.MediaAssets.Add(asset);
        var clip = new VideoClip { MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(end) - F(start), Opacity = opacity };
        clip.SourceOut = clip.Duration;
        track.Clips.Add(clip);
        track.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private PlaybackSnapshot Snapshot() => PlaybackSnapshotBuilder.Build(_project, 1);

    [Fact]
    public void A_fading_layer_has_its_opacity_times_the_fade_and_does_not_occlude_during_the_ramp()
    {
        Video(_v1, 0, 100);
        var top = Video(_v2, 0, 100);
        (top.FadeIn, top.FadeOut) = (F(4), F(5));
        var snapshot = Snapshot();

        var first = snapshot.LayersAt(F(0));
        Assert.Equal(2, first.Length);                                   // V1 visible under the fading V2
        Assert.Equal(1.0 / 5, first[1].Opacity);
        Assert.Equal(1.0, first[0].Opacity);

        var middle = snapshot.LayersAt(F(50));
        Assert.Equal(top.Id, Assert.Single(middle).ClipId);              // after the ramp V2 occludes V1 again

        var last = snapshot.LayersAt(F(99));
        Assert.Equal(2, last.Length);
        Assert.Equal(1.0 / 6, last[1].Opacity);
    }

    [Fact]
    public void The_fade_multiplies_the_clips_own_opacity_and_applies_to_text()
    {
        var clip = Video(_v1, 0, 100, opacity: 0.5);
        clip.FadeIn = F(3);
        var text = new TextClip { Text = "Title", TimelineStart = F(0), Duration = F(10), FadeOut = F(2), Opacity = 0.9 };
        _v2.Clips.Add(text);
        var snapshot = Snapshot();

        Assert.Equal(0.5 * (2.0 / 4), snapshot.LayersAt(F(1))[0].Opacity);
        Assert.Equal(0.5, snapshot.LayersAt(F(3))[0].Opacity);
        Assert.Equal(0.9 * (1.0 / 3), Assert.IsType<TextLayer>(snapshot.LayersAt(F(9))[1]).Opacity);
        Assert.Equal(0.9, Assert.IsType<TextLayer>(snapshot.LayersAt(F(7))[1]).Opacity);
    }

    [Fact]
    public void Without_a_fade_the_layer_opacity_is_exactly_the_clips()
    {
        Video(_v1, 0, 100, opacity: 0.3);
        Assert.Equal(0.3, Assert.Single(Snapshot().LayersAt(F(40))).Opacity);
    }

    [Fact]
    public void Ramp_edges_are_picture_changes_for_prefetch()
    {
        var clip = Video(_v2, 10, 100);
        (clip.FadeIn, clip.FadeOut) = (F(5), F(20));
        var snapshot = Snapshot();

        Assert.Equal(F(15), snapshot.NextPictureChange(F(10)));
        Assert.Equal(F(80), snapshot.NextPictureChange(F(15)));
        Assert.Equal(F(100), snapshot.NextPictureChange(F(80)));
    }

    [Fact]
    public void A_fade_change_is_presentation_only()
    {
        var clip = Video(_v1, 0, 100);
        var before = Snapshot();
        clip.FadeIn = F(10);
        clip.FadeOut = F(7);
        var after = Snapshot();

        Assert.True(before.DiffersOnlyInPresentation(after));
        Assert.Equal((10L, 7L), (after.VideoLayers[1].Spans[0].FadeInFrames, after.VideoLayers[1].Spans[0].FadeOutFrames));   // V1 (topmost first)
        Assert.Equal((10L, 7L), (after.AudioSpans[0].FadeInFrames, after.AudioSpans[0].FadeOutFrames));   // the clip's own sound
    }

    [Fact]
    public void Audio_clips_carry_their_fades()
    {
        var asset = new MediaAsset { FilePath = @"C:\media\a.wav", Kind = MediaKind.Audio, Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(60) } };
        _project.MediaAssets.Add(asset);
        var clip = new AudioClip { MediaAssetId = asset.Id, TimelineStart = F(0), Duration = F(50), FadeIn = F(60), FadeOut = F(5) };
        clip.SourceOut = clip.Duration;
        _a1.Clips.Add(clip);

        var span = Assert.Single(Snapshot().AudioSpans);
        Assert.Equal((50L, 5L), (span.FadeInFrames, span.FadeOutFrames));   // the fade in is clamped to the clip
    }

    [Fact]
    public void A_fade_on_an_edge_with_a_dissolve_is_not_applied_and_applies_again_without_it()
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100);
        (a.FadeIn, a.FadeOut) = (F(4), F(6));
        (b.FadeIn, b.FadeOut) = (F(7), F(8));
        var dissolve = new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(10), LeftClipId = a.Id, RightClipId = b.Id };
        _v1.Transitions.Add(dissolve);

        var with = Snapshot();
        Assert.Equal((4L, 0L), (with.VideoLayers[1].Spans[0].FadeInFrames, with.VideoLayers[1].Spans[0].FadeOutFrames));
        Assert.Equal((0L, 8L), (with.VideoLayers[1].Spans[1].FadeInFrames, with.VideoLayers[1].Spans[1].FadeOutFrames));
        Assert.Equal((4L, 0L), (with.AudioSpans[0].FadeInFrames, with.AudioSpans[0].FadeOutFrames));   // the sound too
        Assert.Equal((0L, 8L), (with.AudioSpans[1].FadeInFrames, with.AudioSpans[1].FadeOutFrames));
        Assert.Equal((F(4), F(6)), (a.FadeIn, a.FadeOut));                                              // stored values kept

        _v1.Transitions.Remove(dissolve);
        var without = Snapshot();
        Assert.Equal((4L, 6L), (without.VideoLayers[1].Spans[0].FadeInFrames, without.VideoLayers[1].Spans[0].FadeOutFrames));
        Assert.Equal((7L, 8L), (without.VideoLayers[1].Spans[1].FadeInFrames, without.VideoLayers[1].Spans[1].FadeOutFrames));
    }
}
