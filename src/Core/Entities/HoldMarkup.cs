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

/// <summary>The order in which the holds of an animated route appear (D036 §1).</summary>
public enum HoldAnimationOrder
{
    /// <summary>From the lowest hold to the highest, by the centre of each contour (routes usually go up).</summary>
    BottomToTop,

    /// <summary>In the order the holds were marked.</summary>
    MarkingOrder
}

/// <summary>
/// The appear / disappear animation of a route's outlines (D036): before the start every contour is hidden; from it the
/// holds appear one by one every <see cref="Interval"/>, each fading in over <see cref="AppearFade"/>, and stay; after the
/// last one all of them stay <see cref="StayAfterLast"/> and then fade out together over <see cref="DisappearFade"/> —
/// or, with <see cref="StayUntilClipEnd"/>, stay to the clip's end. <see cref="StartSourceTime"/> is a time of the source
/// (ticks from the file's start, like <see cref="Hold.SourceTime"/>), so trimming, slipping, a speed change or a split
/// never move the animation against the picture; the durations are timeline (output) time, so the clip's speed does
/// not stretch them. Immutable, part of the markup value.
/// </summary>
public sealed record HoldAnimation
{
    public static readonly MediaTime DefaultInterval = MediaTime.FromSeconds(0.5);
    public static readonly MediaTime DefaultAppearFade = MediaTime.FromSeconds(0.25);
    public static readonly MediaTime DefaultStayAfterLast = MediaTime.FromSeconds(3);
    public static readonly MediaTime DefaultDisappearFade = MediaTime.FromSeconds(0.5);

    public HoldAnimationOrder Order { get; init; } = HoldAnimationOrder.BottomToTop;
    public MediaTime StartSourceTime { get; init; }
    public MediaTime Interval { get; init; } = DefaultInterval;
    public MediaTime AppearFade { get; init; } = DefaultAppearFade;
    public MediaTime StayAfterLast { get; init; } = DefaultStayAfterLast;
    public bool StayUntilClipEnd { get; init; }
    public MediaTime DisappearFade { get; init; } = DefaultDisappearFade;
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

    /// <summary>The appear / disappear animation (D036); null: off — every contour shown for the whole clip.</summary>
    public HoldAnimation? Animation { get; init; }

    /// <summary>Same style, the same animation and the same holds (value comparison of every hold).</summary>
    public bool SameAs(HoldMarkup other) =>
        StrokeWidth1080.Equals(other.StrokeWidth1080) && ColorMode == other.ColorMode &&
        string.Equals(ColorHex, other.ColorHex, StringComparison.OrdinalIgnoreCase) && Keyline == other.Keyline &&
        KeylineActive == other.KeylineActive && ModelId == other.ModelId && Equals(Animation, other.Animation) &&
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

    /// <summary>The animation's limits (D036 §2).</summary>
    public static readonly MediaTime MinInterval = MediaTime.FromSeconds(0.05);
    public static readonly MediaTime MaxInterval = MediaTime.FromSeconds(10);
    public static readonly MediaTime MaxFade = MediaTime.FromSeconds(5);
    public static readonly MediaTime MaxStay = MediaTime.FromSeconds(60);

    /// <summary>The latest animation start: 24 hours into the source.</summary>
    public static readonly MediaTime MaxStartSourceTime = MediaTime.FromSeconds(24 * 3600);

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
        if (markup.Animation is { } animation && ValidateAnimation(animation) is { } animationError) return animationError;

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

    /// <summary>Null when <paramref name="animation"/> is within the limits of D036 §2, otherwise why not.</summary>
    public static string? ValidateAnimation(HoldAnimation animation)
    {
        if (!Enum.IsDefined(animation.Order)) return "Unknown animation order.";
        if (animation.StartSourceTime < MediaTime.Zero || animation.StartSourceTime > MaxStartSourceTime)
            return "The animation start must lie within the source.";
        if (animation.Interval < MinInterval || animation.Interval > MaxInterval)
            return $"The animation interval must be between {Seconds(MinInterval)} and {Seconds(MaxInterval)} s.";
        if (animation.AppearFade < MediaTime.Zero || animation.AppearFade > MaxFade)
            return $"The appear fade must be between 0 and {Seconds(MaxFade)} s.";
        if (animation.DisappearFade < MediaTime.Zero || animation.DisappearFade > MaxFade)
            return $"The disappear fade must be between 0 and {Seconds(MaxFade)} s.";
        if (animation.StayAfterLast < MediaTime.Zero || animation.StayAfterLast > MaxStay)
            return $"The time after the last hold must be between 0 and {Seconds(MaxStay)} s.";
        return null;
    }

    private static string Seconds(MediaTime time) => time.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);

    public static bool IsColorHex(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit);
}
