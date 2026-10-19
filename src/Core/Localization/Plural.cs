namespace AiVideoEditor.Core.Localization;

/// <summary>
/// Plural forms of a resource text (D039 §3): the forms are separated by '|' and each may hold {0} for the count. Two forms
/// are English (one | other), three are Russian (one | few | many: 1 клип, 2 клипа, 5 клипов); the rule follows the number
/// of forms, never the culture, so the English and Russian resources each carry their own.
/// </summary>
public static class Plural
{
    public static string Format(long count, string forms, params object?[] more)
    {
        var parts = forms.Split('|');
        var form = parts.Length switch
        {
            1 => parts[0],
            2 => count == 1 ? parts[0] : parts[1],
            3 => parts[RussianIndex(count)],
            _ => throw new FormatException($"Plural forms '{forms}' must have one to three parts.")
        };
        var args = new object?[more.Length + 1];
        args[0] = count;
        more.CopyTo(args, 1);
        return string.Format(form, args);
    }

    /// <summary>0 = one (1, 21, 101), 1 = few (2–4, 22–24), 2 = many (0, 5–20, 25–30, 111–114).</summary>
    public static int RussianIndex(long count)
    {
        var n = Math.Abs(count);
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod10 == 1 && mod100 != 11) return 0;
        if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return 1;
        return 2;
    }
}
