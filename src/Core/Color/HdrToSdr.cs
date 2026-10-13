namespace AiVideoEditor.Core.Color;

/// <summary>The HDR transfer functions D033 converts.</summary>
public enum HdrTransfer
{
    /// <summary>Hybrid log-gamma (ITU-R BT.2100, ffmpeg <c>arib-std-b67</c>).</summary>
    Hlg,

    /// <summary>Perceptual quantizer (SMPTE ST 2084 / BT.2100, ffmpeg <c>smpte2084</c>).</summary>
    Pq
}

/// <summary>
/// How one HDR asset is converted (D033 §3): its transfer, the peak luminance that sets the tone curve's knee and the
/// range of its Y'CbCr samples. <see cref="PeakNits"/> is always 1000 for HLG (its key; the rule uses
/// <see cref="HdrToSdr.HlgKneePeak"/>); for PQ it comes from <see cref="HdrColor.PqPeakNits"/>. <see cref="FullRange"/>
/// only affects the decoder's Y'CbCr → R'G'B' step, not the LUT.
/// </summary>
public sealed record HdrConversion(HdrTransfer Transfer, int PeakNits, bool FullRange)
{
    public static HdrConversion Hlg(bool fullRange = false) => new(HdrTransfer.Hlg, HdrToSdr.HlgPeakNits, fullRange);
}

/// <summary>
/// The HDR → SDR rule S1 of D033 §3 (Revision R1), in double precision: the picture is rendered from scene light, the
/// way a camera renders SDR. Every step is applied exactly once, in this order:
/// <list type="number">
/// <item>the signal limited to <c>[0, <see cref="SignalDomainMax"/>]</c> (PQ further to <c>[0, 1]</c>);</item>
/// <item>signal → light: the HLG inverse OETF (scene light) or the PQ EOTF (display light, nit);</item>
/// <item>PQ only: ÷ <see cref="PqSceneNormalizationNits"/> and the <b>inverse</b> BT.2100 OOTF on luminance
/// (<c>E = Fd · Yd^((1 − γ) / γ)</c>, γ = 1.2) → the equivalent HLG scene light; HLG has no OOTF at all;</item>
/// <item>BT.2020 → BT.709 primaries in linear light;</item>
/// <item>ACES Reference Gamut Compression 1.3 (its formula and parameters, here on linear BT.709);</item>
/// <item>the exposure × <see cref="Exposure"/>;</item>
/// <item>mobius (ffmpeg <c>vf_tonemap.c</c>, j = <see cref="MobiusJ"/>) on <c>max(R, G, B)</c>, every channel × the same
/// factor, its peak <c>k · P</c> (<see cref="KneePeak"/>); skipped when <c>k · P ≤ 1</c>;</item>
/// <item>no saturation change;</item>
/// <item>clamp to <c>[0, 1]</c>;</item>
/// <item>the BT.709 OETF → R'G'B' BT.709 full range.</item>
/// </list>
/// </summary>
public static class HdrToSdr
{
    /// <summary>Version of this rule. Part of every LUT's key: changing any formula, constant or the LUT format changes
    /// it, so no LUT of an older rule is ever used. 1 was L2; 2 is S1 (D033 Revision R1).</summary>
    public const int RuleVersion = 2;

    /// <summary>BT.2408 reference (diffuse) white; the lower limit of the PQ peak (D033 §3 "PQ peak").</summary>
    public const double ReferenceWhiteNits = 203.0;

    /// <summary>HLG's nominal peak (BT.2100 Lw): the key of every HLG conversion, whatever the file's metadata says.</summary>
    public const int HlgPeakNits = 1000;

    /// <summary>The largest signal value the rule takes (the LUT's <c>DOMAIN_MAX</c>, D033 superwhites): 10-bit limited
    /// code 1019 is 1.0902.</summary>
    public const double SignalDomainMax = 1.1;

    // BT.2100 HLG
    public const double HlgA = 0.17883277;
    public const double HlgB = 0.28466892;              // 1 − 4a
    public const double HlgC = 0.55991073;              // 0.5 − a · ln(4a)
    public const double HlgGamma = 1.2;                 // system gamma at Lw = 1000 nit

    // SMPTE ST 2084 / BT.2100 PQ
    public const double PqM1 = 2610.0 / 16384;
    public const double PqM2 = 2523.0 / 4096 * 128;
    public const double PqC1 = 3424.0 / 4096;
    public const double PqC2 = 2413.0 / 4096 * 32;
    public const double PqC3 = 2392.0 / 4096 * 32;
    public const double PqMaxNits = 10_000;

    /// <summary>PQ display light is divided by this before the inverse OOTF (BT.2408's PQ → HLG conversion at a 1000-nit
    /// display) — a constant, never the file's peak, so the exposure does not depend on the metadata.</summary>
    public const double PqSceneNormalizationNits = 1000;

