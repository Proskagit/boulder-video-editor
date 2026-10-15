using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Vision;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// The local real-weights suite of D034 §7 / Phase 19: the pinned model loaded and verified; the C# pipeline against
/// the Python ONNX reference on every E1 click (the three alternatives, their order, IoU); the criterion-10 table on the
/// 164 SDR clicks (the protocol of DEVELOPMENT_PLAN Phase 19); refinement with several points; DirectML vs CPU;
/// performance. One run computes everything once (the frames are encoded once) and writes its report to
/// <c>&lt;E1&gt;/results/&lt;timestamp&gt;/</c>.
/// </summary>
public sealed class E1Run : IDisposable
{
    public const double InheritIoU = 0.98;

    public HoldModelStatus Status { get; }
    public string ReportFolder { get; }
    public List<ClickResult> Clicks { get; } = new();
    public List<double> EncodeGpuMs { get; } = new();
    public double EncodeColdGpuMs { get; }
    public List<double> ClickGpuMs { get; } = new();
    public List<double> CpuVsGpuIoU { get; } = new();
    public double EncodeCpuMs { get; private set; }
    public List<double> ClickCpuMs { get; } = new();
    public InferenceDevice Device { get; }
    public (int? Chosen, bool ContainsBoth, int Visible)? Refinement { get; private set; }
    public double VerifySeconds { get; }

    public E1Run()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        ReportFolder = Path.Combine(ReferenceData.E1Dir, "results", stamp);
        using var gpu = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        var sw = Stopwatch.StartNew();
        Status = gpu.EnsureReadyAsync().GetAwaiter().GetResult();
        VerifySeconds = sw.Elapsed.TotalSeconds;
        if (!Status.IsReady) return;
        Device = gpu.Device;

        var marks = ReferenceData.Marks();
        var first = true;
        foreach (var group in ReferenceData.Clicks().GroupBy(c => c.Frame))
        {
            var frame = ReferenceData.Frame(group.Key);
            sw.Restart();
            var encoded = gpu.PrepareFrameAsync(frame).GetAwaiter().GetResult();
            var ms = sw.Elapsed.TotalMilliseconds;
            if (first)
            {
                EncodeColdGpuMs = ms;
                encoded.Dispose();
                sw.Restart();
                encoded = gpu.PrepareFrameAsync(frame).GetAwaiter().GetResult();
                ms = sw.Elapsed.TotalMilliseconds;
                first = false;
            }
            EncodeGpuMs.Add(ms);
            using (encoded)
            {
                foreach (var click in group)
                {
                    sw.Restart();
                    var set = encoded.PredictAsync(new[] { new PointD(click.X, click.Y) }).GetAwaiter().GetResult();
                    ClickGpuMs.Add(sw.Elapsed.TotalMilliseconds);
                    marks.TryGetValue((click.Clip, click.Id), out var mark);
                    Clicks.Add(Evaluate(click, set, mark));

                    if (click.Clip == "c8" && click.Id == 16)
                    {
                        // D034 §1: an additive click (the lighter part of the two-tone hold) refines the same candidate.
                        var points = new[] { new PointD(click.X, click.Y), new PointD(click.X + 50, click.Y - 50) };
                        var refined = encoded.PredictAsync(points).GetAwaiter().GetResult();
                        var chosen = HoldCandidateRules.Choose(refined);
                        var both = chosen is { } k && Inside(refined.Masks[k], points[0]) && Inside(refined.Masks[k], points[1]);
                        Refinement = (chosen, both, HoldCandidateRules.Visible(refined).Length);
                    }
                }
            }
        }

