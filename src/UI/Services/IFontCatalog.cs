namespace AiVideoEditor.UI.Services;

/// <summary>Font families installed on this system, for choosing a text clip's font.</summary>
public interface IFontCatalog
{
    /// <summary>Family names, sorted, without duplicates.</summary>
    IReadOnlyList<string> FamilyNames { get; }
}
