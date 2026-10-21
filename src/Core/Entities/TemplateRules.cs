using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Localization;

namespace AiVideoEditor.Core.Entities;

/// <summary>The editable fields of a <see cref="TemplateClip"/> (D045 §6). Every kind holds all of them; each kind shows and
/// draws only its own. The kind and the counter's start are not editable fields.</summary>
public readonly record struct TemplateProperties(
    string Date, string Route, string Grade, BoulderStatus Status, string AccentHex, string Title, string Subtitle, MediaTime StatusAt)
{
    /// <summary>The clip's current values, or null for clips that aren't templates.</summary>
    public static TemplateProperties? Of(Clip clip) => clip is TemplateClip t
        ? new TemplateProperties(t.Date, t.Route, t.Grade, t.Status, t.AccentHex, t.Title, t.Subtitle, t.StatusAt)
        : null;
}

/// <summary>
/// The rules of the templates (D045): defaults, the appearance / disappearance durations, the limits of the fields, the
/// fixed words drawn into the picture (Russian — part of the template's look, not interface text), and validation.
/// </summary>
public static class TemplateRules
{
    /// <summary>«Боулдер»'s accent of a new template (yellow, the hold markup palette's).</summary>
    public const string DefaultAccentHex = "#FFEA00";

    /// <summary>«Хайлайт»'s fixed accent (the product owner, 2026-10-21: yellow).</summary>
    public const string HighlightAccentHex = "#FFC400";

    /// <summary>«Боулдер»'s blocks (the product owner, 2026-10-21): the route plate navy with white text; TOP its own yellow
    /// block with navy text; ПОПЫТКА its own muted blue block with white text.</summary>
    public const string PlateHex = "#0D1B2E";
    public const string TopHex = "#FFD400";
    public const string AttemptHex = "#46648C";

    /// <summary>The status block's appearance (seconds) from <see cref="TemplateClip.StatusAt"/>: a short fade with a small
    /// slide, no overshoot.</summary>
    public const double StatusAppearSeconds = 0.25;

    public const int MaxDateLength = 24;
    public const int MaxRouteLength = 12;
    public const int MaxGradeLength = 12;
    public const int MaxTitleLength = 60;
    public const int MaxSubtitleLength = 80;

    /// <summary>The words drawn on the «Боулдер» plate and its chips.</summary>
    public const string RouteWord = "ТРАССА";
    public const string TopWord = "TOP";
    public const string AttemptWord = "ПОПЫТКА";

    /// <summary>The length of a new template.</summary>
    public static MediaTime DefaultDuration(TemplateKind kind) => MediaTime.FromSeconds(kind switch
    {
        TemplateKind.Rec or TemplateKind.Vhs => 10,
        TemplateKind.Boulder => 8,
        _ => 3
    });

    /// <summary>The appearance and the disappearance (seconds): the clip's fades of a new template; 0 for VHS (it starts
    /// and ends with its cut).</summary>
    public static (double In, double Out) Animation(TemplateKind kind) => kind switch
    {
        TemplateKind.Rec => (0.2, 0.2),
        TemplateKind.Vhs => (0, 0),
        TemplateKind.Boulder => (0.35, 0.3),
        _ => (0.6, 0.3)
    };

    /// <summary>True for the kinds that treat the picture below (D045 §3): VHS.</summary>
    public static bool HasTreatment(TemplateKind kind) => kind == TemplateKind.Vhs;

    /// <summary>The fields of a new template of <paramref name="kind"/>: «Хайлайт» gets its default title.</summary>
    public static TemplateProperties Defaults(TemplateKind kind) => new(string.Empty, string.Empty, string.Empty, BoulderStatus.None,
        DefaultAccentHex, kind == TemplateKind.Highlight ? Messages.Template_DefaultTitle : string.Empty, string.Empty, MediaTime.Zero);

    /// <summary>A short user-facing reason the fields can't be stored, or null.</summary>
    public static string? Validate(TemplateProperties p)
    {
        if (p.Date is null || p.Route is null || p.Grade is null || p.Title is null || p.Subtitle is null || p.AccentHex is null)
            return Messages.Template_FieldMissing;
        var length = Too(p.Date, MaxDateLength) ?? Too(p.Route, MaxRouteLength) ?? Too(p.Grade, MaxGradeLength) ??
                     Too(p.Title, MaxTitleLength) ?? Too(p.Subtitle, MaxSubtitleLength);
        if (length is not null) return length;
        if (new[] { p.Date, p.Route, p.Grade, p.Title, p.Subtitle }.Any(s => s.Any(char.IsControl)))
            return Messages.Template_OneLine;
        if (!Enum.IsDefined(p.Status)) return Messages.Template_UnknownStatus;
        if (p.StatusAt < MediaTime.Zero) return Messages.Template_StatusAtNegative;
        if (!ClipPropertyValidator.IsHexColor(p.AccentHex)) return Messages.Prop_ColorFormat;
        return null;

        static string? Too(string value, int max) => value.Length > max ? string.Format(Messages.Template_FieldLength, max) : null;
    }
}
