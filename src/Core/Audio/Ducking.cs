using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Audio;

/// <summary>
/// D064 §1 / §4: a track's ducking — the track is lowered by <see cref="DepthDb"/> while speech is detected on
/// <see cref="SpeechTrackId"/> (another track, video or audio): it goes down over <see cref="AttackMs"/> before the speech,
/// stays down during it and comes back over <see cref="ReleaseMs"/> after it. <see cref="Sensitivity"/> sets how readily a
/// sound counts as speech. Immutable, replaced as a whole by every edit.
/// </summary>
public sealed record DuckingSettings(Guid SpeechTrackId, double DepthDb, int AttackMs, int ReleaseMs, int Sensitivity)
{
    public const double MinDepthDb = -40;
    public const double MaxDepthDb = -3;
    public const double DefaultDepthDb = -15;
    public const int MaxAttackMs = 2000;
    public const int DefaultAttackMs = 250;
    public const int MinReleaseMs = 50;
    public const int MaxReleaseMs = 3000;
    public const int DefaultReleaseMs = 600;
    public const int MaxSensitivity = 100;
    public const int DefaultSensitivity = 50;

    public static DuckingSettings Default(Guid speechTrackId) =>
        new(speechTrackId, DefaultDepthDb, DefaultAttackMs, DefaultReleaseMs, DefaultSensitivity);

    public bool IsValid =>
        SpeechTrackId != Guid.Empty && double.IsFinite(DepthDb) && DepthDb >= MinDepthDb && DepthDb <= MaxDepthDb &&
        AttackMs is >= 0 and <= MaxAttackMs && ReleaseMs is >= MinReleaseMs and <= MaxReleaseMs &&
        Sensitivity is >= 0 and <= MaxSensitivity;

    /// <summary>D064 §4: the probability from which a window counts as speech: <c>0.85 − 0.6 · sensitivity / 100</c>.</summary>
    public double SpeechThreshold => 0.85 - 0.6 * Sensitivity / 100.0;
}

/// <summary>
/// D064 §4: where speech was found in a source — the speech probability of each window of <see cref="WindowSamples"/> samples
/// at 48 kHz from the source's sample 0 (the detector's 32 ms at 16 kHz). Derived from the file alone, never part of the
/// project; cached. Immutable.
/// </summary>
public sealed class SpeechActivity
{
    /// <summary>32 ms at 48 kHz (512 samples at 16 kHz).</summary>
    public const int WindowSamples = 1536;

    /// <summary>Speech shorter than this is dropped; gaps shorter than <see cref="MinGapSamples"/> are closed.</summary>
    public const long MinSpeechSamples = AudioFormat.SampleRate * 150 / 1000;
    public const long MinGapSamples = AudioFormat.SampleRate * 300 / 1000;

    public SpeechActivity(float[] probabilities)
    {
        Probabilities = probabilities ?? throw new ArgumentNullException(nameof(probabilities));
    }

    public float[] Probabilities { get; }

    /// <summary>The speech intervals of the source as source samples <c>[start, end)</c>: windows at or above
    /// <paramref name="threshold"/>, gaps shorter than <see cref="MinGapSamples"/> closed, then speech shorter than
    /// <see cref="MinSpeechSamples"/> dropped.</summary>
    public IReadOnlyList<(long Start, long End)> Intervals(double threshold)
    {
        var raw = new List<(long Start, long End)>();
        long? open = null;
        for (var i = 0; i < Probabilities.Length; i++)
        {
            var speech = Probabilities[i] >= threshold;
            if (speech && open is null) open = (long)i * WindowSamples;
            else if (!speech && open is { } start)
            {
                raw.Add((start, (long)i * WindowSamples));
                open = null;
            }
        }
        if (open is { } last) raw.Add((last, (long)Probabilities.Length * WindowSamples));

        var merged = new List<(long Start, long End)>();
        foreach (var interval in raw)
        {
            if (merged.Count > 0 && interval.Start - merged[^1].End < MinGapSamples)
                merged[^1] = (merged[^1].Start, interval.End);
            else
                merged.Add(interval);
        }
        merged.RemoveAll(i => i.End - i.Start < MinSpeechSamples);
        return merged;
    }
}

/// <summary>
/// D064 §4: a ducked track's gain over the timeline — closed form, so the Preview and the export get the same value wherever
/// playback starts. For each speech interval <c>[a, b)</c> (timeline samples): <see cref="DepthDb"/> inside, from 0 dB at
/// <c>a − attack</c> down to it at <c>a</c> and back from <c>b</c> to 0 dB at <c>b + release</c>, linearly in dB; overlapping
/// influences take the lowest gain. Immutable; read from any thread.
/// </summary>
public sealed class DuckingCurve : IEquatable<DuckingCurve>
{
    private readonly long[] _starts;
    private readonly long[] _ends;

