using System.Diagnostics;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>Helpers of the encoder tests: run an encoding, inspect and decode its result with ffprobe/ffmpeg.</summary>
internal static class EncoderHarness
{
    public static FfmpegExportEncoder Encoder(IFfmpegLocator? locator = null) =>
        new(locator ?? FfmpegTools.FfmpegLocator, NullLogger<FfmpegExportEncoder>.Instance);

    /// <summary>The output of a sequence of <paramref name="frames"/> frames at <paramref name="rate"/>.</summary>
    public static ExportOutput Output(int width, int height, FrameRate rate, long frames)
    {
        var duration = MediaTime.FromFrame(frames, rate);
        return new ExportOutput(new FrameSize(width, height), rate, frames, duration, Core.Playback.AudioTiming.CeilingSample(duration));
    }

    /// <summary>Encodes: <paramref name="sample"/>(k) is the stereo sample k (same on both channels), <paramref name="frame"/>(n)
    /// fills canvas n (BGRA, stride = width · 4 + <paramref name="stridePadding"/>).</summary>
    public static async Task Encode(ExportOutput output, string destination, Func<long, float> sample, Action<long, byte[], int> frame,
        int stridePadding = 0, IFfmpegLocator? locator = null)
    {
        await using var encoding = await Encoder(locator).StartAsync(output, destination);
        var chunk = new float[2 * 4_800];
        for (long k = 0; k < output.AudioSampleCount; k += 4_800)
        {
            var n = (int)Math.Min(4_800, output.AudioSampleCount - k);
            for (var i = 0; i < n; i++) chunk[2 * i] = chunk[2 * i + 1] = sample(k + i);
            await encoding.WriteAudioAsync(chunk.AsMemory(0, 2 * n));
        }
        var stride = output.Size.Width * 4 + stridePadding;
        var pixels = new byte[stride * output.Size.Height];
        for (long n = 0; n < output.FrameCount; n++)
        {
            frame(n, pixels, stride);
            await encoding.WriteFrameAsync(pixels, stride);
        }
        await encoding.CompleteAsync();
    }

    /// <summary>Frame n shows n as 8 bits in 8 vertical bands (white = 1), bit 0 on the left; α = 255.</summary>
    public static void NumberFrame(long n, byte[] pixels, int stride, int width, int height)
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var on = ((n >> (x * 8 / width)) & 1) == 1 ? (byte)235 : (byte)20;
            var i = y * stride + x * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = on;
            pixels[i + 3] = 255;
        }
    }

    public static int ReadNumber(byte[] bgra, int width, int height)
    {
        var y = height / 2;
        var n = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            var x = (2 * bit + 1) * width / 16;
            if (bgra[(y * width + x) * 4 + 1] > 128) n |= 1 << bit;
        }
        return n;
    }

    public static byte[] Run(string exe, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }
        };
        foreach (var a in args) process.StartInfo.ArgumentList.Add(a);
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(exe)} {string.Join(' ', args)} failed: {stderr.Result}");
        return memory.ToArray();
    }

    public static JsonElement Probe(string path, params string[] entries)
    {
        var json = Run(FfmpegTools.Ffprobe!, new[] { "-v", "error", "-of", "json" }.Concat(entries).Append(path).ToArray());
        return JsonDocument.Parse(json).RootElement;
    }

    public static JsonElement Stream(string path, string type) =>
        Probe(path, "-show_streams").GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("codec_type").GetString() == type);

    public static string Str(this JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "";

    /// <summary>Decoded audio, left channel, as ffmpeg (honouring the MP4 edit list) delivers it.</summary>
    public static float[] DecodeLeft(string path)
    {
        var raw = Run(FfmpegTools.Ffmpeg!, "-v", "error", "-i", path, "-map", "0:a:0", "-f", "f32le", "-ac", "2", "-ar", "48000", "-");
        var left = new float[raw.Length / 8];
        for (var i = 0; i < left.Length; i++) left[i] = BitConverter.ToSingle(raw, 8 * i);
        return left;
    }

    /// <summary>Decoded frames as BGRA through ffmpeg's colour conversion from the stream's tags.</summary>
    public static List<byte[]> DecodeFrames(string path, int width, int height)
    {
        var raw = Run(FfmpegTools.Ffmpeg!, "-v", "error", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough",
            "-vf", "format=bgra", "-f", "rawvideo", "-pix_fmt", "bgra", "-");
        var size = width * height * 4;
        return Enumerable.Range(0, raw.Length / size).Select(i => raw[(i * size)..((i + 1) * size)]).ToList();
    }

    /// <summary>Presentation times of the video packets, as exact (numerator in the stream time base, time base).</summary>
    public static (List<long> Pts, long TbNum, long TbDen) VideoPts(string path)
    {
        var stream = Stream(path, "video");
        var tb = stream.Str("time_base").Split('/');
        var packets = Probe(path, "-select_streams", "v:0", "-show_entries", "packet=pts").GetProperty("packets")
            .EnumerateArray().Select(p => long.Parse(p.Str("pts"))).Order().ToList();
        return (packets, long.Parse(tb[0]), long.Parse(tb[1]));
    }

    /// <summary>Energy centroids (in samples) of the bursts: runs above 0.02 separated by ≥ 20 ms.</summary>
    public static List<double> Centroids(float[] x)
    {
        var result = new List<double>();
        var gap = 960;
        for (var i = 0; i < x.Length;)
        {
            if (Math.Abs(x[i]) <= 0.02) { i++; continue; }
            int j = i, last = i;
            while (j < x.Length && j - last < gap) { if (Math.Abs(x[j]) > 0.02) last = j; j++; }
            double sum = 0, weighted = 0;
            for (var k = i; k <= last; k++) { var e = (double)x[k] * x[k]; sum += e; weighted += e * k; }
            result.Add(weighted / sum);
            i = j;
        }
        return result;
    }

    /// <summary>A 1 kHz burst of <paramref name="length"/> samples starting at every sample of <paramref name="starts"/>.</summary>
    public static Func<long, float> Bursts(IReadOnlyCollection<long> starts, int length = 240, float amplitude = 0.8f) => k =>
    {
        foreach (var s in starts)
            if (k >= s && k < s + length) return amplitude * MathF.Sin(2 * MathF.PI * 1000 * (k - s) / 48_000f);
        return 0;
    };
}
