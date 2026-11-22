#if !BASELINE
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Video;

namespace AiVideoEditor.ExportBench;

/// <summary>
/// The encoder calibration (D059 §5): every candidate setting encodes the same lossless references (the canvases as the
/// export hands them to the encoder: yuv420p after the export's BT.709 conversion, x264 QP 0) with the export's own codec
/// options (<see cref="FfmpegExportEncoder.CodecArgumentsFor"/>); per encoding: the encoder-only speed, the bitrate, bits
/// per pixel and frame, VMAF (the 4K model for frames above 1080p, every frame) against the reference.
/// <c>calibrate --refs a.mkv,b.mkv --settings x264:medium:18,nvenc:p5:22 --results file.jsonl [--vmaf-ffmpeg PATH]</c>; a setting
/// is <c>x264:PRESET:CRF</c>, <c>x265:PRESET:CRF</c>, <c>svtav1:PRESET:CRF</c> (D076), or <c>nvenc:PRESET:CQ[:mp][:noaq]</c>,
/// <c>hevcnvenc:…</c>, <c>av1nvenc:…</c> (D076) — <c>mp</c> multipass. <c>--vmaf-ffmpeg</c>: an FFmpeg with libvmaf when the
/// encoding one (the delivered minimal build) has none.
/// </summary>
internal static class Calibration
{
    public static readonly string[] DefaultSettings =
    {
        "x264:veryfast:18", "x264:faster:18", "x264:fast:18", "x264:medium:18", "x264:slow:16", "x264:slow:18",
        "x264:medium:14", "x264:medium:23", "x264:medium:28",
        "nvenc:p2:20", "nvenc:p2:22", "nvenc:p2:24", "nvenc:p4:22",
        "nvenc:p5:19", "nvenc:p5:21", "nvenc:p5:22", "nvenc:p5:23", "nvenc:p5:24", "nvenc:p5:26", "nvenc:p5:28", "nvenc:p5:31",
        "nvenc:p7:18:mp", "nvenc:p7:20:mp", "nvenc:p7:22:mp", "nvenc:p5:22:noaq",
    };

    public static async Task<int> RunAsync(string ffmpeg, string[] refs, string[] settings, string? results, string work, string? vmafFfmpeg = null)
    {
        Directory.CreateDirectory(work);
        foreach (var reference in refs)
        {
            var (w, h, rate, frames) = Probe(ffmpeg, reference);
            foreach (var setting in settings)
            {
                var plan = Plan(setting);
                var output = Path.Combine(work, $"{Path.GetFileNameWithoutExtension(reference)}-{setting.Replace(':', '_')}.mp4");
                var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-i", reference, "-an", "-map", "0:v:0" };
                args.AddRange(FfmpegExportEncoder.CodecArgumentsFor(plan));
                args.AddRange(new[] { "-pix_fmt", "yuv420p", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
                    "-color_range", "tv", "-f", "mp4", output });
                var watch = Stopwatch.StartNew();
                var (code, err) = await Ffmpeg(ffmpeg, args);
                var seconds = watch.Elapsed.TotalSeconds;
                if (code != 0) { Console.Error.WriteLine($"{reference} {setting}: encode failed: {err}"); continue; }

                var size = new FileInfo(output).Length;
                var duration = frames / rate;
                var (vmaf, vmafMin, vmaf1) = await Vmaf(vmafFfmpeg ?? ffmpeg, output, reference, w, h, work);
                var row = new Dictionary<string, object>
                {
                    ["reference"] = Path.GetFileName(reference), ["setting"] = setting, ["width"] = w, ["height"] = h, ["fps"] = rate,
                    ["frames"] = frames, ["encodeFps"] = frames / seconds, ["mbps"] = size * 8 / duration / 1e6,
                    ["bpp"] = size * 8.0 / ((double)w * h * frames), ["vmaf"] = vmaf, ["vmafMin"] = vmafMin, ["vmaf1pct"] = vmaf1,
                    ["sizeBytes"] = size,
                };
                var line = JsonSerializer.Serialize(row);
                Console.WriteLine(line);
                if (results is not null) await File.AppendAllTextAsync(results, line + Environment.NewLine);
                File.Delete(output);
            }
        }
        return 0;
    }

    public static ExportVideoPlan Plan(string setting)
    {
        var p = setting.Split(':');
        var value = int.Parse(p[2], CultureInfo.InvariantCulture);
        var (encoder, codec) = p[0] switch
        {
            "x264" => (ExportVideoEncoder.Software, ExportVideoCodec.H264),
            "x265" => (ExportVideoEncoder.Software, ExportVideoCodec.Hevc),
            "svtav1" => (ExportVideoEncoder.Software, ExportVideoCodec.Av1),
            "nvenc" => (ExportVideoEncoder.Nvenc, ExportVideoCodec.H264),
            "hevcnvenc" => (ExportVideoEncoder.Nvenc, ExportVideoCodec.Hevc),
            "av1nvenc" => (ExportVideoEncoder.Nvenc, ExportVideoCodec.Av1),
            _ => throw new ArgumentException(setting)
        };
        return new ExportVideoPlan(encoder, p[1], value, ExportRateControl.ConstantQuality, 0, encoder == ExportVideoEncoder.Nvenc && p.Contains("mp"), false)
        {
            Codec = codec, SpatialAq = !p.Contains("noaq")
        };
    }

    private static (int W, int H, double Rate, long Frames) Probe(string ffmpeg, string file)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe" + Path.GetExtension(ffmpeg));
        var psi = new ProcessStartInfo(File.Exists(ffprobe) ? ffprobe : "ffprobe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
        };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=width,height,r_frame_rate,nb_read_packets", "-of", "csv=p=0", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var parts = p.StandardOutput.ReadToEnd().Trim().Split(',');
        p.WaitForExit();
        var r = parts[2].Split('/');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
            double.Parse(r[0], CultureInfo.InvariantCulture) / double.Parse(r[1], CultureInfo.InvariantCulture), long.Parse(parts[3], CultureInfo.InvariantCulture));
    }

    private static async Task<(double Mean, double Min, double Low1)> Vmaf(string ffmpeg, string distorted, string reference, int w, int h, string work)
    {
        var log = Path.Combine(work, "vmaf-" + Guid.NewGuid().ToString("N") + ".json");
        var model = (long)w * h > 1920L * 1080 ? "vmaf_4k_v0.6.1" : "vmaf_v0.6.1";
        var escaped = log.Replace('\\', '/').Replace(":", "\\\\:");
        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "error", "-i", distorted, "-i", reference,
            "-lavfi", $"[0:v]setpts=PTS-STARTPTS[d];[1:v]setpts=PTS-STARTPTS[r];[d][r]libvmaf=model=version={model}:n_threads={Environment.ProcessorCount}:log_fmt=json:log_path={escaped}",
            "-f", "null", "-"
        };
        var (code, err) = await Ffmpeg(ffmpeg, args);
        if (code != 0) { Console.Error.WriteLine("vmaf failed: " + err); return (double.NaN, double.NaN, double.NaN); }
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(log));
        File.Delete(log);
        var frames = doc.RootElement.GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("metrics").GetProperty("vmaf").GetDouble()).OrderBy(v => v).ToArray();
        var low = frames.Take(Math.Max(1, frames.Length / 100)).Average();
        return (frames.Average(), frames[0], low);
    }

    private static async Task<(int Code, string Err)> Ffmpeg(string ffmpeg, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, err.Trim());
    }
}
#endif
