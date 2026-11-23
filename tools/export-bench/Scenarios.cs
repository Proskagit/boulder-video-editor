using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;

namespace AiVideoEditor.ExportBench;

/// <summary>The benchmark scenarios (D059 E0; the research's §8 list): generated projects over the owner's test videos in
/// <c>VideosForTest</c>. Every scenario is a project built the way the editor stores it, so the preflight, the snapshot and
/// the export are the app's own.</summary>
internal sealed class Scenarios
{
    private readonly string _media;
    private readonly IMediaAnalysisService _probe;
    private readonly Dictionary<string, MediaAsset> _assets = new();

    public Scenarios(string mediaFolder, IMediaAnalysisService probe)
    {
        _media = mediaFolder;
        _probe = probe;
    }

    /// <summary>Name → (description, default frames).</summary>
    public static readonly IReadOnlyDictionary<string, (string Description, long Frames)> All = new Dictionary<string, (string, long)>
    {
        ["4k"] = ("4K60 H.264 SDR, one layer, 4K canvas", 600),
        ["4k-1080"] = ("4K60 source → 1920×1080 canvas", 600),
        ["4k30"] = ("4K60 source in a 30 fps project, 4K canvas (D076: the calibration's 30 fps reference)", 300),
        ["vert"] = ("4K60 source → vertical 1080×1920 (contain, black)", 600),
        ["vert-blur"] = ("4K60 source → vertical 1080×1920, blurred background", 600),
        ["vert4k-blur"] = ("4K60 source → vertical 2160×3840, blurred background", 600),
        ["4k120"] = ("4K120 source in a 60 fps project, 4K canvas", 600),
        ["overlays"] = ("4K + outlined text + «Боулдер» + heart rate + animated hold outlines", 600),
        ["vhs"] = ("4K + the VHS template (a treatment)", 600),
        ["tracks2"] = ("4K, two video layers (picture in picture)", 600),
        ["transitions"] = ("4K, three clips joined by a Dissolve and a Push", 600),
        ["hevc-vert"] = ("4K60 HEVC 10-bit HLG (vivo) → vertical 1080×1920 (HDR graph)", 600),
        ["long"] = ("4K60, the twelve 12×10 clips back to back, repeated (a long export)", 36000),
        ["real"] = ("A 12×10 training as the owner edits it: 12 clips with sound, «Боулдер» per route, titles, outlines", 7200),
    };

    private string File12(int i) => Path.Combine(_media, "12x10", i == 0 ? "1014.mp4" : $"1014({i}).mp4");

    private string Vivo => Path.Combine(_media, "vivo x300 pro - боулдеринг нежные цвета контраст.mp4");

