using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 21 (D036 §4, §7): the hold animation through the whole export with real parts (ffmpeg decoding → the shared draw
/// plan → the Avalonia rasterizer → H.264). On frames before the start, in the middle of an appear fade, with every hold
/// shown, in the middle of the disappear fade and after it, the Preview (its own pipeline and control, the same snapshot)
/// draws exactly the export's canvas bytes; the MP4 matches the canvases within the codec's error; the outline's pixels
/// follow the schedule — hidden, partly, fully, partly, gone.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportHoldAnimationEndToEndTests(E2EMedia media, ITestOutputHelper output)
{
    private static ImmutableArray<NormalizedPoint> Box(double x0, double y0, double x1, double y1) => ImmutableArray.Create(
        new NormalizedPoint(x0, y0), new NormalizedPoint(x1, y0), new NormalizedPoint(x1, y1), new NormalizedPoint(x0, y1));

    [FfmpegFact]
    public async Task The_preview_and_the_export_agree_on_every_animation_frame_and_the_mp4_follows_the_schedule()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);   // 320 × 180
        var clip = p.Video(p.VideoTrack(), media.Solid("C83232", FrameRate.Fps25), 0, 50);
        // Three boxes, lowest first; start at source 0.2 s (frame 5); 0.2 s interval / appear fade (5 frames), 0.4 s stay
        // and disappear fade (10): appear 5, 10, 15 — all shown at 20 — disappear 30..39, gone from 40.
        clip.HoldMarkup = new HoldMarkup
        {
            StrokeWidth1080 = 32, ColorHex = "#FFFFFF", Keyline = KeylineMode.On, KeylineActive = true,
            Holds = ImmutableArray.Create(
                new Hold(Guid.NewGuid(), Box(0.1, 0.6, 0.3, 0.9), default, default, MediaTime.Zero),
                new Hold(Guid.NewGuid(), Box(0.4, 0.35, 0.6, 0.65), default, default, MediaTime.Zero),
                new Hold(Guid.NewGuid(), Box(0.7, 0.1, 0.9, 0.4), default, default, MediaTime.Zero)),
            Animation = new HoldAnimation
            {
                StartSourceTime = MediaTime.FromSeconds(0.2), Interval = MediaTime.FromSeconds(0.2), AppearFade = MediaTime.FromSeconds(0.2),
                StayAfterLast = MediaTime.FromSeconds(0.4), DisappearFade = MediaTime.FromSeconds(0.4)
            }
        };

        var run = await ExportProject(p.Project, Path.Combine(media.OutputFolder(), "animated.mp4"));
        var w = run.Output.Size.Width;

        foreach (var n in new long[] { 4, 7, 12, 20, 34, 40 })
        {
            var preview = await Preview(run.Job.Snapshot, n);
            var differing = Enumerable.Range(0, preview.Pixels.Length).Count(i => preview.Pixels[i] != run.Canvases[(int)n][i]);
            Assert.True(differing == 0, $"frame {n}: {differing} bytes differ between the Preview and the export canvas");
        }

        // The core of the lowest box's left edge (x = 32, mid-height y = 135): red wall → white by the schedule.
        double White(long n) => (Pixel(run.Canvases[(int)n], w, 32, 135).G - 0x32) / (255.0 - 0x32);
        Assert.InRange(White(4), -0.01, 0.01);                       // before the start
        Assert.InRange(White(7), 3 / 6.0 - 0.02, 3 / 6.0 + 0.02);   // appear fade, frame 2 of 5: 3/6
        Assert.InRange(White(20), 0.99, 1.0);
        Assert.InRange(White(34), 6 / 11.0 - 0.02, 6 / 11.0 + 0.02); // disappear fade, j = 4 of 10: 6/11
        Assert.InRange(White(40), -0.01, 0.01);                      // gone
        Assert.InRange(Pixel(run.Canvases[12], w, 224, 45).G - 0x32, -2, 2);   // the top box not yet at frame 12

        var decoded = EncoderHarness.DecodeFrames(run.Path, w, run.Output.Size.Height);
        var errors = decoded.Select((frame, n) => MeanAbs(frame, run.Canvases[n])).ToArray();
        output.WriteLine($"{run.Output.FrameCount} frames, MP4 vs canvas mean |Δ| max {errors.Max():0.00}, avg {errors.Average():0.00}");
        Assert.All(errors, e => Assert.InRange(e, 0, 3.0));
        AssertValidMp4(run);
    }
}
