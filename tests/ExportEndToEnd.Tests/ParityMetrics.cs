using Xunit;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// The D023 Preview ↔ Export checks for pictures that can't be byte-equal (Phase 8 Step 8.3, product owner decisions 1a and 2a),
/// shared by the scaled-source (8.3) and viewport (8.4) parity tests. Images are BGRA, <c>w × h</c>, rows of <c>4·w</c> bytes:
/// <list type="bullet">
/// <item><see cref="AssertGeometry"/>, <see cref="AssertBarEdges"/> — geometry ±1 px on luma (BT.601 weights);</item>
/// <item><see cref="AssertFlatColour"/> — colour within one YUV code step where both pictures are flat: R ≤ 4, G ≤ 3, B ≤ 4;</item>
/// <item><see cref="AssertSameSourceFrame"/> — the same source frame (16 × 16 block averages);</item>
/// <item><see cref="WholeFrame"/> — mean |Δ| and PSNR, for reports only.</item>
/// </list>
/// Through the lossy codec (the MP4 against the Preview, Step 8.5, decisions 5a/5c) the geometry mask is checked statistically
/// (<see cref="AssertGeometryThroughCodec"/>) and colour is not a criterion; bar edges and the same-frame check stay strict.
/// </summary>
internal static class ParityMetrics
{
    internal const int FlatRange = 4;
    internal const double MaskThreshold = 10, EdgeStep = 40, EdgePlateau = 4;

    /// <summary>Largest flat-colour difference per BGRA channel: B ≤ 4, G ≤ 3, R ≤ 4 (one YUV code step each, decision 1a).</summary>
    internal static readonly int[] FlatTolerance = { 4, 3, 4 };

    /// <summary>Luma of the BGRA pixel at byte <paramref name="i"/> (BT.601 weights, the untagged sources' matrix).</summary>
    internal static double Luma(byte[] p, int i) => 0.299 * p[i + 2] + 0.587 * p[i + 1] + 0.114 * p[i];

    internal static bool Lit(byte[] p, int i) => Luma(p, i) > MaskThreshold;

    /// <summary>Geometry ±1 px on luma: every lit (black) pixel of one side has a lit (black) pixel of the other within 1 px.</summary>
    internal static void AssertGeometry(byte[] a, byte[] b, int w, int h, string what)
    {
        var (bad, first) = GeometryMismatches(a, b, w, h);
        Assert.True(bad == 0, $"{what}: {bad} pixels outside ±1 px of the other side's lit/black area (first at {first})");
    }

    /// <summary>
    /// The strong vertical edges (colour bars) along rows at 1/8 … 7/8 of the height are at the same x ±1. The edges are found in
    /// the export (full resolution, the reference): a luma step of more than 40 between two flat plateaus (x − 6 … x − 3 and
    /// x + 3 … x + 6, range ≤ 4). On both sides the edge's position is where luma crosses the middle of the two plateau levels
    /// (linear interpolation) — the Preview's edges are softer (decoded at ≤ 1280 × 720 and scaled up), so a one-pixel jump test
    /// would not find them.
    /// </summary>
    internal static void AssertBarEdges(byte[] preview, byte[] export, int w, int h, string what)
    {
        static double V(byte[] p, int w, int x, int y) => Luma(p, (y * w + x) * 4);
        static bool Plateau(byte[] p, int w, int x0, int x1, int y) =>
            Enumerable.Range(x0, x1 - x0 + 1).Max(x => V(p, w, x, y)) - Enumerable.Range(x0, x1 - x0 + 1).Min(x => V(p, w, x, y)) <= EdgePlateau;
        static double? Crossing(byte[] p, int w, int y, int from, int to, double level, bool rising)
        {
            for (var x = from; x < to; x++)
            {
                double a = V(p, w, x, y), b = V(p, w, x + 1, y);
                if (rising ? a <= level && b > level : a >= level && b < level)
                    return x + (level - a) / (b - a);
            }
            return null;
        }
        var checkedEdges = 0;
        foreach (var y in Enumerable.Range(1, 7).Select(k => k * h / 8))
        {
            for (var x = 8; x < w - 8; x++)
            {
                var left = V(export, w, x - 5, y);
                var right = V(export, w, x + 5, y);
                if (Math.Abs(right - left) <= EdgeStep || !Plateau(export, w, x - 6, x - 3, y) || !Plateau(export, w, x + 3, x + 6, y)) continue;
                var level = (left + right) / 2.0;
                var rising = right > left;
                var e = Crossing(export, w, y, x - 3, x + 3, level, rising);
                if (e is null || Math.Abs(e.Value - (x + 0.5)) > 0.5) continue;       // each edge once, at the x nearest to it
                var pv = Crossing(preview, w, y, x - 4, x + 4, level, rising);
                Assert.True(pv is not null && Math.Abs(pv.Value - e.Value) <= 1.0,
                    $"{what}: row {y}: luma edge at x {e.Value:0.00} in the export, {(pv is null ? "none" : $"{pv.Value:0.00}")} in the Preview");
                checkedEdges++;
            }
        }
        Assert.True(checkedEdges >= 7, $"{what}: only {checkedEdges} bar edges found — the check would be empty");
    }

