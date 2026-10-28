using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Playback;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// D048 stage B (<c>artifacts/d048-control-colour/b-masks/PREREGISTRATION.md</c>), diagnostic only: at each saved control
/// point, the model's three masks — score, area, whether <see cref="HoldCandidateRules"/> offers it, which one it chooses,
/// the largest component's outline and its surface colour (<see cref="HoldColorSampler"/>). For every other offered mask
/// L: the share of the chosen mask C inside L, the area ratio and the colour of the ring L \ C. One decoder call per
/// point on the full-frame encoder; the frames are the saved ones (<c>inputs.json</c>). Writes <c>masks.json</c>.
/// </summary>
public sealed class ControlMaskAlternativesTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Dump_the_three_masks()
    {
        var dir = Environment.GetEnvironmentVariable("AIVE_B_DIR");
        if (dir is null || !File.Exists(Path.Combine(dir, "inputs.json")))
        {
            output.WriteLine("SKIPPED: AIVE_B_DIR not set");
            return;
        }
        Assert.False(File.Exists(Path.Combine(dir, "masks.json")), "masks.json exists — never overwritten");
        using var inputs = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "inputs.json")));
        using var model = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await model.EnsureReadyAsync()).IsReady);

        var results = new List<object>();
        foreach (var c in inputs.RootElement.EnumerateArray())
        {
            int w = c.GetProperty("w").GetInt32(), h = c.GetProperty("h").GetInt32(), stride = c.GetProperty("stride").GetInt32();
            var frame = new DecodedFrame(w, h, stride, await File.ReadAllBytesAsync(c.GetProperty("raw").GetString()!), new SourceTimestamp(0, new TimeBase(1, 1000)));   // the time is not used by the model
            using var encoded = await model.PrepareFrameAsync(frame);
            var cp = c.GetProperty("control");
            var point = new PointD(Math.Clamp(cp[0].GetDouble(), 0, 1) * w, Math.Clamp(cp[1].GetDouble(), 0, 1) * h);
            var set = await encoded.PredictAsync(new[] { point });
            var chosen = HoldCandidateRules.Choose(set);
            var components = set.Masks.Select(m => MaskContour.LargestComponent(m.Pixels, w, h).Mask).ToList();
            var masks = set.Masks.Select((m, k) => new
            {
                index = k, score = m.Score, area = m.AreaFraction, offered = HoldCandidateRules.IsOffered(m), chosen = chosen == k,
                component_area = components[k].Count(b => b != 0) / (double)(w * h),
                contour = MaskContour.Outer(components[k], w, h).Select(p => new[] { p.X, p.Y }),
                surface = Lab(Surface(frame, components[k]))
            }).ToList();
            var pairs = new List<object>();
            if (chosen is { } ci)
            {
                var cMask = components[ci]; long cArea = cMask.Count(b => b != 0);
                for (var k = 0; k < set.Masks.Length; k++)
                {
                    if (k == ci || !HoldCandidateRules.IsOffered(set.Masks[k])) continue;
                    var l = components[k]; long lArea = l.Count(b => b != 0), inter = 0;
                    var ring = new byte[w * h]; long ringArea = 0;
                    for (var i = 0; i < l.Length; i++)
                    {
                        if (l[i] != 0 && cMask[i] != 0) inter++;
                        if (l[i] != 0 && cMask[i] == 0) { ring[i] = 1; ringArea++; }
                    }
                    pairs.Add(new { other = k, inside_share = cArea == 0 ? 0 : inter / (double)cArea, area_ratio = cArea == 0 ? 0 : lArea / (double)cArea,
                        ring_area = ringArea / (double)(w * h), ring_surface = ringArea > 0 ? Lab(Surface(frame, ring)) : null });
                }
            }
            output.WriteLine($"{c.GetProperty("case").GetString()}: chosen {chosen}; " + string.Join("; ", set.Masks.Select((m, k) => $"#{k} score {m.Score:0.00} area {m.AreaFraction:P2} offered {HoldCandidateRules.IsOffered(m)}")));
            results.Add(new { set = c.GetProperty("set").GetString(), @case = c.GetProperty("case").GetString(), frame = new[] { w, h }, chosen, masks, pairs });
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "masks.json"), JsonSerializer.Serialize(results));
        await File.WriteAllTextAsync(Path.Combine(dir, "environment.json"), JsonSerializer.Serialize(new { run = DateTimeOffset.Now, device = model.Device.ToString(), model = HoldMarkup.CurrentModelId }));
    }

    private static OkLabColor Surface(DecodedFrame frame, byte[] mask) =>
        HoldColorSampler.Sample(frame, mask, Array.Empty<IReadOnlyList<NormalizedPoint>>()).Surface;

    private static double[] Lab(OkLabColor c) => new[] { c.L, c.A, c.B };
}
