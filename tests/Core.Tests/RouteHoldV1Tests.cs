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
/// D072 §4 and amendment 1, the application's route search: the colour map's large regions split again at 0.06 (a panel of
/// the route's colour no longer swallows the holds on it), the strict gates on top of the tolerance (chroma 0.5, a white / grey
/// hold never joins a coloured route, hue 20°), the route's group grown from the control hold (the same colour far away is
/// another route) and at most 15 proposals; <see cref="RouteHoldSearchOptions.Production"/> without the grid. The record's
/// own defaults stay D048 §2's.
/// </summary>
public class RouteHoldV1Tests
{
    /// <summary>The route: four red holds close together; one red hold of another route at the far corner.</summary>
    private static readonly (int X, int Y, int R)[] Route = { (200, 60, 12), (215, 105, 10), (250, 145, 14), (225, 195, 8) };
    private static readonly (int X, int Y, int R) FarRed = (440, 240, 10);

    private static OkLabColor Lab((byte R, byte G, byte B) c) => RouteColor.SrgbToOkLab(c.R, c.G, c.B);

    private static OkLabColor Polar(double l, double chroma, double hueDegrees) =>
        new(l, chroma * Math.Cos(hueDegrees * Math.PI / 180), chroma * Math.Sin(hueDegrees * Math.PI / 180));

    private static NormalizedPoint At((int X, int Y, int R) disc) => new((disc.X + 0.5) / W, (disc.Y + 0.5) / H);

    private static bool InHold((int X, int Y, int R) h, PointD p) =>
        (p.X - h.X) * (p.X - h.X) + (p.Y - h.Y) * (p.Y - h.Y) <= h.R * h.R;

    private static bool Covers(RouteHoldCandidate c, (int X, int Y, int R) h) => c.Mask.Contains(h.X, h.Y);

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

