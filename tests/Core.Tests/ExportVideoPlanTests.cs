using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>D059 §3–§4: the profiles resolved into the encoding actually used, the NVENC fallback and the size estimate.</summary>
public sealed class ExportVideoPlanTests
{
    private static readonly FrameSize Uhd = new(3840, 2160);
    private static readonly ExportEncoderAvailability WithNvenc = new(true);

    [Fact]
    public void The_default_is_balanced_libx264_with_exactly_the_D028_values()
    {
        var d = ExportEncoding.Default;
        Assert.Equal((ExportProfile.Balanced, ExportVideoEncoder.Libx264, 192), (d.Profile, d.Encoder, d.AudioBitrateKbps));
        var plan = ExportVideoPlan.For(d, Uhd, WithNvenc);
        Assert.Equal((ExportVideoEncoder.Libx264, "medium", 18, ExportRateControl.ConstantQuality, false),
            (plan.Encoder, plan.Preset, plan.Quality, plan.RateControl, plan.FellBack));
        // A D028 Custom(High, Medium, 192) is the same encoding.
        Assert.Equal(plan, ExportVideoPlan.For(new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Medium, 192), Uhd, WithNvenc));
    }

    [Theory]
    [InlineData(ExportProfile.Fast, "veryfast", 18)]
    [InlineData(ExportProfile.Balanced, "medium", 18)]
    [InlineData(ExportProfile.HighQuality, "slow", 16)]
    public void The_libx264_profiles(ExportProfile profile, string preset, int crf)
    {
        var plan = ExportVideoPlan.For(ExportEncoding.Default with { Profile = profile }, Uhd, WithNvenc);
        Assert.Equal((ExportVideoEncoder.Libx264, preset, crf), (plan.Encoder, plan.Preset, plan.Quality));
    }

    [Theory]
    [InlineData(ExportProfile.Fast)]
    [InlineData(ExportProfile.Balanced)]
    [InlineData(ExportProfile.HighQuality)]
    [InlineData(ExportProfile.Custom)]
    public void Nvenc_is_used_only_where_it_is_available_and_falls_back_to_the_same_libx264_profile(ExportProfile profile)
    {
        var nvenc = ExportEncoding.Default with { Profile = profile, Encoder = ExportVideoEncoder.Nvenc };
        var x264 = ExportVideoPlan.For(nvenc.WithLibx264(), Uhd, WithNvenc);

        var used = ExportVideoPlan.For(nvenc, Uhd, WithNvenc);
        Assert.Equal(ExportVideoEncoder.Nvenc, used.Encoder);
        Assert.False(used.FellBack);
        Assert.Matches("^p[1-7]$", used.Preset);

        var none = ExportVideoPlan.For(nvenc, Uhd, ExportEncoderAvailability.SoftwareOnly);
        Assert.Equal(x264 with { FellBack = true }, none);

        // NVENC H.264 encodes at most 4096 px a side: an 8K or 5K canvas falls back.
        var large = ExportVideoPlan.For(nvenc, new FrameSize(4320, 7680), WithNvenc);
        Assert.Equal(ExportVideoEncoder.Libx264, large.Encoder);
        Assert.True(large.FellBack);
        Assert.Equal(ExportVideoEncoder.Nvenc, ExportVideoPlan.For(nvenc, new FrameSize(2160, 3840), WithNvenc).Encoder);
    }

    [Fact]
    public void Custom_quality_levels_map_to_calibrated_cq_values_not_the_crf_numbers()
    {
        foreach (var quality in Enum.GetValues<ExportQuality>())
        {
            var e = new ExportEncoding(quality, ExportSpeedPreset.Medium, 192) { Encoder = ExportVideoEncoder.Nvenc };
            var plan = ExportVideoPlan.For(e, Uhd, WithNvenc);
            Assert.Equal(ExportVideoPlan.NvencCqOf(quality), plan.Quality);
            Assert.NotEqual(ExportEncoding.CrfOf(quality), plan.Quality);
        }
        var levels = Enum.GetValues<ExportQuality>().Select(ExportVideoPlan.NvencCqOf).ToList();
        Assert.Equal(levels.OrderBy(v => v), levels);   // Maximum … Compact: rising CQ
        Assert.Equal(new[] { "p3", "p5", "p7" }, Enum.GetValues<ExportSpeedPreset>().Select(ExportVideoPlan.NvencPresetOf));
    }

    [Fact]
    public void The_bitrate_mode_is_only_custom()
    {
        var bitrate = new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Medium, 192) { RateControl = ExportRateControl.Bitrate, VideoBitrateMbps = 25 };
        var plan = ExportVideoPlan.For(bitrate, Uhd, WithNvenc);
        Assert.Equal((ExportRateControl.Bitrate, 25_000), (plan.RateControl, plan.VideoBitrateKbps));
        Assert.Equal(ExportRateControl.ConstantQuality, ExportVideoPlan.For(bitrate with { Profile = ExportProfile.Balanced }, Uhd, WithNvenc).RateControl);
        Assert.Equal("libx264 medium 25 Mbit/s", plan.Description);
    }

    [Fact]
    public void Validation_refuses_values_not_offered()
    {
        Assert.Null(ExportEncoding.Default.Validate());
        Assert.NotNull((ExportEncoding.Default with { Profile = (ExportProfile)9 }).Validate());
        Assert.NotNull((ExportEncoding.Default with { Encoder = (ExportVideoEncoder)9 }).Validate());
        Assert.NotNull((ExportEncoding.Default with { RateControl = (ExportRateControl)9 }).Validate());
        Assert.NotNull((ExportEncoding.Default with { VideoBitrateMbps = 33 }).Validate());
    }

    // --- the size estimate ---------------------------------------------------------------------------------------------

    private static ExportOutput Output(int w, int h, int fps, double seconds)
    {
        var rate = new FrameRate(fps, 1);
        var frames = (long)Math.Round(seconds * fps);
        return new ExportOutput(new FrameSize(w, h), rate, frames, MediaTime.FromFrame(frames, rate), (long)Math.Round(seconds * 48_000));
    }

    [Fact]
    public void An_average_bitrate_estimate_is_the_bitrate_times_the_length()
    {
        var plan = new ExportVideoPlan(ExportVideoEncoder.Libx264, "medium", 18, ExportRateControl.Bitrate, 40_000, false, false);
        var estimate = ExportSizeEstimate.For(plan, Output(3840, 2160, 60, 600), 192);
        var exact = (40_000 + 192) * 1000.0 / 8 * 600 * (1 + ExportSizeEstimate.ContainerOverhead);
        Assert.True(estimate.FromBitrate);
        Assert.InRange((estimate.LowBytes + estimate.HighBytes) / 2.0, exact * 0.99, exact * 1.01);
        Assert.InRange(estimate.HighBytes / (double)estimate.LowBytes, 1.1, 1.2);
    }

    [Fact]
    public void A_constant_quality_estimate_is_a_range_that_follows_the_quality_the_length_the_size_and_the_encoder()
    {
        ExportSizeEstimate Of(ExportEncoding e, ExportOutput o) => ExportSizeEstimate.For(ExportVideoPlan.For(e, o.Size, WithNvenc), o, 192);
        var uhd = Output(3840, 2160, 60, 60);
        var balanced = Of(ExportEncoding.Default, uhd);
        Assert.False(balanced.FromBitrate);
        Assert.True(balanced.HighBytes > 2 * balanced.LowBytes);                                            // honestly a range
        Assert.True(Of(ExportEncoding.Default, Output(3840, 2160, 60, 120)).LowBytes > 1.9 * balanced.LowBytes);  // twice as long
        Assert.True(Of(ExportEncoding.Default, Output(1920, 1080, 60, 60)).HighBytes < balanced.HighBytes);       // a smaller canvas
        Assert.True(Of(new ExportEncoding(ExportQuality.Compact, ExportSpeedPreset.Medium, 192), uhd).HighBytes < balanced.LowBytes * 2);
        Assert.True(Of(new ExportEncoding(ExportQuality.Maximum, ExportSpeedPreset.Medium, 192), uhd).LowBytes > balanced.LowBytes);
        var nvenc = Of(ExportEncoding.Default with { Encoder = ExportVideoEncoder.Nvenc }, uhd);
        Assert.NotEqual(balanced, nvenc);                                                                     // switching updates it
        // A 4K60 minute at the default lies in the tens to hundreds of MB (measured ~32 Mbit/s → ~240 MB).
        Assert.InRange(balanced.LowBytes, 50L << 20, 400L << 20);
        Assert.InRange(balanced.HighBytes, 150L << 20, 900L << 20);
    }
}
