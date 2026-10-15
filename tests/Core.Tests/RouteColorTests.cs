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
        Assert.All(Saturated, p => Assert.True(RouteColor.RouteScore(p.Lab, holds) <= min));   // the maximin among them
    }

    private static IEnumerable<PaletteColor> Saturated => RouteColor.Palette.Take(RouteColor.SaturatedCount);

    [Fact]
    public void Auto_prefers_a_saturated_colour_to_black_or_white_when_it_reads_without_a_keyline()
    {
        // A red and a yellow hold on a beige wall: black scores highest of all 14, yet a saturated colour reaches the
        // threshold, so it is chosen — the best saturated one — and no keyline is needed.
        var holds = new[] { H("#E53935", "#E8DCC8"), H("#FDD835", "#E8DCC8") };
        var black = RouteColor.RouteScore(RouteColor.HexToOkLab("#000000"), holds);
        Assert.True(RouteColor.Palette.All(p => RouteColor.RouteScore(p.Lab, holds) <= black));
        var choice = RouteColor.Choose(holds);
        Assert.Contains(choice, Saturated);
        var score = RouteColor.RouteScore(choice.Lab, holds);
        Assert.True(score >= RouteColor.KeylineThreshold);
        Assert.Equal(Saturated.Max(p => RouteColor.RouteScore(p.Lab, holds)), score);
        Assert.False(RouteColor.WithResolved(new HoldMarkup { Holds = ImmutableArray.Create(holds) }).KeylineActive);
    }

    [Fact]
    public void Black_or_white_is_the_reserve_when_no_saturated_colour_reads()
    {
        // One hold in each saturated colour on a light wall: every saturated colour scores 0 on its own hold; black reads
        // on all of them and is chosen, without a keyline.
        var holds = Saturated.Select(p => H(p.Hex, "#F0F0F0")).ToArray();
        Assert.True(Saturated.All(p => RouteColor.RouteScore(p.Lab, holds) < RouteColor.KeylineThreshold));
        var markup = RouteColor.WithResolved(new HoldMarkup { Holds = ImmutableArray.Create(holds) });
        Assert.Equal("#000000", markup.ColorHex);
        Assert.False(markup.KeylineActive);
    }

    [Fact]
    public void When_nothing_reads_the_best_contrast_of_all_is_taken_and_the_keyline_turns_on()
    {
        // Holds in every palette colour, black and white included, on a mid-grey wall: no colour reaches the threshold;
        // the maximin over all 14 decides and the Auto keyline is on.
        var holds = RouteColor.Palette.Select(p => H(p.Hex, "#808080")).ToArray();
        var markup = RouteColor.WithResolved(new HoldMarkup { Holds = ImmutableArray.Create(holds) });
        var best = RouteColor.Palette.Max(p => RouteColor.RouteScore(p.Lab, holds));
        Assert.True(best < RouteColor.KeylineThreshold);
        Assert.Equal(best, RouteColor.RouteScore(RouteColor.HexToOkLab(markup.ColorHex), holds));
        Assert.True(markup.KeylineActive);
    }

    [Fact]
    public void The_saturated_preference_never_takes_the_contrast_below_the_threshold_when_a_readable_colour_exists()
    {
        // Pseudo-random routes of 1–8 holds (fixed seed): whenever some palette colour reads (score ≥ T), the Auto colour
        // reads too; otherwise it is the best of all 14. Saturated whenever a saturated colour reads.
        var random = new Random(19);
        OkLabColor Any() => RouteColor.SrgbToOkLab((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        for (var i = 0; i < 500; i++)
        {
            var holds = Enumerable.Range(0, 1 + random.Next(8)).Select(_ => H(Any(), Any())).ToArray();
            var choice = RouteColor.Choose(holds);
            var score = RouteColor.RouteScore(choice.Lab, holds);
            var best = RouteColor.Palette.Max(p => RouteColor.RouteScore(p.Lab, holds));
            if (best >= RouteColor.KeylineThreshold) Assert.True(score >= RouteColor.KeylineThreshold);
            else Assert.Equal(best, score);
            if (Saturated.Any(p => RouteColor.RouteScore(p.Lab, holds) >= RouteColor.KeylineThreshold))
                Assert.Contains(choice, Saturated);
        }
    }

    [Fact]
    public void Ties_go_to_the_larger_mean_then_to_the_palette_order()
    {
        // A hold whose surface and wall are both pure white: every colour's score is limited by the same quantity per
        // colour; the choice is the first of the best-scoring colours in palette order (deterministic).
        var holds = new[] { H("#808080", "#808080") };
        var first = RouteColor.Choose(holds);
        var best = Saturated.Max(p => RouteColor.RouteScore(p.Lab, holds));
        Assert.True(best >= RouteColor.KeylineThreshold);                // a saturated colour reads on mid grey
        Assert.Equal(Saturated.First(p => RouteColor.RouteScore(p.Lab, holds) == best).Hex, first.Hex);
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

    [Fact]
    public void The_calibrated_threshold_turns_the_keyline_on_just_below_0_16()
    {
        // Step 19.6, the product owner's T = 0.16. One hold whose surface lies at a chosen OKLab distance from the manual
        // colour (the wall far away): 0.159 → keyline, 0.161 → none; On / Off ignore the score.
        Assert.Equal(0.16, RouteColor.KeylineThreshold);
        var core = RouteColor.HexToOkLab("#FF4081");
        HoldMarkup At(double distance, KeylineMode mode) => RouteColor.WithResolved(new HoldMarkup
        {
            ColorMode = HoldColorMode.Manual, ColorHex = "#FF4081", Keyline = mode,
            Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(), Tri, new OkLabColor(core.L - distance, core.A, core.B),
                new OkLabColor(0.2, 0, 0), MediaTime.Zero))
        });
        Assert.True(At(0.159, KeylineMode.Auto).KeylineActive);
        Assert.False(At(0.161, KeylineMode.Auto).KeylineActive);
        Assert.True(At(0.5, KeylineMode.On).KeylineActive);
        Assert.False(At(0.01, KeylineMode.Off).KeylineActive);
    }
}
