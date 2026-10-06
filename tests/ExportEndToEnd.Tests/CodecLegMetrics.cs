using System.Globalization;
using AiVideoEditor.Video.Tests;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// The codec leg MP4 → export canvas (decision L1-c, D028 §6, Step 13.8), measured with the Step 8.6 method: the MP4
/// decoded by ffmpeg (BT.709 tags honoured) against the canvases the service encoded, 8-bit R, G, B samples. The error is
/// split with a reference encoded through the same BGRA → BT.709 limited 4:2:0 conversion but libx264 lossless
/// (<c>-qp 0</c>): <b>floor</b> = reference vs canvas (the conversion and the chroma subsampling, no quantization; the same
/// for every quality level), <b>quant</b> = MP4 vs reference (the lossy encoding itself — what the quality level changes),
/// <b>total</b> = MP4 vs canvas.
/// </summary>
internal static class CodecLegMetrics
{
    /// <summary>Pooled statistics of |Δ| over every R, G, B sample of every frame, and per frame.</summary>
    internal sealed record Stats(double Mean, int P99, int P999, int Max, double Psnr, double WorstFrameMean, double WorstFramePsnr)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"mean {Mean:0.000} p99 {P99} p99.9 {P999} max {Max} PSNR {Fmt(Psnr)} | worst frame mean {WorstFrameMean:0.000} PSNR {Fmt(WorstFramePsnr)}");

        private static string Fmt(double db) => double.IsPositiveInfinity(db) ? "inf" : db.ToString("0.00", CultureInfo.InvariantCulture);
    }

    internal static Stats Compare(IReadOnlyList<byte[]> a, IReadOnlyList<byte[]> b)
    {
        if (a.Count != b.Count) throw new InvalidOperationException($"{a.Count} frames against {b.Count}");
        var histogram = new long[256];
        double worstMean = 0, worstPsnr = double.PositiveInfinity, sq = 0;
        long sum = 0, count = 0;
        for (var f = 0; f < a.Count; f++)
        {
            long fs = 0, fc = 0; double fsq = 0;
            var (x, y) = (a[f], b[f]);
            for (var i = 0; i < x.Length; i += 4)
            for (var c = 0; c < 3; c++)
            {
                var d = Math.Abs(x[i + c] - y[i + c]);
                histogram[d]++;
                fs += d; fsq += d * d; fc++;
            }
            sum += fs; sq += fsq; count += fc;
            worstMean = Math.Max(worstMean, (double)fs / fc);
            worstPsnr = Math.Min(worstPsnr, Psnr(fsq / fc));
        }
        return new Stats((double)sum / count, Percentile(histogram, count, 0.99), Percentile(histogram, count, 0.999),
            Array.FindLastIndex(histogram, h => h > 0), Psnr(sq / count), worstMean, worstPsnr);
    }

    private static double Psnr(double mse) => mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 / mse);

    private static int Percentile(long[] histogram, long count, double q)
    {
        var target = (long)Math.Ceiling(q * count);
        long seen = 0;
        for (var d = 0; d < histogram.Length; d++)
        {
            seen += histogram[d];
            if (seen >= target) return d;
        }
        return 255;
    }

    /// <summary>The canvases of <paramref name="run"/> encoded through the export's conversion (the same filter, pixel format and
    /// colour tags as <c>FfmpegExportEncoder</c>) but libx264 lossless, decoded again.</summary>
    internal static List<byte[]> LosslessReference(ExportRun run)
    {
        var (w, h) = (run.Output.Size.Width, run.Output.Size.Height);
        var rate = run.Output.FrameRate;
        var folder = Path.Combine(Path.GetTempPath(), "aive-l1c", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var raw = Path.Combine(folder, "canvases.bgra");
            using (var file = File.Create(raw))
                foreach (var canvas in run.Canvases) file.Write(canvas);
            var reference = Path.Combine(folder, "reference.mp4");
            EncoderHarness.Run(FfmpegTools.Ffmpeg!, "-v", "error", "-y",
                "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{w}x{h}", "-framerate", $"{rate.Numerator}/{rate.Denominator}", "-i", raw,
                "-vf", "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
                "-fps_mode", "passthrough", "-c:v", "libx264", "-preset", "medium", "-qp", "0", "-pix_fmt", "yuv420p",
                "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv", "-f", "mp4", reference);
            return EncoderHarness.DecodeFrames(reference, w, h);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