    public async Task<Project> BuildAsync(string scenario, long frames)
    {
        var rate = new FrameRate(60, 1);
        switch (scenario)
        {
            case "4k": { var p = New(3840, 2160, rate); await Video(p, Track(p), File12(0), 0, frames); return p; }
            case "4k-1080": { var p = New(1920, 1080, rate); await Video(p, Track(p), File12(0), 0, frames); return p; }
            case "4k30": { var r30 = new FrameRate(30, 1); var p = New(3840, 2160, r30); await Video(p, Track(p), File12(2), 0, frames); return p; }
            case "vert": { var p = New(1080, 1920, rate); await Video(p, Track(p), File12(0), 0, frames); return p; }
            case "vert-blur":
            {
                var p = New(1080, 1920, rate);
                (await Video(p, Track(p), File12(0), 0, frames)).Background = ClipBackground.Blur;
                return p;
            }
            case "vert4k-blur":
            {
                var p = New(2160, 3840, rate);
                (await Video(p, Track(p), File12(0), 0, frames)).Background = ClipBackground.Blur;
                return p;
            }
            case "4k120": { var p = New(3840, 2160, rate); await Video(p, Track(p), File12(1), 0, frames); return p; }
            case "hevc-vert": { var p = New(1080, 1920, rate); await Video(p, Track(p), Vivo, 0, frames); return p; }
            case "overlays":
            {
                var p = New(3840, 2160, rate);
                var clip = await Video(p, Track(p), File12(0), 0, frames);
                clip.Routes = ImmutableArray.Create(Outlines(rate));
                await AttachTraining(p, clip);
                var v2 = Track(p);
                Template(p, v2, TemplateKind.Boulder, 0, frames).Route = "7";
                var v3 = Track(p);
                Template(p, v3, TemplateKind.HeartRate, 0, frames);
                var v4 = Track(p);
                var title = Text(p, v4, "Трасса 7 · 6B+", 0, frames);
                title.FontSize = 140; title.Bold = true; title.PositionY = -800;
                title.Outline = new TextOutline(true, "#000000", TextOutline.DefaultThickness);
                return p;
            }
            case "vhs":
            {
                var p = New(3840, 2160, rate);
                await Video(p, Track(p), File12(0), 0, frames);
                Template(p, Track(p), TemplateKind.Vhs, 0, frames);
                return p;
            }
            case "tracks2":
            {
                var p = New(3840, 2160, rate);
                await Video(p, Track(p), File12(0), 0, frames);
                var top = await Video(p, Track(p), File12(2), 0, frames);
                top.Scale = 0.45; top.PositionX = 900; top.PositionY = -450;
                return p;
            }
            case "transitions":
            {
                var p = New(3840, 2160, rate);
                var v1 = Track(p);
                var third = frames / 3;
                // Each transition needs source frames beyond its clips (D025 §4): the later clips start 30 frames in.
                var a = await Video(p, v1, File12(0), 0, third);
                var b = await Video(p, v1, File12(2), third, 2 * third, sourceIn: 30);
                var c = await Video(p, v1, File12(3), 2 * third, frames, sourceIn: 30);
                Join(p, v1, a, b, TransitionRules.CrossDissolve, 30);
                Join(p, v1, b, c, TransitionRules.Push, 30);
                return p;
            }
            case "long":
            {
                var p = New(3840, 2160, rate);
                var v1 = Track(p);
                long at = 0;
                for (var i = 0; at < frames; i = (i + 1) % 12)
                {
                    var length = Math.Min(Math.Min(await SourceFrames(File12(i), rate), 600), frames - at);
                    await Video(p, v1, File12(i), at, at + length);
                    at += length;
                }
                return p;
            }
            case "real":
            {
                var p = New(3840, 2160, rate);
                var v1 = Track(p);
                var a1 = AudioTrack(p);
                var v2 = Track(p);
                var v3 = Track(p);
                long at = 0;
                for (var i = 0; at < frames; i = (i + 1) % 12)
                {
                    var length = Math.Min(Math.Min(await SourceFrames(File12(i), rate), 600), frames - at);
                    var clip = await Video(p, v1, File12(i), at, at + length);
                    if (i % 3 == 0) clip.Routes = ImmutableArray.Create(Outlines(rate));
                    await Audio(p, a1, File12(i), at, at + length);
                    var boulder = Template(p, v2, TemplateKind.Boulder, at, at + length);
                    boulder.Route = (i + 1).ToString();
                    var title = Text(p, v3, $"Трасса {i + 1}", at, Math.Min(at + 120, at + length));
                    title.FontSize = 120; title.PositionY = -820;
                    title.Outline = new TextOutline(true, "#000000", TextOutline.DefaultThickness);
                    at += length;
                }
                return p;
            }
            default:
                throw new ArgumentException($"Unknown scenario '{scenario}'. Known: {string.Join(", ", All.Keys)}.");
        }
    }

    private static Project New(int width, int height, FrameRate rate) =>
        new() { Settings = { FrameRate = rate, IsFrameRateLocked = true, FrameWidth = width, FrameHeight = height } };

    private static Track Track(Project p)
    {
        var track = new Track { Type = TrackType.Video, Name = $"V{p.Timeline.VideoTracks.Count + 1}", Order = p.Timeline.VideoTracks.Count };
        p.Timeline.VideoTracks.Add(track);
        return track;
    }

    private static Track AudioTrack(Project p)
    {
        var track = new Track { Type = TrackType.Audio, Name = $"A{p.Timeline.AudioTracks.Count + 1}", Order = p.Timeline.AudioTracks.Count };
        p.Timeline.AudioTracks.Add(track);
        return track;
    }

    private async Task<MediaAsset> Asset(Project p, string path)
    {
        if (!_assets.TryGetValue(path, out var asset))
        {
            if (!File.Exists(path)) throw new FileNotFoundException("A benchmark source is missing.", path);
            var result = await _probe.AnalyzeAsync(path);
            if (result.Metadata is null) throw new InvalidOperationException($"ffprobe failed on '{path}': {result.ErrorMessage}");
            asset = new MediaAsset { FilePath = path, Kind = MediaKind.Video, Metadata = result.Metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
            _assets[path] = asset;
        }
        if (!p.MediaAssets.Contains(asset)) p.MediaAssets.Add(asset);
        return asset;
    }

    private async Task<long> SourceFrames(string path, FrameRate rate)
    {
        var asset = await Asset(New(2, 2, rate), path);
        return asset.Metadata!.Duration.ToFrameFloor(rate) - 1;
    }

    private async Task<VideoClip> Video(Project p, Track t, string path, long start, long end, long sourceIn = 0)
    {
        var asset = await Asset(p, path);
        var rate = p.Settings.FrameRate;
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.FromFrame(start, rate), Duration = MediaTime.FromFrame(end - start, rate),
            SourceIn = MediaTime.FromFrame(sourceIn, rate), SourceOut = MediaTime.FromFrame(sourceIn + end - start, rate)
        };
        t.Clips.Add(clip);
        return clip;
    }

