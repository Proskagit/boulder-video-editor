using Avalonia.Media;

namespace AiVideoEditor.UI.Services;

/// <summary><see cref="IFontCatalog"/> from Avalonia's <see cref="FontManager"/>; read once, on first use.</summary>
public sealed class AvaloniaFontCatalog : IFontCatalog
{
    private readonly Lazy<IReadOnlyList<string>> _names = new(() => FontManager.Current.SystemFonts
        .Select(f => f.Name)
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
        .ToList());

    public IReadOnlyList<string> FamilyNames => _names.Value;
}
