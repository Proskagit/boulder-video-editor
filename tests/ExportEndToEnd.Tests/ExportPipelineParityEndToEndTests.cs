using System.Collections.Immutable;
using System.Security.Cryptography;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Export;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.Video;
using AiVideoEditor.Video.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// D059 E1–E2 with real parts: the export as it was (one rasterizer, frames over ffmpeg's stdout) and as it is now (several
/// rasterizers on their own threads, frames over the named pipe) give the same canvases byte for byte and the same MP4 byte
/// for byte — on scenes with every component the painter has: outlined bold text, «Боулдер», the heart rate, animated hold
/// outlines, the VHS treatment, a blurred background on a vertical canvas, a Dissolve and a Push, several video tracks and a
/// picture in picture. The research measured the parallel speed but never this equality (§3.3); this is the check.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportPipelineParityEndToEndTests(E2EMedia media, ITestOutputHelper output)
{
    /// <summary>The decoder of the sequential reference: every request over stdout (the transport before D059 E1).</summary>
    private sealed class StdoutDecoder(IVideoDecoder inner) : IVideoDecoder
    {
        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default) =>
            inner.OpenAsync(request with { Transport = DecodeTransport.Standard }, ct);
    }

    private static async Task<(List<byte[]> Canvases, string Sha)> Export(Project project, string path, int rasterizers, bool stdout)
    {
        var job = Preflight(project, path);
        var encoder = new RecordingEncoder(EncoderHarness.Encoder());
        var locator = FfmpegTools.FfmpegLocator;
        IVideoDecoder decoder = new FfmpegVideoDecoder(locator, NullLogger<FfmpegVideoDecoder>.Instance, luts: Luts);
        if (stdout) decoder = new StdoutDecoder(decoder);
        var service = new ExportService(locator, decoder, new FfmpegAudioDecoder(locator, NullLogger<FfmpegAudioDecoder>.Instance), encoder,
            () => new AvaloniaCompositionRasterizer(), NullLogger<ExportService>.Instance, new ExportPipelineSettings { Rasterizers = rasterizers });
        await service.ExportAsync(job, null);
        using var file = File.OpenRead(path);
        return (encoder.Canvases, Convert.ToHexString(SHA256.HashData(file)));
    }

    private static ImmutableArray<NormalizedPoint> Box(double x0, double y0, double x1, double y1) => ImmutableArray.Create(
        new NormalizedPoint(x0, y0), new NormalizedPoint(x1, y0), new NormalizedPoint(x1, y1), new NormalizedPoint(x0, y1));

    private static Transition Join(ProjectBuilder p, Track track, Clip a, Clip b, string type, long frames)
    {
        var t = new Transition
        {
            TransitionTypeId = type, Duration = p.F(frames), LeftClipId = a.Id, RightClipId = b.Id, Direction = TransitionRules.DefaultDirection(type)
        };
        track.Transitions.Add(t);
        return t;
    }

    /// <summary>Everything on one landscape timeline (150 frames at 25 fps).</summary>
    private ProjectBuilder Landscape()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var a = p.Video(v1, media.Pattern(), 0, 50);
        var b = p.Video(v1, media.Pattern(), 50, 100, sourceInFrame: 10);
        var c = p.Video(v1, media.PatternWithTone(), 100, 150, sourceInFrame: 10);
        Join(p, v1, a, b, TransitionRules.CrossDissolve, 10);
        Join(p, v1, b, c, TransitionRules.Push, 10);
        a.Routes = ImmutableArray.Create(new HoldMarkup
        {
            StrokeWidth1080 = 24, ColorHex = "#FFEA00", Keyline = KeylineMode.On, KeylineActive = true,
            Holds = ImmutableArray.Create(
                new Hold(Guid.NewGuid(), Box(0.1, 0.6, 0.3, 0.9), default, default, MediaTime.Zero),
                new Hold(Guid.NewGuid(), Box(0.4, 0.35, 0.6, 0.65), default, default, MediaTime.Zero),
                new Hold(Guid.NewGuid(), Box(0.7, 0.1, 0.9, 0.4), default, default, MediaTime.Zero)),
            Animation = new HoldAnimation
            {
                StartSourceTime = MediaTime.FromSeconds(0.2), Interval = MediaTime.FromSeconds(0.2), AppearFade = MediaTime.FromSeconds(0.2),
                StayAfterLast = MediaTime.FromSeconds(0.4), DisappearFade = MediaTime.FromSeconds(0.4)
            }
        });
        p.Audio(p.AudioTrack(), media.Tone(), 0, 150).Volume = 0.5;

        var v2 = p.VideoTrack();                                        // picture in picture
        var pip = p.Video(v2, media.Pattern(), 20, 130);
        pip.Scale = 0.35; pip.PositionX = 90; pip.PositionY = -45; pip.RotationDegrees = 7; pip.Opacity = 0.85;

        var v3 = p.VideoTrack();
        var title = p.Text(v3, "Трасса 7 · 6B+", 0, 150);
        title.FontSize = 28; title.Bold = true; title.PositionY = -60;
        title.Outline = new TextOutline(true, "#000000", TextOutline.DefaultThickness);

        var v4 = p.VideoTrack();
        p.Template(v4, TemplateKind.Boulder, 0, 60).Route = "7";
        p.Template(v4, TemplateKind.Vhs, 60, 110);
        p.Template(v4, TemplateKind.Highlight, 110, 150);
        return p;
    }

    /// <summary>A vertical canvas with a blurred background and a VHS over it.</summary>
    private ProjectBuilder Vertical()
    {
        var p = new ProjectBuilder(FrameRate.Fps25, E2EMedia.Height, E2EMedia.Width);
        var v1 = p.VideoTrack();
        p.Video(v1, media.PatternWithTone(), 0, 60).Background = ClipBackground.Blur;
        var v2 = p.VideoTrack();
        p.Template(v2, TemplateKind.Vhs, 20, 40);
        var text = p.Text(v2, "Blur", 45, 60);
        text.Background = new TextBackground(true, "#000000", 0.6);
        return p;
    }

    [FfmpegTheory]
    [InlineData("landscape", 6)]
    [InlineData("landscape", 3)]
    [InlineData("vertical", 6)]
    public async Task The_parallel_pipeline_gives_the_sequential_canvases_and_the_same_mp4(string scene, int rasterizers)
    {
        var build = scene == "landscape" ? (Func<ProjectBuilder>)Landscape : Vertical;
        var folder = media.OutputFolder();
        var (sequential, sequentialSha) = await Export(build().Project, Path.Combine(folder, $"{scene}-sequential.mp4"), rasterizers: 1, stdout: true);
        var (parallel, parallelSha) = await Export(build().Project, Path.Combine(folder, $"{scene}-parallel.mp4"), rasterizers, stdout: false);

        Assert.Equal(sequential.Count, parallel.Count);
        var differing = Enumerable.Range(0, sequential.Count).Where(n => !sequential[n].AsSpan().SequenceEqual(parallel[n])).ToList();
        Assert.True(differing.Count == 0, $"{differing.Count} canvases differ, first {string.Join(", ", differing.Take(5))}");
        output.WriteLine($"{scene}, {rasterizers} rasterizers: {parallel.Count} canvases equal; MP4 {parallelSha[..12]}");
        Assert.Equal(sequentialSha, parallelSha);
        AssertNoFfmpegLeft();
    }

    [HeavyFfmpegFact]
    public async Task A_long_parallel_export_stays_equal_and_its_memory_stays_bounded()
    {
        // 1 500 frames (60 s at 25 fps) of the landscape scene's parts, looped: no drift of the ordering, no growth.
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        for (var i = 0; i < 15; i++) p.Video(v1, media.Pattern(), 100 * i, 100 * i + 100);
        var title = p.Text(p.VideoTrack(), "Long", 0, 1500);
        title.Outline = new TextOutline(true, "#000000", TextOutline.DefaultThickness);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var (sequential, a) = await Export(p.Project, Path.Combine(media.OutputFolder(), "long-sequential.mp4"), 1, stdout: true);
        sequential.Clear();
        var (parallel, b) = await Export(p.Project, Path.Combine(media.OutputFolder(), "long-parallel.mp4"), 6, stdout: false);
        Assert.Equal(1500, parallel.Count);
        Assert.Equal(a, b);
        parallel.Clear();
        var after = GC.GetTotalMemory(forceFullCollection: true);
        output.WriteLine($"managed memory before {before >> 20} MB, after {after >> 20} MB");
        Assert.True(after - before < 64L << 20, $"{(after - before) >> 20} MB kept after two exports");
        AssertNoFfmpegLeft();
    }
}