    /// <summary>The wall with the route (on a large panel of <see cref="PanelRed"/> when <paramref name="panel"/>) and the
    /// far red hold of another route.</summary>
    private static DecodedFrame RouteScene(bool panel)
    {
        var panelColor = PanelRed();
        var pixels = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var c = WallColor;
            if (panel && x is >= 150 and < 300 && y is >= 30 and < 230) c = panelColor;
            foreach (var h in Route.Append(FarRed))
                if ((x - h.X) * (x - h.X) + (y - h.Y) * (y - h.Y) <= h.R * h.R) c = Red;
            var i = (y * W + x) * 4;
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, 255);
        }
        return new DecodedFrame(W, H, W * 4, pixels, new SourceTimestamp(0, new TimeBase(1, 25)));
    }

    /// <summary>A candidate of <paramref name="size"/> px square at (<paramref name="x0"/>, <paramref name="y0"/>).</summary>
    private static RouteHoldCandidate Make(int x0, int y0, OkLabColor surface, OkLabColor reference, bool control = false, int size = 16)
    {
        var mask = new byte[W * H];
        for (var y = y0; y < y0 + size; y++)
        for (var x = x0; x < x0 + size; x++) mask[y * W + x] = 1;
        var crop = MaskCrop.From(mask, W, H)!;
        return new RouteHoldCandidate(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0, 0), new NormalizedPoint(0.1, 0),
            new NormalizedPoint(0, 0.1)), new NormalizedPoint((x0 + size / 2.0) / W, (y0 + size / 2.0) / H), 0.9,
            crop.Area / (double)(W * H), surface, RouteHoldColor.Distance(surface, reference), crop, control);
    }

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
        var frame = RouteScene(panel: true);
        var red = Lab(Red);
        var plain = ColorPromptPlanner.Plan(frame, red);
        Assert.Equal(2, plain.Count);                                         // the panel with its holds, the far hold

        var holdFraction = Math.PI * 12 * 12 / (W * H);
        var split = ColorPromptPlanner.Plan(frame, red, ColorPromptPlanner.MapThreshold, ColorPromptPlanner.MapWidth,
            ColorPromptPlanner.MaxPrompts, 0.06, 4 * holdFraction);
        foreach (var hold in Route.Append(FarRed)) Assert.Single(split, p => InHold(hold, p));
        Assert.Equal(Route.Length + 1, split.Count);
    }

    [Fact]
    public void The_strict_gates_refuse_a_washed_out_colour_a_white_hold_and_a_turned_hue_but_not_on_a_dull_route()
    {
        var gates = RouteHoldGates.Strict;
        var reference = Polar(0.6, 0.10, 30);
        Assert.True(gates.Admits(Polar(0.6, 0.10, 45), reference));          // 15°
        Assert.False(gates.Admits(Polar(0.6, 0.10, 55), reference));         // 25°: V1 took it, the strict rule does not
        Assert.True(RouteHoldGates.V1.Admits(Polar(0.6, 0.10, 55), reference));
        Assert.False(gates.Admits(Polar(0.6, 0.10, 30 + 180), reference));   // the opposite hue
        Assert.True(gates.Admits(Polar(0.6, 0.10, 355), Polar(0.6, 0.10, 5)));   // across 0° / 360°: 10°
        Assert.False(gates.Admits(Polar(0.6, 0.045, 30), reference));        // chroma below 0.5 × 0.10
        Assert.True(gates.Admits(Polar(0.6, 0.055, 30), reference));
        // A white or grey hold (chroma below 0.025) never joins a coloured route, even a pale one…
        Assert.False(gates.Admits(Polar(0.9, 0.02, 30), Polar(0.6, 0.035, 30)));
        Assert.False(RouteHoldGates.V1.Admits(Polar(0.9, 0.02, 30), Polar(0.6, 0.035, 30)));
        // …but on a dull route (black, white, grey) the hue is noise: only the tolerance decides.
        Assert.True(gates.Admits(Polar(0.6, 0.02, 200), Polar(0.6, 0.02, 30)));
    }

    [Fact]
    public void A_candidate_within_the_tolerance_but_of_another_hue_is_not_selected_with_the_gates()
    {
        var reference = Polar(0.6, 0.05, 30);
        var turned = Polar(0.6, 0.05, 75);                                     // 45° away, yet ≈ 0.038 from the reference
        Assert.True(RouteHoldColor.Distance(turned, reference) < RouteHoldColor.DefaultTolerance);
        var control = Make(10, 10, reference, reference, control: true);
        var other = Make(40, 10, turned, reference);

        Assert.Equal(2, RouteHoldSelector.Select(new[] { control, other }, RouteHoldColor.DefaultTolerance).Count);
        Assert.Equal(new[] { control.Id },
            RouteHoldSelector.Select(new[] { control, other }, RouteHoldColor.DefaultTolerance, null, RouteHoldGates.Strict, reference).Select(c => c.Id));
        Assert.Single(new RouteHoldSearch(reference, new[] { control, other }, 1, RouteHoldGates.Strict).Select(RouteHoldColor.DefaultTolerance));
    }

    [Fact]
    public void The_route_is_the_group_around_the_control_hold_and_the_same_colour_far_away_is_left_out()
    {
        var red = Lab(Red);
        var control = Make(100, 100, red, red, control: true);
        var chain = new[] { Make(100, 130, red, red), Make(110, 160, red, red), Make(130, 185, red, red), Make(140, 215, red, red) };
        var far = Make(420, 20, red, red);                                     // the other end of the frame
        var all = chain.Prepend(control).Append(far).ToList();

        var kept = RouteHoldSelector.Select(all, RouteHoldColor.DefaultTolerance, null, RouteHoldGates.Strict, red);
        Assert.Equal(chain.Length + 1, kept.Count);
        Assert.DoesNotContain(kept, c => c.Id == far.Id);
        Assert.True(kept[0].IsControl);
        Assert.Contains(far.Id, RouteHoldSelector.Select(all, RouteHoldColor.DefaultTolerance).Select(c => c.Id));   // no gates: kept

        // The link: 2.5 × the median nearest gap, inside 0.04 … 0.10 of the long side.
        var link = RouteHoldGrouping.Link(all);
        Assert.InRange(link, RouteHoldGrouping.MinLink, RouteHoldGrouping.MaxLink);
        Assert.True(RouteHoldGrouping.Gap(far, chain.Last()) > link);
    }

    [Fact]
    public void Removing_a_hold_in_the_middle_of_the_route_never_cuts_the_holds_beyond_it()
    {
        var red = Lab(Red);
        var control = Make(100, 20, red, red, control: true);
        var chain = Enumerable.Range(1, 5).Select(i => Make(100, 20 + i * 40, red, red)).ToList();   // one long line
        var all = chain.Prepend(control).ToList();
        Assert.Equal(6, RouteHoldSelector.Select(all, RouteHoldColor.DefaultTolerance, null, RouteHoldGates.Strict, red).Count);

        var rejected = new HashSet<Guid> { chain[1].Id };                     // the second link removed by the user
        var kept = RouteHoldSelector.Select(all, RouteHoldColor.DefaultTolerance, rejected, RouteHoldGates.Strict, red);
        Assert.Equal(5, kept.Count);
        Assert.DoesNotContain(kept, c => c.Id == chain[1].Id);
        Assert.Contains(kept, c => c.Id == chain[4].Id);                      // the far end still there
    }

    [Fact]
    public void At_most_fifteen_holds_are_proposed_the_nearest_to_the_group_first()
    {
        var red = Lab(Red);
        var control = Make(20, 20, red, red, control: true, size: 10);
        var row = Enumerable.Range(1, 24).Select(i => Make(20 + i * 16, 20, red, red, size: 10)).ToList();
        var kept = RouteHoldSelector.Select(row.Prepend(control).ToList(), RouteHoldColor.DefaultTolerance, null, RouteHoldGates.Strict, red);
        Assert.Equal(RouteHoldGates.Strict.MaxHolds, kept.Count);
        Assert.Equal(row.Take(14).Select(c => c.Id).Append(control.Id).OrderBy(i => i), kept.Select(c => c.Id).OrderBy(i => i));
    }

    [Fact]
    public async Task The_production_search_has_no_grid_and_finds_the_route_without_the_far_hold()
    {
        Assert.True(new RouteHoldSearchOptions().UseGrid);                    // D048 §2's defaults kept for the research suite
        Assert.Null(new RouteHoldSearchOptions().Gates);
        var options = RouteHoldSearchOptions.Production;
        Assert.False(options.UseGrid);
        Assert.Equal(0.06, options.SplitDistance);
        Assert.Same(RouteHoldGates.Strict, options.Gates);

        var model = new FloodModel(RouteScene(panel: false));
        var search = (await RouteHoldFinder.FindAsync(model, model.Frame, At(Route[0]),
            Array.Empty<ImmutableArray<NormalizedPoint>>(), options)).Search!;
        var grid = HoldProposalService.GridPoints(W, H, new ProposalPipelineOptions().GridDivisions);
        Assert.DoesNotContain(model.Points, p => grid.Contains(p));
        Assert.Same(RouteHoldGates.Strict, search.Gates);
        var selected = search.Select(RouteHoldColor.DefaultTolerance);
        Assert.Equal(Route.Length, selected.Count);
        foreach (var hold in Route) Assert.Single(selected, c => Covers(c, hold));
        Assert.DoesNotContain(selected, c => Covers(c, FarRed));
        Assert.Contains(search.Candidates, c => Covers(c, FarRed));            // found, of the colour, but another route
    }

    [Fact]
    public async Task On_a_panel_of_the_routes_colour_the_split_finds_the_holds_the_plain_planner_misses()
    {
        var frame = RouteScene(panel: true);
        var plain = (await RouteHoldFinder.FindAsync(new FloodModel(frame), frame, At(Route[0]),
            Array.Empty<ImmutableArray<NormalizedPoint>>(), new RouteHoldSearchOptions { UseGrid = false })).Search!;
        var split = (await RouteHoldFinder.FindAsync(new FloodModel(frame), frame, At(Route[0]),
            Array.Empty<ImmutableArray<NormalizedPoint>>(), RouteHoldSearchOptions.Production)).Search!;

        Assert.True(plain.Select(RouteHoldColor.DefaultTolerance).Count < Route.Length);
        Assert.Equal(Route.Length, split.Select(RouteHoldColor.DefaultTolerance).Count);
    }
}
