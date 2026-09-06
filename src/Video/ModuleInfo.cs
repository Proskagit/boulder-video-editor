namespace AiVideoEditor.Video;

/// <summary>
/// Decoding, thumbnail extraction and frame-accurate FFmpeg operations for video sources (Phase 2+).
/// This subsystem is scaffolded in Phase 0 as an empty, independently buildable
/// project so the solution's dependency graph is correct from day one. Concrete
/// implementations land in the phase that owns them (see docs/DEVELOPMENT_PLAN.md).
/// </summary>
internal static class ModuleInfo
{
    public const string Name = "Video";
}
