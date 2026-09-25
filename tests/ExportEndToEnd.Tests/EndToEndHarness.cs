using System.Diagnostics;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Export;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Rendering.Tests;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.Video;
using AiVideoEditor.Video.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>Every end-to-end test runs in this collection: one after another (the process-wide ffmpeg counter is
/// asserted), with Avalonia initialised (the export's rasterizer) and the generated media.</summary>
[CollectionDefinition(Name)]
public sealed class EndToEndCollection : ICollectionFixture<AvaloniaFixture>, ICollectionFixture<E2EMedia>
{
    public const string Name = "Export end to end";
}

/// <summary>Source media generated once per run with ffmpeg + lavfi (320 × 180, lossless-ish H.264 / PCM).</summary>
public sealed class E2EMedia : IDisposable
{
    public const int Width = 320;
    public const int Height = 180;

    private readonly Dictionary<string, MediaAsset> _assets = new();

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "aive-export-e2e", Guid.NewGuid().ToString("N"));

    public E2EMedia() => Directory.CreateDirectory(Folder);

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A new empty folder for one export's output.</summary>
    public string OutputFolder()
    {
        var folder = Path.Combine(Folder, "out-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string R(FrameRate rate) => $"{rate.Numerator}/{rate.Denominator}";

    /// <summary>A solid-colour video, 4 s.</summary>
    public MediaAsset Solid(string rgb, FrameRate rate) =>
        Get($"solid-{rgb}-{rate.Numerator}-{rate.Denominator}.mp4", MediaKind.Video, path => Ffmpeg(
            "-f", "lavfi", "-i", $"color=c=0x{rgb}:s={Width}x{Height}:r={R(rate)}:d=4",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "4", "-pix_fmt", "yuv420p", path));

    /// <summary>ffmpeg's test pattern (colours, gradients, moving parts), 25 fps, 4 s, no audio.</summary>
    public MediaAsset Pattern() => Pattern(FrameRate.Fps25);

    /// <summary>ffmpeg's test pattern at <paramref name="rate"/> (exact rational rate, constant), <paramref name="width"/> ×
    /// <paramref name="height"/>, <paramref name="seconds"/> long, H.264 yuv420p, no audio. Every frame differs (moving parts and
    /// the frame counter in the top-left corner), so a wrongly selected source frame is visible — also at other speeds.</summary>
    public MediaAsset Pattern(FrameRate rate, int width = Width, int height = Height, int seconds = 4, int crf = 4) =>
        Get($"pattern-{width}x{height}-{rate.Numerator}-{rate.Denominator}-{seconds}s-crf{crf}.mp4", MediaKind.Video, path => Ffmpeg(
            "-f", "lavfi", "-i", $"testsrc2=s={width}x{height}:r={R(rate)}:d={seconds}",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", path));

    /// <summary>A 1920 × 1080 test pattern, 25 fps, 1 s: larger than the Preview's 1280 × 720 decode limit (the Preview decodes
    /// it at 1280 × 720, the export at 1920 × 1080).</summary>
    public MediaAsset Pattern1080() => Pattern(FrameRate.Fps25, 1920, 1080, seconds: 1, crf: 12);

    /// <summary>A 3840 × 2160 (4K UHD) test pattern, 25 fps, 1 s: larger than the Preview's 1280 × 720 decode limit.</summary>
    public MediaAsset Pattern4K() => Pattern(FrameRate.Fps25, 3840, 2160, seconds: 1, crf: 18);

    /// <summary>A 320 × 180 RGBA PNG still: the test pattern's colours with straight alpha in three vertical bands —
    /// x &lt; 107 opaque (255), 107 ≤ x &lt; 214 half (128), x ≥ 214 transparent (0).</summary>
    public MediaAsset PngAlpha() =>
        Get("alpha-bands.png", MediaKind.Image, path => Ffmpeg(
            "-f", "lavfi", "-i", $"testsrc2=s={Width}x{Height}:r=25:d=1,format=rgba," +
                                 "geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='if(lt(X,107),255,if(lt(X,214),128,0))'",
            "-frames:v", "1", "-c:v", "png", "-pix_fmt", "rgba", path));

    /// <summary>A 320 × 180 JPEG still of the test pattern (baseline, quality 2, no EXIF orientation).</summary>
    public MediaAsset Jpeg() =>
        Get("still.jpg", MediaKind.Image, path => Ffmpeg(
            "-f", "lavfi", "-i", $"testsrc2=s={Width}x{Height}:r=25:d=1", "-frames:v", "1", "-q:v", "2", path));

    /// <summary>A 320 × 180 (coded) 25 fps test pattern, 4 s, whose display matrix turns it 90° clockwise — a phone portrait
    /// video: ffmpeg's <c>-display_rotation -90</c> (counter-clockwise angle), the app's <c>DisplayRotation</c> 90 (clockwise),
    /// shown and decoded as 180 × 320. Stream copy of <see cref="Pattern()"/>: only the matrix is added.</summary>
    public MediaAsset Rotated90()
    {
        var plain = Pattern().FilePath;
        return Get("pattern-cw90.mp4", MediaKind.Video, path => Ffmpeg(
            "-display_rotation", "-90", "-i", plain, "-c", "copy", path));
    }

    /// <summary>A/V sync source at <paramref name="rate"/>: black video with a white frame at every frame n ≡ 15 (mod 30)
    /// and, starting exactly at each of those frames' times, a 40 ms 1 kHz cosine burst (mono PCM), 4 s.</summary>
    public MediaAsset Sync(FrameRate rate)
    {
        var t0 = $"(15*{rate.Denominator}/{rate.Numerator})";
        var period = $"(30*{rate.Denominator}/{rate.Numerator})";
        return Get($"sync-{rate.Numerator}-{rate.Denominator}.mkv", MediaKind.Video, path => Ffmpeg(
            "-f", "lavfi", "-i", $"color=c=black:s={Width}x{Height}:r={R(rate)}:d=4,drawbox=x=0:y=0:w=iw:h=ih:color=white:t=fill:enable='eq(mod(n,30),15)'",
            "-f", "lavfi", "-i", $"aevalsrc=0.8*cos(2*PI*1000*mod(t-{t0}\\,{period}))*lt(mod(t-{t0}\\,{period})\\,0.04)*gte(t\\,{t0}):s=48000:d=4",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "4", "-pix_fmt", "yuv420p", "-c:a", "pcm_s16le", "-shortest", path));
    }

    /// <summary>A 440 Hz sine at 0.25 on both channels, 6 s, PCM.</summary>
    public MediaAsset Tone() =>
        Get("tone.wav", MediaKind.Audio, path => Ffmpeg(
            "-f", "lavfi", "-i", "aevalsrc=0.25*sin(2*PI*440*t)|0.25*sin(2*PI*440*t):s=48000:d=6", "-c:a", "pcm_s16le", path));

    /// <summary>A 40 ms 1 kHz cosine burst at 0.8 every 0.5 s (from 0), 6 s, stereo PCM.</summary>
    public MediaAsset Bursts() =>
        Get("bursts.wav", MediaKind.Audio, path => Ffmpeg(
            "-f", "lavfi", "-i", @"aevalsrc=0.8*cos(2*PI*1000*mod(t\,0.5))*lt(mod(t\,0.5)\,0.04)|0.8*cos(2*PI*1000*mod(t\,0.5))*lt(mod(t\,0.5)\,0.04):s=48000:d=6",
            "-c:a", "pcm_s16le", path));

    private MediaAsset Get(string name, MediaKind kind, Action<string> create)
    {
        lock (_assets)
        {
            if (_assets.TryGetValue(name, out var cached)) return Copy(cached);
            var path = Path.Combine(Folder, name);
            create(path);
            var asset = new MediaAsset { FilePath = path, Kind = kind, Metadata = Analyze(path), AnalysisStatus = MediaAnalysisStatus.Completed };
            _assets[name] = asset;
            return Copy(asset);
        }
    }

    private static MediaAsset Copy(MediaAsset a) =>
        new() { FilePath = a.FilePath, Kind = a.Kind, Metadata = a.Metadata, AnalysisStatus = a.AnalysisStatus };

    /// <summary>A private copy of <paramref name="asset"/>'s file (for tests that delete it).</summary>
    public MediaAsset CopyOf(MediaAsset asset)
    {
        var path = Path.Combine(Folder, Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(asset.FilePath));
        File.Copy(asset.FilePath, path);
        return new MediaAsset { FilePath = path, Kind = asset.Kind, Metadata = asset.Metadata, AnalysisStatus = asset.AnalysisStatus };
    }

    private static void Ffmpeg(params string[] args) =>
        EncoderHarness.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args).ToArray());

    private static MediaMetadata Analyze(string path)
    {
        var options = Options.Create(new FfmpegOptions());
        var service = new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance),
            NullLogger<FfprobeMediaAnalysisService>.Instance);
        var result = service.AnalyzeAsync(path).GetAwaiter().GetResult();
        Assert.True(result.Metadata is not null, $"ffprobe analysis failed for {path}: {result.ErrorMessage}");
        return result.Metadata!;
    }
}

