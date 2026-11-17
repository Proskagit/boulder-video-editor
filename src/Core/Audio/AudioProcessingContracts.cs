using System.Globalization;

namespace AiVideoEditor.Core.Audio;

/// <summary>
/// D064 §5: the noise reduction of a clean-up as an FFmpeg filter — <c>highpass</c> (40 Hz; 80 Hz from strength 60: rumble,
/// wind) and <c>afftdn</c> with noise tracking (<c>nr</c> = 4 + 0.2·strength dB, at most 24; the noise floor −50 dB). DSP, no
/// model: steady noise only, limited so it never turns a voice robotic. <c>afftdn</c> delays its output by a constant half window
/// (<see cref="Latency"/> samples at 48 kHz): the filter pads that much silence after the source and the processor drops that
/// much from the start, so the cleaned sound stays aligned to the source sample for sample.
/// </summary>
public static class NoiseReductionFilter
{
    /// <summary><c>afftdn</c>'s delay at 48 kHz (its 50 ms window's half), measured with FFmpeg 9.0.1 and guarded by a test.</summary>
    public const int Latency = 1200;

    /// <summary>The noise reduction in dB of <paramref name="strength"/> (1–100).</summary>
    public static double ReductionDb(int strength) => Math.Min(24, 4 + 0.2 * Math.Clamp(strength, 0, AudioCleanup.MaxStrength));

    /// <summary>The filter for <paramref name="strength"/>; null for 0.</summary>
    public static string? Of(int strength)
    {
        if (strength <= 0) return null;
        var highpass = strength >= 60 ? 80 : 40;
        return string.Create(CultureInfo.InvariantCulture,
            $"apad=pad_len={Latency},highpass=f={highpass},afftdn=nr={ReductionDb(strength):0.##}:nf=-50:tn=1");
    }
}

/// <summary>
/// D064 §5: the voice model (DeepFilterNet3, streaming, 48 kHz mono): one hop of <see cref="HopSamples"/> in, one out, the
/// output <see cref="DelaySamples"/> behind the input. A stream keeps its state; each processing chunk opens its own.
/// </summary>
public interface IVoiceEnhancer
{
    int HopSamples { get; }

    int DelaySamples { get; }

    /// <summary>Null when the model can be used; otherwise the user-facing reason (not installed, damaged, failed to load).</summary>
    Task<string?> CheckAsync(CancellationToken ct);

    /// <summary>A new stream from silence. Each stream is used by one thread at a time.</summary>
    IVoiceEnhancerStream Open();
}

public interface IVoiceEnhancerStream : IDisposable
{
    /// <summary>One hop of input (mono) → one hop of output.</summary>
    void Process(ReadOnlySpan<float> input, Span<float> output);
}

/// <summary>D064 §4: the speech model (Silero VAD v5, 16 kHz): one window of <see cref="WindowSamples"/> → its speech
/// probability. A stream keeps its state.</summary>
public interface ISpeechModel
{
    /// <summary>512 samples at 16 kHz (32 ms).</summary>
    int WindowSamples { get; }

    Task<string?> CheckAsync(CancellationToken ct);

    ISpeechModelStream Open();
}

public interface ISpeechModelStream : IDisposable
{
    float Process(ReadOnlySpan<float> window16k);
}

/// <summary>D064 §4–§5: computes a file's cleaned sound and the speech in a sound (implemented by the Media project's
/// processor); the UI's coordinator schedules it.</summary>
public interface IAudioCleanupService
{
    /// <summary>The identity of the algorithms and models (part of every cache key).</summary>
    string Version { get; }

    /// <summary>Null when <paramref name="cleanup"/> can be computed here; otherwise the reason (the voice model).</summary>
    Task<string?> CheckAsync(AudioCleanup cleanup, CancellationToken ct);

    /// <summary>Null when speech can be detected here; otherwise the reason (the speech model).</summary>
    Task<string?> CheckSpeechAsync(CancellationToken ct);

    Task ProduceAsync(string filePath, Common.MediaTime startTime, Common.MediaTime duration, AudioCleanup cleanup, string outputPath,
        IProgress<double>? progress, CancellationToken ct);

    Task<SpeechActivity> AnalyseSpeechAsync(string filePath, Common.MediaTime startTime, Common.MediaTime duration, IProgress<double>? progress,
        CancellationToken ct);
}

/// <summary>D064 §5: the cleaned sounds and the speech analyses kept between sessions (in the application's cache, never the
/// project), by the source file's content, the settings and the algorithm version.</summary>
public interface IAudioAnalysisCache
{
    /// <summary>The content identity of a file (size, write time, head and tail); null when it can't be read.</summary>
    string? Fingerprint(string filePath);

    /// <summary>The cached cleaned WAV of <paramref name="key"/>, or null.</summary>
    string? TryGetCleaned(string key);

    /// <summary>Where a new cleaned WAV of <paramref name="key"/> is written (a temporary path; <see cref="CommitCleaned"/> moves
    /// it in).</summary>
    string NewCleanedPath(string key);

    /// <summary>Moves a finished WAV into the cache; returns its final path.</summary>
    string CommitCleaned(string key, string temporaryPath);

    SpeechActivity? TryLoadSpeech(string key);

    void SaveSpeech(string key, SpeechActivity activity);
}

/// <summary>A clean-up or speech analysis that can't be done (no model, a file too long), with the user-facing reason.</summary>
public sealed class AudioCleanupException(string message) : Exception(message);

/// <summary>What the export's preflight uses (D064 §5): the Preview's own results, the clean-ups and speech still pending
/// (waiting, running or stopped — the export never mixes without them) and the failed ones (exported with the original sound /
/// without the ducking, as the Preview plays them).</summary>
public sealed record ExportAudioAnalyses(AudioAnalyses Analyses, IReadOnlySet<(Guid Asset, AudioCleanup Cleanup)> PendingCleanups,
    IReadOnlyDictionary<(Guid Asset, AudioCleanup Cleanup), string> FailedCleanups, IReadOnlySet<Guid> PendingDucking,
    IReadOnlyDictionary<Guid, string> FailedDucking)
{
    public static readonly ExportAudioAnalyses None = new(AudioAnalyses.None, new HashSet<(Guid, AudioCleanup)>(),
        new Dictionary<(Guid, AudioCleanup), string>(), new HashSet<Guid>(), new Dictionary<Guid, string>());
}
