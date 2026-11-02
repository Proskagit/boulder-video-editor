namespace AiVideoEditor.Core.Telemetry.Research;

/// <summary>
/// The reference marking rule of Step 35.3R1 / 35.3R2 (D050 amendment of 2026-11-02) — the one source of its id and text,
/// stored in the study as its <see cref="MarkingRule"/> and shown by the marking window; one rule for every video. The
/// text is the method's wording, independent of the interface language.
/// </summary>
public static class ReferenceRule
{
    public const string Id = "coros-ref-1";

    public const string Text =
        "Начало подъёма: момент, когда последняя нога отрывается от пола и спортсмен переходит из стартового положения на стену " +
        "(не первое движение рукой и не регистрация часами). Конец подъёма (необязательно): последний момент контакта со стеной " +
        "при спрыгивании (не приземление и не регистрация часами). Обе отметки выбираются вручную по видео; небольшая погрешность " +
        "допустима.";

    /// <summary>The rule as the study stores it.</summary>
    public static MarkingRule Rule => new(Id, Text);
}