/// <summary>
/// A test that needs ffmpeg and is too heavy for the regular suite (4K, long exports; Phase 8 Step 8 decision F): skipped
/// unless the environment variable <c>AIVE_HEAVY_TESTS</c> is <c>1</c>, e.g.
/// <c>$env:AIVE_HEAVY_TESTS=1; dotnet test tests/ExportEndToEnd.Tests</c>.
/// </summary>
public sealed class HeavyFfmpegFactAttribute : FactAttribute
{
    public HeavyFfmpegFactAttribute() => Skip = HeavyTests.SkipReason;
}

public sealed class HeavyFfmpegTheoryAttribute : TheoryAttribute
{
    public HeavyFfmpegTheoryAttribute() => Skip = HeavyTests.SkipReason;
}

public static class HeavyTests
{
    public const string Variable = "AIVE_HEAVY_TESTS";

    public static string? SkipReason =>
        FfmpegTools.SkipReason ?? (System.Environment.GetEnvironmentVariable(Variable) == "1" ? null : $"Heavy scenario: set {Variable}=1 to run.");
}

/// <summary>Builds a project the way the editor stores it (entities → <see cref="PlaybackSnapshotBuilder"/> in the preflight).</summary>
public sealed class ProjectBuilder
{
    public ProjectBuilder(FrameRate rate, int width = E2EMedia.Width, int height = E2EMedia.Height)
    {
        Rate = rate;
        Project = new Project { Settings = { FrameRate = rate, IsFrameRateLocked = true, FrameWidth = width, FrameHeight = height } };
    }