        // DirectML vs CPU on one frame (24 clicks), and the CPU timings.
        using var cpu = new OnnxHoldSegmentationService(new HoldModelVerifier(ReferenceData.ModelDir),
            device => new OnnxHoldModelBackend(Path.Combine(ReferenceData.ModelDir, HoldModel.Encoder.Name), Path.Combine(ReferenceData.ModelDir, HoldModel.Decoder.Name), InferenceDevice.Cpu), null);
        var c6 = ReferenceData.Clicks().Where(c => c.Clip == "c6").ToList();
        var c6Frame = ReferenceData.Frame(c6[0].Frame);
        sw.Restart();
        using var cpuFrame = cpu.PrepareFrameAsync(c6Frame).GetAwaiter().GetResult();
        EncodeCpuMs = sw.Elapsed.TotalMilliseconds;
        using var gpuFrame = gpu.PrepareFrameAsync(c6Frame).GetAwaiter().GetResult();
        foreach (var click in c6)
        {
            sw.Restart();
            var c = cpuFrame.PredictAsync(new[] { new PointD(click.X, click.Y) }).GetAwaiter().GetResult();
            ClickCpuMs.Add(sw.Elapsed.TotalMilliseconds);
            var g = gpuFrame.PredictAsync(new[] { new PointD(click.X, click.Y) }).GetAwaiter().GetResult();
            for (var k = 0; k < 3; k++) CpuVsGpuIoU.Add(ReferenceData.IoU(c.Masks[k].Pixels, g.Masks[k].Pixels));
        }

