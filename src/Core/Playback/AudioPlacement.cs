using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// Where the samples of one <see cref="AudioSpan"/> come from (D013, D022) — the one placement rule of
/// realtime playback (<c>AudioSpanReader</c>) and of the export (<c>ExportAudioReader</c>, D023).
/// <list type="bullet">
/// <item>The clip <c>[S, E)</c> owns timeline samples <c>[FirstSample, EndSample)</c> =
/// <c>[⌈S·48000/10⁷⌉, ⌈E·48000/10⁷⌉)</c>: adjacent clips (a split) neither overlap nor leave a gap.</item>
/// <item>1×: timeline sample <c>k</c> plays source sample <c>k + d</c>, <c>d = round((SourceIn − S)·48000/10⁷)</c>,
/// rounded once per clip (<see cref="AudioTiming.SourceOffset"/>).</item>
/// <item>Other speeds: timeline sample <c>k</c> plays source time <c>SourceIn + (k/48000 − S)·s</c>; the decoder
/// delivers a tempo-changed stream (one output sample per timeline sample, pitch kept) and reports the source
/// sample its first output sample stands for — its own filter latency already compensated
/// (<see cref="IAudioSampleStream.FirstSampleIndex"/>); the stream is placed once at the nearest timeline sample.</item>
/// </list>
/// Playback may start anywhere inside the clip (seek, resume after pause): <see cref="Request"/> asks the decoder
/// for the source of that timeline sample and <see cref="TimelineSampleOfStream"/> places whatever the decoder
/// delivers by its real first sample — never by the request. Samples of a stream before the needed one are
/// dropped, a stream starting later leaves silence before it, nothing is played at or after
/// <see cref="EndSample"/>, and a stream that ends early leaves silence (the source has no audio there).
/// SourceOut is not needed: the timeline edge ends the clip (the timing invariant of D022 keeps it inside the
/// selected source range).
/// </summary>
public readonly record struct AudioPlacement
{
    private readonly MediaTime _start;
    private readonly MediaTime _sourceIn;
    private readonly long _sourceOffset;   // d at 1×

    private AudioPlacement(MediaTime start, MediaTime end, MediaTime sourceIn, ClipSpeed speed)
    {
        _start = start;
        _sourceIn = sourceIn;
        Speed = speed;
        FirstSample = AudioTiming.CeilingSample(start);
        EndSample = AudioTiming.CeilingSample(end);
        _sourceOffset = AudioTiming.SourceOffset(start, sourceIn);
    }

    public static AudioPlacement Of(AudioSpan span) => new(span.TimelineStart, span.TimelineEnd, span.SourceIn, span.Speed);

    public static AudioPlacement Of(MediaTime timelineStart, MediaTime timelineEnd, MediaTime sourceIn, ClipSpeed speed) =>
        new(timelineStart, timelineEnd, sourceIn, speed);

    public ClipSpeed Speed { get; }

    /// <summary>First timeline sample the clip owns.</summary>
    public long FirstSample { get; }

    /// <summary>First timeline sample after the clip.</summary>
    public long EndSample { get; }

    /// <summary><paramref name="timelineSample"/> moved into <c>[FirstSample, EndSample]</c>.</summary>
    public long Clamp(long timelineSample) => Math.Clamp(timelineSample, FirstSample, EndSample);

    /// <summary>Source time (from the file's start time) played at <paramref name="timelineSample"/>:
    /// at 1× the start of source sample <c>k + d</c> (ticks rounded down), otherwise
    /// <see cref="AudioTiming.SourceTimeAt"/>.</summary>
    public MediaTime SourcePositionAt(long timelineSample) => Speed.IsNormal
        ? new MediaTime(AudioTiming.SampleToTicksFloor(timelineSample + _sourceOffset))
        : AudioTiming.SourceTimeAt(timelineSample, _start, _sourceIn, Speed);

    /// <summary>Timeline sample of the first sample of a decoded stream whose first sample stands for source
    /// sample <paramref name="firstSourceSample"/> (<see cref="IAudioSampleStream.FirstSampleIndex"/>).</summary>
    public long TimelineSampleOfStream(long firstSourceSample) => Speed.IsNormal
        ? firstSourceSample - _sourceOffset
        : AudioTiming.TimelineSampleOfStreamStart(_start, _sourceIn, Speed, firstSourceSample);

    /// <summary>The decode request for playing this clip of <paramref name="asset"/> from
    /// <paramref name="timelineSample"/> on.</summary>
    public AudioDecodeRequest Request(PlaybackAsset asset, long timelineSample) => new()
    {
        FilePath = asset.FilePath,
        StartTime = asset.StartTime,
        SourcePosition = SourcePositionAt(timelineSample),
        Speed = Speed
    };
}

/// <summary>
/// The mix (D013; D023: the same for playback and export): every audible clip adds its samples × its gain,
/// then the sum is clamped to [−1, 1]. Nothing else — no normalization, limiter, compressor, automatic gain
/// or headroom. Silence where no clip plays.
/// </summary>
public static class AudioMix
{
    /// <summary>The gain a span is mixed with: <see cref="AudioSpan.EffectiveGain"/> (0 while muted) as float.</summary>
    public static float Gain(AudioSpan span) => (float)span.EffectiveGain;

    /// <summary><paramref name="mix"/>[i] += <paramref name="samples"/>[i] × <paramref name="gain"/>.</summary>
    public static void Add(ReadOnlySpan<float> samples, Span<float> mix, float gain)
    {
        if (samples.Length > mix.Length) throw new ArgumentException("More samples than mix positions.", nameof(samples));
        for (var i = 0; i < samples.Length; i++)
            mix[i] += samples[i] * gain;
    }

    /// <summary>Clamps every sample of the finished sum to [−1, 1].</summary>
    public static void Clamp(Span<float> mix)
    {
        for (var i = 0; i < mix.Length; i++)
            mix[i] = Math.Clamp(mix[i], -1f, 1f);
    }
}