    private async Task Audio(Project p, Track t, string path, long start, long end)
    {
        var asset = await Asset(p, path);
        var rate = p.Settings.FrameRate;
        t.Clips.Add(new AudioClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.FromFrame(start, rate), Duration = MediaTime.FromFrame(end - start, rate),
            SourceIn = MediaTime.Zero, SourceOut = MediaTime.FromFrame(end - start, rate)
        });
    }

    private static TemplateClip Template(Project p, Track track, TemplateKind kind, long start, long end)
    {
        var rate = p.Settings.FrameRate;
        var fields = TemplateRules.Defaults(kind);
        var (fadeIn, fadeOut) = TemplateRules.Animation(kind);
        var clip = new TemplateClip
        {
            Kind = kind, TimelineStart = MediaTime.FromFrame(start, rate), Duration = MediaTime.FromFrame(end - start, rate),
            Title = fields.Title, AccentHex = fields.AccentHex,
            FadeIn = MediaTime.FromFrame(MediaTime.FromSeconds(fadeIn).ToNearestFrame(rate), rate),
            FadeOut = MediaTime.FromFrame(MediaTime.FromSeconds(fadeOut).ToNearestFrame(rate), rate)
        };
        track.Clips.Add(clip);
        return clip;
    }

    private static TextClip Text(Project p, Track track, string text, long start, long end)
    {
        var rate = p.Settings.FrameRate;
        var clip = new TextClip { TimelineStart = MediaTime.FromFrame(start, rate), Duration = MediaTime.FromFrame(end - start, rate), Text = text };
        track.Clips.Add(clip);
        return clip;
    }

    private static void Join(Project p, Track track, Clip a, Clip b, string type, long frames)
    {
        track.Transitions.Add(new Transition
        {
            TransitionTypeId = type, Duration = MediaTime.FromFrame(frames, p.Settings.FrameRate), LeftClipId = a.Id, RightClipId = b.Id,
            Direction = TransitionRules.DefaultDirection(type)
        });
    }

    private static ImmutableArray<NormalizedPoint> Box(double x0, double y0, double x1, double y1) => ImmutableArray.Create(
        new NormalizedPoint(x0, y0), new NormalizedPoint(x1, y0), new NormalizedPoint(x1, y1), new NormalizedPoint(x0, y1));

    /// <summary>Eight hold outlines with the appear / disappear animation (D036 §4).</summary>
    private static HoldMarkup Outlines(FrameRate rate) => new()
    {
        StrokeWidth1080 = 6, ColorHex = "#FFEA00", Keyline = KeylineMode.On, KeylineActive = true,
        Holds = Enumerable.Range(0, 8).Select(i =>
        {
            var x = 0.15 + 0.08 * i;
            var y = 0.8 - 0.08 * i;
            return new Hold(Guid.NewGuid(), Box(x, y, x + 0.05, y + 0.06), default, default, MediaTime.Zero);
        }).ToImmutableArray(),
        Animation = new HoldAnimation
        {
            StartSourceTime = MediaTime.FromSeconds(0.5), Interval = MediaTime.FromSeconds(0.4), AppearFade = MediaTime.FromSeconds(0.3),
            StayAfterLast = MediaTime.FromSeconds(2), DisappearFade = MediaTime.FromSeconds(0.5)
        }
    };

    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);

    /// <summary>A synthetic COROS training under the clip (D055: heart rate by the file's time) — one sample a second.</summary>
    private static Task AttachTraining(Project p, VideoClip clip)
    {
        var asset = p.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
        asset.Metadata!.RecordingTimeProbe = RecordingTimeProbe.Found(
            T0.AddTicks(asset.Metadata.Duration.Ticks).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), RecordingTimeTagOrigin.Format);
        var seconds = (int)Math.Ceiling(asset.Metadata.Duration.TotalSeconds) + 2;
        var workout = new ClimbingWorkout
        {
            DeviceName = "COROS APEX 4 42mm", Manufacturer = 294, Product = 814, StartUtc = T0, EndUtc = T0.AddSeconds(seconds),
            UtcOffset = null, ElapsedTime = MediaTime.FromSeconds(seconds), TimerTime = MediaTime.FromSeconds(seconds), Sport = 31, SubSport = 69,
            AverageHeartRate = null, MaxHeartRate = null,
            HeartRate = Enumerable.Range(0, seconds).Select(i => new HeartRateSample(T0.AddSeconds(i), 110 + (i * 7) % 50)).ToList(),
            Pauses = Array.Empty<WorkoutInterval>(), Segments = Array.Empty<WorkoutSegment>(), Warnings = Array.Empty<TrainingFileWarning>()
        };
        p.Training = new TrainingReference
        {
            FilePath = @"C:\fit\bench.fit", FileSizeBytes = 1, Sha256 = new string('a', 64), Activity = TrainingActivity.Of(workout),
            Workout = workout, Availability = TrainingAvailability.Available
        };
        return Task.CompletedTask;
    }
}
