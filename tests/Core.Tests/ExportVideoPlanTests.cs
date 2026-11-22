using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>D059 §3–§4, D076: the settings resolved into the encoding actually used per codec, the NVENC fallback (the same
/// codec), the built-in presets and the size estimate.</summary>
public sealed class ExportVideoPlanTests
{
    private static readonly FrameSize Uhd = new(3840, 2160);
    private static readonly FrameSize FullHd = new(1920, 1080);
    private static readonly ExportEncoderAvailability WithNvenc = new(true) { NvencHevc = true, NvencAv1 = true };

    [Fact]
    public void The_default_is_libx264_medium_crf_18_mp4_exactly_the_D028_values()
    {
        var d = ExportEncoding.Default;
        Assert.Equal((ExportVideoCodec.H264, ExportContainer.Mp4, ExportVideoEncoder.Software, 192), (d.Codec, d.Container, d.Encoder, d.AudioBitrateKbps));
        var plan = ExportVideoPlan.For(d, Uhd, WithNvenc);
        Assert.Equal((ExportVideoEncoder.Software, "medium", 18, ExportRateControl.ConstantQuality, false),
            (plan.Encoder, plan.Preset, plan.Quality, plan.RateControl, plan.FellBack));
        Assert.Equal("libx264 medium CRF 18", plan.Description);
    }

    [Theory]   // D059's profiles, read as explicit settings (D076 Q7 A): the same encodings as before
    [InlineData(ExportSpeedPreset.VeryFast, ExportQuality.High, "veryfast", 18, "p2", 25, false)]   // «Быстрый»
    [InlineData(ExportSpeedPreset.Medium, ExportQuality.High, "medium", 18, "p5", 22, false)]       // «Сбалансированный»
    [InlineData(ExportSpeedPreset.Slow, ExportQuality.Maximum, "slow", 14, "p7", 19, true)]         // «Высокое качество» (D074 §6)
    public void The_old_profiles_keep_their_h264_encodings(ExportSpeedPreset speed, ExportQuality quality, string x264Preset, int crf,
        string nvencPreset, int cq, bool multipass)
    {
        var e = new ExportEncoding(quality, speed, 192);
        var x264 = ExportVideoPlan.For(e, Uhd, WithNvenc);
        Assert.Equal((ExportVideoEncoder.Software, x264Preset, crf), (x264.Encoder, x264.Preset, x264.Quality));
        var nvenc = ExportVideoPlan.For(e with { Encoder = ExportVideoEncoder.Nvenc }, Uhd, WithNvenc);
        Assert.Equal((ExportVideoEncoder.Nvenc, nvencPreset, cq, multipass), (nvenc.Encoder, nvenc.Preset, nvenc.Quality, nvenc.Multipass));
    }

    [Theory]
    [InlineData(ExportVideoCodec.H264, "libx264", "NVENC")]
    [InlineData(ExportVideoCodec.Hevc, "libx265", "NVENC HEVC")]
    [InlineData(ExportVideoCodec.Av1, "SVT-AV1", "NVENC AV1")]
    public void Nvenc_falls_back_to_the_processor_with_the_same_codec(ExportVideoCodec codec, string software, string hardware)
    {
        var nvenc = new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Medium, 192) { Codec = codec, Encoder = ExportVideoEncoder.Nvenc };
        var used = ExportVideoPlan.For(nvenc, Uhd, WithNvenc);
        Assert.Equal((ExportVideoEncoder.Nvenc, codec, false, hardware), (used.Encoder, used.Codec, used.FellBack, used.EncoderName));

        var none = ExportVideoPlan.For(nvenc, Uhd, ExportEncoderAvailability.SoftwareOnly);
        Assert.Equal(ExportVideoPlan.For(nvenc.WithSoftware(), Uhd, WithNvenc) with { FellBack = true }, none);
        Assert.Equal((codec, software), (none.Codec, none.EncoderName));

        // Only this codec's NVENC counts: H.264 NVENC alone does not encode HEVC / AV1.
        var h264Only = ExportVideoPlan.For(nvenc, Uhd, new ExportEncoderAvailability(true));
        Assert.Equal(codec == ExportVideoCodec.H264 ? ExportVideoEncoder.Nvenc : ExportVideoEncoder.Software, h264Only.Encoder);
    }

    [Fact]
    public void Nvenc_h264_stops_at_4096_px_hevc_and_av1_at_8192()
    {
        var size = new FrameSize(4320, 7680);
        foreach (var codec in Enum.GetValues<ExportVideoCodec>())
        {
            var e = ExportEncoding.Default with { Codec = codec, Encoder = ExportVideoEncoder.Nvenc };
            var plan = ExportVideoPlan.For(e, size, WithNvenc);
            Assert.Equal(codec == ExportVideoCodec.H264, plan.FellBack);
        }
    }

    [Fact]
    public void A_codec_with_neither_nvenc_nor_its_software_encoder_is_unavailable()
    {
        var noSvt = ExportEncoderAvailability.SoftwareOnly with { SoftwareAv1 = false };
        Assert.True(ExportVideoPlan.For(ExportEncoding.Default with { Codec = ExportVideoCodec.Av1 }, Uhd, noSvt).Unavailable);
        Assert.False(ExportVideoPlan.For(ExportEncoding.Default with { Codec = ExportVideoCodec.Av1, Encoder = ExportVideoEncoder.Nvenc }, Uhd,
            noSvt with { Nvenc = true, NvencAv1 = true }).Unavailable);
        Assert.False(ExportVideoPlan.For(ExportEncoding.Default, Uhd, noSvt).Unavailable);   // H.264 always
    }

    [Fact]
    public void Every_encoder_maps_the_quality_levels_to_rising_values_never_the_x264_numbers_for_nvenc()
    {
        foreach (var codec in Enum.GetValues<ExportVideoCodec>())
        foreach (var encoder in Enum.GetValues<ExportVideoEncoder>())
        {
            var values = Enum.GetValues<ExportQuality>().Select(q => ExportVideoPlan.For(
                new ExportEncoding(q, ExportSpeedPreset.Medium, 192) { Codec = codec, Encoder = encoder }, Uhd, WithNvenc).Quality).ToList();
            Assert.Equal(values.OrderBy(v => v), values);   // Maximum … Compact: a rising CRF / CQ
            Assert.Equal(values.Count, values.Distinct().Count());
        }
        foreach (var q in Enum.GetValues<ExportQuality>()) Assert.NotEqual(ExportEncoding.CrfOf(q), ExportVideoPlan.NvencCqOf(q));
        Assert.Equal(new[] { "p2", "p3", "p5", "p7" }, Enum.GetValues<ExportSpeedPreset>().Select(ExportVideoPlan.NvencPresetOf));
        Assert.Equal(new[] { "11", "9", "7", "5" }, Enum.GetValues<ExportSpeedPreset>().Select(s => ExportVideoPlan.SoftwarePreset(ExportVideoCodec.Av1, s)));
    }

    [Fact]
    public void The_bitrate_mode_works_with_every_codec()
    {
        var bitrate = new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Medium, 192) { RateControl = ExportRateControl.Bitrate, VideoBitrateMbps = 25 };
        var plan = ExportVideoPlan.For(bitrate, Uhd, WithNvenc);
        Assert.Equal((ExportRateControl.Bitrate, 25_000), (plan.RateControl, plan.VideoBitrateKbps));
        Assert.Equal("libx264 medium 25 Mbit/s", plan.Description);
        Assert.Equal("NVENC HEVC p5 25 Mbit/s", ExportVideoPlan.For(bitrate with { Codec = ExportVideoCodec.Hevc, Encoder = ExportVideoEncoder.Nvenc }, Uhd, WithNvenc).Description);
        Assert.Equal("SVT-AV1 preset 7 25 Mbit/s", ExportVideoPlan.For(bitrate with { Codec = ExportVideoCodec.Av1 }, Uhd, WithNvenc).Description);
    }

    [Fact]
    public void Validation_refuses_values_not_offered_and_av1_in_mov()
    {
        Assert.Null(ExportEncoding.Default.Validate());
        Assert.NotNull((ExportEncoding.Default with { Encoder = (ExportVideoEncoder)9 }).Validate());
        Assert.NotNull((ExportEncoding.Default with { RateControl = (ExportRateControl)9 }).Validate());
        Assert.NotNull((ExportEncoding.Default with { VideoBitrateMbps = 33 }).Validate());
        Assert.NotNull((ExportEncoding.Default with { Codec = (ExportVideoCodec)9 }).Validate());
        Assert.NotNull((ExportEncoding.Default with { Container = (ExportContainer)9 }).Validate());
        Assert.NotNull((ExportEncoding.Default with { Codec = ExportVideoCodec.Av1, Container = ExportContainer.Mov }).Validate());
        Assert.Null((ExportEncoding.Default with { Codec = ExportVideoCodec.Av1, Container = ExportContainer.Mkv }).Validate());
        Assert.Null((ExportEncoding.Default with { Codec = ExportVideoCodec.Hevc, Container = ExportContainer.Mov }).Validate());
    }

    // --- the built-in presets (D076 Q2) --------------------------------------------------------------------------------

    [Fact]
    public void The_presets_state_every_field_and_a_new_project_is_maximum()
    {
        Assert.Equal((ExportVideoCodec.H264, ExportResolution.FullHd, ExportSpeedPreset.VeryFast, ExportQuality.High, 192),
            (ExportPresets.Fast.Codec, ExportPresets.Fast.Resolution, ExportPresets.Fast.Preset, ExportPresets.Fast.Quality, ExportPresets.Fast.AudioBitrateKbps));
        Assert.Equal((ExportVideoCodec.Av1, ExportResolution.Qhd, ExportSpeedPreset.Medium, ExportQuality.High),
            (ExportPresets.Medium.Codec, ExportPresets.Medium.Resolution, ExportPresets.Medium.Preset, ExportPresets.Medium.Quality));
        Assert.Equal((ExportVideoCodec.H264, ExportResolution.Uhd4K, ExportSpeedPreset.Slow, ExportQuality.VeryHigh, 256),
            (ExportPresets.High.Codec, ExportPresets.High.Resolution, ExportPresets.High.Preset, ExportPresets.High.Quality, ExportPresets.High.AudioBitrateKbps));
        Assert.Equal((ExportVideoCodec.H264, ExportResolution.Project, ExportSpeedPreset.Slow, ExportQuality.Maximum, 320),
            (ExportPresets.Maximum.Codec, ExportPresets.Maximum.Resolution, ExportPresets.Maximum.Preset, ExportPresets.Maximum.Quality, ExportPresets.Maximum.AudioBitrateKbps));
        foreach (var kind in ExportPresets.All)
        {
            Assert.Equal(ExportVideoEncoder.Nvenc, ExportPresets.Of(kind).Encoder);
            Assert.Null(ExportPresets.Of(kind).Validate());
        }
        Assert.Equal(ExportPresets.Maximum, ExportEncoding.NewProject);
        var plan = ExportVideoPlan.For(ExportEncoding.NewProject, Uhd, WithNvenc);
        // The owner (D076 §3, after the calibration): «Максимальный» is H.264 — D074 §6's «Высокое качество» with AAC 320.
        Assert.Equal((ExportVideoEncoder.Nvenc, ExportVideoCodec.H264, "p7", 19, true), (plan.Encoder, plan.Codec, plan.Preset, plan.Quality, plan.Multipass));
    }

    [Fact]
    public void A_preset_resolution_is_a_ceiling_at_the_canvas()   // Q4 A
    {
        // A Full HD canvas: «Высокий» (4K) and «Средний» (2K) export the canvas's own size, never enlarged.
        Assert.Equal(ExportResolution.Project, ExportPresets.Apply(ExportPresets.High, FullHd).Resolution);
        Assert.Equal(ExportResolution.Project, ExportPresets.Apply(ExportPresets.Medium, FullHd).Resolution);
        Assert.Equal(ExportResolution.Project, ExportPresets.Apply(ExportPresets.Fast, FullHd).Resolution);
        // A 4K canvas: the presets' own sizes.
        Assert.Equal(ExportResolution.FullHd, ExportPresets.Apply(ExportPresets.Fast, Uhd).Resolution);
        Assert.Equal(ExportResolution.Qhd, ExportPresets.Apply(ExportPresets.Medium, Uhd).Resolution);
        Assert.Equal(ExportResolution.Project, ExportPresets.Apply(ExportPresets.High, Uhd).Resolution);
        Assert.Equal(new FrameSize(2560, 1440), ExportResolutions.SizeFor(ExportResolution.Qhd, Uhd));
        Assert.Equal(new FrameSize(1440, 2560), ExportResolutions.SizeFor(ExportResolution.Qhd, new FrameSize(2160, 3840)));
    }

    [Fact]
    public void Choosing_a_preset_keeps_the_file_type_but_never_av1_in_mov_and_matching_ignores_it()
    {
        Assert.Equal(ExportContainer.Mkv, ExportPresets.Apply(ExportPresets.Maximum, Uhd, ExportContainer.Mkv).Container);
        var av1 = ExportPresets.Maximum with { Codec = ExportVideoCodec.Av1 };
        Assert.Equal(ExportContainer.Mp4, ExportPresets.Apply(av1, Uhd, ExportContainer.Mov).Container);

        Assert.Equal(ExportPresetKind.Maximum, ExportPresets.Match(ExportPresets.Maximum with { Container = ExportContainer.Mov }, Uhd));
        Assert.Equal(ExportPresetKind.High, ExportPresets.Match(ExportPresets.High with { Resolution = ExportResolution.Project }, Uhd));
        // On a Full HD canvas «Высокий» and «Максимальный» differ by quality and sound, never by the size.
        Assert.Equal(ExportPresetKind.High, ExportPresets.Match(ExportPresets.Apply(ExportPresets.High, FullHd), FullHd));
        Assert.Null(ExportPresets.Match(ExportPresets.Maximum with { AudioBitrateKbps = 192 }, Uhd));
        Assert.Null(ExportPresets.Match(ExportEncoding.Default, Uhd));   // an old project's settings: «Пользовательский» (Q7 A)
    }

    [Fact]
    public void A_size_chosen_by_hand_above_the_canvas_is_no_preset_and_the_bitrate_field_does_not_count_at_constant_quality()
    {
        // A Full HD canvas, «Высокий»'s fields but 4K chosen by hand (D060 §3 allows it): it exports 3840 × 2160, «Высокий» here
        // exports 1920 × 1080 — not the same output.
        Assert.Null(ExportPresets.Match(ExportPresets.High, FullHd));
        Assert.Equal(ExportPresetKind.High, ExportPresets.Match(ExportPresets.High with { Resolution = ExportResolution.Project }, FullHd));
        Assert.Equal(ExportPresetKind.Maximum, ExportPresets.Match(ExportPresets.Maximum with { VideoBitrateMbps = 80 }, Uhd));
        Assert.Null(ExportPresets.Match(ExportPresets.Maximum with { RateControl = ExportRateControl.Bitrate }, Uhd));
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
        var plan = new ExportVideoPlan(ExportVideoEncoder.Software, "medium", 18, ExportRateControl.Bitrate, 40_000, false, false);
        var estimate = ExportSizeEstimate.For(plan, Output(3840, 2160, 60, 600), 192);
        var exact = (40_000 + 192) * 1000.0 / 8 * 600 * (1 + ExportSizeEstimate.ContainerOverhead);
        Assert.True(estimate.FromBitrate);
        Assert.InRange((estimate.LowBytes + estimate.HighBytes) / 2.0, exact * 0.99, exact * 1.01);
        Assert.InRange(estimate.HighBytes / (double)estimate.LowBytes, 1.1, 1.2);
    }

    [Fact]
    public void A_constant_quality_estimate_is_a_range_that_follows_the_quality_the_length_the_size_the_encoder_and_the_codec()
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
        Assert.NotEqual(balanced, Of(ExportEncoding.Default with { Encoder = ExportVideoEncoder.Nvenc }, uhd));   // switching updates it
        // D076 §5 (measured): on NVENC, HEVC and AV1 hold the same picture in fewer bits than H.264 at this level (AV1 ≈ 30 % fewer);
        // the codec changes the estimate.
        var h264 = Of(ExportEncoding.Default with { Encoder = ExportVideoEncoder.Nvenc }, uhd);
        var hevc = Of(ExportEncoding.Default with { Encoder = ExportVideoEncoder.Nvenc, Codec = ExportVideoCodec.Hevc }, uhd);
        var av1 = Of(ExportEncoding.Default with { Encoder = ExportVideoEncoder.Nvenc, Codec = ExportVideoCodec.Av1 }, uhd);
        Assert.True(hevc.HighBytes < h264.HighBytes);
        Assert.True(av1.HighBytes < hevc.HighBytes);
        Assert.NotEqual(balanced, Of(ExportEncoding.Default with { Codec = ExportVideoCodec.Hevc }, uhd));
        // A 4K60 minute at the default lies in the tens to hundreds of MB (measured ~32 Mbit/s → ~240 MB).
        Assert.InRange(balanced.LowBytes, 50L << 20, 400L << 20);
        Assert.InRange(balanced.HighBytes, 150L << 20, 900L << 20);
    }
}
