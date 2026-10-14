using System.Collections.Immutable;
using System.Globalization;
using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Entities;

/// <summary>A point in normalized coordinates of a clip's display-oriented source frame (D019): 0..1 across the
/// width (X, right) and the height (Y, down), independent of the resolution the frame is decoded at (D034 §6).</summary>
public readonly record struct NormalizedPoint(double X, double Y);

/// <summary>A colour in OKLab (L 0..1, a and b around 0) — the space D034 §5 measures route-colour contrast in.</summary>
public readonly record struct OkLabColor(double L, double A, double B)
{
    public bool IsFinite => double.IsFinite(L) && double.IsFinite(A) && double.IsFinite(B);

    /// <summary>Euclidean distance in OKLab (ΔE_OK).</summary>
    public double DistanceTo(OkLabColor other)
    {
        double dl = L - other.L, da = A - other.A, db = B - other.B;
        return Math.Sqrt(dl * dl + da * da + db * db);
    }
}

/// <summary>How the route colour is chosen (D034 §5).</summary>
public enum HoldColorMode
{
    /// <summary>From the palette, by the maximin of the contrast with every hold and the wall around it.</summary>
    Auto,

    /// <summary>The user's colour, kept as set.</summary>
    Manual
}

/// <summary>The route keyline (D034 §5): a black or white edge on both sides of the core, for the whole route.</summary>
public enum KeylineMode
{
    /// <summary>On when the route's minimum contrast score for the colour in use is below the threshold.</summary>
    Auto,
    On,
    Off
}

/// <summary>
/// One confirmed hold (D034 §6): its outer contour as a polygon in normalized source coordinates and the colour samples
/// taken when it was confirmed, from the frame it was marked on — <see cref="SurfaceLab"/> (the hold's surface) and
/// <see cref="WallLab"/> (the wall around it), at <see cref="SourceTime"/> in the source. Immutable: the samples never
/// change after the hold is confirmed, so the route colour is recomputed from them without decoding a frame again.
/// </summary>
public sealed class Hold
{
    public Hold(Guid id, ImmutableArray<NormalizedPoint> points, OkLabColor surfaceLab, OkLabColor wallLab, MediaTime sourceTime)
    {
        Id = id;
        Points = points.IsDefault ? ImmutableArray<NormalizedPoint>.Empty : points;
        SurfaceLab = surfaceLab;
        WallLab = wallLab;
        SourceTime = sourceTime;
    }

    public Guid Id { get; }
    public ImmutableArray<NormalizedPoint> Points { get; }
    public OkLabColor SurfaceLab { get; }
    public OkLabColor WallLab { get; }
    public MediaTime SourceTime { get; }

    /// <summary>Same id, points, samples and time.</summary>
    public bool SameAs(Hold other) =>
        Id == other.Id && SurfaceLab == other.SurfaceLab && WallLab == other.WallLab && SourceTime == other.SourceTime &&
        Points.SequenceEqual(other.Points);
}

/// <summary>
/// The hold markup of a video clip (D034): the confirmed holds and the route outline's style. Immutable — every edit
/// replaces the whole value (one undo step, absolute before / after). <see cref="ColorHex"/> and
/// <see cref="KeylineActive"/> are the resolved values (<see cref="RouteColor.Resolve"/>), stored so the export is
/// deterministic and nothing is recomputed on open.
/// </summary>
public sealed record HoldMarkup
{
    /// <summary>The model the contours came from (D034 §2).</summary>
    public const string CurrentModelId = "sam2.1-large-v1";

    public const double DefaultStrokeWidth1080 = 4;

    /// <summary>Stroke width per 1080 pixels of the canvas's short side (D034 §4); the keyline is inside it.</summary>
    public double StrokeWidth1080 { get; init; } = DefaultStrokeWidth1080;

    public HoldColorMode ColorMode { get; init; } = HoldColorMode.Auto;

    /// <summary>The route colour in use, <c>#RRGGBB</c>: the Auto result or the manual colour.</summary>
    public string ColorHex { get; init; } = RouteColor.Palette[0].Hex;

    public KeylineMode Keyline { get; init; } = KeylineMode.Auto;

    /// <summary>Whether the keyline is drawn (the resolved <see cref="Keyline"/>).</summary>
    public bool KeylineActive { get; init; }

    public string ModelId { get; init; } = CurrentModelId;

    public ImmutableArray<Hold> Holds { get; init; } = ImmutableArray<Hold>.Empty;

    /// <summary>Same style and the same holds (value comparison of every hold).</summary>
    public bool SameAs(HoldMarkup other) =>
        StrokeWidth1080.Equals(other.StrokeWidth1080) && ColorMode == other.ColorMode &&
        string.Equals(ColorHex, other.ColorHex, StringComparison.OrdinalIgnoreCase) && Keyline == other.Keyline &&
        KeylineActive == other.KeylineActive && ModelId == other.ModelId &&
        Holds.Length == other.Holds.Length && Holds.Zip(other.Holds).All(p => p.First.SameAs(p.Second));
}

/// <summary>Limits and validation of <see cref="HoldMarkup"/> — used by the edit service and the project loader, so no
/// invalid markup enters the model.</summary>
public static class HoldMarkupRules
{
    public const double MinStrokeWidth1080 = 1;
    public const double MaxStrokeWidth1080 = 32;
    public const int MinPoints = 3;
    public const int MaxPoints = 8192;
    public const int MaxHolds = 500;

    /// <summary>Null when <paramref name="markup"/> is valid, otherwise why not.</summary>
    public static string? Validate(HoldMarkup markup)
    {
        if (!double.IsFinite(markup.StrokeWidth1080) || markup.StrokeWidth1080 < MinStrokeWidth1080 || markup.StrokeWidth1080 > MaxStrokeWidth1080)
            return $"The outline width must be between {MinStrokeWidth1080.ToString(CultureInfo.InvariantCulture)} and {MaxStrokeWidth1080.ToString(CultureInfo.InvariantCulture)} px.";
        if (!Enum.IsDefined(markup.ColorMode)) return "Unknown colour mode.";
        if (!Enum.IsDefined(markup.Keyline)) return "Unknown keyline mode.";
        if (!IsColorHex(markup.ColorHex)) return "The outline colour must be #RRGGBB.";
        if (string.IsNullOrWhiteSpace(markup.ModelId)) return "The markup's model is missing.";
        if (markup.Holds.Length > MaxHolds) return $"A clip can have at most {MaxHolds} holds.";

        var ids = new HashSet<Guid>();
        foreach (var hold in markup.Holds)
        {
            if (hold.Id == Guid.Empty || !ids.Add(hold.Id)) return "A hold has a missing or duplicate id.";
            if (ValidateHold(hold) is { } error) return error;
        }
        return null;
    }

    public static string? ValidateHold(Hold hold)
    {
        if (hold.Points.Length < MinPoints || hold.Points.Length > MaxPoints) return "A hold's contour has too few or too many points.";
        foreach (var p in hold.Points)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1)
                return "A hold's contour lies outside the frame.";
        }
        if (!hold.SurfaceLab.IsFinite || !hold.WallLab.IsFinite) return "A hold's colour samples are invalid.";
        if (hold.SourceTime < MediaTime.Zero) return "A hold's frame time is negative.";
        return null;
    }

    public static bool IsColorHex(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit);
}