    // BT.2020 luma
    public const double Kr2020 = 0.2627, Kg2020 = 0.6780, Kb2020 = 0.0593;

    /// <summary>BT.2020 → BT.709, linear RGB, derived in double precision from the two sets of primaries and D65 (as zimg
    /// derives it): ≈ (1.6605, −0.5876, −0.0728 / −0.1246, 1.1329, −0.0083 / −0.0182, −0.1006, 1.1187). Each row sums to 1,
    /// so a neutral colour stays exactly neutral — the rounded 4-decimal matrix would tint it.</summary>
    public static readonly double[,] Bt2020ToBt709 = Multiply(Invert(RgbToXyz(0.64, 0.33, 0.30, 0.60, 0.15, 0.06)),
        RgbToXyz(0.708, 0.292, 0.170, 0.797, 0.131, 0.046));

    // ACES Reference Gamut Compression 1.3
    public static readonly double[] RgcThreshold = { 0.815, 0.803, 0.880 };
    public static readonly double[] RgcLimit = { 1.147, 1.264, 1.312 };
    public const double RgcPower = 1.2;

    /// <summary>The exposure k (step 6): the scene's reference white (HLG 75 %, scene light 0.265) → 0.69 · 2^−0.55, i.e.
    /// 0.55 stop below the scene light 0.69 that the BT.709 OETF encodes as 0.85. ≈ 1.77843.</summary>
    public static readonly double Exposure = 0.69 * Math.Pow(2, -0.55) / 0.265;

    /// <summary>mobius' linear part ends here (S1).</summary>
    public const double MobiusJ = 0.375;

    /// <summary>HLG's knee peak in scene light (P): the scene light of 10-bit limited code 1019 (1.6402), rounded as
    /// approved.</summary>
    public const double HlgKneePeak = 1.64;

    // BT.709 OETF
    public const double Bt709Alpha = 1.099, Bt709Beta = 0.018, Bt709Power = 0.45, Bt709LinearSlope = 4.5;

    /// <summary>BT.2100 HLG inverse OETF: signal → normalized scene light. Continued above 1 (superwhites).</summary>
    public static double HlgInverseOetf(double e) =>
        e <= 0.5 ? e * e / 3 : (Math.Exp((e - HlgC) / HlgA) + HlgB) / 12;

    /// <summary>BT.2100 HLG OETF: normalized scene light → signal.</summary>
    public static double HlgOetf(double x) =>
        x <= 1.0 / 12 ? Math.Sqrt(3 * x) : HlgA * Math.Log(12 * x - HlgB) + HlgC;

    /// <summary>ST 2084 EOTF: signal in [0, 1] → display light in nit (per component).</summary>
    public static double PqEotfNits(double e)
    {
        var p = Math.Pow(Math.Clamp(e, 0, 1), 1 / PqM2);
        return PqMaxNits * Math.Pow(Math.Max(p - PqC1, 0) / (PqC2 - PqC3 * p), 1 / PqM1);
    }

    /// <summary>ST 2084 inverse EOTF: display light in nit → signal.</summary>
    public static double PqInverseEotf(double nits)
    {
        var y = Math.Pow(Math.Clamp(nits / PqMaxNits, 0, 1), PqM1);
        return Math.Pow((PqC1 + PqC2 * y) / (1 + PqC3 * y), PqM2);
    }

    /// <summary>BT.709 OETF: linear light in [0, 1] → signal.</summary>
    public static double Bt709Oetf(double l) =>
        l < Bt709Beta ? Bt709LinearSlope * l : Bt709Alpha * Math.Pow(l, Bt709Power) - (Bt709Alpha - 1);

    /// <summary>ffmpeg's mobius (<c>vf_tonemap.c</c>): identity up to <paramref name="j"/>, then a curve reaching 1 at
    /// <paramref name="peak"/>.</summary>
    public static double Mobius(double x, double j, double peak)
    {
        if (x <= j)
            return x;
        var a = -j * j * (peak - 1) / (j * j - 2 * j + peak);
        var b = (j * j - 2 * j * peak + peak) / Math.Max(peak - 1, 1e-6);
        return (b * b + 2 * b * j + j * j) / (b - a) * (x + a) / (x + b);
    }

    /// <summary>ACES Reference Gamut Compression 1.3 on linear RGB: each channel's distance from the achromatic axis
    /// (<c>max(R, G, B)</c>) above its threshold is compressed so that the limit lands on the gamut's edge. Achromatic and
    /// low-saturation colours are unchanged.</summary>
    public static void GamutCompress(Span<double> rgb)
    {
        var ach = Math.Max(rgb[0], Math.Max(rgb[1], rgb[2]));
        if (ach <= 0)
            return;
        for (var c = 0; c < 3; c++)
        {
            var d = (ach - rgb[c]) / ach;
            var t = RgcThreshold[c];
            var l = RgcLimit[c];
            if (d < t)
                continue;
            var s = (l - t) / Math.Pow(Math.Pow((1 - t) / (l - t), -RgcPower) - 1, 1 / RgcPower);
            var cd = t + (d - t) / Math.Pow(1 + Math.Pow((d - t) / s, RgcPower), 1 / RgcPower);
            rgb[c] = ach - cd * ach;
        }
    }

