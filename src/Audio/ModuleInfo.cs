namespace AiVideoEditor.Audio;

/// <summary>
/// Audio output for playback (WASAPI, <c>WasapiAudioOutput</c>). Waveforms (Phase 9) are made in Media
/// (<c>WaveformService</c>) from the app's audio decoder, not here.
/// </summary>
internal static class ModuleInfo
{
    public const string Name = "Audio";
}