        WriteReport();
    }

    private static bool Inside(HoldMask mask, PointD p) => mask.Pixels[(int)p.Y * mask.Width + (int)p.X] != 0;

    private static ClickResult Evaluate(RefClick click, HoldMaskSet set, RefMark? mark)
    {
        var altIoU = new double[3];
        for (var k = 0; k < 3; k++)
            altIoU[k] = ReferenceData.IoU(set.Masks[k].Pixels, ReferenceData.Mask($"reference/onnx_masks/{click.Clip}/{click.Id}_alt{k}.png"));
        var scores = set.Masks.Select(m => m.Score).ToArray();
        var orderCs = Enumerable.Range(0, 3).OrderByDescending(k => scores[k]).ToArray();
        var orderPy = Enumerable.Range(0, 3).OrderByDescending(k => click.Scores[k]).ToArray();
        var chosen = HoldCandidateRules.Choose(set);

        // Criterion 10 (SDR only): the mask the final mark was given on — the graded PyTorch mask (inherited), the Python
        // ONNX chosen mask (re-checked) or none (all masks over 2 %: no candidate, graded on the D034 behaviour).
        string? result = null;
        double? iouRef = null;
        if (mark is not null)
        {
            if (mark.AllMasksOver2Pct)
            {
                result = chosen is null ? mark.BehaviourMark : "RECHECK";
            }
            else if (chosen is { } k)
            {
                var reference = mark.Source == "inherited"
                    ? ReferenceData.Mask($"reference/graded_masks/{click.Clip}/{click.Id}.png")
                    : ReferenceData.Mask($"reference/onnx_masks/{click.Clip}/{click.Id}_alt{click.Chosen}.png");
                iouRef = ReferenceData.IoU(set.Masks[k].Pixels, reference);
                result = iouRef >= InheritIoU ? mark.BehaviourMark : "RECHECK";
            }
            else
            {
                result = "RECHECK";   // no candidate where the reference had one
            }
        }
        return new ClickResult(click.Clip, click.Id, altIoU, scores, click.Scores, orderCs.SequenceEqual(orderPy),
            MinGap(click.Scores), chosen, iouRef, mark?.Source, mark?.BehaviourMark, result);
    }

    private static double MinGap(double[] s)
    {
        var o = s.OrderByDescending(x => x).ToArray();
        return Math.Min(o[0] - o[1], o[1] - o[2]);
    }

    public static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        return v.Length == 0 ? double.NaN : v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    private void WriteReport()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;   // the report's numbers with a decimal point
        try { WriteReportCore(); } finally { CultureInfo.CurrentCulture = culture; }
    }

    private void WriteReportCore()
    {
        Directory.CreateDirectory(ReportFolder);
        var sdr = Clicks.Where(c => c.Result is not null).ToList();
        var md = new StringBuilder();
        md.AppendLine("# Phase 19 — local real-weights suite (D034 §7)");
        md.AppendLine();
        md.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}; machine: {Environment.MachineName}; OS: {Environment.OSVersion}");
        md.AppendLine($"- Model: {HoldModel.Id}; encoder SHA-256 {HoldModel.Encoder.Sha256}; decoder SHA-256 {HoldModel.Decoder.Sha256} (verified in {VerifySeconds:0.0} s)");
        md.AppendLine($"- ONNX Runtime: {typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version}; device: {Device}");
        md.AppendLine($"- GPU: {Gpu()}");
        md.AppendLine();
        md.AppendLine("## C# vs the Python ONNX reference (all 188 clicks)");
        md.AppendLine($"- Median IoU, alternative 0 / 1 / 2: {Median(Clicks.Select(c => c.AltIoU[0])):0.0000} / {Median(Clicks.Select(c => c.AltIoU[1])):0.0000} / {Median(Clicks.Select(c => c.AltIoU[2])):0.0000}");
        md.AppendLine($"- Median IoU of the shown alternative: {Median(Clicks.Where(c => c.Chosen is not null).Select(c => c.AltIoU[c.Chosen!.Value])):0.0000}");
        md.AppendLine($"- Score order equal to the reference: {Clicks.Count(c => c.OrderEqual)}/{Clicks.Count} (where the reference's scores differ by > 0.01: {Clicks.Count(c => c.MinGap > 0.01 && c.OrderEqual)}/{Clicks.Count(c => c.MinGap > 0.01)})");
        md.AppendLine($"- Max |score − reference|: {Clicks.Max(c => Enumerable.Range(0, 3).Max(k => Math.Abs(c.Scores[k] - c.RefScores[k]))):0.0000}");
        md.AppendLine();
        md.AppendLine("## Criterion 10 (164 SDR clicks, same set and protocol)");
        md.AppendLine($"- Without marked problems: **{sdr.Count(c => c.Result == "ok")}/{sdr.Count}** (bar 155); О {sdr.Count(c => c.Result == "O")}, К {sdr.Count(c => c.Result == "K")}, RECHECK {sdr.Count(c => c.Result == "RECHECK")}");
        md.AppendLine($"- RECHECK: {string.Join(", ", sdr.Where(c => c.Result == "RECHECK").Select(c => $"{c.Clip}#{c.Id} ({c.IoURef?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "no mask"})"))}");
        md.AppendLine();
        md.AppendLine("## Refinement, devices, performance");
        md.AppendLine($"- c8 #16 + a click on its lighter part: shown alternative {Refinement?.Chosen?.ToString() ?? "none"}, both points inside: {Refinement?.ContainsBoth}");
        md.AppendLine($"- DirectML vs CPU (c6, 24 clicks × 3 masks): median IoU {Median(CpuVsGpuIoU):0.0000}, min {CpuVsGpuIoU.DefaultIfEmpty(double.NaN).Min():0.0000}");
        md.AppendLine($"- Frame preparation (4K, warm), GPU: median {Median(EncodeGpuMs):0} ms, max {EncodeGpuMs.Max():0} ms; first frame {EncodeColdGpuMs:0} ms (sessions already loaded); CPU {EncodeCpuMs:0} ms");
        md.AppendLine($"- Click (decode + upsample of three masks), GPU: median {Median(ClickGpuMs):0} ms, max {ClickGpuMs.Max():0} ms; CPU median {Median(ClickCpuMs):0} ms");
        File.WriteAllText(Path.Combine(ReportFolder, "report.md"), md.ToString());

        var csv = new StringBuilder("clip,id,iou_alt0,iou_alt1,iou_alt2,score0,score1,score2,ref_score0,ref_score1,ref_score2,order_equal,chosen,iou_with_marked_mask,mark_source,reference_mark,criterion10\n");
        foreach (var c in Clicks)
            csv.AppendLine(string.Join(",", c.Clip, c.Id, F(c.AltIoU[0]), F(c.AltIoU[1]), F(c.AltIoU[2]), F(c.Scores[0]), F(c.Scores[1]), F(c.Scores[2]),
                F(c.RefScores[0]), F(c.RefScores[1]), F(c.RefScores[2]), c.OrderEqual, c.Chosen?.ToString() ?? "none",
                c.IoURef is { } r ? F(r) : "", c.MarkSource ?? "", c.ReferenceMark ?? "", c.Result ?? ""));
        File.WriteAllText(Path.Combine(ReportFolder, "clicks.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(ReportFolder, "summary.json"), JsonSerializer.Serialize(new
        {
            model = HoldModel.Id, device = Device.ToString(), criterion10Ok = sdr.Count(c => c.Result == "ok"), criterion10Total = sdr.Count,
            recheck = sdr.Where(c => c.Result == "RECHECK").Select(c => $"{c.Clip}#{c.Id}").ToArray(),
            medianIoUShown = Median(Clicks.Where(c => c.Chosen is not null).Select(c => c.AltIoU[c.Chosen!.Value])),
            cpuVsGpuMedianIoU = Median(CpuVsGpuIoU), encodeGpuMsMedian = Median(EncodeGpuMs), encodeCpuMs = EncodeCpuMs,
            clickGpuMsMedian = Median(ClickGpuMs)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string F(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string Gpu()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=name,driver_version --format=csv,noheader")
                { RedirectStandardOutput = true, UseShellExecute = false })!;
            var s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return s;
        }
        catch { return "unknown"; }
    }

    public void Dispose() { }
}

public sealed record ClickResult(string Clip, int Id, double[] AltIoU, double[] Scores, double[] RefScores, bool OrderEqual, double MinGap,
    int? Chosen, double? IoURef, string? MarkSource, string? ReferenceMark, string? Result);

public sealed class E1ModelTests(E1Run run, ITestOutputHelper output) : IClassFixture<E1Run>
{
    private void Report() => output.WriteLine(File.Exists(Path.Combine(run.ReportFolder, "report.md")) ? File.ReadAllText(Path.Combine(run.ReportFolder, "report.md")) : "no report");

    [Fact]
    public void The_pinned_model_is_verified_and_loaded()
    {
        Assert.True(run.Status.IsReady, run.Status.Message);
        Report();
    }

    [Fact]
    public void The_three_alternatives_match_the_python_reference()
    {
        Assert.True(run.Status.IsReady, run.Status.Message);
        for (var k = 0; k < 3; k++)
            Assert.True(E1Run.Median(run.Clicks.Select(c => c.AltIoU[k])) >= 0.98, $"alternative {k}: median IoU below 0.98");
        // The order of the scores where the reference separates them (> 0.01).
        var separated = run.Clicks.Where(c => c.MinGap > 0.01).ToList();
        Assert.All(separated, c => Assert.True(c.OrderEqual, $"{c.Clip}#{c.Id}: score order differs"));
    }

    [Fact]
    public void Criterion_10_on_the_E1_set()
    {
        Assert.True(run.Status.IsReady, run.Status.Message);
        var sdr = run.Clicks.Where(c => c.Result is not null).ToList();
        Assert.Equal(164, sdr.Count);
        var ok = sdr.Count(c => c.Result == "ok");
        Report();
        Assert.True(ok >= 155, $"{ok}/164 without marked problems; RECHECK: {string.Join(", ", sdr.Where(c => c.Result == "RECHECK").Select(c => $"{c.Clip}#{c.Id}"))}");
    }

    [Fact]
    public void An_additive_click_refines_the_same_candidate_to_one_mask()
    {
        Assert.True(run.Status.IsReady, run.Status.Message);
        Assert.NotNull(run.Refinement);
        Assert.NotNull(run.Refinement!.Value.Chosen);
        Assert.True(run.Refinement.Value.ContainsBoth);
    }

    [Fact]
    public void DirectML_and_the_CPU_give_the_same_masks()
    {
        Assert.True(run.Status.IsReady, run.Status.Message);
        Assert.Equal(InferenceDevice.Gpu, run.Device);
        Assert.True(E1Run.Median(run.CpuVsGpuIoU) >= 0.98);
    }

    [Fact]
    public void Performance_within_the_D034_bounds()
    {
        Assert.True(run.Status.IsReady, run.Status.Message);
        Assert.True(E1Run.Median(run.EncodeGpuMs) <= 1000, $"GPU frame preparation {E1Run.Median(run.EncodeGpuMs):0} ms");
        Assert.True(run.EncodeCpuMs <= 10000, $"CPU frame preparation {run.EncodeCpuMs:0} ms");
        Assert.True(E1Run.Median(run.ClickGpuMs) <= 150, $"click {E1Run.Median(run.ClickGpuMs):0} ms");
    }
}