    /// <summary>Colour within YUV quantization where both pictures are flat; returns the flat share and the largest difference per
    /// BGRA channel.</summary>
    internal static (double Share, int[] Max) AssertFlatColour(byte[] a, byte[] b, int w, int h, string what)
    {
        bool Flat(byte[] p, int x, int y)
        {
            for (var c = 0; c < 3; c++)
            {
                int lo = 255, hi = 0;
                for (var dy = -2; dy <= 2; dy++)
                for (var dx = -2; dx <= 2; dx++)
                {
                    var v = p[((y + dy) * w + x + dx) * 4 + c];
                    lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                }
                if (hi - lo > FlatRange) return false;
            }
            return true;
        }
        long flat = 0, total = 0; var max = new int[3]; string? violation = null;
        for (var y = 2; y < h - 2; y += 2)
        for (var x = 2; x < w - 2; x += 2)
        {
            total++;
            if (!Flat(a, x, y) || !Flat(b, x, y)) continue;
            flat++;
            var i = (y * w + x) * 4;
            for (var c = 0; c < 3; c++)
            {
                var d = Math.Abs(a[i + c] - b[i + c]);
                max[c] = Math.Max(max[c], d);
                if (d > FlatTolerance[c]) violation ??= $"{"BGR"[c]} differs by {d} (limit {FlatTolerance[c]}) at ({x},{y})";
            }
        }
        var share = (double)flat / total;
        Assert.True(share > 0.3, $"{what}: only {share:P0} of the frame is flat — the colour check would be empty");
        Assert.True(violation is null, $"{what}: flat colour {violation}");
        return (share, max);
    }

    /// <summary>16 × 16 block averages: the Preview's frame n is closest to reference frame n among n − 1 … n + 1.</summary>
    // --- Through the codec (decision 5a) ----------------------------------------------------------------------------------

    /// <summary>Largest share of pixels outside ±1 px of the other side's lit/black area through the codec (H.264 ringing at thin
    /// dark detail around the mask threshold).</summary>
    internal const double CodecGeometryShare = 0.0001;

    /// <summary>The <see cref="AssertGeometry"/> rule, counted: returns the number of pixels outside ±1 px of the other side's
    /// lit/black area and the first one.</summary>
    internal static (int Bad, (int X, int Y)? First) GeometryMismatches(byte[] a, byte[] b, int w, int h)
    {
        int bad = 0; (int X, int Y)? first = null;
        foreach (var (from, to) in new[] { (a, b), (b, a) })
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var lit = Lit(from, (y * w + x) * 4);
            var found = false;
            for (var dy = -1; dy <= 1 && !found; dy++)
            for (var dx = -1; dx <= 1 && !found; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                found = Lit(to, (yy * w + xx) * 4) == lit;
            }
            if (!found) { bad++; first ??= (x, y); }
        }
        return (bad, first);
    }

    /// <summary>Geometry through the codec: at most <see cref="CodecGeometryShare"/> of the pixels outside ±1 px (luma mask).</summary>
    internal static double AssertGeometryThroughCodec(byte[] a, byte[] b, int w, int h, string what)
    {
        var (bad, first) = GeometryMismatches(a, b, w, h);
        var share = (double)bad / ((long)w * h);
        Assert.True(share <= CodecGeometryShare,
            $"{what}: {bad} pixels ({share:P4}) outside ±1 px of the other side's lit/black area, more than {CodecGeometryShare:P2} (first at {first})");
        return share;
    }

    internal static void AssertSameSourceFrame(byte[] preview, IReadOnlyList<byte[]> reference, long n, int w, int h, string what)
    {
        static double[] Blocks(byte[] p, int w, int h)
        {
            var bw = w / 16; var bh = h / 16;
            var r = new double[bw * bh * 3];
            for (var y = 0; y < bh * 16; y++)
            for (var x = 0; x < bw * 16; x++)
            for (var c = 0; c < 3; c++)
                r[((y / 16) * bw + x / 16) * 3 + c] += p[(y * w + x) * 4 + c] / 256.0;
            return r;
        }
        static double Distance(double[] a, double[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Sum();
        var p = Blocks(preview, w, h);
        var candidates = new[] { n - 1, n, n + 1 }.Where(k => k >= 0 && k < reference.Count).ToList();
        var distances = candidates.ToDictionary(k => k, k => Distance(p, Blocks(reference[(int)k], w, h)));
        var closest = distances.MinBy(kv => kv.Value).Key;
        Assert.True(closest == n, $"{what}: the Preview is closest to export frame {closest} ({string.Join(", ", distances.Select(kv => $"{kv.Key}: {kv.Value:0}"))})");
    }

    internal static (double Mean, double Psnr) WholeFrame(byte[] a, byte[] b)
    {
        double sum = 0, sq = 0; long count = 0;
        for (var i = 0; i < a.Length; i += 4)
        for (var c = 0; c < 3; c++) { var d = a[i + c] - b[i + c]; sum += Math.Abs(d); sq += d * d; count++; }
        var mse = sq / count;
        return (sum / count, mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 / mse));
    }
}
