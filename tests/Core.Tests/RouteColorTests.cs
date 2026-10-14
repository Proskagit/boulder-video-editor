using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 19 Step 19.2 (D034 §5): the route colour and keyline from the samples stored with the holds — OKLab, the score
/// <c>min(ΔE(c, surface), 2·ΔE(c, wall))</c>, the maximin over the holds (not the mean), the ties, the keyline modes.
/// </summary>
public class RouteColorTests
{
    private static readonly ImmutableArray<NormalizedPoint> Tri = ImmutableArray.Create(
        new NormalizedPoint(0.1, 0.1), new NormalizedPoint(0.2, 0.1), new NormalizedPoint(0.15, 0.2));

    private static Hold H(OkLabColor surface, OkLabColor wall) => new(Guid.NewGuid(), Tri, surface, wall, MediaTime.Zero);
    private static Hold H(string surfaceHex, string wallHex) => H(RouteColor.HexToOkLab(surfaceHex), RouteColor.HexToOkLab(wallHex));

    [Fact]
    public void Srgb_to_oklab_matches_the_reference_values()
    {
        var white = RouteColor.HexToOkLab("#FFFFFF");
        Assert.Equal(1.0, white.L, 4);
        Assert.Equal(0.0, white.A, 4);
        Assert.Equal(0.0, white.B, 4);
        Assert.Equal(new OkLabColor(0, 0, 0), RouteColor.HexToOkLab("#000000"));

        var red = RouteColor.HexToOkLab("#FF0000");   // Ottosson's published value for sRGB red
        Assert.Equal(0.62796, red.L, 4);
        Assert.Equal(0.22486, red.A, 4);
        Assert.Equal(0.12585, red.B, 4);
    }

    [Fact]
    public void The_palette_is_twelve_saturated_colours_then_white_and_black()
    {
        Assert.Equal(14, RouteColor.Palette.Length);
        Assert.Equal("#FFFFFF", RouteColor.Palette[12].Hex);
        Assert.Equal("#000000", RouteColor.Palette[13].Hex);
        Assert.Equal(14, RouteColor.Palette.Select(p => p.Hex).Distinct().Count());
    }

    [Fact]
    public void The_score_counts_the_wall_with_weight_one_half()
    {
        var c = new OkLabColor(0.5, 0, 0);
        var hold = H(new OkLabColor(0.9, 0, 0), new OkLabColor(0.6, 0, 0));   // ΔE surface 0.4, wall 0.1
        Assert.Equal(0.2, RouteColor.Score(c, hold), 12);                    // min(0.4, 2·0.1)
        var far = H(new OkLabColor(0.6, 0, 0), new OkLabColor(0.0, 0, 0));    // surface 0.1, wall 0.5
        Assert.Equal(0.1, RouteColor.Score(c, far), 12);
    }

    [Fact]
    public void Auto_takes_the_maximin_not_the_best_mean()
    {
        // Two holds on a white wall: one yellow, one exactly the palette's blue. Blue scores 0 on its own hold however
        // well it does on the yellow one; the maximin must avoid it even if its mean were the best.
        var holds = new[] { H("#FFEA00", "#F0F0F0"), H("#2979FF", "#F0F0F0") };
        var choice = RouteColor.Choose(holds);
        Assert.NotEqual("#2979FF", choice.Hex);
        Assert.NotEqual("#FFEA00", choice.Hex);
        var min = RouteColor.RouteScore(choice.Lab, holds);
        Assert.All(RouteColor.Palette, p => Assert.True(RouteColor.RouteScore(p.Lab, holds) <= min));
    }

    [Fact]
    public void Ties_go_to_the_larger_mean_then_to_the_palette_order()
    {
        // A hold whose surface and wall are both pure white: every colour's score is limited by the same quantity per
        // colour; the choice is the first of the best-scoring colours in palette order (deterministic).
        var holds = new[] { H("#808080", "#808080") };
        var first = RouteColor.Choose(holds);
        var best = RouteColor.Palette.Max(p => RouteColor.RouteScore(p.Lab, holds));
        Assert.Equal(RouteColor.Palette.First(p => RouteColor.RouteScore(p.Lab, holds) == best).Hex, first.Hex);
        Assert.Equal(first, RouteColor.Choose(holds));   // deterministic
    }

    [Fact]
    public void Without_holds_auto_keeps_the_colour_and_the_auto_keyline_is_off()
    {
        var markup = new HoldMarkup { ColorHex = "#00E676" };
        Assert.Equal(("#00E676", false), RouteColor.Resolve(markup));
        Assert.Equal(("#00E676", true), RouteColor.Resolve(markup with { Keyline = KeylineMode.On }));
    }

    [Fact]
    public void Keyline_auto_on_and_off_for_the_automatic_and_the_manual_colour()
    {
        var hold = H("#FF00FF", "#FF00FF");
        var manual = new HoldMarkup { ColorMode = HoldColorMode.Manual, ColorHex = "#FF00FF", Holds = ImmutableArray.Create(hold) };
        Assert.True(RouteColor.Resolve(manual).KeylineActive);                                        // score 0 < T
        Assert.False(RouteColor.Resolve(manual with { Keyline = KeylineMode.Off }).KeylineActive);
        Assert.True(RouteColor.Resolve(manual with { Keyline = KeylineMode.On, ColorHex = "#000000" }).KeylineActive);
        Assert.False(RouteColor.Resolve(manual with { ColorHex = "#000000" }).KeylineActive);        // high contrast → off

        var auto = manual with { ColorMode = HoldColorMode.Auto };
        var (color, keyline) = RouteColor.Resolve(auto);
        Assert.Equal(RouteColor.RouteScore(RouteColor.HexToOkLab(color), auto.Holds) < RouteColor.KeylineThreshold, keyline);
    }

    [Fact]
    public void The_keyline_is_black_on_a_light_core_and_white_on_a_dark_one()
    {
        Assert.Equal("#000000", RouteColor.KeylineHex("#FFEA00"));
        Assert.Equal("#000000", RouteColor.KeylineHex("#FFFFFF"));
        Assert.Equal("#FFFFFF", RouteColor.KeylineHex("#3D5AFE"));
        Assert.Equal("#FFFFFF", RouteColor.KeylineHex("#000000"));
    }

    [Fact]
    public void Validation_limits()
    {
        var ok = RouteColor.WithResolved(new HoldMarkup { Holds = ImmutableArray.Create(H("#E53935", "#EEEEEE")) });
        Assert.Null(HoldMarkupRules.Validate(ok));
        Assert.NotNull(HoldMarkupRules.Validate(ok with { StrokeWidth1080 = double.NaN }));
        Assert.NotNull(HoldMarkupRules.Validate(ok with { StrokeWidth1080 = 33 }));
        Assert.NotNull(HoldMarkupRules.Validate(ok with { ColorHex = "#12345" }));
        Assert.NotNull(HoldMarkupRules.Validate(ok with { Holds = ok.Holds.Add(ok.Holds[0]) }));   // duplicate id
        Assert.NotNull(HoldMarkupRules.ValidateHold(new Hold(Guid.NewGuid(), Tri, new OkLabColor(double.NaN, 0, 0), default, MediaTime.Zero)));
        Assert.NotNull(HoldMarkupRules.ValidateHold(new Hold(Guid.NewGuid(), Tri, default, default, new MediaTime(-1))));
    }
}