    public DuckingCurve(IEnumerable<(long Start, long End)> speech, double depthDb, long attackSamples, long releaseSamples)
    {
        var merged = new List<(long Start, long End)>();
        foreach (var interval in speech.Where(i => i.End > i.Start).OrderBy(i => i.Start))
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
            else
                merged.Add(interval);
        }
        _starts = merged.Select(i => i.Start).ToArray();
        _ends = merged.Select(i => i.End).ToArray();
        DepthDb = depthDb;
        AttackSamples = Math.Max(0, attackSamples);
        ReleaseSamples = Math.Max(1, releaseSamples);
        DepthGain = Math.Pow(10, depthDb / 20);
    }

    public double DepthDb { get; }
    public long AttackSamples { get; }
    public long ReleaseSamples { get; }
    public double DepthGain { get; }

    /// <summary>The speech intervals, merged and sorted (timeline samples).</summary>
    public IEnumerable<(long Start, long End)> Speech => _starts.Select((s, i) => (s, _ends[i]));

    public bool IsEmpty => _starts.Length == 0;

    /// <summary>True when a sample in <c>[from, until)</c> is lowered.</summary>
    public bool Affects(long from, long until)
    {
        if (_starts.Length == 0) return false;
        // The first interval whose influence ends after from.
        var i = FirstEndingAfter(from - ReleaseSamples);
        return i < _starts.Length && _starts[i] - AttackSamples < until;
    }

    /// <summary>The gain factor of timeline sample <paramref name="sample"/> (exactly 1 outside every influence).</summary>
    public double Gain(long sample)
    {
        if (_starts.Length == 0) return 1;
        var i = FirstEndingAfter(sample - ReleaseSamples);
        var lowest = 0.0;   // in dB
        for (; i < _starts.Length && _starts[i] - AttackSamples <= sample; i++)
        {
            double db;
            if (sample >= _starts[i] && sample < _ends[i]) return DepthGain;
            if (sample < _starts[i])
                db = AttackSamples == 0 ? 0 : DepthDb * (1 - (_starts[i] - sample) / (double)AttackSamples);
            else
                db = DepthDb * (1 - (sample - _ends[i] + 1) / (double)ReleaseSamples);
            if (db < lowest) lowest = db;
        }
        return lowest >= 0 ? 1 : Math.Pow(10, lowest / 20);
    }

    /// <summary>The corners of the curve (where its slope changes), timeline samples sorted — the points «Закрепить в точки»
    /// writes.</summary>
    public IEnumerable<long> Corners()
    {
        for (var i = 0; i < _starts.Length; i++)
        {
            yield return _starts[i] - AttackSamples;
            yield return _starts[i];
            yield return _ends[i];
            yield return _ends[i] + ReleaseSamples;
        }
    }

    private int FirstEndingAfter(long sample)
    {
        int lo = 0, hi = _ends.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_ends[mid] > sample) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    public bool Equals(DuckingCurve? other) =>
        other is not null && (ReferenceEquals(this, other) ||
                              (DepthDb.Equals(other.DepthDb) && AttackSamples == other.AttackSamples && ReleaseSamples == other.ReleaseSamples &&
                               _starts.AsSpan().SequenceEqual(other._starts) && _ends.AsSpan().SequenceEqual(other._ends)));

    public override bool Equals(object? obj) => Equals(obj as DuckingCurve);

    public override int GetHashCode() => HashCode.Combine(DepthDb, AttackSamples, ReleaseSamples, _starts.Length);
}

/// <summary>D064 §3: the master's protection from overload — a stateless soft clip at −1 dBFS.</summary>
public static class SoftClip
{
    /// <summary>−1 dBFS: below it a sample passes bit for bit.</summary>
    public const float Knee = 0.891f;

    /// <summary><c>|x| ≤ t</c>: x; above: <c>sign(x)·(t + (1 − t)·tanh((|x| − t)/(1 − t)))</c> — continuous with slope 1 at the
    /// knee and always below 1.</summary>
    public static float Apply(float x)
    {
        var a = Math.Abs(x);
        if (a <= Knee) return x;
        if (float.IsNaN(x)) return 0;
        var y = Knee + (1 - Knee) * MathF.Tanh((a - Knee) / (1 - Knee));
        return x < 0 ? -y : y;
    }
}

/// <summary>D064 §4: the speech analyses of the project's sources (by asset and clean-up), what the snapshot builds the ducking
/// with. Derived from the files alone; never part of the project.</summary>
public sealed class SpeechAnalyses
{
    public static readonly SpeechAnalyses None = new(ImmutableDictionary<(Guid Asset, AudioCleanup Cleanup), SpeechActivity>.Empty);

    public SpeechAnalyses(ImmutableDictionary<(Guid Asset, AudioCleanup Cleanup), SpeechActivity> byAsset) => ByAsset = byAsset;

    public ImmutableDictionary<(Guid Asset, AudioCleanup Cleanup), SpeechActivity> ByAsset { get; }
}

/// <summary>D064 §5: the cleaned sounds ready so far — the processed file of an asset with a clean-up.</summary>
public sealed class CleanedAudio
{
    public static readonly CleanedAudio None = new(ImmutableDictionary<(Guid Asset, AudioCleanup Cleanup), string>.Empty);

    public CleanedAudio(ImmutableDictionary<(Guid Asset, AudioCleanup Cleanup), string> files) => Files = files;

    /// <summary>The WAV (48 kHz stereo float, aligned to the source's sample 0) of each asset and clean-up.</summary>
    public ImmutableDictionary<(Guid Asset, AudioCleanup Cleanup), string> Files { get; }
}

/// <summary>What the snapshot builds the audio clean-up and the ducking from (D064 §4–§5); <see cref="None"/>: every clip's
/// original sound, no ducking.</summary>
public sealed record AudioAnalyses(CleanedAudio Cleaned, SpeechAnalyses Speech)
{
    public static readonly AudioAnalyses None = new(CleanedAudio.None, SpeechAnalyses.None);

    /// <summary>The Preview's comparison (D064 §5, «Исходный звук»): every clip plays its original sound.</summary>
    public bool OriginalSound { get; init; }
}
