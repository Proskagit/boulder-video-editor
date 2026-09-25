using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Rendering.Tests;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 8 Step 6, the whole picture path with real parts: project → preflight → <c>ExportService</c> →
/// <c>ExportFrameSource</c> (ffmpeg decoding) → Core <c>CompositionDrawPlan</c> → <c>AvaloniaCompositionRasterizer</c> →
/// <c>FfmpegExportEncoder</c> → MP4. For every scene: the MP4 is valid (ffprobe), its decoded frames match the canvases
/// the service encoded within the codec's error, and the Preview (its own pipeline and control, same snapshot) draws
/// the same canvas bytes at sampled frames — the parity boundary fixed in Step 3 (Preview model == export rasterizer;
/// the encoded MP4 only within the codec's error).
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportCompositionEndToEndTests
{
    private const string Red = "C83232";
    private const string Green = "20C040";
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportCompositionEndToEndTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    public static TheoryData<string> Scenes => new() { "solid", "crop", "scale", "rotation", "opacity", "text", "layers", "vertical", "hidden" };

    private ProjectBuilder Scene(string name)
    {
        var p = name == "vertical" ? new ProjectBuilder(FrameRate.Fps25, 180, 320) : new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        switch (name)
        {
            case "solid": p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 0, 25); break;
            case "crop": p.Video(v1, _media.Pattern(), 0, 25).Crop = new CropRect(0.25, 0, 0.25, 0); break;
            case "scale": p.Video(v1, _media.Pattern(), 0, 25).Scale = 0.5; break;
            case "rotation": p.Video(v1, _media.Pattern(), 0, 25).RotationDegrees = 90; break;
            case "opacity": p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 0, 25).Opacity = 0.5; break;
            case "text":
                var text = p.Text(v1, "EXPORT", 0, 25);
                text.FontSize = 64;
                break;
            case "layers":
                p.Video(v1, _media.Pattern(), 0, 25);
                var top = p.Video(p.VideoTrack(), _media.Solid(Red, FrameRate.Fps25), 0, 25);
                (top.Scale, top.RotationDegrees, top.Opacity, top.PositionX, top.PositionY) = (0.5, 30, 0.6, 40, -20);
                top.Crop = new CropRect(0.1, 0, 0.2, 0.1);
                var caption = p.Text(p.VideoTrack(), "Step 6\nexport", 5, 20);
                (caption.FontSize, caption.RotationDegrees, caption.ColorHex, caption.Alignment) = (28, -10, "#FFE040", TextAlignment.Left);
                break;
            case "vertical": p.Video(v1, _media.Pattern(), 0, 25); break;
            case "hidden":
                p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 0, 25);
                p.Video(p.VideoTrack(hidden: true), _media.Solid(Green, FrameRate.Fps25), 0, 25);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        return p;
    }

    [FfmpegTheory]
    [MemberData(nameof(Scenes))]
    public async Task Scene_exports_to_a_valid_mp4_that_matches_the_canvases_and_the_preview(string name)
    {
        var project = Scene(name);
        var run = await ExportProject(project.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));

        AssertValidMp4(run);
        var decoded = EncoderHarness.DecodeFrames(run.Path, run.Output.Size.Width, run.Output.Size.Height);
        var errors = decoded.Select((frame, n) => MeanAbs(frame, run.Canvases[n])).ToArray();
        _output.WriteLine($"{name}: {run.Output.FrameCount} frames, MP4 vs canvas mean |Δ| max {errors.Max():0.00}, avg {errors.Average():0.00}");
        Assert.All(errors, e => Assert.InRange(e, 0, 3.0));

        foreach (var n in new long[] { 0, 12, 24 })
        {
            var preview = await Preview(run.Job.Snapshot, n);
            var differing = Enumerable.Range(0, preview.Pixels.Length).Count(i => preview.Pixels[i] != run.Canvases[(int)n][i]);
            Assert.True(differing == 0, $"{name}, frame {n}: {differing} bytes differ between the Preview and the export canvas");
        }
        AssertNoFfmpegLeft();

        var w = run.Output.Size.Width;
        var h = run.Output.Size.Height;
        var canvas = run.Canvases[12];
        var mp4 = decoded[12];
        (byte R, byte G, byte B) C(byte[] image, int x, int y) => Pixel(image, w, x, y);
        void Near((byte R, byte G, byte B) actual, (int R, int G, int B) expected, int tolerance)
        {
            Assert.InRange(actual.R, expected.R - tolerance, expected.R + tolerance);
            Assert.InRange(actual.G, expected.G - tolerance, expected.G + tolerance);
            Assert.InRange(actual.B, expected.B - tolerance, expected.B + tolerance);
        }
        void Black(int x, int y) { Near(C(canvas, x, y), (0, 0, 0), 0); Near(C(mp4, x, y), (0, 0, 0), 4); }
        bool Lit(byte[] image, int x, int y) { var (r, g, b) = C(image, x, y); return r + g + b > 60; }

        switch (name)
        {
            case "solid":
            case "hidden":                                                                   // the hidden green track never shows
                Near(C(canvas, w / 2, h / 2), (0xC8, 0x32, 0x32), 6);
                Near(C(mp4, w / 2, h / 2), C(canvas, w / 2, h / 2), 4);
                Assert.All(Enumerable.Range(0, w * h), i => Assert.True(canvas[4 * i + 1] < 90, "green pixel"));
                break;
            case "opacity":                                                                  // red at 0.5 over the black background
                Near(C(canvas, w / 2, h / 2), (0x64, 0x19, 0x19), 5);
                Near(C(mp4, w / 2, h / 2), C(canvas, w / 2, h / 2), 4);
                break;
            case "crop":                                                                     // 160 × 180 left of 320 × 180: pillarbox
                Black(40, h / 2); Black(w - 40, h / 2);
                Assert.True(Lit(canvas, w / 2, h / 2) && Lit(mp4, w / 2, h / 2));
                break;
            case "scale":                                                                    // 160 × 90 in the middle
                Black(40, 20); Black(w - 40, h - 20);
                Assert.True(Lit(canvas, w / 2, h / 2) && Lit(mp4, w / 2, h / 2));
                break;
            case "rotation":                                                                 // 320 × 180 turned: 180 wide, centred
                Black(30, h / 2); Black(w - 30, h / 2);
                Assert.True(Lit(canvas, w / 2, 10) && Lit(mp4, w / 2, 10));
                break;
            case "vertical":                                                                 // 180 × 320 canvas: letterbox
                Assert.Equal((180, 320), (w, h));
                Black(w / 2, 40); Black(w / 2, h - 40);
                Assert.True(Lit(canvas, w / 2, h / 2) && Lit(mp4, w / 2, h / 2));
                break;
            case "text":
                var ink = new Image(w, h, canvas).InkBox(200);
                var encodedInk = new Image(w, h, mp4).InkBox(128);
                Assert.NotNull(ink);
                Assert.NotNull(encodedInk);
                Assert.InRange(ink!.Value.Left, w / 8, w / 2);
                Assert.InRange(Math.Abs(ink.Value.Left - encodedInk!.Value.Left) + Math.Abs(ink.Value.Right - encodedInk.Value.Right)
                             + Math.Abs(ink.Value.Top - encodedInk.Value.Top) + Math.Abs(ink.Value.Bottom - encodedInk.Value.Bottom), 0, 6);
                break;
        }
    }
}
