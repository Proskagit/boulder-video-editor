using System.Diagnostics;
using System.Globalization;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Synthetic HDR test files (D033 §5), generated once per test run by ffmpeg / libx265 (present in the pinned CI build):
/// 320 × 180, 10 frames at 25 fps, 10-bit HEVC (lossless, so the only error left is the 8-bit → 10-bit Y'CbCr step),
/// eight flat patches of known <b>signal</b> R'G'B' values (4 columns × 2 rows of 80 × 90 px). The tags say which
/// transfer / primaries / matrix the signal is in; nothing is converted, so a patch's expected SDR output is
/// <c>HdrToSdr.Convert</c> of its signal. Also linked into ExportEndToEnd.Tests.
/// </summary>
internal static class HdrMedia
{
    public const int Width = 320, Height = 180, Columns = 4, Rows = 2;

    /// <summary>The patches' signal values (8-bit codes), row by row: grey at HLG reference white, white, a dark grey,
    /// skin, a saturated red, blue, green, black.</summary>
    public static readonly (byte R, byte G, byte B)[] Patches =
    {
        (191, 191, 191), (255, 255, 255), (64, 64, 64), (204, 166, 140),
        (230, 77, 64), (77, 128, 217), (115, 204, 102), (0, 0, 0)
    };

    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "aive-hdr-tests", Guid.NewGuid().ToString("N"));
    private static readonly object Lock = new();
    private static readonly Dictionary<string, string> Files = new();

    /// <summary>HLG, BT.2020, BT.2020 NCL, limited range: the phones' format.</summary>
    public static string Hlg => Get("hlg.mp4", "colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc",
        "bt2020", "arib-std-b67", "bt2020nc");

    /// <summary>PQ with HDR10 static metadata: mastering display maximum 4000 nit, MaxCLL 1500 nit.</summary>
    public static string PqWithMetadata => Get("pq-meta.mp4",
        "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:hdr10=1:max-cll=1500,400:" +
        "master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(40000000,50)",
        "bt2020", "smpte2084", "bt2020nc");

    /// <summary>PQ without any static metadata (the 1000 nit fallback).</summary>
    public static string PqWithoutMetadata => Get("pq.mp4", "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc",
        "bt2020", "smpte2084", "bt2020nc");

    /// <summary>HLG tagged with BT.709 primaries: an HDR variant the rule does not convert (D033 §3, OQ1).</summary>
    public static string HlgBt709Primaries => Get("hlg-709.mp4", "colorprim=bt709:transfer=arib-std-b67:colormatrix=bt2020nc",
        "bt709", "arib-std-b67", "bt2020nc");

    /// <summary>An SDR file with BT.709 tags, 10 bit.</summary>
    public static string Sdr10 => Get("sdr10.mp4", "colorprim=bt709:transfer=bt709:colormatrix=bt709", "bt709", "bt709", "bt709");

    /// <summary>The patch at (<paramref name="column"/>, <paramref name="row"/>)'s centre rectangle, inside every frame size
    /// the decoder delivers for this file (scaled or not).</summary>
    public static (int X, int Y, int W, int H) Centre(int column, int row, int width, int height)
    {
        var pw = width / Columns;
        var ph = height / Rows;
        return (column * pw + pw / 4, row * ph + ph / 4, pw / 2, ph / 2);
    }

    private static string Get(string name, string x265Params, string primaries, string transfer, string matrix)
    {
        lock (Lock)
        {
            if (Files.TryGetValue(name, out var existing))
                return existing;
            Directory.CreateDirectory(Dir);
            var raw = Path.Combine(Dir, "patches.rgb");
            if (!File.Exists(raw))
                File.WriteAllBytes(raw, Frames(10));
            var path = Path.Combine(Dir, name);
            // RGB signal → Y'CbCr with the BT.2020 / BT.709 matrix and limited range; the transfer is only a tag.
            var matrixName = matrix == "bt709" ? "709" : "2020_ncl";
            Run(new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
                "-s", $"{Width}x{Height}", "-r", "25", "-i", raw,
                "-vf", $"zscale=m={matrixName}:r=limited:rin=full,format=yuv420p10le",
                "-c:v", "libx265", "-x265-params", $"lossless=1:range=limited:log-level=error:{x265Params}",
                "-color_primaries", primaries, "-color_trc", transfer, "-colorspace", matrix, "-color_range", "tv",
                path
            });
            Files[name] = path;
            return path;
        }
    }

    private static byte[] Frames(int count)
    {
        var frame = new byte[Width * Height * 3];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var patch = Patches[(y / (Height / Rows)) * Columns + x / (Width / Columns)];
            var i = (y * Width + x) * 3;
            (frame[i], frame[i + 1], frame[i + 2]) = (patch.R, patch.G, patch.B);
        }
        return Enumerable.Repeat(frame, count).SelectMany(f => f).ToArray();
    }

    /// <summary>CIE76 between two BT.709 R'G'B' (0–255) colours displayed with gamma 2.4.</summary>
    public static double DeltaE(double[] a, double[] b)
    {
        static (double L, double A, double B) Lab(double[] rgb)
        {
            var (r, g, b) = (Math.Pow(rgb[0] / 255, 2.4), Math.Pow(rgb[1] / 255, 2.4), Math.Pow(rgb[2] / 255, 2.4));
            var x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
            var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            var z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
            static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
            return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
        }
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    /// <summary>Runs ffmpeg; its own runner, since the copy linked into ExportEndToEnd.Tests has no other helper.</summary>
    private static void Run(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(FfmpegTools.Ffmpeg!) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {stderr}");
    }

    public static string Describe(int patch) =>
        string.Create(CultureInfo.InvariantCulture, $"patch {patch} {Patches[patch]}");
}
