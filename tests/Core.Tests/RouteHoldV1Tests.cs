using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Playback;
using Xunit;
using static AiVideoEditor.Core.Tests.RouteHoldClassificationTests;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// D072 §4, the D048 research variant in production: the colour map's large regions split again at 0.06 (a panel of the
/// route's colour no longer swallows the holds on it), the chroma gate 0.3 and the hue gate V1 30° on top of the tolerance,
/// and <see cref="RouteHoldSearchOptions.Production"/> without the grid. The record's own defaults stay D048 §2's.
/// </summary>
public class RouteHoldV1Tests
{
    private static OkLabColor Lab((byte R, byte G, byte B) c) => RouteColor.SrgbToOkLab(c.R, c.G, c.B);

    private static OkLabColor Polar(double l, double chroma, double hueDegrees) =>
        new(l, chroma * Math.Cos(hueDegrees * Math.PI / 180), chroma * Math.Sin(hueDegrees * Math.PI / 180));

    /// <summary>A red a little off the route's red: inside the colour map's threshold 0.10, outside the split 0.06.</summary>
    private static (byte R, byte G, byte B) PanelRed()
    {
        var red = Lab(Red);
        for (var g = 36; g < 160; g++)
        {
            var c = ((byte)200, (byte)g, (byte)50);
            var d = RouteHoldColor.Distance(Lab(c), red);
            if (d is > 0.07 and < 0.09) return c;
        }
        throw new InvalidOperationException("No panel colour.");
    }

