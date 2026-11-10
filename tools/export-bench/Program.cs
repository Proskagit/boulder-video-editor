using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Export;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Media.Caching;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.Video;
using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiVideoEditor.ExportBench;

/// <summary>
/// The export benchmark (D059 E0). Commands:
/// <list type="bullet">
/// <item><c>run &lt;scenario&gt; [options]</c> — one export in this process; prints one <c>RESULT {json}</c> line.</item>
/// <item><c>suite --scenarios a,b --runs 3 [options]</c> — every run of every scenario in its own process; prints the
/// medians as a table and appends every run to <c>--results</c> (JSON lines).</item>
/// <item><c>list</c> — the scenarios.</item>
/// </list>
/// Options: <c>--media</c> (the test videos, default <c>C:\AiVideoEditor\VideosForTest</c> or <c>AIVE_BENCH_MEDIA</c>),
/// <c>--frames N</c>, <c>--encoding SPEC</c> (see <see cref="Encodings"/>), <c>--out DIR</c>, <c>--keep</c> (keep the MP4),
/// <c>--hash-canvases</c>, <c>--reference PATH</c> (a lossless copy of the canvases, for VMAF), <c>--label TEXT</c>.
/// </summary>
internal static class Program
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("usage: run <scenario> | suite --scenarios a,b | list"); return 2; }
        var options = Options.Parse(args.Skip(1));
        switch (args[0])
        {
            case "list":
                foreach (var (name, (description, frames)) in Scenarios.All) Console.WriteLine($"{name,-14} {frames,6} frames  {description}");
                return 0;
            case "run":
                try
                {
                    return await RunAsync(args[1], Options.Parse(args.Skip(2)));
                }
                catch (Exception ex)
                {
                    // Never an unhandled crash: the suite reads this process's output to its end.
                    Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
                    return 1;
                }
            case "suite":
                return await SuiteAsync(options, args.Skip(1).ToArray());
#if !BASELINE
            case "calibrate":
            {
                var ffmpeg = await new FfmpegLocator(Microsoft.Extensions.Options.Options.Create(new FfmpegOptions()), NullLogger<FfmpegLocator>.Instance)
                    .GetFfmpegPathAsync() ?? throw new InvalidOperationException("ffmpeg not found");
                return await Calibration.RunAsync(ffmpeg, (options.Refs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
                    options.Settings?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Calibration.DefaultSettings, options.Results,
                    options.Out ?? Path.Combine(Path.GetTempPath(), "aive-export-calibration"));
            }
#endif
            default:
                Console.Error.WriteLine($"unknown command {args[0]}");
                return 2;
        }
    }

    private static string DefaultMedia => Environment.GetEnvironmentVariable("AIVE_BENCH_MEDIA") ?? @"C:\AiVideoEditor\VideosForTest";

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> RunAsync(string scenario, Options o)
    {
        StartAvalonia();
        var ffmpegOptions = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var locator = new FfmpegLocator(ffmpegOptions, NullLogger<FfmpegLocator>.Instance);
        var ffmpeg = await locator.GetFfmpegPathAsync() ?? throw new InvalidOperationException("ffmpeg not found");
        var probe = new FfprobeMediaAnalysisService(new FfprobeLocator(ffmpegOptions, NullLogger<FfprobeLocator>.Instance),
            NullLogger<FfprobeMediaAnalysisService>.Instance);

        var frames = o.Frames ?? Scenarios.All[scenario].Frames;
        var project = await new Scenarios(o.Media ?? DefaultMedia, probe).BuildAsync(scenario, frames);
        project.Settings.Export = Encodings.Parse(o.Encoding);

        var outDir = o.Out ?? Path.Combine(Path.GetTempPath(), "aive-export-bench");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, $"{scenario}-{Guid.NewGuid():N}.mp4");
        var check = ExportPreflight.Check(project, outPath, ExportPreflightEnvironment.Default(true, _ => true));
        if (!check.CanExport)
        {
            Console.Error.WriteLine("PREFLIGHT: " + string.Join("; ", check.Errors.Select(e => e.Message)));
            return 1;
        }
        var job = check.Job!;

        var luts = new HdrLutStore(Path.Combine(Path.GetTempPath(), "aive-export-bench-luts"), NullLogger<HdrLutStore>.Instance);
        var decoder = new TimedDecoder(new FfmpegVideoDecoder(locator, NullLogger<FfmpegVideoDecoder>.Instance, luts: luts));
        var encoder = new TimedEncoder(Encodings.Encoder(locator), o.HashCanvases, o.Reference, ffmpeg);
        var raster = new RasterTiming();
        var service = Encodings.Service(locator, decoder, new FfmpegAudioDecoder(locator, NullLogger<FfmpegAudioDecoder>.Instance), encoder,
            raster.Wrap(() => new AvaloniaCompositionRasterizer()), o.Rasterizers);

        var stageAt = new Dictionary<ExportStage, double>();
        var watch = Stopwatch.StartNew();
        string? encoderUsed = null;
