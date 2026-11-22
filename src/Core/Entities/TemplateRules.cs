using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Localization;

namespace AiVideoEditor.Core.Entities;

/// <summary>The editable fields of a <see cref="TemplateClip"/> (D045 §6). Every kind holds all of them; each kind shows and
/// draws only its own. The kind and the counter's start are not editable fields. <see cref="Look"/> and
/// <see cref="Corner"/> are the heart-rate overlay's (Phase 35.5); its heart takes <see cref="AccentHex"/>. A «Боулдер» shows
/// its anchored clip's heart rate (D055 §5) when <see cref="ShowHeartRate"/>, with <see cref="Look"/>, <see cref="Corner"/>
/// and its heart in <see cref="HeartHex"/>.</summary>
public readonly record struct TemplateProperties(
    string Date, string Route, string Grade, BoulderStatus Status, string AccentHex, string Title, string Subtitle, MediaTime StatusAt,
    HeartRateLook Look = HeartRateLook.Plate, OverlayCorner Corner = OverlayCorner.BottomRight, bool ShowHeartRate = true,
    string HeartHex = TemplateRules.HeartRateAccentHex)
{
    /// <summary>D063 §C: «Боулдер»'s route plate corner (bottom left by default, as before).</summary>
    public OverlayCorner PlateCorner { get; init; } = OverlayCorner.BottomLeft;

    /// <summary>D073: «Боулдер»'s stated attempts (null: counted from the video).</summary>
    public int? Attempts { get; init; }

    /// <summary>D073: the language of the drawn words («Боулдер», «Итоги»).</summary>
    public TemplateLanguage Language { get; init; } = TemplateLanguage.Russian;

    /// <summary>D073: «Итоги» on its own full-frame background.</summary>
    public bool OwnBackground { get; init; }

    /// <summary>The clip's current values, or null for clips that aren't templates.</summary>
    public static TemplateProperties? Of(Clip clip) => clip is TemplateClip t
        ? new TemplateProperties(t.Date, t.Route, t.Grade, t.Status, t.AccentHex, t.Title, t.Subtitle, t.StatusAt, t.Look, t.Corner,
            t.ShowHeartRate, t.HeartHex) { PlateCorner = t.PlateCorner, Attempts = t.Attempts, Language = t.Language, OwnBackground = t.OwnBackground }
        : null;

    /// <summary>The heart's colour as drawn: the overlay's accent, a «Боулдер»'s own heart colour (D055 §5).</summary>
    public string HeartColorFor(TemplateKind kind) => kind == TemplateKind.HeartRate ? AccentHex : HeartHex;
}

/// <summary>
/// The rules of the templates (D045): defaults, the appearance / disappearance durations, the limits of the fields, the
/// fixed words drawn into the picture (Russian — part of the template's look, not interface text), and validation.
/// </summary>
public static class TemplateRules
{
    /// <summary>«Боулдер»'s accent of a new template (yellow, the hold markup palette's).</summary>
    public const string DefaultAccentHex = "#FFEA00";

    /// <summary>The heart-rate overlay's heart of a new overlay (red, a swatch of the route colours).</summary>
    public const string HeartRateAccentHex = "#FF1744";

    /// <summary>The word under the heart-rate overlay's digits.</summary>
    public const string BpmWord = "BPM";

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

    /// <summary>D073: the Flash's word — English in every language (the product owner, 2026-11-22), in lower case «Flash»
    /// (amendment 2, his pick B1).</summary>
    public const string FlashWord = "Flash";

    /// <summary>D073 amendment 2 (B1): the Flash block — a slanted orange block after a yellow slash, navy italic «Flash»
    /// (never TOP's straight yellow block).</summary>
    public const string FlashHex = "#FF6D00";

    /// <summary>D073: the stated attempts' bounds.</summary>
    public const int MinAttempts = 1;
    public const int MaxAttempts = 99;

    /// <summary>D073: a new template's language — the interface's: Russian for a Russian interface, English otherwise (the neutral resources are English).</summary>
    public static TemplateLanguage InterfaceLanguage() =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? TemplateLanguage.Russian : TemplateLanguage.English;

