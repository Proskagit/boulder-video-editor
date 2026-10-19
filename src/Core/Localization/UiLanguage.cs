using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace AiVideoEditor.Core.Localization;

/// <summary>
/// The language of the user-visible texts (D039 §1). The resources' neutral language is English; the app picks Russian (or
/// English) at start from <c>App:Language</c> with <see cref="Apply"/>. The operating system's language never decides:
/// until <see cref="Apply"/> runs — in tests and tools — the texts are the neutral English, also on a Russian Windows.
/// Only the UI culture (which resources are read) is set; number and date formatting (<c>CurrentCulture</c>) still follows
/// the OS.
/// </summary>
public static class UiLanguage
{
    /// <summary>The language used when none is configured: the neutral resources (English).</summary>
    public static CultureInfo Neutral => CultureInfo.InvariantCulture;

    /// <summary>The culture of <paramref name="language"/> ("ru", "en", empty = neutral); null for an unknown name.</summary>
    public static CultureInfo? Resolve(string? language)
    {
        var name = language?.Trim() ?? "";
        if (name.Length == 0 || name.Equals("en", StringComparison.OrdinalIgnoreCase)) return Neutral;
        if (name.Equals("ru", StringComparison.OrdinalIgnoreCase)) return CultureInfo.GetCultureInfo("ru");
        return null;
    }

    /// <summary>Sets the language of every text read from now on (the app's start). An unknown name gives the neutral
    /// English and returns false (the caller logs it).</summary>
    public static bool Apply(string? language)
    {
        var culture = Resolve(language);
        CultureInfo.DefaultThreadCurrentUICulture = culture ?? Neutral;
        CultureInfo.CurrentUICulture = culture ?? Neutral;
        return culture is not null;
    }

    /// <summary>Uses <paramref name="language"/> for the texts of the current flow until disposed (tests).</summary>
    public static IDisposable Use(string language)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = Resolve(language) ?? throw new ArgumentException($"Unknown language '{language}'.", nameof(language));
        return new Restore(previous);
    }

    /// <summary>Makes the neutral English the default until <see cref="Apply"/> runs (never overrides a language already
    /// applied). Called when Core and the UI assembly load.</summary>
    public static void EnsureNeutralDefault() => CultureInfo.DefaultThreadCurrentUICulture ??= Neutral;

    // A library normally has no module initializer (CA2255); here it is the point: whichever assembly reads a text first,
    // the OS language must not choose it.
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries")]
    [ModuleInitializer]
    internal static void OnLoad() => EnsureNeutralDefault();

    private sealed class Restore(CultureInfo previous) : IDisposable
    {
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }
}