#if BASELINE
        var progress = new Sync(p => { lock (stageAt) stageAt.TryAdd(p.Stage, watch.Elapsed.TotalMilliseconds); });
#else
        var progress = new Sync(p => { lock (stageAt) { stageAt.TryAdd(p.Stage, watch.Elapsed.TotalMilliseconds); encoderUsed ??= p.Encoder; } });
#endif
        using var sampler = new Sampler();
        var gc0 = GC.CollectionCount(2);
        await service.ExportAsync(job, progress);
        var wall = watch.Elapsed.TotalMilliseconds;
        var system = sampler.Stop();

        var n = job.Output.FrameCount;
        var video = stageAt[ExportStage.Finalizing] - stageAt[ExportStage.Video];
        var size = new FileInfo(outPath).Length;
        var result = new Dictionary<string, object?>
        {
            ["scenario"] = scenario, ["label"] = o.Label, ["encoding"] = o.Encoding ?? "default", ["encoder"] = encoderUsed,
            ["canvas"] = $"{job.Output.Size.Width}x{job.Output.Size.Height}", ["frames"] = n,
            ["wallMs"] = wall, ["prepMs"] = stageAt[ExportStage.Audio], ["audioMs"] = stageAt[ExportStage.Video] - stageAt[ExportStage.Audio],
            ["videoMs"] = video, ["finalizeMs"] = wall - stageAt[ExportStage.Finalizing], ["videoFps"] = n / (video / 1000),
            ["rasterMsPerFrame"] = raster.Ms / n, ["rasterizers"] = raster.Created, ["writeMsPerFrame"] = encoder.FrameMs / n,
            ["decoderMsPerSourceFrame"] = decoder.Ms / Math.Max(1, decoder.Frames), ["sourceFrames"] = decoder.Frames,
            ["gen2"] = GC.CollectionCount(2) - gc0,
            ["sizeBytes"] = size, ["videoMbps"] = size * 8.0 / 1e6 / job.Output.Duration.TotalSeconds,
            ["sha256"] = Sha256(outPath), ["canvasSha256"] = encoder.CanvasHash,
            ["machine"] = Environment.MachineName.Length > 0 ? $"{Environment.ProcessorCount} threads" : "", ["ffmpeg"] = FfmpegVersion(ffmpeg),
        };
        foreach (var (k, v) in system) result[k] = v;
        if (!o.Keep) File.Delete(outPath);
        else result["path"] = outPath;
        Console.WriteLine("RESULT " + JsonSerializer.Serialize(result));
        return 0;
    }

    private static async Task<int> SuiteAsync(Options o, string[] raw)
    {
        var scenarios = (o.Scenarios ?? "4k").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var runs = o.Runs ?? 3;
        var exe = Environment.ProcessPath!;
        var dll = typeof(Program).Assembly.Location;
        var forward = Options.Forward(raw);
        var all = new List<(string Scenario, Dictionary<string, JsonElement> Result)>();
        foreach (var scenario in scenarios)
        {
            for (var i = 0; i < runs; i++)
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(dll);
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add(scenario);
                foreach (var a in forward) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var stderr = p.StandardError.ReadToEndAsync();
                string? line;
                Dictionary<string, JsonElement>? result = null;
                while ((line = await p.StandardOutput.ReadLineAsync()) is not null)
                    if (line.StartsWith("RESULT ", StringComparison.Ordinal))
                        result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line[7..]);
                await p.WaitForExitAsync();
                if (result is null)
                {
                    Console.Error.WriteLine($"{scenario} run {i + 1} failed (exit {p.ExitCode}): {await stderr}");
                    continue;
                }
                Console.Error.WriteLine($"{scenario} run {i + 1}: {result["videoFps"].GetDouble():0.0} fps, wall {result["wallMs"].GetDouble() / 1000:0.0} s");
                all.Add((scenario, result));
                if (o.Results is { } file)
                    await File.AppendAllTextAsync(file, JsonSerializer.Serialize(result) + Environment.NewLine);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"| scenario | label | encoding | encoder | runs | video fps (median) | wall s | raster ms | write ms | dec ms/src | proc CPU | sys CPU | GPU % | NVENC % | VRAM Δ MB | peak WS MB | Mbit/s | MP4 SHA-256 (first 12) |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var group in all.GroupBy(r => r.Scenario))
        {
            var r = group.Select(g => g.Result).ToList();
            double M(string key) => Median(r.Select(x => x.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN));
            var hashes = r.Select(x => x["sha256"].GetString()![..12]).Distinct().ToList();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {group.Key} | {o.Label} | {o.Encoding ?? "default"} | {(r[0].TryGetValue("encoder", out var enc) ? enc.ToString() : "")} | {r.Count} | {M("videoFps"):0.0} | {M("wallMs") / 1000:0.0} | {M("rasterMsPerFrame"):0.0} | {M("writeMsPerFrame"):0.0} | {M("decoderMsPerSourceFrame"):0.0} | {M("procCpuCores"):0.0} | {M("sysCpuCores"):0.0} | {M("gpuUtilAvg"):0} | {M("encUtilAvg"):0} | {M("vramDeltaMaxMB"):0} | {M("peakWorkingSetMB"):0} | {M("videoMbps"):0.0} | {string.Join(" ", hashes)} |"));
        }
        return 0;
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (v.Length == 0) return double.NaN;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string FfmpegVersion(string ffmpeg)
    {
        var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -version") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var first = p.StandardOutput.ReadLine() ?? "";
        p.WaitForExit();
        return first;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void StartAvalonia()
    {
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            AppBuilder.Configure<Application>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            ready.Set();
            Dispatcher.UIThread.MainLoop(CancellationToken.None);
        }) { IsBackground = true, Name = "avalonia-ui" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
    }
}

