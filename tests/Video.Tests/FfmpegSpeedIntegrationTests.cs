using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Phase 7 Step 9 (D022) with the real ffmpeg: the decoder's tempo chain for other speeds keeps the
/// pitch, places the source within 10 ms of the exact mapping (output sample j = source sample
/// FirstSampleIndex + j·s) with no drift along the clip, and delivers the source up to the end of the
/// file. These tests guard the decoder's measured atempo latency compensation.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class FfmpegSpeedIntegrationTests : IDisposable
{
    private const int Rate = AudioFormat.SampleRate;
    private const double SeekSeconds = 2.5;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-speed-tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public FfmpegSpeedIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A 20 s MP4 (AAC) whose audio is 1 kHz: bursts of 250 ms at every whole second, or a continuous tone.</summary>
    private string Media(string name, bool bursts)
    {
        var path = Path.Combine(_dir, name);
        var audio = bursts ? @"aevalsrc=sin(2*PI*1000*t)*0.8*lt(mod(t\,1)\,0.25):s=48000:d=20" : "sine=frequency=1000:sample_rate=48000:duration=20";
        TestMedia.Run(FfmpegTools.Ffmpeg!, new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=20",
            "-f", "lavfi", "-i", audio, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest", path
        });
        return path;
    }

    private static async Task<(long First, float[] Left)> Decode(string path, ClipSpeed speed)
    {
        var decoder = new FfmpegAudioDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegAudioDecoder>.Instance);
        await using var stream = await decoder.OpenStreamAsync(new AudioDecodeRequest
        {
            FilePath = path, StartTime = MediaTime.Zero, SourcePosition = MediaTime.FromSeconds(SeekSeconds), Speed = speed
        }, CancellationToken.None);
        var left = new List<float>();
        var buffer = new float[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            for (var i = 0; i < read; i += 2) left.Add(buffer[i]);
        return (stream.FirstSampleIndex, left.ToArray());
    }

    /// <summary>Energy centroid (in samples) of each burst: runs above 0.05 separated by ≥ 150 ms.</summary>
    private static List<double> Centroids(float[] x)
    {
        var result = new List<double>();
        var gap = (int)(0.15 * Rate);
        for (var i = 0; i < x.Length;)
        {
            if (Math.Abs(x[i]) <= 0.05) { i++; continue; }
            int j = i, last = i;
            while (j < x.Length && j - last < gap) { if (Math.Abs(x[j]) > 0.05) last = j; j++; }
            double sum = 0, weighted = 0;
            for (var k = i; k <= last; k++) { var e = (double)x[k] * x[k]; sum += e; weighted += e * k; }
            result.Add(weighted / sum);
            i = j;
        }
        return result;
    }

    private static double Frequency(float[] x)
    {
        var start = x.Length / 3;
        var crossings = 0;
        for (var i = start + 1; i < start + Rate / 2; i++) if ((x[i - 1] < 0) != (x[i] < 0)) crossings++;
        return crossings / 2.0 / 0.5;
    }

    public static readonly TheoryData<int> SpeedSteps = new() { 5, 7, 10, 27, 40, 80 };   // 0.25×, 0.35×, 0.5×, 1.35×, 2×, 4×

    [FfmpegTheory]
    [MemberData(nameof(SpeedSteps))]
    public async Task Bursts_land_within_10_ms_of_the_exact_mapping_without_drift(int steps)
    {
        var speed = ClipSpeed.FromSteps(steps);
        var s = (double)speed.ToDecimal();
        var (first, left) = await Decode(Media("bursts.mp4", bursts: true), speed);

        // Burst k is centred at source second k + 0.125; output sample j stands for source first + j·s.
        var errorsMs = new List<double>();
        foreach (var centroid in Centroids(left))
        {
            var source = first + centroid * s;
            var burst = Math.Round(source / Rate - 0.125);
            var exact = (burst + 0.125) * Rate;
            errorsMs.Add((source - exact) / s / Rate * 1000);   // timeline milliseconds
        }

        _output.WriteLine($"{speed}: first {first}, {errorsMs.Count} bursts, errors ms: {string.Join(" ", errorsMs.Select(e => e.ToString("0.00")))}");
        Assert.True(errorsMs.Count >= 15);
        Assert.All(errorsMs, e => Assert.InRange(e, -10.0, 10.0));
        Assert.InRange(errorsMs[^1] - errorsMs[0], -10.0, 10.0);                 // no accumulated drift
    }

    [FfmpegTheory]
    [MemberData(nameof(SpeedSteps))]
    public async Task The_pitch_is_kept_and_the_source_reaches_the_end_of_the_file(int steps)
    {
        var speed = ClipSpeed.FromSteps(steps);
        var s = (double)speed.ToDecimal();
        var (first, left) = await Decode(Media("tone.mp4", bursts: false), speed);

        Assert.InRange(Frequency(left), 990.0, 1010.0);
        // Without apad the tempo filter drops 16–180 ms before the end; with it the output covers the
        // whole source up to 20 s (plus padding silence).
        var coveredSourceEnd = first + left.Length * s;
        Assert.True(coveredSourceEnd >= 20.0 * Rate, $"source covered up to {coveredSourceEnd / Rate:0.000} s");
    }

    [FfmpegFact]
    public async Task At_1x_the_stream_is_unchanged()
    {
        var (first, left) = await Decode(Media("tone1.mp4", bursts: false), ClipSpeed.Normal);
        Assert.True(first <= AudioTiming.NearestSample(MediaTime.FromSeconds(SeekSeconds)));   // preroll as before, no latency offset
        Assert.InRange(first + left.Length - 20L * Rate, -48, 48);                            // contiguous source up to the end
    }

    [Theory]
    [InlineData(5, ",apad=pad_dur=0.25,atempo=0.5,atempo=0.5")]
    [InlineData(7, ",apad=pad_dur=0.25,atempo=0.5,atempo=0.7")]
    [InlineData(10, ",apad=pad_dur=0.25,atempo=0.5")]
    [InlineData(19, ",apad=pad_dur=0.25,atempo=0.95")]
    [InlineData(20, "")]
    [InlineData(27, ",apad=pad_dur=0.25,atempo=1.35")]
    [InlineData(40, ",apad=pad_dur=0.25,atempo=2")]
    [InlineData(80, ",apad=pad_dur=0.25,atempo=4")]
    public void Tempo_chain_per_speed(int steps, string chain)
    {
        Assert.Equal(chain, FfmpegAudioDecoder.TempoFilters(ClipSpeed.FromSteps(steps)));
        var af = FfmpegAudioDecoder.BuildArguments("x.mp4", null, ClipSpeed.FromSteps(steps)).SkipWhile(a => a != "-af").Skip(1).First();
        Assert.Equal(1, af.Split(',').Count(f => f.StartsWith("ashowinfo")));   // one ashowinfo, before the tempo change
        Assert.True(af.IndexOf("ashowinfo", StringComparison.Ordinal) < (af.IndexOf("atempo", StringComparison.Ordinal) is var t && t >= 0 ? t : int.MaxValue));
    }
}
