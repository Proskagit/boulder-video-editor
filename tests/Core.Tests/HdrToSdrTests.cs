using System.Globalization;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 18 (D033 §3, §5; the rule S1 of Revision R1, Step 18.9): the HDR → SDR rule — HLG / PQ transfer functions, no
/// OOTF on HLG, the inverse BT.2100 OOTF on luminance for PQ, the gamut compression, the exposure, mobius on max RGB with
/// the S1 knee, the BT.709 OETF, the order of the steps (each once), the signal domain's boundaries, the PQ peak and its
/// 501 / 502 nit boundary, the asset classification and the LUT generator. Expected values are the BT.2100 / ST 2084 /
/// BT.709 formulas evaluated independently (the measurement series' reference, D033 R1), not this implementation's output.
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
    private static double[] PqNits(int peak, double nits)
    {
        var s = HdrToSdr.PqInverseEotf(nits);
        return Out(HdrTransfer.Pq, peak, s, s, s);
    }

    private static double[] Scene(HdrTransfer transfer, double r, double g, double b)
    {
        var scene = new double[3];
        HdrToSdr.ToSceneLight(transfer, new[] { r, g, b }, scene);
        return scene;
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

    [Fact]
    public void The_BT_709_OETF_has_its_linear_part_below_0_018()
    {
        Assert.Equal(0.0, HdrToSdr.Bt709Oetf(0));
        Assert.Equal(4.5 * 0.01, HdrToSdr.Bt709Oetf(0.01), 15);              // linear part
        Assert.Equal(1.099 * Math.Pow(0.018, 0.45) - 0.099, HdrToSdr.Bt709Oetf(0.018), 15);
        Assert.Equal(0.081, HdrToSdr.Bt709Oetf(0.018), 3);                    // the two parts meet
        Assert.Equal(0.409008, HdrToSdr.Bt709Oetf(0.18), 6);                 // not L^(1/2.4) = 0.489
        Assert.Equal(1.0, HdrToSdr.Bt709Oetf(1), 12);
    }

    // --- light: no OOTF for HLG; PQ → ÷ 1000 → the inverse OOTF on luminance ----------------------------------------

    [Fact]
    public void HLG_takes_no_OOTF_its_scene_light_is_the_inverse_OETF_per_channel()
    {
        var scene = Scene(HdrTransfer.Hlg, 0.8, 0.7, 0.6);
        Assert.Equal(HdrToSdr.HlgInverseOetf(0.8), scene[0], 15);
        Assert.Equal(HdrToSdr.HlgInverseOetf(0.7), scene[1], 15);
        Assert.Equal(HdrToSdr.HlgInverseOetf(0.6), scene[2], 15);
        Assert.Equal(0.2649626, Scene(HdrTransfer.Hlg, 0.75, 0.75, 0.75)[0], 7);   // not 203 nit / 203 (L2)
    }

    [Fact]
    public void PQ_takes_the_inverse_OOTF_on_luminance_after_dividing_by_1000_nit()
    {
        // Fd = (0.8, 0.7, 0.6): E = Fd · Yd^(−1/6). The per-channel form would give (0.8303, 0.7429, 0.6533), the forward
        // OOTF (0.7492, 0.6555, 0.5619) — D033 §5 (R1).
        var scene = Scene(HdrTransfer.Pq, HdrToSdr.PqInverseEotf(800), HdrToSdr.PqInverseEotf(700), HdrToSdr.PqInverseEotf(600));
        Assert.Equal(0.844955, scene[0], 5);
        Assert.Equal(0.739336, scene[1], 5);
        Assert.Equal(0.633716, scene[2], 5);
        Assert.Equal(0.8 / 0.6, scene[0] / scene[2], 6);                     // the channel ratios kept

        // 203 nit → the scene light of HLG 75 %, within 0.1 %
        Assert.Equal(0.264797, Scene(HdrTransfer.Pq, HdrToSdr.PqInverseEotf(203), HdrToSdr.PqInverseEotf(203), HdrToSdr.PqInverseEotf(203))[1], 5);
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, Scene(HdrTransfer.Pq, 0, 0, 0));   // Yd = 0 → black
    }

    [Fact]
    public void PQ_scene_light_does_not_depend_on_the_peak()
    {
        var s = HdrToSdr.PqInverseEotf(100);
        Assert.Equal(PqNits(1000, 100), Out(HdrTransfer.Pq, 4000, s, s, s));   // below both knees: the peak changes nothing
        Assert.Equal(127.88 / 255, PqNits(203, 100)[0], 4);
        Assert.Equal(127.88 / 255, PqNits(1000, 100)[0], 4);
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

    // --- exposure and mobius -----------------------------------------------------------------------------------------

    [Fact]
    public void The_S1_exposure_knee_and_peaks_are_D033s()
    {
        Assert.Equal(1.778430, HdrToSdr.Exposure, 6);
        Assert.Equal(0.471284, HdrToSdr.Exposure * 0.265, 6);                // reference white → 0.69 · 2^−0.55
        Assert.Equal(0.375, HdrToSdr.MobiusJ);
        Assert.Equal(1.64, HdrToSdr.KneePeak(HdrTransfer.Hlg, 1000));
        Assert.Equal(1.64, HdrToSdr.KneePeak(HdrTransfer.Hlg, 4000));        // HLG ignores the peak
        Assert.Equal(1.0, HdrToSdr.KneePeak(HdrTransfer.Pq, 1000), 12);
        Assert.Equal(Math.Pow(4, 1 / 1.2), HdrToSdr.KneePeak(HdrTransfer.Pq, 4000), 12);
    }

    [Fact]
    public void Mobius_is_the_vf_tonemap_curve()
    {
        const double peak = 1000.0 / 203;
        Assert.Equal(0.25, HdrToSdr.Mobius(0.25, 0.3, peak));             // identity up to j
        Assert.Equal(0.3, HdrToSdr.Mobius(0.3, 0.3, peak));
        Assert.Equal(0.678648, HdrToSdr.Mobius(1.0, 0.3, peak), 6);
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

    // --- the whole rule: the check values of D033 §3 (R1) ------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 93.45)]
    [InlineData(0.75, 172.58)]      // reference white (L2: 217)
    [InlineData(1.0, 241.5)]        // below white: the knee's peak is the superwhite top
    [InlineData(1.0902, 255.0)]     // 10-bit limited code 1019
    [InlineData(1.1, 255.0)]
    public void HLG_neutrals_land_on_the_D033_check_values(double signal, double code) =>
        Assert.All(Hlg(signal), v => Assert.Equal(code, v * 255, 2));

    [Theory]
    [InlineData(1000, 0.1, 0.95)]
    [InlineData(1000, 1.0, 6.45)]
    [InlineData(1000, 5.0, 24.55)]
    [InlineData(1000, 100.0, 127.88)]
    [InlineData(1000, 203.0, 173.01)]   // within 0.5 code of HLG 75 %
    [InlineData(1000, 500.0, 224.81)]
    [InlineData(1000, 1000.0, 255.0)]
    [InlineData(1000, 2000.0, 255.0)]   // above Lpeak: white
    [InlineData(203, 100.0, 127.88)]
    [InlineData(203, 203.0, 174.45)]
    [InlineData(203, 400.0, 232.28)]    // no knee at 203: clamp only
    [InlineData(300, 300.0, 205.95)]
    [InlineData(300, 600.0, 255.0)]
    [InlineData(400, 400.0, 232.28)]
    [InlineData(4000, 203.0, 172.25)]
    [InlineData(4000, 1000.0, 234.69)]
    [InlineData(4000, 4000.0, 255.0)]
    [InlineData(10000, 1000.0, 231.61)]
    public void PQ_neutrals_land_on_the_D033_check_values(int peak, double nits, double code) =>
        Assert.All(PqNits(peak, nits), v => Assert.Equal(code, v * 255, 2));

    [Fact]
    public void Black_stays_black()
    {
        Assert.All(Hlg(0), v => Assert.Equal(0.0, v));
        foreach (var peak in new[] { 203, 1000, 4000, 10000 })
            Assert.All(Out(HdrTransfer.Pq, peak, 0, 0, 0), v => Assert.Equal(0.0, v));
    }

    [Fact]
    public void PQ_at_501_nit_skips_the_knee_and_at_502_applies_it_continuously()
    {
        Assert.Equal(0.99977, HdrToSdr.Exposure * HdrToSdr.KneePeak(HdrTransfer.Pq, 501), 5);   // ≤ 1: skipped
        Assert.Equal(1.00144, HdrToSdr.Exposure * HdrToSdr.KneePeak(HdrTransfer.Pq, 502), 5);   // > 1: applied

        foreach (var (nits, at501, at502) in new[] { (400.0, 232.28, 232.18), (490.0, 252.65, 252.48), (500.0, 254.76, 254.58),
                     (501.0, 254.97, 254.79), (502.0, 255.0, 255.0), (503.0, 255.0, 255.0), (600.0, 255.0, 255.0) })
        {
            Assert.Equal(at501, PqNits(501, nits)[0] * 255, 2);
            Assert.Equal(at502, PqNits(502, nits)[0] * 255, 2);
        }

        double previous501 = -1, previous502 = -1, largest = 0;
        for (var i = 1; i <= 20000; i++)
        {
            var nits = i * 0.05;
            var a = PqNits(501, nits)[0];
            var b = PqNits(502, nits)[0];
            largest = Math.Max(largest, Math.Abs(a - b) * 255);
            Assert.True(a >= previous501 && b >= previous502, $"not monotonic at {nits} nit");
            (previous501, previous502) = (a, b);
        }
        Assert.InRange(largest, 0.17, 0.25);   // measured 0.18 code (D033 §3), the plan's bound 0.25
    }

    [Theory]
    [InlineData(HdrTransfer.Hlg, 1000)]
    [InlineData(HdrTransfer.Pq, 203)]
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

    [Fact]
    public void There_is_no_saturation_step()
    {
        // A colour well inside the gamut and below the knee: after the matrix and the exposure only the OETF follows, so
        // the output is the OETF of k · (the matrix × the scene light) — a saturation step ≠ 1 would move it.
        var e = new[] { HdrToSdr.HlgInverseOetf(0.45), HdrToSdr.HlgInverseOetf(0.40), HdrToSdr.HlgInverseOetf(0.35) };
        var m = HdrToSdr.Bt2020ToBt709;
        var output = Hlg(0.45, 0.40, 0.35);
        for (var c = 0; c < 3; c++)
        {
            var linear = HdrToSdr.Exposure * (m[c, 0] * e[0] + m[c, 1] * e[1] + m[c, 2] * e[2]);
            Assert.True(linear < HdrToSdr.MobiusJ);
            Assert.Equal(HdrToSdr.Bt709Oetf(linear), output[c], 12);
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
        // Neutral superwhites keep a gradation up to the knee's peak (code 1019), then white (D033 superwhites, R1).
        Assert.True(Hlg(1.0)[0] < Hlg(1.05)[0] && Hlg(1.05)[0] < Hlg(1.08)[0] && Hlg(1.08)[0] < 1.0);
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
            var peak = transfer == HdrTransfer.Hlg ? 1000 : 203 + 4 * i;
            var s = new[] { random.NextDouble() * 1.2 - 0.05, random.NextDouble() * 1.2 - 0.05, random.NextDouble() * 1.2 - 0.05 };
            var expected = Reference(transfer, peak, s);
            var actual = Out(transfer, peak, s[0], s[1], s[2]);
            for (var c = 0; c < 3; c++)
                Assert.Equal(expected[c], actual[c], 12);
        }
    }

    /// <summary>D033 §3 (S1) written out step by step, independently of <see cref="HdrToSdr"/>.</summary>
    private static double[] Reference(HdrTransfer transfer, int peakNits, double[] signal)
    {
        const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
        double[] scene;
        double p;
        if (transfer == HdrTransfer.Hlg)
        {
            scene = signal.Select(v => Math.Clamp(v, 0, 1.1)).Select(v => v <= 0.5 ? v * v / 3 : (Math.Exp((v - c) / a) + b) / 12).ToArray();
            p = 1.64;
        }
        else
        {
            const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
            var fd = signal.Select(v => Math.Clamp(v, 0, 1)).Select(v =>
            {
                var q = Math.Pow(v, 1 / m2);
                return 10000 * Math.Pow(Math.Max(q - c1, 0) / (c2 - c3 * q), 1 / m1) / 1000;
            }).ToArray();
            var yd = 0.2627 * fd[0] + 0.6780 * fd[1] + 0.0593 * fd[2];
            scene = fd.Select(v => yd > 0 ? v * Math.Pow(yd, -0.2 / 1.2) : 0).ToArray();
            p = Math.Pow(peakNits / 1000.0, 1 / 1.2);
        }
        var m = HdrToSdr.Bt2020ToBt709;   // checked on its own above
        var rgb = Enumerable.Range(0, 3).Select(r => m[r, 0] * scene[0] + m[r, 1] * scene[1] + m[r, 2] * scene[2]).ToArray();

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

        var exposure = 0.69 * Math.Pow(2, -0.55) / 0.265;
        rgb = rgb.Select(v => v * exposure).ToArray();
        var peak = exposure * p;
        if (peak > 1)
        {
            var sig = Math.Max(rgb.Max(), 1e-6);
            const double j = 0.375;
            var mapped = sig;
            if (sig > j)
            {
                var ma = -j * j * (peak - 1) / (j * j - 2 * j + peak);
                var mb = (j * j - 2 * j * peak + peak) / Math.Max(peak - 1, 1e-6);
                mapped = (mb * mb + 2 * mb * j + j * j) / (mb - ma) * (sig + ma) / (sig + mb);
            }
            rgb = rgb.Select(v => v * mapped / sig).ToArray();
        }
        return rgb.Select(v => Math.Clamp(v, 0, 1)).Select(l => l < 0.018 ? 4.5 * l : 1.099 * Math.Pow(l, 0.45) - 0.099).ToArray();
    }

    // --- PQ peak (D033 §3) -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1500, 4000.0, 1500)]     // MaxCLL wins
    [InlineData(4000, 1000.0, 4000)]     // a contradiction: MaxCLL above the mastering maximum — taken as stated
    [InlineData(300, 1000.0, 300)]       // a contradiction: MaxCLL far below it — taken as stated
    [InlineData(0, 4000.0, 4000)]        // MaxCLL 0 = not set → the mastering maximum
    [InlineData(-5, 4000.0, 4000)]       // ≤ 0 = not set
    [InlineData(null, 1000.0, 1000)]
    [InlineData(null, null, 1000)]       // neither → the 1000 nit fallback
    [InlineData(0, 0.0, 1000)]
    [InlineData(null, double.NaN, 1000)] // not a number = not set
    [InlineData(100, null, 203)]         // limited to [203, 10 000]
    [InlineData(null, 12000.0, 10000)]
    [InlineData(null, 999.6, 1000)]      // whole nit
    [InlineData(null, 500.5, 501)]       // halves away from zero
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

    /// <summary>The generated LUT reproduces the LUT the product owner approved ("Telegram-match-sat100", D033 R1) — its
    /// nodes copied here from that file (6 decimals), not regenerated.</summary>
    [Theory]
    [InlineData(0, 0, 0, 0.000000, 0.000000, 0.000000)]
    [InlineData(3, 3, 3, 0.007092, 0.007092, 0.007092)]
    [InlineData(10, 10, 10, 0.078805, 0.078805, 0.078805)]
    [InlineData(20, 5, 40, 0.270290, 0.102052, 0.615234)]
    [InlineData(44, 44, 44, 0.685861, 0.685861, 0.685861)]
    [InlineData(55, 55, 55, 0.904324, 0.904324, 0.904324)]
    [InlineData(64, 64, 64, 1.000000, 1.000000, 1.000000)]
    [InlineData(64, 30, 10, 1.000000, 0.199978, 0.175501)]
    [InlineData(5, 40, 60, 0.000000, 0.376626, 0.978311)]
    [InlineData(30, 50, 20, 0.000000, 0.840080, 0.128824)]
    [InlineData(60, 2, 2, 1.000000, 0.164429, 0.169622)]
    public void The_HLG_LUT_reproduces_the_approved_one(int r, int g, int b, double outR, double outG, double outB)
    {
        var output = Hlg(HdrLut.Node(r), HdrLut.Node(g), HdrLut.Node(b));
        // within 1e-6 (the plan's bound): the file's 6 decimals plus its rounded BT.2020 → BT.709 matrix (the rule derives
        // it exactly, D033 "Refined in 18.3")
        Assert.InRange(Math.Abs(outR - output[0]), 0, 1e-6);
        Assert.InRange(Math.Abs(outG - output[1]), 0, 1e-6);
        Assert.InRange(Math.Abs(outB - output[2]), 0, 1e-6);
    }
}
