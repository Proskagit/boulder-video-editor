namespace AiVideoEditor.Video;

/// <summary>
/// FFmpeg-backed operations for video sources: ffprobe metadata analysis (Phase 3), decoding (Phase 5), thumbnail extraction (Phase 9).
/// This subsystem is scaffolded in Phase 0 as an empty, independently buildable
/// project so the solution's dependency graph is correct from day one. Concrete
/// implementations land in the phase that owns them (see docs/DEVELOPMENT_PLAN.md).
/// </summary>
internal static class ModuleInfo
{
    public const string Name = "Video";
}
