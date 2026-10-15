using System.Collections.Immutable;
using System.Globalization;

namespace AiVideoEditor.Core.Entities;

/// <summary>A palette entry: its <c>#RRGGBB</c> and its OKLab value.</summary>
public readonly record struct PaletteColor(string Name, string Hex, OkLabColor Lab);

/// <summary>
/// The route outline's colour and keyline (D034 §5) — pure, from the colour samples stored with the holds, so it never
/// needs a decoded frame.
/// <list type="bullet">
/// <item>Score of a colour c for hold i: <c>min(ΔE_OK(c, SurfaceLab_i), 2 · ΔE_OK(c, WallLab_i))</c> — the wall counts with
/// weight 0.5, as only the outer half of the line lies on it.</item>
/// <item>Route score: the minimum over the holds (the worst case first).</item>
/// <item>Auto: the palette colour with the largest route score; ties → the larger mean score, then the palette order.</item>
/// <item>Keyline Auto: on when the route score of the colour in use is below <see cref="KeylineThreshold"/>; On always; Off
/// never — for the automatic and the manual colour alike.</item>
/// </list>
/// </summary>
public static class RouteColor
{
    /// <summary>
    /// The keyline threshold T on the route score (ΔE_OK; D034 §5, starting hypothesis 0.20). Calibrated at Step 19.6 on
    /// the E1 frames (24 routes of 3–11 holds from the app's own samples; example sheets with and without the keyline per
    /// score band, 1080p, stroke 4): below ≈ 0.12 the plain line merges with the holds (pink on pink, orange on orange);
    /// between 0.12 and 0.20 both versions read and the keyline trades a crisper edge for half the coloured core — a
    /// choice for the product owner (report `T_REPORT.md` with the calibration). The Auto colour of every E1 route scores
    /// ≥ 0.33 even without black and white, so in practice T decides for manual colours (routes outlined in their own
    /// hold colour score 0.03–0.30). Not accepted yet: manual plan item 16.
    /// </summary>
    public const double KeylineThreshold = 0.12;

    /// <summary>The fixed Auto palette (D034 §5): 12 saturated colours, then white and black, in this order.</summary>
    public static readonly ImmutableArray<PaletteColor> Palette = ImmutableArray.Create(
        Entry("red", "#FF1744"), Entry("orange", "#FF6D00"), Entry("yellow", "#FFEA00"), Entry("lime", "#76FF03"),
        Entry("green", "#00E676"), Entry("teal", "#1DE9B6"), Entry("cyan", "#00E5FF"), Entry("blue", "#2979FF"),
        Entry("indigo", "#3D5AFE"), Entry("violet", "#D500F9"), Entry("magenta", "#FF00FF"), Entry("pink", "#FF4081"),
        Entry("white", "#FFFFFF"), Entry("black", "#000000"));

    /// <summary>A light core (OKLab L above this) gets a black keyline, otherwise a white one.</summary>
    public const double LightCoreL = 0.6;

    /// <summary>The colour in use and whether the keyline is drawn for <paramref name="markup"/>'s holds and settings.
    /// Without holds Auto keeps the current colour and the Auto keyline is off.</summary>
    public static (string ColorHex, bool KeylineActive) Resolve(HoldMarkup markup)
    {
        var holds = markup.Holds;
        var color = markup.ColorMode == HoldColorMode.Auto && holds.Length > 0
            ? Choose(holds).Hex
            : markup.ColorHex.ToUpperInvariant();
        var keyline = markup.Keyline switch
        {
            KeylineMode.On => true,
            KeylineMode.Off => false,
            _ => holds.Length > 0 && RouteScore(HexToOkLab(color), holds) < KeylineThreshold
        };
        return (color, keyline);
    }

    /// <summary><paramref name="markup"/> with <see cref="HoldMarkup.KeylineActive"/> resolved for its stored
    /// <see cref="HoldMarkup.ColorHex"/> (the colour itself kept).</summary>
    public static HoldMarkup WithKeyline(HoldMarkup markup) =>
        markup with { KeylineActive = Resolve(markup with { ColorMode = HoldColorMode.Manual }).KeylineActive };

    /// <summary><paramref name="markup"/> with <see cref="HoldMarkup.ColorHex"/> and <see cref="HoldMarkup.KeylineActive"/>
    /// resolved.</summary>
    public static HoldMarkup WithResolved(HoldMarkup markup)
    {
        var (color, keyline) = Resolve(markup);
        return markup with { ColorHex = color, KeylineActive = keyline };
    }

    /// <summary>The Auto choice for <paramref name="holds"/> (at least one).</summary>
    public static PaletteColor Choose(IReadOnlyList<Hold> holds)
    {
        if (holds.Count == 0) throw new ArgumentException("At least one hold is needed.", nameof(holds));
        var best = Palette[0];
        double bestMin = double.NegativeInfinity, bestMean = double.NegativeInfinity;
        foreach (var candidate in Palette)
        {
            double min = double.PositiveInfinity, sum = 0;
            foreach (var hold in holds)
            {
                var s = Score(candidate.Lab, hold);
                min = Math.Min(min, s);
                sum += s;
            }
            var mean = sum / holds.Count;
            if (min > bestMin || (min == bestMin && mean > bestMean))
            {
                (best, bestMin, bestMean) = (candidate, min, mean);
            }
        }
        return best;
    }

    /// <summary>The score of <paramref name="color"/> for one hold.</summary>
    public static double Score(OkLabColor color, Hold hold) =>
        Math.Min(color.DistanceTo(hold.SurfaceLab), 2 * color.DistanceTo(hold.WallLab));

    /// <summary>The route score: the minimum over <paramref name="holds"/>; +∞ without holds.</summary>
    public static double RouteScore(OkLabColor color, IReadOnlyList<Hold> holds)
    {
        var min = double.PositiveInfinity;
        foreach (var hold in holds) min = Math.Min(min, Score(color, hold));
        return min;
    }

    /// <summary>The keyline's colour for a core colour: black for a light core, white otherwise.</summary>
    public static string KeylineHex(string coreHex) => HexToOkLab(coreHex).L > LightCoreL ? "#000000" : "#FFFFFF";

    // ---- colour conversion: sRGB (8-bit, the SDR frames the editor shows) → OKLab (Björn Ottosson's matrices) ------

    public static OkLabColor HexToOkLab(string hex)
    {
        if (!HoldMarkupRules.IsColorHex(hex)) throw new ArgumentException("Expected #RRGGBB.", nameof(hex));
        var r = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return SrgbToOkLab(r, g, b);
    }

    public static OkLabColor SrgbToOkLab(byte r, byte g, byte b) => LinearToOkLab(Linear.Table[r], Linear.Table[g], Linear.Table[b]);

    /// <summary>The sRGB decoding of the 256 code values (its own class, so it is ready before <see cref="Palette"/>).</summary>
    private static class Linear
    {
        public static readonly double[] Table = Enumerable.Range(0, 256).Select(i => SrgbToLinear(i / 255.0)).ToArray();
    }

    public static double SrgbToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static OkLabColor LinearToOkLab(double r, double g, double b)
    {
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return new OkLabColor(
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static PaletteColor Entry(string name, string hex) => new(name, hex, HexToOkLab(hex));
}
