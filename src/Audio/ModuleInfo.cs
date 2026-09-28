namespace AiVideoEditor.Audio;

/// <summary>
/// Audio output for playback (WASAPI). Waveforms (Phase 9) are made in Media (<c>WaveformService</c>) from the
/// app's audio decoder, not here.
/// This subsystem is scaffolded in Phase 0 as an empty, independently buildable
/// project so the solution's dependency graph is correct from day one. Concrete
/// implementations land in the phase that owns them (see docs/DEVELOPMENT_PLAN.md).
/// </summary>
internal static class ModuleInfo
{
    public const string Name = "Audio";
}
