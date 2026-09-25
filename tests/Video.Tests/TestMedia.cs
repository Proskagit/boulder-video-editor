using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>Exact rational number (seconds) for test oracles.</summary>
internal readonly record struct Rational(BigInteger Num, BigInteger Den) : IComparable<Rational>
{
    public static Rational Of(BigInteger num, BigInteger den) => den < 0 ? new(-num, -den) : new(num, den);
    public static Rational FromTicks(long ticks) => new(ticks, TimeSpan.TicksPerSecond);
    public static Rational FromDouble(double seconds) => new(new BigInteger(Math.Round(seconds * 1e9)), 1_000_000_000);
    public static Rational operator +(Rational a, Rational b) => Of(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
    public static Rational operator -(Rational a, Rational b) => Of(a.Num * b.Den - b.Num * a.Den, a.Den * b.Den);
    public int CompareTo(Rational other) => (Num * other.Den).CompareTo(other.Num * Den);
    public static bool operator <=(Rational a, Rational b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Rational a, Rational b) => a.CompareTo(b) >= 0;
    public static bool operator <(Rational a, Rational b) => a.CompareTo(b) < 0;
    public static bool operator >(Rational a, Rational b) => a.CompareTo(b) > 0;
    public Rational Abs() => Num < 0 ? new(-Num, Den) : this;
    public double ToDouble() => (double)Num / (double)Den;
    public override string ToString() => ToDouble().ToString("0.#########", CultureInfo.InvariantCulture) + "s";
}

/// <summary>A generated frame: the number encoded in its pixels and its intended source
/// time (seconds from the file's format start_time), computed from the generation recipe
/// and ffprobe's stream start — never from the decoder under test.</summary>
internal readonly record struct IdealFrame(long Number, Rational Time);

internal sealed record MediaFile(string Name, string Path, MediaMetadata Metadata, IReadOnlyList<IdealFrame> Frames);

/// <summary>Generates the test videos once per test run (ffmpeg + lavfi). Each frame's
/// number is written as 16 black/white bit columns (bit k = column k, 16 px wide at
/// 256 px), which survives lossy H.264/H.265 encoding and scaling.</summary>
public sealed class TestMedia : IDisposable
{
    private const string Bits = @"geq=lum='if(mod(floor(N/pow(2\,floor(X/16)))\,2)\,235\,16)':cb=128:cr=128";
    private const string H264 = "-c:v libx264 -preset veryfast -crf 16 -pix_fmt yuv420p";

    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aive-video-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, MediaFile> _files = new();
    private readonly object _lock = new();

    public TestMedia() => Directory.CreateDirectory(_dir);

    internal MediaFile Get(string name)
    {
        lock (_lock)
        {
            if (!_files.TryGetValue(name, out var file))
                _files[name] = file = Create(name);
            return file;
        }
    }

    private MediaFile Create(string name)
    {
        var path = System.IO.Path.Combine(_dir, name);
        Func<long, Rational?> recipe; // frame number → intended time from the video stream start, null = frame absent
        long count;

        switch (name)
        {
            case "cfr25.mp4": (recipe, count) = Cfr(25, 1, 12); Encode(path, Src("25", 12), H264); break;
            case "cfr24.mp4": (recipe, count) = Cfr(24, 1, 10); Encode(path, Src("24", 10), H264); break;
            case "cfr2997.mp4": (recipe, count) = Cfr(30000, 1001, 10); Encode(path, Src("30000/1001", 10), H264); break;
            case "cfr5994.mp4": (recipe, count) = Cfr(60000, 1001, 6); Encode(path, Src("60000/1001", 6), H264); break;
            case "hd2997.mp4":
                // 1920×1080 (the bit columns scaled up, so ReadNumber still finds them): larger than the Preview's decode limit.
                (recipe, count) = Cfr(30000, 1001, 4); Encode(path, Src("30000/1001", 4) + ",scale=1920:1080:flags=neighbor", H264); break;
            case "hevc2997.mp4":
                (recipe, count) = Cfr(30000, 1001, 4);
                Encode(path, Src("30000/1001", 4), "-c:v libx265 -preset veryfast -crf 16 -pix_fmt yuv420p -x265-params log-level=error");
                break;
            case "vfr.mkv":
                // 25 fps base, odd frames 50..99 dropped; original timestamps kept.
                (recipe, count) = Cfr(25, 1, 10);
                recipe = Where(recipe, n => !(n is >= 50 and <= 99 && n % 2 == 1));
                Encode(path, Src("25", 10) + @",select='not(between(n\,50\,99)*mod(n\,2))'", H264 + " -fps_mode vfr");
                break;
            case "sparse.mkv":
                // Frames 0..49 (0–1.96 s), then nothing until frame 200 (8.0 s).
                (recipe, count) = Cfr(25, 1, 10);
                recipe = Where(recipe, n => n < 50 || n >= 200);
                Encode(path, Src("25", 10) + @",select='lt(n\,50)+gte(n\,200)'", H264 + " -fps_mode vfr");
                break;
            case "jitter.mp4":
                // Deterministic jitter of up to ±0.35 frame around a 25 fps grid.
                count = 200;
                recipe = n => Rational.FromDouble((n + 0.35 * Math.Sin(1.7 * n)) / 25.0);
                // settb/enc_time_base keep the jittered PTS; the default encoder time base 1/25 would round them away.
                Encode(path, Src("25", 8) + ",settb=1/90000,setpts='(N+0.35*sin(1.7*N))/(25*TB)'",
                    H264 + " -fps_mode passthrough -enc_time_base 1/90000 -video_track_timescale 90000");
                break;
            case "offset.ts":
                // MPEG-TS (start_time ≈ 1.4 s) with audio; video delayed 0.3 s against audio,
                // so format start_time (audio) ≠ video stream start. (-itsoffset, because ffmpeg
                // re-bases each input to its own start and would undo a setpts shift.)
                (recipe, count) = Cfr(25, 1, 10);
                Encode(path, Src("25", 10), H264 + " -c:a aac", "-f lavfi -i sine=f=440:d=10", "-itsoffset 0.3");
                break;
            case "video.ts":
                (recipe, count) = Cfr(25, 1, 8);
                Encode(path, Src("25", 8), H264);
                break;
            case "longaudio.mp4":
                // 4 s of video, 6 s of audio: the container runs past the last video frame.
                (recipe, count) = Cfr(25, 1, 4);
                Encode(path, Src("25", 4), H264 + " -c:a aac", "-f lavfi -i sine=f=440:d=6");
                break;
            case "avsync.mp4":
                // Video frame numbers at 25 fps + a mono AAC click at 2.02 s (sample 96960),
                // i.e. in the middle of video frame 50 (2.00–2.04 s).
                (recipe, count) = Cfr(25, 1, 4);
                Encode(path, Src("25", 4), H264 + " -c:a aac -b:a 192k",
                    @"-f lavfi -i aevalsrc=exprs='if(eq(n\,96960)\,0.9\,0)':s=48000:d=4");
                break;
            default:
                throw new ArgumentException($"Unknown test media '{name}'.");
        }

        var metadata = Analyze(path);
        var formatStart = Rational.FromTicks((metadata.StartTime ?? MediaTime.Zero).Ticks);
        var videoStart = ProbeVideoStart(path);
        var frames = new List<IdealFrame>();
        for (long n = 0; n < count; n++)
        {
            if (recipe(n) is { } t)
                frames.Add(new IdealFrame(n, videoStart - formatStart + t));
        }
        return new MediaFile(name, path, metadata, frames);
    }

    private static string Src(string rate, int seconds) => $"nullsrc=s=256x144:r={rate}:d={seconds},{Bits}";

    private static (Func<long, Rational?>, long) Cfr(int num, int den, int seconds) =>
        (n => Rational.Of(n * den, num), ((long)seconds * num + den - 1) / den); // nullsrc emits ceil(d·rate) frames

    private static Func<long, Rational?> Where(Func<long, Rational?> recipe, Func<long, bool> present) =>
        n => present(n) ? recipe(n) : null;

    private static void Encode(string path, string videoGraph, string codecArgs, string? extraInput = null, string? videoInputOptions = null)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (videoInputOptions is not null) args.AddRange(videoInputOptions.Split(' '));
        args.AddRange(new[] { "-f", "lavfi", "-i", videoGraph });
        if (extraInput is not null) args.AddRange(extraInput.Split(' '));
        args.AddRange(codecArgs.Split(' '));
        args.Add(path);
        Run(FfmpegTools.Ffmpeg!, args);
    }

    private static MediaMetadata Analyze(string path)
    {
        var options = Options.Create(new FfmpegOptions());
        var service = new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance),
            NullLogger<FfprobeMediaAnalysisService>.Instance);
        var result = service.AnalyzeAsync(path).GetAwaiter().GetResult();
        Assert.True(result.Metadata is not null, $"ffprobe analysis failed for {path}: {result.ErrorMessage}");
        return result.Metadata!;
    }

    /// <summary>Exact start of the first video stream (start_pts · time_base) via ffprobe.</summary>
    private static Rational ProbeVideoStart(string path)
    {
        var output = Run(FfmpegTools.Ffprobe!, new[]
        {
            "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=start_pts,time_base", "-of", "default=nw=1", path
        });
        var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=')).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]); // MPEG-TS repeats the stream under PROGRAM
        var tb = values["time_base"].Split('/');
        return Rational.Of(BigInteger.Parse(values["start_pts"]) * BigInteger.Parse(tb[0]), BigInteger.Parse(tb[1]));
    }

    internal static string Run(string exe, IEnumerable<string> args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            }
        };
        foreach (var a in args) process.StartInfo.ArgumentList.Add(a);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000)) { process.Kill(true); throw new TimeoutException($"{exe} timed out."); }
        if (process.ExitCode != 0) throw new InvalidOperationException($"{exe} failed: {stderr.Result}");
        return stdout.Result;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[CollectionDefinition(Name)]
public sealed class MediaCollection : ICollectionFixture<TestMedia>
{
    public const string Name = "Generated media";
}
