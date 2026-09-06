namespace AiVideoEditor.Media;

/// <summary>
/// Media Browser services: import, metadata probing, thumbnail caching (Phase 2).
/// This subsystem is scaffolded in Phase 0 as an empty, independently buildable
/// project so the solution's dependency graph is correct from day one. Concrete
/// implementations land in the phase that owns them (see docs/DEVELOPMENT_PLAN.md).
/// </summary>
internal static class ModuleInfo
{
    public const string Name = "Media";
}
