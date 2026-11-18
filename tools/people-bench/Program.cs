// D061: the people analysis of the product on real clips (see README.md).
//
//   analyze <video> <out dir> [masks]   — PeopleAnalyzer.DetectAsync (and SegmentAsync with "masks") with the app's decoder and
//                                         the installed models; writes <name>.tracks.json / <name>.masks.json / <name>.summary.json
//   follow <out dir> <name> <duration s> <canvas WxH> <scale> [<seed s> <x> <y>] [soft|normal|fast]
//                                       — Follow on the analysed tracks (from analyze): the trajectory, the camera path and its
//                                         metrics → <name>.follow.json
//   decode <video> [hw]                 — the app's decoder alone at the analysis size: frames per second
//   samcheck <video> <start s> <seconds> <rate Hz> <x0,y0,x1,y1> <out.bin>
//                                       — the C# video segmenter alone on one person (the box on the first frame), every frame's
//                                         coverage on the grid → <out.bin> (for compare_sam.py against the PyTorch predictor)
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Video;
using AiVideoEditor.Vision.People;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: analyze <video> <out dir> [masks] | samcheck <video> <start> <seconds> <rate> <box> <out.bin>");
    return 2;
}
double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
var models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoulderVideoEditor", "models", PeopleModel.FolderName);
var locator = new FfmpegLocator(Options.Create(new FfmpegOptions()), NullLogger<FfmpegLocator>.Instance);
var decoder = new FfmpegVideoDecoder(locator, NullLogger<FfmpegVideoDecoder>.Instance);
using var vision = new OnnxPeopleVision(models);