    /// <summary>The wall with a large panel of <see cref="PanelRed"/> and the route's red holds on it.</summary>
    private static DecodedFrame PanelScene()
    {
        var panel = PanelRed();
        var pixels = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var c = WallColor;
            if (x is >= 20 and < 460 && y is >= 20 and < 250) c = panel;
            foreach (var h in RedHolds)
                if ((x - h.X) * (x - h.X) + (y - h.Y) * (y - h.Y) <= h.R * h.R) c = Red;
            var i = (y * W + x) * 4;
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, 255);
        }
        return new DecodedFrame(W, H, W * 4, pixels, new SourceTimestamp(0, new TimeBase(1, 25)));
    }

    private static bool InHold((int X, int Y, int R) h, PointD p) =>
        (p.X - h.X) * (p.X - h.X) + (p.Y - h.Y) * (p.Y - h.Y) <= h.R * h.R;

    [Fact]
    public void Without_a_split_the_planner_is_the_plain_one()
    {
        var frame = Scene();
        var red = Lab(Red);
        Assert.Equal(ColorPromptPlanner.Plan(frame, red, threshold: 0.05),
            ColorPromptPlanner.Plan(frame, red, 0.05, ColorPromptPlanner.MapWidth, ColorPromptPlanner.MaxPrompts, null, 0));
        // A split whose regions are all small changes nothing either.
        Assert.Equal(ColorPromptPlanner.Plan(frame, red, threshold: 0.05),
            ColorPromptPlanner.Plan(frame, red, 0.05, ColorPromptPlanner.MapWidth, ColorPromptPlanner.MaxPrompts, 0.06, 0.5));
    }

    [Fact]
    public void A_large_region_of_the_routes_colour_is_split_into_the_holds_on_it()
    {
        var frame = PanelScene();
        var red = Lab(Red);
        var plain = ColorPromptPlanner.Plan(frame, red);
        Assert.Single(plain);                                                 // the panel and its holds: one region, one prompt

        var holdFraction = Math.PI * 12 * 12 / (W * H);
        var split = ColorPromptPlanner.Plan(frame, red, ColorPromptPlanner.MapThreshold, ColorPromptPlanner.MapWidth,
            ColorPromptPlanner.MaxPrompts, 0.06, 4 * holdFraction);
        Assert.Equal(RedHolds.Length, split.Count);
        foreach (var hold in RedHolds) Assert.Single(split, p => InHold(hold, p));
        Assert.True(InHold(RedHolds[2], split[0]));                          // the largest first
    }

    [Fact]
    public void The_gates_refuse_a_washed_out_colour_and_a_turned_hue_but_not_a_dull_one()
    {
        var gates = RouteHoldGates.V1;
        var reference = Polar(0.6, 0.10, 30);
        Assert.True(gates.Admits(Polar(0.6, 0.10, 55), reference));          // 25°
        Assert.False(gates.Admits(Polar(0.6, 0.10, 65), reference));         // 35°
        Assert.False(gates.Admits(Polar(0.6, 0.10, 30 + 180), reference));   // the opposite hue
        Assert.True(gates.Admits(Polar(0.6, 0.10, 355), Polar(0.6, 0.10, 5)));   // across 0° / 360°: 10°
        Assert.False(gates.Admits(Polar(0.6, 0.025, 30), reference));        // chroma below 0.3 × 0.10
        Assert.True(gates.Admits(Polar(0.6, 0.031, 30), reference));
        // A dull reference (chroma below 0.025): the hue is noise, only the tolerance decides.
        Assert.True(gates.Admits(Polar(0.6, 0.02, 200), Polar(0.6, 0.02, 30)));
    }

    [Fact]
    public void A_candidate_within_the_tolerance_but_of_another_hue_is_not_selected_with_the_gates()
    {
        var reference = Polar(0.6, 0.05, 30);
        var turned = Polar(0.6, 0.05, 75);                                     // 45° away, yet ≈ 0.038 from the reference
        Assert.True(RouteHoldColor.Distance(turned, reference) < RouteHoldColor.DefaultTolerance);
        RouteHoldCandidate Make(int x0, OkLabColor surface, bool control)
        {
            var mask = new byte[W * H];
            for (var y = 10; y < 30; y++)
            for (var x = x0; x < x0 + 20; x++) mask[y * W + x] = 1;
            var crop = MaskCrop.From(mask, W, H)!;
            return new RouteHoldCandidate(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0, 0), new NormalizedPoint(0.1, 0),
                new NormalizedPoint(0, 0.1)), new NormalizedPoint((x0 + 10.0) / W, 20.0 / H), 0.9, crop.Area / (double)(W * H), surface,
                RouteHoldColor.Distance(surface, reference), crop, control);
        }
        var control = Make(10, reference, control: true);
        var other = Make(100, turned, control: false);

        Assert.Equal(2, RouteHoldSelector.Select(new[] { control, other }, RouteHoldColor.DefaultTolerance).Count);
        Assert.Equal(new[] { control.Id },
            RouteHoldSelector.Select(new[] { control, other }, RouteHoldColor.DefaultTolerance, null, RouteHoldGates.V1, reference).Select(c => c.Id));
        Assert.Single(new RouteHoldSearch(reference, new[] { control, other }, 1, RouteHoldGates.V1).Select(RouteHoldColor.DefaultTolerance));
    }

    [Fact]
    public async Task The_production_search_has_no_grid_and_still_finds_the_route()
    {
        Assert.True(new RouteHoldSearchOptions().UseGrid);                    // D048 §2's defaults kept for the research suite
        Assert.Null(new RouteHoldSearchOptions().Gates);
        var options = RouteHoldSearchOptions.Production;
        Assert.False(options.UseGrid);
        Assert.Equal(0.06, options.SplitDistance);
        Assert.Same(RouteHoldGates.V1, options.Gates);

        var model = new FloodModel(Scene());
        var result = await RouteHoldFinder.FindAsync(model, model.Frame, new NormalizedPoint((RedHolds[0].X + 0.5) / W, (RedHolds[0].Y + 0.5) / H),
            Array.Empty<ImmutableArray<NormalizedPoint>>(), options);
        var search = result.Search!;
        var grid = HoldProposalService.GridPoints(W, H, new ProposalPipelineOptions().GridDivisions);
        Assert.DoesNotContain(model.Points, p => grid.Contains(p));
        Assert.Same(RouteHoldGates.V1, search.Gates);
        var selected = search.Select(RouteHoldColor.DefaultTolerance);
        Assert.Equal(RedHolds.Length, selected.Count);
        Assert.True(selected[0].IsControl);
    }

    [Fact]
    public async Task On_a_panel_of_the_routes_colour_the_split_finds_the_holds_the_plain_planner_misses()
    {
        var frame = PanelScene();
        var control = new NormalizedPoint((RedHolds[0].X + 0.5) / W, (RedHolds[0].Y + 0.5) / H);
        var plain = (await RouteHoldFinder.FindAsync(new FloodModel(frame), frame, control,
            Array.Empty<ImmutableArray<NormalizedPoint>>(), new RouteHoldSearchOptions { UseGrid = false })).Search!;
        var split = (await RouteHoldFinder.FindAsync(new FloodModel(frame), frame, control,
            Array.Empty<ImmutableArray<NormalizedPoint>>(), RouteHoldSearchOptions.Production)).Search!;

        Assert.True(plain.Select(RouteHoldColor.DefaultTolerance).Count < RedHolds.Length);
        Assert.Equal(RedHolds.Length, split.Select(RouteHoldColor.DefaultTolerance).Count);
    }
}
