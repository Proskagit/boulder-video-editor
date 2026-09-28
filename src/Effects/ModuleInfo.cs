namespace AiVideoEditor.Effects;

/// <summary>
/// Effect and transition definitions plus their parameter schemas, feeding the generic Inspector. Empty: no effect
/// type is implemented yet (<c>Clip.Effects</c> is only kept in <c>project.json</c>); the project exists so the
/// solution's dependency graph is ready for it.
/// </summary>
internal static class ModuleInfo
{
    public const string Name = "Effects";
}
