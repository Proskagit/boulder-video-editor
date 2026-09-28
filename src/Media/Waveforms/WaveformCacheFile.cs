using System.Buffers.Binary;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Media.Waveforms;

/// <summary>
/// The waveform cache file (D024 Step 9.5): a 20-byte header — magic <c>AIVW</c>, then little-endian format version
/// (32-bit), samples per peak (32-bit) and sample count (64-bit) — followed by exactly
/// <c>⌈sample count / samples per peak⌉</c> peak bytes. Anything else is not a waveform: <see cref="TryRead"/> returns
/// null and the caller treats it as a cache miss.
/// </summary>
internal static class WaveformCacheFile
{
    public const int FormatVersion = 1;
    public const int HeaderLength = 20;
    /// <summary>More than a day of audio at the service's resolution; bounds what a damaged header can make us allocate.</summary>
    public const long MaxPeakCount = 64L * 1024 * 1024;
    public const int MaxSamplesPerPeak = 1 << 20;

    private static ReadOnlySpan<byte> Magic => "AIVW"u8;

    public static byte[] Encode(Waveform waveform)
    {
        var bytes = new byte[HeaderLength + waveform.Peaks.Length];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), waveform.SamplesPerPeak);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), waveform.SampleCount);
        waveform.Peaks.Span.CopyTo(bytes.AsSpan(HeaderLength));
        return bytes;
    }

    /// <summary>The waveform in <paramref name="path"/>, or null when the file is missing, unreadable or not a complete
    /// waveform of this format.</summary>
    public static Waveform? TryRead(string path)
    {
        byte[] bytes;
        try
        {
            var length = new FileInfo(path).Length;
            if (length < HeaderLength || length > HeaderLength + MaxPeakCount)
                return null;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (bytes.Length < HeaderLength || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
            return null;
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        var samplesPerPeak = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var sampleCount = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12));
        if (version != FormatVersion || samplesPerPeak is <= 0 or > MaxSamplesPerPeak || sampleCount < 0)
            return null;
        if (sampleCount / samplesPerPeak > MaxPeakCount ||
            bytes.Length - HeaderLength != Waveform.PeakCountFor(sampleCount, samplesPerPeak))
            return null;
        return new Waveform(samplesPerPeak, sampleCount, bytes.AsMemory(HeaderLength));
    }
}