    public Project Project { get; }
    public FrameRate Rate { get; }

    public MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    public Track VideoTrack(bool hidden = false)
    {
        var track = new Track { Type = TrackType.Video, Name = $"V{Project.Timeline.VideoTracks.Count + 1}", Order = Project.Timeline.VideoTracks.Count, IsHidden = hidden };
        Project.Timeline.VideoTracks.Add(track);
        return track;
    }

    public Track AudioTrack(bool muted = false)
    {
        var track = new Track { Type = TrackType.Audio, Name = $"A{Project.Timeline.AudioTracks.Count + 1}", Order = Project.Timeline.AudioTracks.Count, IsMuted = muted };
        Project.Timeline.AudioTracks.Add(track);
        return track;
    }

    private MediaAsset Add(MediaAsset asset)
    {
        var existing = Project.MediaAssets.FirstOrDefault(a => a.FilePath == asset.FilePath);
        if (existing is not null) return existing;
        Project.MediaAssets.Add(asset);
        return asset;
    }

    public VideoClip Video(Track track, MediaAsset asset, long start, long end, long sourceInFrame = 0, ClipSpeed speed = default)
    {
        asset = Add(asset);
        var sourceIn = F(sourceInFrame);
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(end) - F(start), Speed = speed,
            SourceIn = sourceIn, SourceOut = sourceIn + SpeedTiming.SourceLength(end - start, speed, Rate)
        };
        track.Clips.Add(clip);
        return clip;
    }

    public AudioClip Audio(Track track, MediaAsset asset, long start, long end, long sourceInFrame = 0, ClipSpeed speed = default)
    {
        asset = Add(asset);
        var sourceIn = F(sourceInFrame);
        var clip = new AudioClip
        {
            MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(end) - F(start), Speed = speed,
            SourceIn = sourceIn, SourceOut = sourceIn + SpeedTiming.SourceLength(end - start, speed, Rate)
        };
        track.Clips.Add(clip);
        return clip;
    }

    /// <summary>An image clip over [<paramref name="start"/>, <paramref name="end"/>) as the editor adds one (no source timing:
    /// SourceIn 0, SourceOut = duration).</summary>
    public ImageClip Image(Track track, MediaAsset asset, long start, long end)
    {
        asset = Add(asset);
        var clip = new ImageClip { MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(end) - F(start), SourceIn = MediaTime.Zero, SourceOut = F(end) - F(start) };
        track.Clips.Add(clip);
        return clip;
    }

    public TextClip Text(Track track, string text, long start, long end)
    {
        var clip = new TextClip { TimelineStart = F(start), Duration = F(end) - F(start), Text = text };
        track.Clips.Add(clip);
        return clip;
    }
}

