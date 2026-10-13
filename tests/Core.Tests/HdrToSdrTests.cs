using System.Globalization;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 18 Step 18.3 (D033 §3, §5): the HDR → SDR rule — HLG / PQ transfer functions, the BT.2100 OOTF on luminance,
/// the gamut compression, mobius on max RGB, BT.1886, the order of the steps (each once), the signal domain's
/// boundaries, the PQ peak, the asset classification and the LUT generator. Expected values are the BT.2100 / ST 2084
/// formulas evaluated independently (the pre-flight's reference, D033 "Context"), not this implementation's output.
/// </summary>
public class HdrToSdrTests
{
    private static double[] Out(HdrTransfer transfer, int peak, double r, double g, double b)
    {
        var output = new double[3];
        HdrToSdr.Convert(transfer, peak, new[] { r, g, b }, output);
        return output;
    }

    private static double[] Hlg(double r, double g, double b) => Out(HdrTransfer.Hlg, 1000, r, g, b);
    private static double[] Hlg(double v) => Hlg(v, v, v);
    private static int Code(double v) => (int)Math.Round(v * 255, MidpointRounding.AwayFromZero);

    private static double[] Linear(HdrTransfer transfer, double r, double g, double b)
    {
        var linear = new double[3];
        HdrToSdr.ToLinear(transfer, new[] { r, g, b }, linear);
        return linear;
    }

    // --- transfer functions ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    [InlineData(1.09)]
    public void The_HLG_OETF_and_its_inverse_are_each_others_inverse(double signal) =>
        Assert.Equal(signal, HdrToSdr.HlgOetf(HdrToSdr.HlgInverseOetf(signal)), 12);

    [Fact]
    public void HLG_scene_light_follows_BT_2100()
    {
        Assert.Equal(0.0, HdrToSdr.HlgInverseOetf(0));
        Assert.Equal(1.0 / 12, HdrToSdr.HlgInverseOetf(0.5), 12);           // the knee
        Assert.Equal(0.2649626, HdrToSdr.HlgInverseOetf(0.75), 7);           // reference white
        Assert.Equal(1.0, HdrToSdr.HlgInverseOetf(1.0), 7);
        Assert.Equal(1.6400, HdrToSdr.HlgInverseOetf((1019 - 64) / 876.0), 3); // 10-bit limited code 1019, above 1
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(100.0, 0.508078)]
    [InlineData(203.0, 0.580690)]
    [InlineData(1000.0, 0.751827)]
    [InlineData(10000.0, 1.0)]
    public void The_PQ_EOTF_and_its_inverse_follow_ST_2084(double nits, double signal)
    {
        Assert.Equal(signal, HdrToSdr.PqInverseEotf(nits), 5);
        Assert.Equal(nits, HdrToSdr.PqEotfNits(HdrToSdr.PqInverseEotf(nits)), 6);
    }

    // --- light: the OOTF, once, on luminance; none for PQ ------------------------------------------------------------

    [Fact]
    public void HLG_reference_white_is_203_nit_and_signal_1_is_1000_nit()
    {
        Assert.Equal(203.15 / 203, Linear(HdrTransfer.Hlg, 0.75, 0.75, 0.75)[0], 4);
        Assert.Equal(1000.0 / 203, Linear(HdrTransfer.Hlg, 1, 1, 1)[1], 5);   // BT.2100's rounded a, b, c: 1 − 3·10⁻⁸
    }

    [Fact]
    public void The_HLG_OOTF_is_applied_on_luminance_not_per_channel()
    {
        // BT.2100: F = Lw · Ys^0.2 · E — the colour's channel ratios are kept. zimg's per-channel form (zscale agamma=1)
        // would give (1.3631, 0.7404, 0.4180) here (D033 "Context").
        var light = Linear(HdrTransfer.Hlg, 0.8, 0.7, 0.6);
        Assert.Equal(1.2665, light[0], 4);
        Assert.Equal(0.7616, light[1], 4);
        Assert.Equal(0.4729, light[2], 4);

        var e = new[] { HdrToSdr.HlgInverseOetf(0.8), HdrToSdr.HlgInverseOetf(0.7), HdrToSdr.HlgInverseOetf(0.6) };
        Assert.Equal(e[0] / e[2], light[0] / light[2], 10);
    }

