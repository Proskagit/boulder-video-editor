using System.Text.Json;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Holds;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// D034 §1 as amended 2026-10-15, local: the product owner's close-up (`<E1>/results/large-holds-20261015/`, a frame of
/// their clip with three holds of 2.5–6.2 % of the frame and their own outlines). A click at the centre of each hold gives the hold as the
/// first candidate (IoU ≥ 0.9 with the owner's outline); a click elsewhere on it gives the hold either at once or after one
/// Shift+click at the centre (a printed logo is a mask of its own); and no click on the wall gives a
/// candidate larger than 2 % (the wall is never offered). The same masks under the original 2 % rule are reported.
/// </summary>
public sealed class LargeHoldsTests(ITestOutputHelper output)
{
    private const string Folder = "results/large-holds-20261015";

    [Fact]
    public async Task Large_holds_of_a_close_up_are_offered_and_the_wall_is_not()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ReferenceData.Path_($"{Folder}/clicks.json")));
        var frame = ReferenceData.Frame($"{Folder}/frame.png");
        using var service = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await service.EnsureReadyAsync()).IsReady);
        using var encoded = await service.PrepareFrameAsync(frame);

        foreach (var hold in doc.RootElement.GetProperty("holds").EnumerateObject())
        {
            var truth = ReferenceData.Mask($"{Folder}/truth_{hold.Name}.png");
            foreach (var p in hold.Value.EnumerateArray())
            {
                var point = new PointD(p[0].GetDouble(), p[1].GetDouble());
                var set = await encoded.PredictAsync(new[] { point });
                var chosen = HoldCandidateRules.Choose(set);
                var original = Enumerable.Range(0, 3).Where(i => set.Masks[i].Area > 0 && set.Masks[i].AreaFraction <= 0.02)
                    .OrderByDescending(i => set.Masks[i].Score).Cast<int?>().FirstOrDefault();
                string Describe(int? i) => i is { } k
                    ? $"{set.Masks[k].AreaFraction * 100:F2} % score {set.Masks[k].Score:F3} IoU {ReferenceData.IoU(set.Masks[k].Pixels, truth):F3}"
                    : "none";
                output.WriteLine($"{hold.Name} click ({point.X:F0},{point.Y:F0}): amended rule → {Describe(chosen)}; original 2 % rule → {Describe(original)}");
                if (chosen is { } c && ReferenceData.IoU(set.Masks[c].Pixels, truth) >= 0.9) continue;
                // Not the hold at once (e.g. a click on a printed logo, which the model outlines on its own): one Shift+click
                // at the hold's first point gives the hold. The first point of every hold must give it directly.
                Assert.NotEqual(hold.Value[0].ToString(), p.ToString());
                var first = hold.Value[0];
                var refined = await encoded.PredictAsync(new[] { point, new PointD(first[0].GetDouble(), first[1].GetDouble()) });
                var r = HoldCandidateRules.Choose(refined);
                output.WriteLine($"   + Shift+click at the first point → {(r is { } q ? $"{refined.Masks[q].AreaFraction * 100:F2} % score {refined.Masks[q].Score:F3} IoU {ReferenceData.IoU(refined.Masks[q].Pixels, truth):F3}" : "none")}");
                Assert.True(r is { } rr && ReferenceData.IoU(refined.Masks[rr].Pixels, truth) >= 0.9, $"{hold.Name}: no hold even after a Shift+click.");
            }
        }

        var wallClicks = 0;
        foreach (var p in doc.RootElement.GetProperty("wall").EnumerateArray())
        {
            var set = await encoded.PredictAsync(new[] { new PointD(p[0].GetDouble(), p[1].GetDouble()) });
            if (HoldCandidateRules.Choose(set) is { } k)
                Assert.True(set.Masks[k].AreaFraction <= HoldCandidateRules.MaxAreaFraction,
                    $"A wall click at ({p[0]},{p[1]}) offered {set.Masks[k].AreaFraction * 100:F1} % (score {set.Masks[k].Score:F3}).");
            wallClicks++;
        }
        output.WriteLine($"{wallClicks} wall clicks: no candidate over 2 %.");
    }
}