/// <summary>One finished export: the MP4 and what the service handed to the encoder (canvases and PCM).</summary>
public sealed record ExportRun(string Path, ExportJob Job, IReadOnlyList<byte[]> Canvases, float[] Pcm, IReadOnlyList<ExportProgress> Progress)
{
    public ExportOutput Output => Job.Output;
}

public static class EndToEnd
{
    public static readonly ExportPreflightEnvironment Environment = ExportPreflightEnvironment.Default(true, _ => true);

    public static ExportJob Preflight(Project project, string outputPath)
    {
        var result = ExportPreflight.Check(project, outputPath, Environment);
        Assert.True(result.CanExport, string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Job!;
    }

    public static ExportService Service(IExportEncoder encoder, IFfmpegLocator? locator = null)
    {
        locator ??= FfmpegTools.FfmpegLocator;
        return new ExportService(locator,
            new FfmpegVideoDecoder(locator, NullLogger<FfmpegVideoDecoder>.Instance),
            new FfmpegAudioDecoder(locator, NullLogger<FfmpegAudioDecoder>.Instance),
            encoder, () => new AvaloniaCompositionRasterizer(), NullLogger<ExportService>.Instance);
    }

    /// <summary>Preflight → <see cref="ExportService"/> (real decoders, Avalonia rasterizer, ffmpeg encoder) → MP4.</summary>
    public static async Task<ExportRun> ExportProject(Project project, string outputPath)
    {
        var job = Preflight(project, outputPath);
        var encoder = new RecordingEncoder(EncoderHarness.Encoder());
        var progress = new List<ExportProgress>();
        await Service(encoder).ExportAsync(job, new SyncProgress(progress.Add));
        return new ExportRun(outputPath, job, encoder.Canvases, encoder.Pcm.ToArray(), progress);
    }

    /// <summary>Every check a finished export must pass: an MP4 that opens, H.264 at the canvas size and the exact rate
    /// with every frame, AAC-LC 48 kHz stereo with exactly the output's samples, the expected duration; nothing but
    /// the MP4 in its folder, no ffmpeg process left.</summary>
    public static void AssertValidMp4(ExportRun run)
    {
        var o = run.Output;
        var video = EncoderHarness.Stream(run.Path, "video");
        Assert.Equal("h264", video.Str("codec_name"));
        Assert.Equal((o.Size.Width.ToString(), o.Size.Height.ToString()), (video.Str("width"), video.Str("height")));
        Assert.Equal($"{o.FrameRate.Numerator}/{o.FrameRate.Denominator}", video.Str("r_frame_rate"));
        Assert.Equal(o.FrameCount.ToString(), video.Str("nb_frames"));
        Assert.Equal("yuv420p", video.Str("pix_fmt"));

        var audio = EncoderHarness.Stream(run.Path, "audio");
        Assert.Equal(("aac", "LC", "48000", "2"), (audio.Str("codec_name"), audio.Str("profile"), audio.Str("sample_rate"), audio.Str("channels")));

        var duration = double.Parse(EncoderHarness.Probe(run.Path, "-show_format").GetProperty("format").Str("duration"), CultureInfo.InvariantCulture);
        Assert.InRange(duration - o.Duration.TotalSeconds, -0.0011, 0.0011);                // µs printing + ≤ 1 audio sample

        Assert.Equal(o.AudioSampleCount, EncoderHarness.DecodeLeft(run.Path).Length);
        Assert.Equal(o.FrameCount, run.Canvases.Count);
        Assert.Equal(o.AudioSampleCount * 2, run.Pcm.Length);
        Assert.Equal(new[] { run.Path }, Directory.GetFiles(Path.GetDirectoryName(run.Path)!));
        AssertNoFfmpegLeft();
    }

    public static void AssertNoFfmpegLeft()
    {
        var watch = Stopwatch.StartNew();
        while (FfmpegProcess.LiveProcesses != 0 && watch.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(10);
        Assert.Equal(0, FfmpegProcess.LiveProcesses);
    }

    /// <summary>The decoded MP4 frames against the canvases the service encoded: mean |Δ| over R, G, B of each frame.</summary>
    public static double[] FrameErrors(ExportRun run)
    {
        var decoded = EncoderHarness.DecodeFrames(run.Path, run.Output.Size.Width, run.Output.Size.Height);
        Assert.Equal(run.Canvases.Count, decoded.Count);
        return decoded.Select((frame, n) => MeanAbs(frame, run.Canvases[n])).ToArray();
    }

    public static double MeanAbs(byte[] a, byte[] b)
    {
        long sum = 0, count = 0;
        for (var i = 0; i < a.Length; i += 4)
        for (var c = 0; c < 3; c++) { sum += Math.Abs(a[i + c] - b[i + c]); count++; }
        return (double)sum / count;
    }

    public static (byte R, byte G, byte B) Pixel(byte[] bgra, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    /// <summary>Left channel of the PCM the service encoded.</summary>
    public static float[] Left(float[] interleaved) => Enumerable.Range(0, interleaved.Length / 2).Select(i => interleaved[2 * i]).ToArray();

    public static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var v in x) sum += (double)v * v;
        return x.Length == 0 ? 0 : Math.Sqrt(sum / x.Length);
    }

    /// <summary>The Preview's picture of timeline frame <paramref name="n"/>: its own pipeline (seeked there, software
    /// decoding, ≤ 1280 × 720) and its own control (<c>CompositionView</c>) rendered at the canvas size, or laid out at a
    /// <paramref name="viewportWidth"/> × <paramref name="viewportHeight"/> control (the canvas "contained" in it).</summary>
    public static async Task<Image> Preview(PlaybackSnapshot snapshot, long n, int? viewportWidth = null, int? viewportHeight = null)
    {
        var decoder = new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance);
        await using var pipeline = new VideoPipeline(snapshot, 1, n, decoder,
            new PlaybackSettings { Hardware = HardwareDecoding.Disabled }, NullLogger.Instance);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var layers = pipeline.GetFrame(n).Layers;
            Assert.DoesNotContain(layers, l => l.IsPlaceholder);
            if (layers.All(l => l.State == LayerPictureState.Text || l is { State: LayerPictureState.Frame, IsCurrent: true }))
                return Render.Preview(snapshot.Canvas, viewportWidth ?? snapshot.Canvas.Width, viewportHeight ?? snapshot.Canvas.Height, layers);
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException($"preview frame {n}");
            await Task.Delay(2);
        }
    }

    public sealed class SyncProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value)
        {
            lock (this) report(value);
        }
    }
}

/// <summary>The real encoder, recording what it is given (a copy of every canvas, the PCM).</summary>
public sealed class RecordingEncoder(IExportEncoder inner) : IExportEncoder
{
    public List<byte[]> Canvases { get; } = new();
    public List<float> Pcm { get; } = new();

    public async Task<IExportEncoding> StartAsync(ExportOutput output, string destinationPath, CancellationToken ct = default) =>
        new Encoding(this, output, await inner.StartAsync(output, destinationPath, ct));

    private sealed class Encoding(RecordingEncoder owner, ExportOutput output, IExportEncoding inner) : IExportEncoding
    {
        public ValueTask WriteAudioAsync(ReadOnlyMemory<float> interleaved, CancellationToken ct = default)
        {
            owner.Pcm.AddRange(interleaved.ToArray());
            return inner.WriteAudioAsync(interleaved, ct);
        }

        public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> bgra, int stride, CancellationToken ct = default)
        {
            Assert.Equal(output.Size.Width * 4, stride);
            owner.Canvases.Add(bgra.ToArray());
            return inner.WriteFrameAsync(bgra, stride, ct);
        }

        public Task CompleteAsync(CancellationToken ct = default) => inner.CompleteAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