internal sealed class Sync(Action<ExportProgress> report) : IProgress<ExportProgress>
{
    public void Report(ExportProgress value) => report(value);
}

internal sealed record Options(string? Media, long? Frames, string? Encoding, string? Out, bool Keep, bool HashCanvases, string? Reference,
    string? Label, string? Scenarios, int? Runs, string? Results, int? Rasterizers = null, string? Refs = null, string? Settings = null)
{
    public static Options Parse(IEnumerable<string> args)
    {
        var a = args.ToArray();
        string? Get(string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        return new Options(Get("--media"), Get("--frames") is { } f ? long.Parse(f, CultureInfo.InvariantCulture) : null, Get("--encoding"),
            Get("--out"), a.Contains("--keep"), a.Contains("--hash-canvases"), Get("--reference"), Get("--label"), Get("--scenarios"),
            Get("--runs") is { } r ? int.Parse(r, CultureInfo.InvariantCulture) : null, Get("--results"),
            Get("--rasterizers") is { } k ? int.Parse(k, CultureInfo.InvariantCulture) : null, Get("--refs"), Get("--settings"));
    }

    /// <summary>The options a suite passes on to every run (everything but its own).</summary>
    public static IEnumerable<string> Forward(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--scenarios" or "--runs" or "--results") { i++; continue; }
            yield return args[i];
        }
    }
}