switch (args[0])
{
    case "analyze":
    {
        var (video, outDir, withMasks) = (args[1], args[2], args.Length > 3 && args[3] == "masks");
        Directory.CreateDirectory(outDir);
        var name = Path.GetFileNameWithoutExtension(video);
        var (start, fileDuration) = Probe(video);
        var duration = fileDuration;
        var source = new PeopleAnalyzer.Source(video, start, null, null, fileDuration);
        var analyzer = new PeopleAnalyzer(decoder, vision);
        var watch = Stopwatch.StartNew();
        // PEOPLE_BENCH_RANGE=from:to (seconds): the tracks of [from, to) and the masks of [from + 1, to - 1) only, in chunks,
        // as the app analyses a followed part (D061 amendment 1); prints when the first chunk was ready.
        var range = Environment.GetEnvironmentVariable("PEOPLE_BENCH_RANGE")?.Split(':').Select(v => MediaTime.FromSeconds(double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        double? firstChunk = null;
        var tracks = range is null
            ? await analyzer.DetectAsync(source, null, CancellationToken.None)
            : await analyzer.DetectRangeAsync(source, range[0], range[1], (_, _) => firstChunk ??= watch.Elapsed.TotalSeconds, null, CancellationToken.None);
        var detectSeconds = watch.Elapsed.TotalSeconds;
        if (range is not null) duration = range[1] - range[0];
        if (firstChunk is { } first) Console.WriteLine($"{name}: first chunk of tracks ready after {first:F1} s");
        Console.WriteLine($"{name}: detection {tracks.Samples.Length} frames, {tracks.Tracks.Length} tracks, {detectSeconds:F1} s ({duration.TotalSeconds / detectSeconds:F2}x realtime, {vision.DetectorDevice})");
        var store = new AiVideoEditor.Media.Caching.PeopleAnalysisCacheStore(Path.Combine(outDir, "cache"),
            NullLogger<AiVideoEditor.Media.Caching.PeopleAnalysisCacheStore>.Instance);
        store.SaveTracks(name, tracks);
        await File.WriteAllTextAsync(Path.Combine(outDir, name + ".tracks.json"), JsonSerializer.Serialize(new
        {
            interval = tracks.Interval.TotalSeconds,
            samples = tracks.Samples.Select(s => new
            {
                t = s.Time.TotalSeconds,
                people = s.People.Select((p, i) => new
                {
                    id = s.TrackIdOf(i), score = p.Score, box = new[] { p.Box.X0, p.Box.Y0, p.Box.X1, p.Box.Y1 },
                    torso = p.Torso is { } t ? new[] { t.X, t.Y } : null
                })
            }),
            tracks = tracks.Tracks.Select(t => new { t.Id, start = t.Start.TotalSeconds, end = t.End.TotalSeconds, t.Count })
        }));
        double? maskSeconds = null;
        if (withMasks)
        {
            watch.Restart();
            var masks = range is null
                ? await analyzer.SegmentAsync(source, tracks, null, CancellationToken.None)
                : await analyzer.SegmentRangeAsync(source, tracks, range[0] + MediaTime.FromSeconds(1), range[1] - MediaTime.FromSeconds(1), null, null, CancellationToken.None);
            if (range is not null) duration = range[1] - range[0] - MediaTime.FromSeconds(2);
            maskSeconds = watch.Elapsed.TotalSeconds;
            store.SaveMasks(name, masks);
            Console.WriteLine($"{name}: masks {masks.Samples.Length} frames, {masks.PersonIds.Count()} people, {maskSeconds:F1} s ({duration.TotalSeconds / maskSeconds:F2}x realtime)");
            await File.WriteAllTextAsync(Path.Combine(outDir, name + ".masks.json"), JsonSerializer.Serialize(new
            {
                width = masks.Width, height = masks.Height, interval = masks.Interval.TotalSeconds,
                samples = masks.Samples.Select(s => new
                {
                    t = s.Time.TotalSeconds,
                    people = s.People.Select(p => new { id = p.PersonId, p.X, p.Y, w = p.Width, h = p.Height, data = Convert.ToBase64String(p.Deflated) })
                })
            }));
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, name + ".summary.json"), JsonSerializer.Serialize(new
        {
            video = name, duration = duration.TotalSeconds, detection_s = detectSeconds, masks_s = maskSeconds,
            detection_frames = tracks.Samples.Length, tracks = tracks.Tracks.Length, device = vision.DetectorDevice.ToString(),
            peak_working_set_mb = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0
        }));
        return 0;
    }
    case "follow":
    {
        var (outDir, name, duration) = (args[1], args[2], MediaTime.FromSeconds(D(args[3])));
        var size = args[4].Split('x').Select(int.Parse).ToArray();
        var canvas = new AiVideoEditor.Core.Composition.FrameSize(size[0], size[1]);
        var scale = D(args[5]);
        var store = new AiVideoEditor.Media.Caching.PeopleAnalysisCacheStore(Path.Combine(outDir, "cache"),
            NullLogger<AiVideoEditor.Media.Caching.PeopleAnalysisCacheStore>.Instance);
        var tracks = store.TryLoadTracks(name) ?? throw new InvalidOperationException("analyze first");
        var smoothness = args[^1] switch { "soft" => FollowSmoothness.Soft, "fast" => FollowSmoothness.Fast, _ => FollowSmoothness.Normal };
        var follow = args.Length >= 9
            ? FollowSettings.Whole(smoothness, MediaTime.FromSeconds(D(args[6])), new NormalizedPoint(D(args[7]), D(args[8])))
            : FollowSettings.Whole(smoothness);
        // The display size of the analysed video (default: the portrait test videos); PEOPLE_BENCH_SOURCE=WxH for another.
        var sourceSize = (Environment.GetEnvironmentVariable("PEOPLE_BENCH_SOURCE") ?? "2160x3840").Split('x').Select(int.Parse).ToArray();
        var source = new AiVideoEditor.Core.Composition.FrameSize(sourceSize[0], sourceSize[1]);
        var clip = new VideoClip { MediaAssetId = Guid.Empty, Scale = scale, SourceIn = MediaTime.Zero, SourceOut = duration, Duration = duration, Follow = follow };
        var resolution = FollowResolution.For(clip, follow, tracks, canvas, source);
        var path = resolution.Path;
        var points = resolution.Trajectory?.Points ?? System.Collections.Immutable.ImmutableArray<FollowPoint>.Empty;
        var visual = VisualProperties.Of(clip)!.Value;
        // Per analysed frame: the person's anchor, the camera's position, the anchor's offset from the canvas centre.
        var rows = points.Select(p =>
        {
            var (x, y) = path?.At(p.Time) ?? (0, 0);
            double? ox = null, oy = null;
            if (path is not null && p.Anchor is { } a)
            {
                var g = AiVideoEditor.Core.Composition.CompositionMath.Layout(canvas, source, visual with { PositionX = x, PositionY = y });
                var q = g.Transform.Apply(new AiVideoEditor.Core.Composition.PointD(a.X * source.Width, a.Y * source.Height));
                (ox, oy) = ((q.X - canvas.Width / 2.0) / canvas.Width, (q.Y - canvas.Height / 2.0) / canvas.Height);
            }
            return new { t = p.Time.TotalSeconds, seen = p.IsSeen, anchor = p.Anchor is { } an ? new[] { an.X, an.Y } : null,
                box = p.Box is { } b ? new[] { b.X0, b.Y0, b.X1, b.Y1 } : null, x, y, ox, oy };
        }).ToList();
        // Smoothness: the camera's speed and acceleration (canvas px / s, px / s²) and its jitter (the RMS of the second
        // difference, px per frame²); centring: the anchor's offset from the centre while seen (canvas fractions).
        var dt = tracks.Interval.TotalSeconds;
        var speed = rows.Zip(rows.Skip(1), (a, b) => Math.Sqrt(Math.Pow(b.x - a.x, 2) + Math.Pow(b.y - a.y, 2)) / dt).ToList();
        var accel = speed.Zip(speed.Skip(1), (a, b) => Math.Abs(b - a) / dt).ToList();
        var second = rows.Skip(2).Select((r, i) => Math.Sqrt(Math.Pow(r.x - 2 * rows[i + 1].x + rows[i].x, 2) + Math.Pow(r.y - 2 * rows[i + 1].y + rows[i].y, 2))).ToList();
        var offsets = rows.Where(r => r.ox is not null).Select(r => Math.Sqrt(r.ox!.Value * r.ox.Value + r.oy!.Value * r.oy.Value)).OrderBy(v => v).ToList();
        double P(List<double> v, double q) => v.Count == 0 ? double.NaN : v.OrderBy(x => x).ElementAt(Math.Min(v.Count - 1, (int)(q * v.Count)));
        var summary = new
        {
            name, state = resolution.State.ToString(), unavailable = resolution.Unavailable.ToString(), seen = resolution.SeenFraction,
            chain = resolution.Trajectory?.TrackIds, gaps = resolution.Gaps.Select(g => new[] { g.Start.TotalSeconds, g.End.TotalSeconds }),
            longest_gap_s = resolution.Gaps.IsEmpty ? 0 : resolution.Gaps.Max(g => g.Duration.TotalSeconds),
            speed_p95 = P(speed, 0.95), speed_max = speed.DefaultIfEmpty().Max(), accel_p95 = P(accel, 0.95),
            jitter_rms = second.Count == 0 ? 0 : Math.Sqrt(second.Average(v => v * v)),
            centre_offset_median = P(offsets, 0.5), centre_offset_p95 = P(offsets, 0.95), moves_x = path?.MovesX, moves_y = path?.MovesY
        };
        Console.WriteLine(JsonSerializer.Serialize(summary, Lenient));
        await File.WriteAllTextAsync(Path.Combine(outDir, name + ".follow.json"), JsonSerializer.Serialize(new { summary, rows }, Lenient));
        return 0;
    }
    case "cachedump":
    {
        // The app's own cached analyses (%LOCALAPPDATA%\AiVideoEditor\cache\people): each file's key, then its masks / tracks
        // as JSON in <out dir> (<n>.masks.json / <n>.tracks.json), for people_sheet.py.
        var (folder, outDir) = (args[1], args[2]);
        Directory.CreateDirectory(outDir);
        var store = new AiVideoEditor.Media.Caching.PeopleAnalysisCacheStore(folder, NullLogger<AiVideoEditor.Media.Caching.PeopleAnalysisCacheStore>.Instance);
        var n = 0;
        foreach (var file in Directory.GetFiles(folder, "*.pp?").OrderBy(f => File.GetLastWriteTimeUtc(f)))
        {
            using var reader = new BinaryReader(File.OpenRead(file), System.Text.Encoding.UTF8);
            reader.ReadBytes(8);
            var key = reader.ReadString();
            reader.Dispose();
            n++;
            Console.WriteLine($"{n}: {Path.GetFileName(file)} {File.GetLastWriteTime(file):HH:mm:ss} key={key}");
            if (file.EndsWith(".ppm") && store.TryLoadMasks(key) is { } masks)
            {
                Console.WriteLine($"   masks {masks.Width}x{masks.Height}, {masks.Samples.Length} frames, people {string.Join(",", masks.PersonIds)}");
                await File.WriteAllTextAsync(Path.Combine(outDir, $"{n}.masks.json"), JsonSerializer.Serialize(new
                {
                    width = masks.Width, height = masks.Height, interval = masks.Interval.TotalSeconds,
                    samples = masks.Samples.Select(s => new
                    {
                        t = s.Time.TotalSeconds,
                        people = s.People.Select(p => new { id = p.PersonId, p.X, p.Y, w = p.Width, h = p.Height, data = Convert.ToBase64String(p.Deflated) })
                    })
                }));
            }
            else if (file.EndsWith(".ppt") && store.TryLoadTracks(key) is { } tracks)
            {
                Console.WriteLine($"   tracks {tracks.Samples.Length} frames, {tracks.Tracks.Length} tracks");
                await File.WriteAllTextAsync(Path.Combine(outDir, $"{n}.tracks.json"), JsonSerializer.Serialize(new
                {
                    interval = tracks.Interval.TotalSeconds,
                    samples = tracks.Samples.Select(s => new
                    {
                        t = s.Time.TotalSeconds,
                        people = s.People.Select((p, i) => new
                        {
                            id = s.TrackIdOf(i), score = p.Score, box = new[] { p.Box.X0, p.Box.Y0, p.Box.X1, p.Box.Y1 },
                            torso = p.Torso is { } t ? new[] { t.X, t.Y } : null
                        })
                    }),
                    tracks = tracks.Tracks.Select(t => new { t.Id, start = t.Start.TotalSeconds, end = t.End.TotalSeconds, t.Count })
                }));
            }
        }
        return 0;
    }
    case "decode":
    {
        var video = args[1];
        var (fileStart, duration) = Probe(video);
        var watch = Stopwatch.StartNew();
        var frames = 0;
        await using (var stream = await decoder.OpenAsync(new VideoDecodeRequest
        {
            FilePath = video, StartTime = fileStart, FirstSamplePoint = new SourceSamplePoint(0, 1),
            MaxWidth = PeopleAnalyzer.AnalysisLongSide, MaxHeight = PeopleAnalyzer.AnalysisLongSide,
            Hardware = args.Length > 2 && args[2] == "hw" ? HardwareDecoding.Auto : HardwareDecoding.Disabled
        }))
            while (await stream.ReadFrameAsync() is not null) frames++;
        Console.WriteLine($"decode: {frames} frames in {watch.Elapsed.TotalSeconds:F1} s = {frames / watch.Elapsed.TotalSeconds:F0} fps ({duration.TotalSeconds / watch.Elapsed.TotalSeconds:F2}x realtime)");
        return 0;
    }
    case "samcheck":
    {
        var (video, startS, seconds, rate) = (args[1], D(args[2]), D(args[3]), D(args[4]));
        var b = args[5].Split(',').Select(D).ToArray();
        var box = new NormalizedBox(b[0], b[1], b[2], b[3]);
        var (fileStart, _) = Probe(video);
        using var segmenter = vision.CreateSegmenter(CancellationToken.None);
        await using var stream = await decoder.OpenAsync(new VideoDecodeRequest
        {
            FilePath = video, StartTime = fileStart, FirstSamplePoint = new SourceSamplePoint(MediaTime.FromSeconds(startS).Ticks, 1),
            MaxWidth = PeopleAnalyzer.AnalysisLongSide, MaxHeight = PeopleAnalyzer.AnalysisLongSide, Hardware = HardwareDecoding.Disabled
        });
        await using var output = File.Create(args[6]);
        var interval = MediaTime.FromSeconds(1 / rate);
        long next = MediaTime.FromSeconds(startS).Ticks;
        var end = MediaTime.FromSeconds(startS + seconds);
        int frames = 0, gw = 0, gh = 0;
        var watch = Stopwatch.StartNew();
        while (await stream.ReadFrameAsync() is { } frame)
        {
            var t = CameraMotionMath.SourceTimeOf(frame.Timestamp, fileStart);
            if (t.Ticks + 5_000 < next) continue;
            if (t >= end) break;
            next += interval.Ticks;
            if (gw == 0)
            {
                var scale = (double)PeopleAnalyzer.MaskLongSide / Math.Max(frame.Width, frame.Height);
                (gw, gh) = ((int)Math.Round(frame.Width * scale), (int)Math.Round(frame.Height * scale));
                output.Write(BitConverter.GetBytes(gw));
                output.Write(BitConverter.GetBytes(gh));
            }
            var start = frames == 0 ? new[] { new SegmenterPrompt(1, box) } : Array.Empty<SegmenterPrompt>();
            var result = segmenter.Step(frame, start, Array.Empty<int>(), gw, gh, CancellationToken.None);
            output.Write(result[0].Coverage);
            frames++;
        }
        Console.WriteLine($"samcheck: {frames} frames {gw}x{gh}, {frames / watch.Elapsed.TotalSeconds:F2} fps");
        return 0;
    }
    default:
        Console.Error.WriteLine("unknown command");
        return 2;
}

static (MediaTime Start, MediaTime Duration) Probe(string video)
{
    var info = new ProcessStartInfo("ffprobe", $"-v error -show_entries format=start_time,duration -of csv=p=0 \"{video}\"")
    {
        RedirectStandardOutput = true, UseShellExecute = false
    };
    using var p = Process.Start(info)!;
    var parts = p.StandardOutput.ReadToEnd().Trim().Split(',');
    p.WaitForExit();
    return (MediaTime.FromSeconds(double.Parse(parts[0], CultureInfo.InvariantCulture)), MediaTime.FromSeconds(double.Parse(parts[1], CultureInfo.InvariantCulture)));
}

static partial class Program
{
    private static readonly JsonSerializerOptions Lenient = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
}
