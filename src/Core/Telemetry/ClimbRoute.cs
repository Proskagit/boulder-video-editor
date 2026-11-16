using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>The grading scale a route's grade is given in (FIT <c>climb_grading_scale</c>, Garmin FIT SDK profile 21.221).</summary>
public enum ClimbGradingScale
{
    Yds = 0,
    Uiaa = 1,
    French = 2,
    BritishAdjectival = 3,
    BritishTechnical = 4,
    Ewbank = 5,
    Brazilian = 6,
    Saxon = 7,
    Vermin = 8,
    Font = 9,
    Dankyu = 10,
    YdsPlusMinus = 11,
}

/// <summary>The result of a climb as the watch recorded it (FIT <c>split_status</c>).</summary>
public enum ClimbResult
{
    Failure = 0,
    Success = 1,

    /// <summary>The route was tried and not completed (Garmin «Опробована»).</summary>
    Attempted = 2,

    /// <summary>The route was completed, with or without falls (Garmin «Завершено с падениями» / «Отправить»).</summary>
    Completed = 3,
}

/// <summary>
/// What the watch recorded about the route of one climb (FIT split fields 69 <c>climb_grading_scale</c>, 70
/// <c>climb_grade_value</c>, 71 <c>status</c>, 72 <c>num_falls</c>, 73 <c>climb_send</c>; D062). Each part is optional:
/// a watch writes what its owner entered, nothing is guessed.
/// </summary>
public sealed record ClimbRoute(ClimbGradingScale? Scale, int? GradeValue, ClimbResult? Result, int? Falls, bool? Send)
{
    /// <summary>The grade as climbers write it in its scale («6b», «6B+», «V4», «5.10a», «VI+»), or null when the scale or
    /// the value is missing or not one the app knows.</summary>
    public string? Grade => Scale is { } scale && GradeValue is { } value ? ClimbGrades.Text(scale, value) : null;

    /// <summary>The overlay's status (D062 §3): completed — with falls or without, or a send — is «Топ»; attempted or failed
    /// is «Попытка»; no result recorded is none.</summary>
    public BoulderStatus Status => Send == true || Result is ClimbResult.Completed or ClimbResult.Success
        ? BoulderStatus.Top
        : Result is ClimbResult.Attempted or ClimbResult.Failure ? BoulderStatus.Attempt : BoulderStatus.None;

    /// <summary>True when the split states nothing about its route.</summary>
    public bool IsEmpty => Scale is null && GradeValue is null && Result is null && Falls is null && Send is null;
}

/// <summary>The text of a FIT grade value in its scale (the enums of the Garmin FIT SDK profile 21.221).</summary>
public static class ClimbGrades
{
    private static readonly string[] French =
    [
        "1", "2", "3", "4a", "4b", "4c", "5a", "5b", "5c", "6a", "6a+", "6b", "6b+", "6c", "6c+", "7a", "7a+", "7b", "7b+", "7c",
        "7c+", "8a", "8a+", "8b", "8b+", "8c", "8c+", "9a", "9a+", "9b",
    ];

    private static readonly string[] Font =
    [
        "1", "2", "3", "4", "4+", "5", "5+", "6A", "6A+", "6B", "6B+", "6C", "6C+", "7A", "7A+", "7B", "7B+", "7C", "7C+", "8A",
        "8A+", "8B", "8B+", "8C", "8C+", "9A",
    ];

    private static readonly string[] Uiaa =
    [
        "I", "II", "III", "IV-", "IV", "IV+", "V-", "V", "V+", "VI-", "VI", "VI+", "VII-", "VII", "VII+", "VIII-", "VIII", "VIII+",
        "IX-", "IX", "IX+", "X-", "X", "X+", "XI-", "XI", "XI+", "XII-",
    ];

    private static readonly string[] Yds =
    [
        "5.1", "5.2", "5.3", "5.4", "5.5", "5.6", "5.7", "5.8", "5.9", "5.10", "5.10a", "5.10b", "5.10c", "5.10d", "5.11", "5.11a",
        "5.11b", "5.11c", "5.11d", "5.12", "5.12a", "5.12b", "5.12c", "5.12d", "5.13", "5.13a", "5.13b", "5.13c", "5.13d", "5.14",
        "5.14a", "5.14b", "5.14c", "5.14d", "5.15", "5.15a", "5.15b", "5.15c", "5.15d",
    ];

    private static readonly string[] Dankyu = ["7 kyu", "6 kyu", "5 kyu", "4 kyu", "3 kyu", "2 kyu", "1 kyu", "1 dan", "2 dan", "3 dan", "4 dan", "5 dan"];

    /// <summary>The grade text, or null for a value outside the scale or a scale the app does not spell (British,
    /// Brazilian, Saxon, YDS ±).</summary>
    public static string? Text(ClimbGradingScale scale, int value)
    {
        if (value < 0) return null;
        return scale switch
        {
            ClimbGradingScale.French => At(French, value),
            ClimbGradingScale.Font => At(Font, value),
            ClimbGradingScale.Vermin => value <= 18 ? (value == 0 ? "VB" : $"V{value - 1}") : null,
            ClimbGradingScale.Uiaa => At(Uiaa, value),
            ClimbGradingScale.Yds => At(Yds, value),
            ClimbGradingScale.Ewbank => value <= 34 ? (value + 4).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            ClimbGradingScale.Dankyu => At(Dankyu, value),
            _ => null,
        };
    }

    private static string? At(string[] table, int value) => value < table.Length ? table[value] : null;
}
