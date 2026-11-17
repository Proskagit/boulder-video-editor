namespace AiVideoEditor.Core.Audio;

/// <summary>
/// D064 §1 / §5: the clean-up of a clip's sound — two independent tools, each a strength 0–100 (0 = off):
/// <see cref="NoiseReduction"/> («Шумоподавление»: steady noise — the hall, ventilation, hum, hiss, wind rumble; DSP) and
/// <see cref="VoiceEnhancement"/> («Улучшение речи»: the voice separated from noise, music and the hall; a neural model). The
/// source file is never changed: the cleaned sound is computed once per file and setting and cached.
/// </summary>
public readonly record struct AudioCleanup(int NoiseReduction, int VoiceEnhancement)
{
    public const int MaxStrength = 100;

    /// <summary>The strength a tool gets when it is turned on.</summary>
    public const int DefaultStrength = 50;

    public static readonly AudioCleanup None = default;

    public bool IsNone => NoiseReduction == 0 && VoiceEnhancement == 0;

    public bool IsValid => NoiseReduction is >= 0 and <= MaxStrength && VoiceEnhancement is >= 0 and <= MaxStrength;

    /// <summary>A stored clean-up (null when off).</summary>
    public static AudioCleanup? Normalize(AudioCleanup? cleanup) => cleanup is { IsNone: false } on ? on : null;
}
