using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Decision L1-c (D028 §6, Phase 13 Step 13.8): the codec leg MP4 → export canvas at every quality level (preset medium) and
/// every preset (quality High), over eleven scenes — flat colour, PNG with alpha over video, JPEG, text, the moving test
/// pattern, a rotated portrait video, 4× speed, mixed layers, portrait and square canvases, a 1080p canvas. The error is
/// measured with the Step 8.6 method (<see cref="CodecLegMetrics"/>) and every line of it is written to the test output.
/// Criteria (set from that data, D028 "Refined in Step 13.8"):
/// <list type="bullet">
/// <item>the codec's own error per level — every frame's PSNR of the MP4 against the lossless reference of the same
/// canvases ("quant") at least <see cref="QuantFloorDb"/> of its level, also for the other presets;</item>
/// <item>the levels in order on every scene with detail — each one at least <see cref="LevelStepDb"/> above the next;</item>
/// <item>flat colour through the whole leg (conversion and codec) within one YUV code step — max |Δ| ≤ <see cref="FlatMax"/> —
/// at every setting.</item>
/// </list>
/// The conversion floor (BT.709 limited 4:2:0, the same for every level) is reported, not bounded beyond the flat-colour
/// check: strict colour parity is the canvas-level Preview ↔ export rule of D023, unchanged.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportCodecLegTests
{
    private const string Red = "C83232";
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportCodecLegTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    public static readonly string[] SceneNames =
        { "flat", "png", "jpeg", "text", "pattern", "rotated", "speed4x", "mixed", "portrait", "square", "1080p" };

    public static readonly (string Name, ExportEncoding Settings)[] Settings =
    {
        ("Maximum/medium", new ExportEncoding(ExportQuality.Maximum, ExportSpeedPreset.Medium, 192)),
        ("High/medium", ExportEncoding.Default),
        ("Standard/medium", new ExportEncoding(ExportQuality.Standard, ExportSpeedPreset.Medium, 192)),
        ("Compact/medium", new ExportEncoding(ExportQuality.Compact, ExportSpeedPreset.Medium, 192)),
        ("High/fast", new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Fast, 192)),
        ("High/slow", new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Slow, 192)),
    };

    private ProjectBuilder Scene(string name)
    {
        ProjectBuilder p;
        switch (name)
        {
            case "flat":
                p = new ProjectBuilder(FrameRate.Fps25);
                p.Video(p.VideoTrack(), _media.Solid(Red, FrameRate.Fps25), 0, 25);
                break;
            case "png":
                p = new ProjectBuilder(FrameRate.Fps25);
                p.Video(p.VideoTrack(), _media.Solid(Red, FrameRate.Fps25), 0, 25);
                p.Image(p.VideoTrack(), _media.PngAlpha(), 0, 25);
                break;
            case "jpeg":
                p = new ProjectBuilder(FrameRate.Fps25);
                p.Image(p.VideoTrack(), _media.Jpeg(), 0, 25);
                break;
            case "text":
                p = new ProjectBuilder(FrameRate.Fps25);
                var text = p.Text(p.VideoTrack(), "EXPORT 13.8", 0, 25);
                (text.FontSize, text.ColorHex) = (40, "#FFE040");
                break;
            case "pattern":
                p = new ProjectBuilder(FrameRate.Fps25);
                p.Video(p.VideoTrack(), _media.Pattern(), 0, 25);
                break;
            case "rotated":
                p = new ProjectBuilder(FrameRate.Fps25, 180, 320);
                p.Video(p.VideoTrack(), _media.Rotated90(), 0, 25);
                break;
            case "speed4x":
                p = new ProjectBuilder(FrameRate.Fps25);
                p.Video(p.VideoTrack(), _media.Pattern(FrameRate.Fps25, seconds: 6), 0, 25, sourceInFrame: 10, speed: ClipSpeed.FromSteps(80));
                break;
            case "mixed":
                p = new ProjectBuilder(FrameRate.Ntsc30);
                p.Video(p.VideoTrack(), _media.Pattern(FrameRate.Fps25, seconds: 6), 0, 30, speed: ClipSpeed.FromSteps(27));
                var rotated = p.Video(p.VideoTrack(), _media.Rotated90(), 0, 30);
                (rotated.Scale, rotated.PositionX, rotated.RotationDegrees) = (0.6, 90, 10);
                var png = p.Image(p.VideoTrack(), _media.PngAlpha(), 5, 30);
                (png.Scale, png.RotationDegrees, png.PositionY) = (0.5, -15, 40);
                var caption = p.Text(p.VideoTrack(), "Step 13.8", 10, 30);
                (caption.FontSize, caption.ColorHex) = (24, "#80FF80");
                break;
            case "portrait":
                p = new ProjectBuilder(FrameRate.Fps25, 180, 320);
                p.Video(p.VideoTrack(), _media.Pattern(), 0, 25);
                break;
            case "square":
                p = new ProjectBuilder(FrameRate.Fps25, 240, 240);
                p.Video(p.VideoTrack(), _media.Pattern(), 0, 25);
                break;
            case "1080p":
                p = new ProjectBuilder(FrameRate.Fps25, 1920, 1080);
                p.Video(p.VideoTrack(), _media.Pattern1080(), 0, 5);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        return p;
    }

    public static TheoryData<string> Scenes()
    {
        var data = new TheoryData<string>();
        foreach (var s in SceneNames) data.Add(s);
        return data;
    }

    /// <summary>Lowest per-frame PSNR of the MP4 against the lossless reference, by level (dB). The measured lowest values
    /// (FFmpeg 9.0.1, all eleven scenes, every preset): Maximum 41.3, High 37.5, Standard 33.3, Compact 29.9 — each bound is
    /// about 2.3–2.5 dB below, so thread count or rounding can't fail it, while one level's CRF in another's place does.</summary>
    internal static readonly IReadOnlyDictionary<ExportQuality, double> QuantFloorDb = new Dictionary<ExportQuality, double>
    {
        [ExportQuality.Maximum] = 39.0,
        [ExportQuality.High] = 35.0,
        [ExportQuality.Standard] = 31.0,
        [ExportQuality.Compact] = 27.5,
    };

    /// <summary>Least distance between neighbouring levels on one scene (dB; measured: at least 3.4).</summary>
    internal const double LevelStepDb = 2.0;

    /// <summary>Flat colour through the leg: one YUV code step (D023 Step 8.6: 1–2 per channel on flat content).</summary>
    internal const int FlatMax = 2;

    [FfmpegTheory]
    [MemberData(nameof(Scenes))]
    public async Task Every_quality_level_and_preset_meets_its_codec_leg_criterion(string scene)
    {
        var quant = new Dictionary<string, CodecLegMetrics.Stats>();
        foreach (var (name, settings) in Settings)
        {
            var p = Scene(scene);
            p.Project.Settings.Export = settings;
            var path = Path.Combine(_media.OutputFolder(), $"l1c-{scene}-{name.Replace('/', '-')}.mp4");
            var watch = Stopwatch.StartNew();
            var run = await ExportProject(p.Project, path);
            var ms = watch.ElapsedMilliseconds;
            var mp4 = EncoderHarness.DecodeFrames(run.Path, run.Output.Size.Width, run.Output.Size.Height);
            var reference = CodecLegMetrics.LosslessReference(run);
            var total = CodecLegMetrics.Compare(mp4, run.Canvases);
            var floor = CodecLegMetrics.Compare(reference, run.Canvases);
            var q = CodecLegMetrics.Compare(mp4, reference);
            quant[name] = q;
            _output.WriteLine($"L1C|{scene}|{name}|frames {run.Canvases.Count}|{new FileInfo(path).Length} B|{ms} ms|TOTAL {total}|FLOOR {floor}|QUANT {q}");

            Assert.Same(settings, run.EncoderSettings);
            Assert.True(q.WorstFramePsnr >= QuantFloorDb[settings.Quality],
                $"{scene}, {name}: the codec's own error reaches {q.WorstFramePsnr:0.00} dB PSNR on a frame, below {QuantFloorDb[settings.Quality]} dB");
            if (scene == "flat")
                Assert.True(total.Max <= FlatMax, $"{scene}, {name}: flat colour off by {total.Max} through the codec leg (at most {FlatMax})");
        }

        if (scene != "flat")                                                       // flat colour is lossless at every level
        {
            var levels = new[] { "Maximum/medium", "High/medium", "Standard/medium", "Compact/medium" };
            for (var i = 0; i + 1 < levels.Length; i++)
            {
                var (better, worse) = (quant[levels[i]].WorstFramePsnr, quant[levels[i + 1]].WorstFramePsnr);
                Assert.True(better >= worse + LevelStepDb,
                    $"{scene}: {levels[i]} {better:0.00} dB is not {LevelStepDb} dB above {levels[i + 1]} {worse:0.00} dB");
            }
        }
        AssertNoFfmpegLeft();
    }
}
