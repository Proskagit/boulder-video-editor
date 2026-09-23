namespace AiVideoEditor.Media;

/// <summary>
/// Media Browser services: import and file validation (Phase 2). Metadata probing lives in Video (Phase 3); thumbnail caching is Phase 9.
/// This subsystem is scaffolded in Phase 0 as an empty, independently buildable
/// project so the solution's dependency graph is correct from day one. Concrete
/// implementations land in the phase that owns them (see docs/DEVELOPMENT_PLAN.md).
/// </summary>
internal static class ModuleInfo
{
    public const string Name = "Media";
}
