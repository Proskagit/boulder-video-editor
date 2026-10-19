using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AiVideoEditor.Core.Localization;

namespace AiVideoEditor.UI.Localization;

/// <summary>
/// The UI's texts are <see cref="Strings"/> (generated from <c>Strings.resx</c> / <c>Strings.ru.resx</c>, D039 §3). The UI
/// assembly can read a text before anything loads Core, so it pins the neutral English as well (see
/// <see cref="UiLanguage"/>).
/// </summary>
internal static class UiTexts
{
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries")]
    [ModuleInitializer]
    internal static void OnLoad() => UiLanguage.EnsureNeutralDefault();
}
