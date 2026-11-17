using System.Buffers.Binary;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace AiVideoEditor.Media.Audio;

/// <summary>
/// D064 §4–§5: computes a source file's cleaned sound and its speech — the whole file once per setting, never the file itself
/// changed. The source is decoded by the D013 decoder from its sample 0 (the noise reduction as its filter, its constant delay
/// compensated), so the result is aligned to the source sample for sample and every clip of the file plays it with its own
/// placement. The voice model runs in chunks of <see cref="ChunkSamples"/> with <see cref="PrerollSamples"/> of warm-up before
/// each, in parallel on the CPU — the chunks are fixed from the source's start, so the result never depends on the machine.
/// The output is a 48 kHz stereo float WAV.
/// </summary>
public sealed class AudioCleanupProcessor : IAudioCleanupService
{
    /// <summary>Changes whenever the cleaned sound or the speech analysis of the same settings would change (part of the cache
    /// key).</summary>
    public const int AlgorithmVersion = 1;

    /// <summary>30 s at 48 kHz — a whole number of the model's hops.</summary>
    public const int ChunkSamples = 30 * AudioFormat.SampleRate;

    /// <summary>1 s of warm-up before each chunk (its output dropped).</summary>
    public const int PrerollSamples = AudioFormat.SampleRate;

    /// <summary>A RIFF file is at most 4 GiB (about 3 h of 48 kHz stereo float).</summary>
    private const long MaxDataBytes = uint.MaxValue - 64;

    private const int HeaderBytes = 44;
    private const int FrameBytes = AudioFormat.Channels * sizeof(float);

    private readonly IAudioDecoder _decoder;
    private readonly IVoiceEnhancer? _voice;
    private readonly ISpeechModel? _speech;
    private readonly ILogger _logger;
    private readonly int _parallelism;

    /// <summary>The algorithms and the pinned models (the cache key's part).</summary>
    public string Version => $"clean-v{AlgorithmVersion}|audio-v1";

    public AudioCleanupProcessor(IAudioDecoder decoder, IVoiceEnhancer? voice = null, ISpeechModel? speech = null,
        ILogger<AudioCleanupProcessor>? logger = null, int? parallelism = null)
    {
        _decoder = decoder;
        _voice = voice;
        _speech = speech;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _parallelism = Math.Clamp(parallelism ?? Environment.ProcessorCount / 2, 1, 8);
    }

