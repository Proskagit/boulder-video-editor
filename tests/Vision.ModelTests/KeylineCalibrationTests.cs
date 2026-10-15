using System.Globalization;
using System.Text;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// Phase 19 Step 19.6, local (D034 §5): the colour samples of every E1 SDR click through the application's own pipeline
/// (the pinned model's candidate, its largest component and outer contour, <see cref="HoldColorSampler"/> with the other
/// clicks of the frame excluded from the wall ring) — written to <c>&lt;E1&gt;/results/calibration-&lt;stamp&gt;/samples.csv</c>
/// for the threshold calibration outside the repository. Clicks without a visible candidate are listed without samples.
/// </summary>
public sealed class KeylineCalibrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_colour_samples_of_the_E1_holds_are_written_for_the_calibration()
    {
        using var service = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await service.EnsureReadyAsync()).IsReady);
        var folder = Path.Combine(ReferenceData.E1Dir, "results", "calibration-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);

        var csv = new StringBuilder("clip,id,frame,x,y,visible,surface_l,surface_a,surface_b,wall_l,wall_a,wall_b,contour\n");
        var written = 0;
        foreach (var group in ReferenceData.Clicks().Where(c => c.Clip.StartsWith('c')).GroupBy(c => c.Frame))
        {
            var frame = ReferenceData.Frame(group.Key);
            using var encoded = await service.PrepareFrameAsync(frame);
            var holds = new List<(RefClick Click, byte[]? Mask, System.Collections.Immutable.ImmutableArray<NormalizedPoint> Contour)>();
            foreach (var click in group)
            {
                var set = await encoded.PredictAsync(new[] { new PointD(click.X, click.Y) });
                if (HoldCandidateRules.Choose(set) is not { } chosen)
                {
                    holds.Add((click, null, default));
                    continue;
                }
                var mask = set.Masks[chosen];
                var (component, _) = MaskContour.LargestComponent(mask.Pixels, mask.Width, mask.Height);
                holds.Add((click, component, MaskContour.Outer(component, mask.Width, mask.Height)));
            }

            foreach (var (click, mask, contour) in holds)
            {
                var f = CultureInfo.InvariantCulture;
                if (mask is null)
                {
                    csv.Append(string.Format(f, "{0},{1},{2},{3},{4},0,,,,,,,\n", click.Clip, click.Id, click.Frame, click.X, click.Y));
                    continue;
                }
                var others = holds.Where(h => h.Mask is not null && !ReferenceEquals(h.Mask, mask))
                    .Select(h => (IReadOnlyList<NormalizedPoint>)h.Contour).ToList();
                var (surface, wall) = HoldColorSampler.Sample(frame, mask, others);
                var points = string.Join(' ', contour.Select(p => string.Format(f, "{0:F6}:{1:F6}", p.X, p.Y)));
                csv.Append(string.Format(f, "{0},{1},{2},{3},{4},1,{5:R},{6:R},{7:R},{8:R},{9:R},{10:R},{11}\n",
                    click.Clip, click.Id, click.Frame, click.X, click.Y, surface.L, surface.A, surface.B, wall.L, wall.A, wall.B, points));
                written++;
            }
        }
        File.WriteAllText(Path.Combine(folder, "samples.csv"), csv.ToString());
        output.WriteLine($"{written} holds sampled → {folder}");
        Assert.True(written > 150);
    }
}
