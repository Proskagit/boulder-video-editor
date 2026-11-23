using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Rendering.Tests;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// D063 through the whole export path (ffmpeg decoding → the composition → the rasterizer → the encoder → MP4): a keyframed
/// clip moves between its keyframes in the file exactly as the Preview draws it (the same canvas bytes), and a picture
/// snapped onto the canvas edges leaves no black line at any edge of the encoded frames.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportKeyframeEndToEndTests
{
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportKeyframeEndToEndTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    private static TransformKeyframes Keys(params (double Seconds, double X, double Scale, KeyframeEasing Easing)[] keys) =>
        new(keys.Select(k => new TransformKeyframe(Guid.NewGuid(), MediaTime.FromSeconds(k.Seconds), k.X, 0, k.Scale, k.Easing)).ToImmutableArray());

    [FfmpegFact]
    public async Task A_keyframed_clip_moves_in_the_file_exactly_as_the_Preview_draws_it()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var clip = p.Video(p.VideoTrack(), _media.Pattern(), 0, 25);
        clip.Keyframes = Keys((0, -80, 0.5, KeyframeEasing.EaseInOut), (0.96, 80, 0.5, KeyframeEasing.Linear));
        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "keyframes.mp4"), PainterParity);

        AssertValidMp4(run);
        var decoded = EncoderHarness.DecodeFrames(run.Path, run.Output.Size.Width, run.Output.Size.Height);
        var w = run.Output.Size.Width;
        double Centre(byte[] image)
        {
            var lit = Enumerable.Range(0, w).Where(x => { var (r, g, b) = Pixel(image, w, x, run.Output.Size.Height / 2); return r + g + b > 60; }).ToList();
            return (lit.First() + lit.Last() + 1) / 2.0;
        }
        var centres = run.Canvases.Select(Centre).ToList();
        _output.WriteLine(string.Join(" ", centres.Select(c => c.ToString("0.#"))));
        Assert.Equal(80, centres[0], 0);                                         // the first keyframe: 80 px left of the centre
        Assert.Equal(240, centres[24], 0);                                       // the second: 80 px right
        for (var n = 1; n < 25; n++) Assert.True(centres[n] >= centres[n - 1], $"frame {n} went back");
        Assert.True(centres[3] - centres[0] < centres[13] - centres[10]);        // eased in: slower at the start
        for (var n = 0; n < 25; n++) Assert.InRange(Math.Abs(Centre(decoded[n]) - centres[n]), 0, 1.5);

        foreach (var n in new long[] { 0, 7, 12, 19, 24 })
        {
            var preview = await Preview(run.Job.Snapshot, n);
            var differing = Enumerable.Range(0, preview.Pixels.Length).Count(i => preview.Pixels[i] != run.Canvases[(int)n][i]);
            Assert.True(differing == 0, $"frame {n}: {differing} bytes differ between the Preview and the export canvas");
        }
        AssertNoFfmpegLeft();
    }

    [FfmpegFact]
    public async Task A_picture_snapped_to_the_canvas_edges_leaves_no_black_line_in_the_file()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var clip = p.Video(p.VideoTrack(), _media.Pattern(), 0, 10);
        var canvas = new FrameSize(p.Project.Settings.FrameWidth, p.Project.Settings.FrameHeight);
        var source = new FrameSize(320, 180);
        // A hand-scaled picture 1.4 % short of the canvas: thin black lines at every edge before the snap.
        var hand = VisualProperties.Default with { Scale = 0.986 };
        Assert.False(CompositionMath.Layout(canvas, source, hand).CoversCanvas);
        var snapped = CanvasSnap.Scale(canvas, source, hand, 4).Visual;
        (clip.Scale, clip.PositionX, clip.PositionY) = (snapped.Scale, snapped.PositionX, snapped.PositionY);
        Assert.True(CompositionMath.Layout(canvas, source, snapped).CoversCanvas);

        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "snapped.mp4"), PainterParity);
        AssertValidMp4(run);
        var (w, h) = (run.Output.Size.Width, run.Output.Size.Height);
        var decoded = EncoderHarness.DecodeFrames(run.Path, w, h);
        foreach (var image in new[] { run.Canvases[5], decoded[5] })
            foreach (var (x, y) in Enumerable.Range(0, h).SelectMany(y => new[] { (0, y), (w - 1, y) })
                         .Concat(Enumerable.Range(0, w).SelectMany(x => new[] { (x, 0), (x, h - 1) })))
            {
                var (r, g, b) = Pixel(image, w, x, y);
                Assert.True(r + g + b > 40, $"a black edge pixel at ({x},{y})");
            }
        AssertNoFfmpegLeft();
    }
}