    /// <summary>Why <paramref name="cleanup"/> can't be computed on this computer (the voice model), or null.</summary>
    public async Task<string?> CheckAsync(AudioCleanup cleanup, CancellationToken ct)
    {
        if (cleanup.VoiceEnhancement == 0) return null;
        if (_voice is null) return Core.Localization.Messages.Audio_VoiceModelUnavailable;
        return await _voice.CheckAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> CheckSpeechAsync(CancellationToken ct) =>
        _speech is null ? Core.Localization.Messages.Audio_SpeechModelUnavailable : await _speech.CheckAsync(ct).ConfigureAwait(false);

    /// <summary>Writes the cleaned sound of the file (<paramref name="filePath"/>, its start time <paramref name="startTime"/>,
    /// about <paramref name="duration"/> long — for the progress) to <paramref name="outputPath"/>.</summary>
    public async Task ProduceAsync(string filePath, MediaTime startTime, MediaTime duration, AudioCleanup cleanup, string outputPath,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (cleanup.IsNone) throw new ArgumentException("Nothing to clean.", nameof(cleanup));
        var expected = Math.Max(1, AudioTiming.CeilingSample(duration));
        var filter = NoiseReductionFilter.Of(cleanup.NoiseReduction);
        var latency = filter is null ? 0 : NoiseReductionFilter.Latency;

        if (cleanup.VoiceEnhancement == 0)
        {
            using var output = File.OpenHandle(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            long frames = 0;
            await DecodeAlignedAsync(filePath, startTime, filter, latency, async block =>
            {
                CheckSize(frames + block.Length / AudioFormat.Channels);
                RandomAccess.Write(output, System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.Span), HeaderBytes + frames * FrameBytes);
                frames += block.Length / AudioFormat.Channels;
                progress?.Report(Math.Min(0.99, frames / (double)expected));
                await Task.CompletedTask;
            }, ct).ConfigureAwait(false);
            WriteHeader(output, frames);
            progress?.Report(1);
            return;
        }

        if (await CheckAsync(cleanup, ct).ConfigureAwait(false) is { } unavailable) throw new AudioCleanupException(unavailable);
        var raw = outputPath + ".src";
        try
        {
            // 1. The (noise-reduced) source, aligned, into a raw file the chunks read at random.
            long frames = 0;
            using (var input = File.OpenHandle(raw, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                await DecodeAlignedAsync(filePath, startTime, filter, latency, async block =>
                {
                    CheckSize(frames + block.Length / AudioFormat.Channels);
                    RandomAccess.Write(input, System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.Span), frames * FrameBytes);
                    frames += block.Length / AudioFormat.Channels;
                    progress?.Report(Math.Min(0.25, 0.25 * frames / expected));
                    await Task.CompletedTask;
                }, ct).ConfigureAwait(false);
            }

            // 2. The voice, chunk by chunk in parallel, blended with the dry sound.
            using var source = File.OpenHandle(raw, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = File.OpenHandle(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            RandomAccess.SetLength(output, HeaderBytes + frames * FrameBytes);
            var chunks = (int)((frames + ChunkSamples - 1) / ChunkSamples);
            var done = 0;
            var wet = cleanup.VoiceEnhancement / (float)AudioCleanup.MaxStrength;
            await Parallel.ForEachAsync(Enumerable.Range(0, chunks),
                new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = ct },
                (chunk, token) =>
                {
                    EnhanceChunk(source, output, frames, chunk, wet, token);
                    progress?.Report(0.25 + 0.75 * Interlocked.Increment(ref done) / chunks);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            WriteHeader(output, frames);
        }
        finally
        {
            TryDelete(raw);
        }
    }

    /// <summary>The speech of a sound (an original file or a cleaned WAV, start time 0) — Silero's probability per 32 ms.</summary>
    public async Task<SpeechActivity> AnalyseSpeechAsync(string filePath, MediaTime startTime, MediaTime duration, IProgress<double>? progress,
        CancellationToken ct)
    {
        if (await CheckSpeechAsync(ct).ConfigureAwait(false) is { } unavailable) throw new AudioCleanupException(unavailable);
        var expected = Math.Max(1, AudioTiming.CeilingSample(duration));
        using var model = _speech!.Open();
        var window = _speech.WindowSamples;                                     // 512 at 16 kHz
        var pending = new float[window];
        var filled = 0;
        var decimate = new float[3];
        var decimated = 0;
        long frames = 0;
        var probabilities = new List<float>((int)(expected / SpeechActivity.WindowSamples) + 2);

        await DecodeAlignedAsync(filePath, startTime, null, 0, block =>
        {
            var span = block.Span;
            for (var i = 0; i < span.Length; i += AudioFormat.Channels)
            {
                decimate[decimated++] = 0.5f * (span[i] + span[i + 1]);
                if (decimated < 3) continue;
                decimated = 0;
                pending[filled++] = (decimate[0] + decimate[1] + decimate[2]) / 3;   // 48 → 16 kHz: a 3-sample mean
                if (filled < window) continue;
                filled = 0;
                probabilities.Add(model.Process(pending));
            }
            frames += span.Length / AudioFormat.Channels;
            progress?.Report(Math.Min(0.99, frames / (double)expected));
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
        if (filled > 0)
        {
            Array.Clear(pending, filled, window - filled);
            probabilities.Add(model.Process(pending));
        }
        progress?.Report(1);
        return new SpeechActivity(probabilities.ToArray());
    }

    /// <summary>The voice model over one chunk: input from <c>chunk·C − P</c> (silence before the source's start) to past its end
    /// by the model's delay, the output of <c>[chunk·C, min(end, (chunk+1)·C))</c> blended <c>wet·voice + (1 − wet)·dry</c>.</summary>
    private void EnhanceChunk(SafeFileHandle source, SafeFileHandle output, long frames, int chunk, float wet, CancellationToken ct)
    {
        var voice = _voice!;
        var hop = voice.HopSamples;
        var delay = voice.DelaySamples;
        var first = (long)chunk * ChunkSamples;
        var last = Math.Min(frames, first + ChunkSamples);
        var inputStart = first - PrerollSamples;
        var length = (int)(last - inputStart + delay);
        length = (length + hop - 1) / hop * hop;

        var stereo = new float[length * AudioFormat.Channels];
        var readFrom = Math.Max(0, inputStart);
        var readUntil = Math.Min(frames, inputStart + length);
        if (readUntil > readFrom)
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(stereo.AsSpan((int)(readFrom - inputStart) * AudioFormat.Channels,
                (int)(readUntil - readFrom) * AudioFormat.Channels));
            var read = 0;
            while (read < bytes.Length)
            {
                var n = RandomAccess.Read(source, bytes[read..], readFrom * FrameBytes + read);
                if (n <= 0) break;
                read += n;
            }
        }

        var mid = new float[length];
        for (var i = 0; i < length; i++) mid[i] = 0.5f * (stereo[2 * i] + stereo[2 * i + 1]);
        var enhanced = new float[length];
        using (var stream = voice.Open())
        {
            for (var h = 0; h < length; h += hop)
            {
                if ((h / hop & 63) == 0) ct.ThrowIfCancellationRequested();
                stream.Process(mid.AsSpan(h, hop), enhanced.AsSpan(h, hop));
            }
        }

        var count = (int)(last - first);
        var result = new float[count * AudioFormat.Channels];
        var dry = 1 - wet;
        for (var i = 0; i < count; i++)
        {
            var q = (int)(first - inputStart) + i;                   // the dry sample's place in the chunk's input
            var v = enhanced[q + delay] * wet;
            result[2 * i] = v + stereo[2 * q] * dry;
            result[2 * i + 1] = v + stereo[2 * q + 1] * dry;
        }
        RandomAccess.Write(output, System.Runtime.InteropServices.MemoryMarshal.AsBytes(result.AsSpan()), HeaderBytes + first * FrameBytes);
    }

    /// <summary>
    /// Decodes the file's first audio stream at 1× from its source sample 0 and hands the samples on in blocks, aligned: silence
    /// for a stream starting later, a stream starting earlier (or the filter's <paramref name="latency"/>) dropped.
    /// </summary>
    private async Task DecodeAlignedAsync(string filePath, MediaTime startTime, string? filter, int latency,
        Func<ReadOnlyMemory<float>, ValueTask> sink, CancellationToken ct)
    {
        await using var stream = await _decoder.OpenAsync(new AudioDecodeRequest
        {
            FilePath = filePath, StartTime = startTime, SourcePosition = MediaTime.Zero, Speed = ClipSpeed.Normal, StrictEnd = true, Filter = filter
        }, ct).ConfigureAwait(false);

        var skip = latency + Math.Max(0, -stream.FirstSampleIndex);               // frames to drop
        var silence = Math.Max(0, stream.FirstSampleIndex);                       // frames to add first
        var buffer = new float[16_384 * AudioFormat.Channels];
        if (silence > 0)
        {
            var zeros = new float[Math.Min(buffer.Length, silence * AudioFormat.Channels)];
            while (silence > 0)
            {
                var n = (int)Math.Min(zeros.Length / AudioFormat.Channels, silence);
                await sink(zeros.AsMemory(0, n * AudioFormat.Channels)).ConfigureAwait(false);
                silence -= n;
            }
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            var offset = 0;
            if (skip > 0)
            {
                var drop = (int)Math.Min(skip, read / AudioFormat.Channels);
                skip -= drop;
                offset = drop * AudioFormat.Channels;
            }
            if (offset < read) await sink(buffer.AsMemory(offset, read - offset)).ConfigureAwait(false);
        }
    }

    private static void CheckSize(long frames)
    {
        if (frames * FrameBytes > MaxDataBytes) throw new AudioCleanupException(Core.Localization.Messages.Audio_TooLong);
    }

    /// <summary>The 44-byte header of a 48 kHz stereo IEEE-float WAV with <paramref name="frames"/> frames.</summary>
    private static void WriteHeader(SafeFileHandle file, long frames)
    {
        var data = (uint)(frames * FrameBytes);
        Span<byte> h = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(h[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 3);                            // IEEE float
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], AudioFormat.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], AudioFormat.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], AudioFormat.SampleRate * FrameBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], FrameBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 32);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], data);
        RandomAccess.Write(file, h, 0);
        RandomAccess.SetLength(file, HeaderBytes + (long)data);
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Audio clean-up: cannot delete {File}.", path); }
    }
}