    /// <summary>Signal → scene light, BT.2020, linear (steps 1–3): HLG's inverse OETF; PQ's EOTF, ÷ 1000 and the inverse
    /// BT.2100 OOTF on luminance (one factor for the three channels; <c>Yd ≤ 0</c> → black).</summary>
    public static void ToSceneLight(HdrTransfer transfer, ReadOnlySpan<double> signal, Span<double> scene)
    {
        if (transfer == HdrTransfer.Hlg)
        {
            for (var c = 0; c < 3; c++)
                scene[c] = HlgInverseOetf(Math.Clamp(signal[c], 0, SignalDomainMax));
            return;
        }

        for (var c = 0; c < 3; c++)
            scene[c] = PqEotfNits(Math.Clamp(signal[c], 0, 1)) / PqSceneNormalizationNits;
        var yd = Kr2020 * scene[0] + Kg2020 * scene[1] + Kb2020 * scene[2];
        var factor = yd > 0 ? Math.Pow(yd, (1 - HlgGamma) / HlgGamma) : 0;
        for (var c = 0; c < 3; c++)
            scene[c] *= factor;
    }

    /// <summary>The knee's peak in scene light, P (step 7): HLG's <see cref="HlgKneePeak"/>; for PQ the scene light of a
    /// neutral at the peak, <c>(Lpeak / 1000)^(1/γ)</c>.</summary>
    public static double KneePeak(HdrTransfer transfer, int peakNits) =>
        transfer == HdrTransfer.Hlg ? HlgKneePeak : Math.Pow(peakNits / PqSceneNormalizationNits, 1 / HlgGamma);

    /// <summary>The whole rule: R'G'B' HDR signal (BT.2020, full range) → R'G'B' BT.709 SDR, each in [0, 1].</summary>
    public static void Convert(HdrTransfer transfer, int peakNits, ReadOnlySpan<double> signal, Span<double> output)
    {
        Span<double> light = stackalloc double[3];
        ToSceneLight(transfer, signal, light);

        Span<double> rgb = stackalloc double[3];
        for (var r = 0; r < 3; r++)
            rgb[r] = Bt2020ToBt709[r, 0] * light[0] + Bt2020ToBt709[r, 1] * light[1] + Bt2020ToBt709[r, 2] * light[2];

        GamutCompress(rgb);

        for (var c = 0; c < 3; c++)
            rgb[c] *= Exposure;

        var peak = Exposure * KneePeak(transfer, peakNits);
        if (peak > 1)
        {
            var sig = Math.Max(Math.Max(Math.Max(rgb[0], rgb[1]), rgb[2]), 1e-6);
            var factor = Mobius(sig, MobiusJ, peak) / sig;
            for (var c = 0; c < 3; c++)
                rgb[c] *= factor;
        }

        for (var c = 0; c < 3; c++)
            output[c] = Bt709Oetf(Math.Clamp(rgb[c], 0, 1));
    }

    /// <summary>The RGB → XYZ matrix of primaries (x, y) with the D65 white point (0.3127, 0.3290).</summary>
    private static double[,] RgbToXyz(double rx, double ry, double gx, double gy, double bx, double by)
    {
        const double wx = 0.3127, wy = 0.3290;
        var p = new[,]
        {
            { rx / ry, gx / gy, bx / by },
            { 1.0, 1.0, 1.0 },
            { (1 - rx - ry) / ry, (1 - gx - gy) / gy, (1 - bx - by) / by }
        };
        var white = new[] { wx / wy, 1.0, (1 - wx - wy) / wy };
        var inverse = Invert(p);
        var s = new double[3];
        for (var i = 0; i < 3; i++)
            s[i] = inverse[i, 0] * white[0] + inverse[i, 1] * white[1] + inverse[i, 2] * white[2];
        var m = new double[3, 3];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            m[i, j] = p[i, j] * s[j];
        return m;
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var m = new double[3, 3];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            m[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
        return m;
    }

    private static double[,] Invert(double[,] m)
    {
        double a = m[0, 0], b = m[0, 1], c = m[0, 2], d = m[1, 0], e = m[1, 1], f = m[1, 2], g = m[2, 0], h = m[2, 1], i = m[2, 2];
        var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return new[,]
        {
            { (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det },
            { (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det },
            { (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det }
        };
    }
}
