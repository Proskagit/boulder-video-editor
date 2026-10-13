using System.Globalization;
using System.Text;

namespace AiVideoEditor.Core.Color;

/// <summary>
/// The 3D LUT form of <see cref="HdrToSdr"/> (D033 §3 "LUT"): a <c>.cube</c> file of 65³ nodes over the input domain
/// <c>[0, 1.1]³</c> (R'G'B' HDR signal, BT.2020, full range) holding the R'G'B' BT.709 output. ffmpeg's <c>lut3d</c>
/// applies it with tetrahedral interpolation. The text is a pure function of (transfer, peak, rule version):
/// generated twice, it is byte for byte the same.
/// </summary>
public static class HdrLut
{
    public const int Size = 65;

    /// <summary>The file name of a LUT: the rule version and the key (transfer, peak), so a rule change or another key
    /// never reuses a file.</summary>
    public static string FileName(HdrConversion conversion) =>
        $"hdr-r{HdrToSdr.RuleVersion}-{(conversion.Transfer == HdrTransfer.Hlg ? "hlg" : "pq")}-{conversion.PeakNits}.cube";

    /// <summary>The prefix shared by every LUT file of any rule version (cache clean-up).</summary>
    public const string FilePrefix = "hdr-r";

    /// <summary>The <c>.cube</c> text: <c>LUT_3D_SIZE</c>, <c>DOMAIN_MIN</c> / <c>DOMAIN_MAX</c>, then the nodes with red
    /// changing fastest, then green, then blue; values with 6 decimals, invariant culture, <c>\n</c> line ends.</summary>
    public static string Generate(HdrConversion conversion)
    {
        const int n = Size;
        var domain = HdrToSdr.SignalDomainMax.ToString("0.0###", CultureInfo.InvariantCulture);
        var text = new StringBuilder(n * n * n * 27 + 200);
        text.Append("TITLE \"AiVideoEditor HDR to SDR, rule ").Append(HdrToSdr.RuleVersion).Append(", ")
            .Append(conversion.Transfer == HdrTransfer.Hlg ? "HLG" : "PQ").Append(", peak ").Append(conversion.PeakNits)
            .Append(" nit\"\n");
        text.Append("LUT_3D_SIZE ").Append(n).Append('\n');
        text.Append("DOMAIN_MIN 0.0 0.0 0.0\n");
        text.Append("DOMAIN_MAX ").Append(domain).Append(' ').Append(domain).Append(' ').Append(domain).Append('\n');

        Span<double> signal = stackalloc double[3];
        Span<double> output = stackalloc double[3];
        for (var b = 0; b < n; b++)
        for (var g = 0; g < n; g++)
        for (var r = 0; r < n; r++)
        {
            signal[0] = Node(r);
            signal[1] = Node(g);
            signal[2] = Node(b);
            HdrToSdr.Convert(conversion.Transfer, conversion.PeakNits, signal, output);
            text.Append(output[0].ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
                .Append(output[1].ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
                .Append(output[2].ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>The signal value of node <paramref name="index"/> on one axis.</summary>
    public static double Node(int index) => index * HdrToSdr.SignalDomainMax / (Size - 1);
}
