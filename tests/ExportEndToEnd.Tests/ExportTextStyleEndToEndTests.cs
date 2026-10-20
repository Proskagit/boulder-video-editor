using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 27 (D043 §1–§2, §7; variant B): a text with an outline, a background, bold and both motions through the whole
/// export with real parts (ffmpeg decoding → the shared draw plan → the Avalonia rasterizer with its own glyph cache, the
/// geometry prepared on the UI thread → H.264). On frames of the appearance, at rest and of the disappearance the Preview
/// draws exactly the export's canvas bytes; the MP4 matches the canvases within the codec's error; the text slides in from
/// below (its ink lower on the first frame than at rest).
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportTextStyleEndToEndTests(E2EMedia media, ITestOutputHelper output)
{
    [FfmpegFact]
    public async Task The_preview_and_the_export_agree_on_a_styled_moving_text_and_the_mp4_follows()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);   // 320 × 180
        var track = p.VideoTrack();
        p.Video(track, media.Solid("3278C8", FrameRate.Fps25), 0, 40);
        var text = p.Text(p.VideoTrack(), "Маршрут 6B+", 0, 40);
        text.FontSize = 28;
        text.Bold = true;
        text.Outline = new TextOutline(true, "#000000", 0.12);
        text.Background = new TextBackground(true, "#FFD600", 0.6);
        text.FadeIn = MediaTime.FromFrame(8, FrameRate.Fps25);
        text.FadeOut = MediaTime.FromFrame(6, FrameRate.Fps25);
        text.MotionIn = new TextMotion(TextMotionKind.Slide, TextMotionSide.Bottom);
        text.MotionOut = new TextMotion(TextMotionKind.Zoom);

        var run = await ExportProject(p.Project, Path.Combine(media.OutputFolder(), "styled-text.mp4"));
        var (w, h) = (run.Output.Size.Width, run.Output.Size.Height);

        foreach (var n in new long[] { 0, 4, 8, 20, 35, 39 })
        {
            var preview = await Preview(run.Job.Snapshot, n);
            var differing = Enumerable.Range(0, preview.Pixels.Length).Count(i => preview.Pixels[i] != run.Canvases[(int)n][i]);
            Assert.True(differing == 0, $"frame {n}: {differing} bytes differ between the Preview and the export canvas");
        }

        // The lowest row that differs from the wall (#3278C8, BGRA C8 78 32) — independent of the fade's opacity: lower on
        // frame 2 (sliding in from below) than at rest (frame 20).
        int LowestDrawn(byte[] canvas)
        {
            for (var y = h - 1; y >= 0; y--)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (Math.Abs(canvas[i] - 0xC8) + Math.Abs(canvas[i + 1] - 0x78) + Math.Abs(canvas[i + 2] - 0x32) > 12) return y;
            }
            return -1;
        }
        var (moving, rest) = (LowestDrawn(run.Canvases[2]), LowestDrawn(run.Canvases[20]));
        output.WriteLine($"lowest drawn row: frame 2 {moving}, frame 20 {rest}");
        Assert.True(rest > 0 && moving > rest, $"the text comes from below ({moving} vs {rest})");

        var decoded = EncoderHarness.DecodeFrames(run.Path, w, h);
        var errors = decoded.Select((frame, n) => MeanAbs(frame, run.Canvases[n])).ToArray();
        output.WriteLine($"{run.Output.FrameCount} frames, MP4 vs canvas mean |Δ| max {errors.Max():0.00}, avg {errors.Average():0.00}");
        Assert.All(errors, e => Assert.InRange(e, 0, 3.0));
        AssertValidMp4(run);
    }
}