    /// <summary>The length of a new template.</summary>
    public static MediaTime DefaultDuration(TemplateKind kind) => MediaTime.FromSeconds(kind switch
    {
        TemplateKind.Rec or TemplateKind.Vhs or TemplateKind.HeartRate or TemplateKind.Summary => 10,
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
        TemplateKind.HeartRate => (0.3, 0.3),
        TemplateKind.Summary => (0.4, 0.4),
        _ => (0.6, 0.3)
    };

    /// <summary>True for the kinds that treat the picture below (D045 §3): VHS.</summary>
    public static bool HasTreatment(TemplateKind kind) => kind == TemplateKind.Vhs;

    /// <summary>The fields of a new template of <paramref name="kind"/>: «Хайлайт» gets its default title, the heart-rate
    /// overlay its red heart.</summary>
    public static TemplateProperties Defaults(TemplateKind kind)
    {
        var language = InterfaceLanguage();
        return new(string.Empty, string.Empty, string.Empty, BoulderStatus.None,
            kind == TemplateKind.HeartRate ? HeartRateAccentHex : DefaultAccentHex,
            kind switch
            {
                TemplateKind.Highlight => Messages.Template_DefaultTitle,
                TemplateKind.Summary => TemplateWords.Of(language).SummaryTitle,
                _ => string.Empty
            }, string.Empty, MediaTime.Zero) { Language = language };
    }

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
        if (!Enum.IsDefined(p.Language)) return Messages.Template_UnknownLanguage;
        if (p.Attempts is { } attempts && (attempts < MinAttempts || attempts > MaxAttempts))
            return string.Format(Messages.Template_AttemptsRange, MinAttempts, MaxAttempts);
        if (!Enum.IsDefined(p.Look) || !Enum.IsDefined(p.Corner)) return Messages.Template_UnknownLook;
        if (p.StatusAt < MediaTime.Zero) return Messages.Template_StatusAtNegative;
        if (!ClipPropertyValidator.IsHexColor(p.AccentHex)) return Messages.Prop_ColorFormat;
        if (p.HeartHex is null || !ClipPropertyValidator.IsHexColor(p.HeartHex)) return Messages.Prop_ColorFormat;
        return null;

        static string? Too(string value, int max) => value.Length > max ? string.Format(Messages.Template_FieldLength, max) : null;
    }
}

/// <summary>
/// D073: the words «Боулдер» and «Итоги» draw, in the template's language (part of the look, not interface text). «FLASH» is
/// English in both languages (the product owner, 2026-11-22).
/// </summary>
public sealed record TemplateWords(
    string Route, string Top, string Attempt, string SummaryTitle,
    string Number, string RouteColumn, string Result, string AttemptsColumn, string Time, string Grade, string AverageHr, string MaxHr,
    string Tops, string Flashes, string OnRoutes, string Bpm)
{
    public string Flash => TemplateRules.FlashWord;

    public static readonly TemplateWords Russian = new(
        TemplateRules.RouteWord, TemplateRules.TopWord, TemplateRules.AttemptWord, "Итоги дня",
        "#", "ТРАССА", "РЕЗУЛЬТАТ", "ПОПЫТКИ", "ВРЕМЯ", "СЛОЖН.", "ПУЛЬС СР", "ПУЛЬС МАКС",
        "ТОПЫ", "ФЛЕШИ", "НА ТРАССАХ", "уд/мин");

    public static readonly TemplateWords English = new(
        "ROUTE", "TOP", "ATTEMPT", "Day summary",
        "#", "ROUTE", "RESULT", "ATTEMPTS", "TIME", "GRADE", "AVG HR", "MAX HR",
        "TOPS", "FLASHES", "ON ROUTES", "bpm");

    public static TemplateWords Of(TemplateLanguage language) => language == TemplateLanguage.English ? English : Russian;

    /// <summary>The status's word (empty for none).</summary>
    public string StatusWord(BoulderStatus status) => status switch
    {
        BoulderStatus.Top => Top,
        BoulderStatus.Attempt => Attempt,
        BoulderStatus.Flash => Flash,
        _ => string.Empty
    };
}