    [Fact]
    public void PQ_has_no_OOTF_its_light_is_the_EOTF_per_channel()
    {
        var light = Linear(HdrTransfer.Pq, 0.7, 0.55, 0.4);
        Assert.Equal(HdrToSdr.PqEotfNits(0.7) / 203, light[0], 12);
        Assert.Equal(HdrToSdr.PqEotfNits(0.55) / 203, light[1], 12);
        Assert.Equal(HdrToSdr.PqEotfNits(0.4) / 203, light[2], 12);
        Assert.Equal(1.0, Linear(HdrTransfer.Pq, 0.580690, 0.580690, 0.580690)[0], 4);   // 203 nit → 1
    }

    [Fact]
    public void The_BT_2020_to_BT_709_matrix_is_ITU_R_BT_2087s_and_keeps_neutral_colours_neutral()
    {
        double[,] bt2087 = { { 1.6605, -0.5876, -0.0728 }, { -0.1246, 1.1329, -0.0083 }, { -0.0182, -0.1006, 1.1187 } };
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
                Assert.Equal(bt2087[r, c], HdrToSdr.Bt2020ToBt709[r, c], 4);
            Assert.Equal(1.0, HdrToSdr.Bt2020ToBt709[r, 0] + HdrToSdr.Bt2020ToBt709[r, 1] + HdrToSdr.Bt2020ToBt709[r, 2], 12);
        }
    }

    // --- mobius ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Mobius_is_the_vf_tonemap_curve()
    {
        const double peak = 1000.0 / 203;
        Assert.Equal(0.25, HdrToSdr.Mobius(0.25, 0.3, peak));             // identity up to j
        Assert.Equal(0.3, HdrToSdr.Mobius(0.3, 0.3, peak));
        Assert.Equal(0.678648, HdrToSdr.Mobius(1.0, 0.3, peak), 6);       // reference white
        Assert.Equal(1.0, HdrToSdr.Mobius(peak, 0.3, peak), 4);           // the peak reaches 1
        var previous = 0.0;
        for (var x = 0.0; x <= 2 * peak; x += 0.01)
        {
            var y = HdrToSdr.Mobius(x, 0.3, peak);
            Assert.True(y >= previous, $"mobius decreases at {x}");
            previous = y;
        }
    }

    // --- gamut compression -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.5, 0.5, 0.5)]   // neutral
    [InlineData(0.6, 0.4, 0.3)]   // skin
    [InlineData(0.9, 0.8, 0.7)]   // a beige wall
    [InlineData(0.0, 0.0, 0.0)]
    public void Gamut_compression_leaves_neutral_and_low_saturation_colours_alone(double r, double g, double b)
    {
        var rgb = new[] { r, g, b };
        HdrToSdr.GamutCompress(rgb);
        Assert.Equal(new[] { r, g, b }, rgb);
    }

    [Fact]
    public void Gamut_compression_brings_colours_outside_BT_709_inside_and_keeps_the_achromatic_value()
    {
        var rgb = new[] { 1.0, -0.1, 0.2 };   // green distance 1.1, within its limit 1.264
        HdrToSdr.GamutCompress(rgb);
        Assert.Equal(1.0, rgb[0]);
        Assert.InRange(rgb[1], 0.0, 0.2);
        Assert.InRange(rgb[2], 0.2, 1.0);

        var edge = new[] { 1.0, 0.0, 0.5 };   // on the edge: distance 1 → compressed inward, never outward
        HdrToSdr.GamutCompress(edge);
        Assert.True(edge[1] > 0);
    }

    // --- the whole rule ----------------------------------------------------------------------------------------------

    [Fact]
    public void Black_stays_black_and_HLG_reference_white_and_peak_land_on_217_and_255()
    {
        Assert.All(Hlg(0), v => Assert.Equal(0.0, v));
        Assert.All(Hlg(0.75), v => Assert.Equal(217, Code(v)));
        Assert.All(Hlg(0.75), v => Assert.Equal(216.97 / 255, v, 3));     // a second transfer / OOTF / tone map breaks this
        Assert.All(Hlg(1.0), v => Assert.Equal(255, Code(v)));
    }

    [Fact]
    public void PQ_reference_white_at_a_1000_nit_peak_lands_on_217_like_HLG()
    {
        var pq203 = HdrToSdr.PqInverseEotf(203);
        Assert.All(Out(HdrTransfer.Pq, 1000, pq203, pq203, pq203), v => Assert.Equal(217, Code(v)));
    }

    [Fact]
    public void PQ_at_a_peak_of_203_nit_skips_the_tone_map_and_only_clamps()
    {
        var pq203 = HdrToSdr.PqInverseEotf(203);
        Assert.All(Out(HdrTransfer.Pq, 203, pq203, pq203, pq203), v => Assert.Equal(1.0, v, 6));
        var pq100 = HdrToSdr.PqInverseEotf(100);
        Assert.All(Out(HdrTransfer.Pq, 203, pq100, pq100, pq100), v => Assert.Equal(Math.Pow(100.0 / 203, 1 / 2.4), v, 6));
        Assert.All(Out(HdrTransfer.Pq, 203, 1, 1, 1), v => Assert.Equal(1.0, v));
    }

    [Theory]
    [InlineData(HdrTransfer.Hlg, 1000)]
    [InlineData(HdrTransfer.Pq, 1000)]
    [InlineData(HdrTransfer.Pq, 4000)]
    public void A_neutral_ramp_stays_neutral_and_monotonic(HdrTransfer transfer, int peak)
    {
        var previous = -1.0;
        for (var i = 0; i <= 1100; i++)
        {
            var v = i / 1000.0;
            var o = Out(transfer, peak, v, v, v);
            Assert.Equal(o[0], o[1], 12);
            Assert.Equal(o[0], o[2], 12);
            Assert.True(o[0] >= previous, $"not monotonic at {v}");
            previous = o[0];
        }
    }

    // --- signal domain boundaries (D033 §3 step 1; superwhites) ------------------------------------------------------

    [Fact]
    public void HLG_below_0_is_0_and_above_the_1_1_domain_is_1_1()
    {
        Assert.Equal(Hlg(0, 0.6, 0.4), Hlg(-0.1, 0.6, 0.4));
        Assert.Equal(Hlg(1.1, 0.6, 0.4), Hlg(1.2, 0.6, 0.4));
        Assert.Equal(Hlg(1.1, 0.6, 0.4), Hlg(5, 0.6, 0.4));
    }

    [Fact]
    public void HLG_superwhites_up_to_1_1_keep_the_channel_ratios_of_a_coloured_highlight()
    {
        // A coloured highlight: red above 1. Kept to 1.1, red's light keeps growing against green and blue, which after
        // the tone map (red clipped at 1) shows as less green / blue — not as the hue shift a clip at 1.0 would give.
        var at1 = Hlg(1.0, 0.6, 0.4);
        var at105 = Hlg(1.05, 0.6, 0.4);
        var at11 = Hlg(1.1, 0.6, 0.4);
        Assert.Equal(1.0, at11[0], 6);
        Assert.True(at105[1] < at1[1] && at11[1] < at105[1]);
        // Neutral superwhites render white: the 1000 nit peak is unchanged (D033 superwhites).
        Assert.All(Hlg(1.05), v => Assert.Equal(1.0, v));
        Assert.All(Hlg(1.1), v => Assert.Equal(1.0, v));
    }

    [Fact]
    public void PQ_above_1_is_1()
    {
        Assert.Equal(Out(HdrTransfer.Pq, 1000, 1, 0.5, 0.4), Out(HdrTransfer.Pq, 1000, 1.05, 0.5, 0.4));
        Assert.Equal(Out(HdrTransfer.Pq, 1000, 1, 0.5, 0.4), Out(HdrTransfer.Pq, 1000, 1.1, 0.5, 0.4));
        Assert.Equal(Out(HdrTransfer.Pq, 1000, 0, 0.5, 0.4), Out(HdrTransfer.Pq, 1000, -0.2, 0.5, 0.4));
    }

    [Fact]
    public void The_rule_matches_an_independent_evaluation_of_D033_section_3()
    {
        var random = new Random(18);
        for (var i = 0; i < 2000; i++)
        {
            var transfer = i % 2 == 0 ? HdrTransfer.Hlg : HdrTransfer.Pq;
            var peak = transfer == HdrTransfer.Hlg ? 1000 : 600 + i;
            var s = new[] { random.NextDouble() * 1.2 - 0.05, random.NextDouble() * 1.2 - 0.05, random.NextDouble() * 1.2 - 0.05 };
            var expected = Reference(transfer, peak, s);
            var actual = Out(transfer, peak, s[0], s[1], s[2]);
            for (var c = 0; c < 3; c++)
                Assert.Equal(expected[c], actual[c], 12);
        }
    }

    /// <summary>D033 §3 written out step by step, independently of <see cref="HdrToSdr"/>.</summary>
    private static double[] Reference(HdrTransfer transfer, int peakNits, double[] signal)
    {
        const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
        double[] light;
        if (transfer == HdrTransfer.Hlg)
        {
            var e = signal.Select(v => Math.Clamp(v, 0, 1.1)).Select(v => v <= 0.5 ? v * v / 3 : (Math.Exp((v - c) / a) + b) / 12).ToArray();
            var ys = 0.2627 * e[0] + 0.6780 * e[1] + 0.0593 * e[2];
            light = e.Select(v => ys > 0 ? 1000 * Math.Pow(ys, 0.2) * v / 203 : 0).ToArray();
        }
        else
        {
            const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
            light = signal.Select(v => Math.Clamp(v, 0, 1)).Select(v =>
            {
                var p = Math.Pow(v, 1 / m2);
                return 10000 * Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1) / 203;
            }).ToArray();
        }
        var m = HdrToSdr.Bt2020ToBt709;   // checked on its own below
        var rgb = Enumerable.Range(0, 3).Select(r => m[r, 0] * light[0] + m[r, 1] * light[1] + m[r, 2] * light[2]).ToArray();

        var ach = rgb.Max();
        if (ach > 0)
        {
            double[] thr = { 0.815, 0.803, 0.880 }, lim = { 1.147, 1.264, 1.312 };
            for (var k = 0; k < 3; k++)
            {
                var d = (ach - rgb[k]) / ach;
                if (d < thr[k]) continue;
                var s = (lim[k] - thr[k]) / Math.Pow(Math.Pow((1 - thr[k]) / (lim[k] - thr[k]), -1.2) - 1, 1 / 1.2);
                rgb[k] = ach - (thr[k] + (d - thr[k]) / Math.Pow(1 + Math.Pow((d - thr[k]) / s, 1.2), 1 / 1.2)) * ach;
            }
        }

        var peak = peakNits / 203.0;
        if (peak > 1)
        {
            var sig = Math.Max(rgb.Max(), 1e-6);
            const double j = 0.3;
            var mapped = sig;
            if (sig > j)
            {
                var ma = -j * j * (peak - 1) / (j * j - 2 * j + peak);
                var mb = (j * j - 2 * j * peak + peak) / Math.Max(peak - 1, 1e-6);
                mapped = (mb * mb + 2 * mb * j + j * j) / (mb - ma) * (sig + ma) / (sig + mb);
            }
            rgb = rgb.Select(v => v * mapped / sig).ToArray();
        }
        return rgb.Select(v => Math.Pow(Math.Clamp(v, 0, 1), 1 / 2.4)).ToArray();
    }

    // --- PQ peak (D033 §3) -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1500, 4000.0, 1500)]     // MaxCLL wins
    [InlineData(0, 4000.0, 4000)]        // MaxCLL 0 = not set → the mastering maximum
    [InlineData(null, 1000.0, 1000)]
    [InlineData(null, null, 1000)]       // neither → the 1000 nit fallback
    [InlineData(0, 0.0, 1000)]
    [InlineData(100, null, 203)]         // limited to [203, 10 000]
    [InlineData(null, 12000.0, 10000)]
    [InlineData(null, 999.6, 1000)]      // whole nit
    public void The_PQ_peak_is_MaxCLL_else_the_mastering_maximum_else_1000(int? maxCll, double? mastering, int expected) =>
        Assert.Equal(expected, HdrColor.PqPeakNits(new MediaMetadata { MaxContentLightLevel = maxCll, MasteringMaxLuminance = mastering }));

    // --- classification (D033 §3 "Which assets", OQ1) ----------------------------------------------------------------

    private static MediaMetadata Meta(string? transfer, string? primaries = "bt2020", string? matrix = "bt2020nc",
        string? pixelFormat = "yuv420p10le", string? range = "tv") => new()
    {
        Width = 3840, Height = 2160, PixelFormat = pixelFormat, BitDepth = HdrColor.BitDepthOf(pixelFormat),
        ColorTransfer = transfer, ColorPrimaries = primaries, ColorMatrix = matrix, ColorRange = range
    };

    [Fact]
    public void HLG_and_PQ_in_BT_2020_at_10_bits_take_the_HDR_path()
    {
        Assert.Equal(new HdrClassification(HdrKind.Hdr, new HdrConversion(HdrTransfer.Hlg, 1000, false)),
            HdrColor.Classify(MediaKind.Video, Meta("arib-std-b67")));
        var pq = Meta("smpte2084", range: "pc");
        pq.MaxContentLightLevel = 1500;
        Assert.Equal(new HdrClassification(HdrKind.Hdr, new HdrConversion(HdrTransfer.Pq, 1500, true)),
            HdrColor.Classify(MediaKind.Video, pq));
        Assert.Equal(1000, HdrColor.Classify(MediaKind.Video, Meta("smpte2084"))!.Conversion!.PeakNits);   // no metadata: fallback
    }

    [Fact]
    public void SDR_untagged_and_unknown_files_are_never_HDR()
    {
        Assert.Same(HdrClassification.Sdr, HdrColor.Classify(MediaKind.Video, Meta("bt709", "bt709", "bt709", "yuv420p")));
        Assert.Same(HdrClassification.Sdr, HdrColor.Classify(MediaKind.Video, Meta(null, null, null, "yuv420p10le", null)));
        Assert.Same(HdrClassification.Sdr, HdrColor.Classify(MediaKind.Video, new MediaMetadata()));
        Assert.Same(HdrClassification.Sdr, HdrColor.Classify(MediaKind.Video, null));
        Assert.Same(HdrClassification.Sdr, HdrColor.Classify(MediaKind.Image, Meta("arib-std-b67")));
    }

    [Theory]
    [InlineData("arib-std-b67", "bt709", "bt2020nc", "yuv420p10le", "primaries")]
    [InlineData("arib-std-b67", "bt2020", "bt2020c", "yuv420p10le", "matrix")]
    [InlineData("smpte2084", "bt2020", "bt2020nc", "yuv420p", "8 bits")]
    [InlineData("smpte2084", "bt2020", "bt2020nc", null, "unknown bit depth")]
    [InlineData("smpte2084", null, "bt2020nc", "yuv420p10le", "unknown primaries")]
    public void Other_HDR_combinations_are_unsupported_with_a_reason(string transfer, string? primaries, string matrix, string? pixelFormat,
        string reasonPart)
    {
        var result = HdrColor.Classify(MediaKind.Video, Meta(transfer, primaries, matrix, pixelFormat));
        Assert.Equal(HdrKind.UnsupportedHdr, result.Kind);
        Assert.Null(result.Conversion);
        Assert.Contains(reasonPart, result.Reason);
    }

    [Theory]
    [InlineData("yuv420p10le", 10)]
    [InlineData("yuv420p10be", 10)]
    [InlineData("p010le", 10)]
    [InlineData("yuv420p", 8)]
    [InlineData("yuvj420p", 8)]
    [InlineData("nv12", 8)]
    [InlineData("yuv422p12le", 12)]
    [InlineData("gray16le", 16)]
    [InlineData("rgb48le", 16)]
    [InlineData("rgba", 8)]
    [InlineData("gbrp10le", 10)]
    [InlineData("gray10le", 10)]
    [InlineData("yuva444p10le", 10)]
    [InlineData("gbrpf32le", null)]   // float: no integer bit depth
    [InlineData("unknown", null)]     // ffprobe gave none (D033 T2's marker)
    [InlineData(null, null)]
    [InlineData("something", null)]
    public void The_bit_depth_comes_from_the_pixel_format(string? pixelFormat, int? bits) =>
        Assert.Equal(bits, HdrColor.BitDepthOf(pixelFormat));

    [Fact]
    public void Old_picture_metadata_without_a_pixel_format_needs_the_colour_probe()
    {
        Assert.True(new MediaMetadata { Width = 1920, Height = 1080 }.NeedsColorProbe);
        Assert.False(new MediaMetadata { Width = 1920, Height = 1080, PixelFormat = "yuv420p" }.NeedsColorProbe);
        Assert.False(new MediaMetadata().NeedsColorProbe);   // audio: no picture
    }

    [Fact]
    public void The_Inspector_text_names_the_HDR_kind_and_the_dynamic_metadata_that_is_not_applied()
    {
        var vivo = Meta("arib-std-b67");
        vivo.DolbyVisionProfile = 8;
        Assert.Equal("HDR · HLG · BT.2020 · 10-bit · Dolby Vision 8 (not applied)", HdrColor.Describe(MediaKind.Video, vivo));
        vivo.DolbyVisionCompatibility = 4;
        Assert.Equal("HDR · HLG · BT.2020 · 10-bit · Dolby Vision 8.4 (not applied)", HdrColor.Describe(MediaKind.Video, vivo));
        var huawei = Meta("arib-std-b67");
        huawei.HasHdrVivid = true;
        Assert.Equal("HDR · HLG · BT.2020 · 10-bit · HDR Vivid (not applied)", HdrColor.Describe(MediaKind.Video, huawei));
        Assert.Equal("HDR · PQ (peak 1000 nit) · BT.2020 · 10-bit", HdrColor.Describe(MediaKind.Video, Meta("smpte2084")));
        Assert.Equal("SDR · BT.709 · 8-bit", HdrColor.Describe(MediaKind.Video, Meta("bt709", "bt709", "bt709", "yuv420p")));
        Assert.StartsWith("HDR not converted: HLG with BT.709 primaries", HdrColor.Describe(MediaKind.Video, Meta("arib-std-b67", "bt709")));
        Assert.Null(HdrColor.Describe(MediaKind.Video, new MediaMetadata { Width = 10, Height = 10 }));   // not probed for colour
    }

    // --- the LUT generator (D033 §3 "LUT") ---------------------------------------------------------------------------

    [Fact]
    public void The_LUT_is_a_65_cube_over_the_1_1_domain_with_red_fastest()
    {
        var conversion = HdrConversion.Hlg();
        var lines = HdrLut.Generate(conversion).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("TITLE ", lines[0]);
        Assert.Equal("LUT_3D_SIZE 65", lines[1]);
        Assert.Equal("DOMAIN_MIN 0.0 0.0 0.0", lines[2]);
        Assert.Equal("DOMAIN_MAX 1.1 1.1 1.1", lines[3]);
        Assert.Equal(4 + 65 * 65 * 65, lines.Length);

        double[] Node(int r, int g, int b) =>
            lines[4 + r + 65 * g + 65 * 65 * b].Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        foreach (var (r, g, b) in new[] { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1), (44, 44, 44), (64, 64, 64), (64, 30, 10), (5, 40, 60) })
        {
            var expected = Hlg(HdrLut.Node(r), HdrLut.Node(g), HdrLut.Node(b));
            var node = Node(r, g, b);
            for (var c = 0; c < 3; c++)
                Assert.Equal(expected[c], node[c], 6);
        }
        Assert.Equal(1.1, HdrLut.Node(64), 12);
    }

    [Fact]
    public void The_LUT_text_is_deterministic_and_its_name_carries_the_rule_version_and_the_key()
    {
        var pq = new HdrConversion(HdrTransfer.Pq, 4000, false);
        Assert.Equal(HdrLut.Generate(pq), HdrLut.Generate(pq));
        Assert.Equal(HdrLut.Generate(pq), HdrLut.Generate(pq with { FullRange = true }));   // the range is the decoder's
        Assert.NotEqual(HdrLut.Generate(pq), HdrLut.Generate(pq with { PeakNits = 1000 }));
        Assert.Equal($"hdr-r{HdrToSdr.RuleVersion}-hlg-1000.cube", HdrLut.FileName(HdrConversion.Hlg()));
        Assert.Equal($"hdr-r{HdrToSdr.RuleVersion}-pq-4000.cube", HdrLut.FileName(pq));
        Assert.Equal(HdrLut.FileName(pq), HdrLut.FileName(pq with { FullRange = true }));
        Assert.StartsWith(HdrLut.FilePrefix, HdrLut.FileName(pq));
    }
}
