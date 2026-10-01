using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 10 Step 10.7 (D025 §3–§4, PO-5, PO-6): cross dissolves end to end with real decoders, the Avalonia rasterizer and
/// the ffmpeg encoder. The Preview (its own pipeline at the canvas size) draws exactly the export's canvas in every zone —
/// video → video at 1× and 2×, image and text neighbours, an odd F at 29.97, dissolves on both edges of a clip, a dissolve
/// under a partly covering upper clip and one over a lower track, handles missing at render time (the first frame held)
/// — with the D023 Step 8 criteria, no new tolerance. Independently of the app, a zone frame is ffmpeg's A frame (from
/// the handle) and B frame blended as B over A at p = (j+1)/(F+1). The sound is unchanged by a dissolve; cancelling inside
/// a zone ends like any cancel.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportDissolveEndToEndTests
{
    private const string Red = "C83232";
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportDissolveEndToEndTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    private static Transition Dissolve(ProjectBuilder p, Track track, Clip a, Clip b, long frames)
    {
        var t = new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = p.F(frames), LeftClipId = a.Id, RightClipId = b.Id };
        track.Transitions.Add(t);
        return t;
    }

    private async Task<ExportRun> Export(ProjectBuilder p, string name)
    {
        Assert.Null(TransitionRules.ValidateTrack(p.Project.Timeline.VideoTracks[0], p.Rate));
        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));
        AssertValidMp4(run);
        return run;
    }

    private static async Task AssertPreviewEqualsExport(ExportRun run, string scene, params long[] frames)
    {
        foreach (var n in frames)
        {
            var preview = await Preview(run.Job.Snapshot, n);
            var canvas = run.Canvases[(int)n];
            Assert.Equal(canvas.Length, preview.Pixels.Length);
            var differing = 0;
            var max = 0;
            for (var i = 0; i < canvas.Length; i++)
            {
                if (preview.Pixels[i] == canvas[i]) continue;
                differing++;
                max = Math.Max(max, Math.Abs(preview.Pixels[i] - canvas[i]));
            }
            Assert.True(differing == 0, $"{scene}, frame {n}: {differing} bytes differ between the Preview and the export canvas (max {max})");
        }
        AssertNoFfmpegLeft();
    }

    private static byte[] SourceFrame(string path, long index) =>
        EncoderHarness.Run(FfmpegTools.Ffmpeg!, "-v", "error", "-i", path, "-vf", $"select=eq(n\\,{index})", "-frames:v", "1",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-");

    /// <summary>The canvas is <paramref name="b"/> over <paramref name="a"/> at <paramref name="p"/> (both opaque, full
    /// canvas): a·(1 − p) + b·p per channel, to rounding.</summary>
    private void AssertBlend(byte[] canvas, byte[] a, byte[] b, double p, string what)
    {
        var max = 0;
        for (var i = 0; i < canvas.Length; i += 4)
            for (var c = 0; c < 3; c++)
                max = Math.Max(max, (int)Math.Abs(canvas[i + c] - (a[i + c] * (1 - p) + b[i + c] * p)));
        _output.WriteLine($"{what}: p {p:0.####}, max |canvas − blend| = {max}");
        Assert.True(max <= 3, $"{what}: max {max}");
    }

    // --- pictures ---------------------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task A_video_dissolves_into_another_from_both_handles()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var pattern = _media.Pattern();
        var red = _media.Solid(Red, FrameRate.Fps25);
        var a = p.Video(v1, pattern, 0, 40);
        var b = p.Video(v1, red, 40, 80, sourceInFrame: 20);
        Dissolve(p, v1, a, b, 20);                                                // zone [30, 50)
        var run = await Export(p, "dissolve-video");

        await AssertPreviewEqualsExport(run, "video → video", 29, 30, 35, 39, 40, 45, 49, 50);

        var redFrame = SourceFrame(red.FilePath, 0);
        foreach (var n in new long[] { 30, 35, 39, 40, 44, 49 })                  // A at n ≥ 40 is its handle
            AssertBlend(run.Canvases[(int)n], SourceFrame(pattern.FilePath, n), redFrame, FadeRule.Ramp(n - 30, 20), $"frame {n}");
    }

    [FfmpegFact]
    public async Task At_2x_the_handle_frames_follow_the_speed()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var pattern = _media.Pattern(FrameRate.Fps25, seconds: 6);
        var red = _media.Solid(Red, FrameRate.Fps25);
        var a = p.Video(v1, pattern, 0, 30, speed: ClipSpeed.FromSteps(40));     // source frame 2n
        var b = p.Video(v1, red, 30, 60, sourceInFrame: 20, speed: ClipSpeed.FromSteps(40));
        Dissolve(p, v1, a, b, 10);                                                // zone [25, 35)
        var run = await Export(p, "dissolve-speed");

        await AssertPreviewEqualsExport(run, "2×", 25, 29, 30, 34, 35);

        var redFrame = SourceFrame(red.FilePath, 0);
        foreach (var n in new long[] { 25, 30, 34 })
            AssertBlend(run.Canvases[(int)n], SourceFrame(pattern.FilePath, 2 * n), redFrame, FadeRule.Ramp(n - 25, 10), $"2× frame {n}");
    }

    [FfmpegFact]
    public async Task Image_and_text_neighbours_dissolve()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var video = p.Video(v1, _media.Pattern(), 0, 30);
        var image = p.Image(v1, _media.Jpeg(), 30, 60);
        var text = p.Text(v1, "Dissolve", 60, 90);
        text.FontSize = 40;
        Dissolve(p, v1, video, image, 10);
        Dissolve(p, v1, image, text, 9);
        var run = await Export(p, "dissolve-image-text");

        await AssertPreviewEqualsExport(run, "image and text", 25, 29, 30, 34, 55, 59, 60, 64);
    }

    [FfmpegFact]
    public async Task An_odd_dissolve_at_29_97_and_dissolves_on_both_edges()
    {
        var rate = FrameRate.Ntsc30;
        var p = new ProjectBuilder(rate);
        var v1 = p.VideoTrack();
        var a = p.Video(v1, _media.Pattern(rate), 0, 30);
        var b = p.Video(v1, _media.Solid(Red, rate), 30, 60, sourceInFrame: 20);
        var c = p.Video(v1, _media.Pattern(rate), 60, 90, sourceInFrame: 40);
        Dissolve(p, v1, a, b, 7);                                                 // [27, 34)
        Dissolve(p, v1, b, c, 13);                                                // [54, 67)
        var run = await Export(p, "dissolve-both-edges");

        await AssertPreviewEqualsExport(run, "29.97, both edges", 26, 27, 29, 30, 33, 34, 54, 59, 60, 66, 67);
    }

    [FfmpegFact]
    public async Task Dissolves_under_a_partly_covering_clip_and_over_a_lower_track()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var v2 = p.VideoTrack();
        var a = p.Video(v1, _media.Pattern(), 0, 40);
        var b = p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 40, 80, sourceInFrame: 20);
        Dissolve(p, v1, a, b, 16);                                                // V1 [32, 48)
        var c = p.Video(v2, _media.Pattern(), 20, 45, sourceInFrame: 10);
        var d = p.Image(v2, _media.PngAlpha(), 45, 70);
        (c.Scale, d.Scale) = (0.5, 0.6);
        Dissolve(p, v2, c, d, 12);                                                // V2 [39, 51)
        var run = await Export(p, "dissolve-two-tracks");
        Assert.Null(TransitionRules.ValidateTrack(v2, p.Rate));

        await AssertPreviewEqualsExport(run, "two tracks", 31, 32, 39, 40, 45, 47, 48, 50, 51);
    }

    [FfmpegFact]
    public async Task Handles_missing_at_render_time_hold_the_first_frame_in_both()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var pattern = _media.Pattern();
        var a = p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 0, 40);
        var b = p.Video(v1, pattern, 40, 80);                                     // SourceIn 0: no handle before B
        Dissolve(p, v1, a, b, 20);                                                // B shown from 30 anyway (media changed since)
        var run = await Export(p, "dissolve-no-handles");

        await AssertPreviewEqualsExport(run, "no handles", 30, 35, 39, 40);
        var first = SourceFrame(pattern.FilePath, 0);
        var redFrame = SourceFrame(_media.Solid(Red, FrameRate.Fps25).FilePath, 0);
        AssertBlend(run.Canvases[35], redFrame, first, FadeRule.Ramp(5, 20), "held first frame");
    }

    // --- sound and cancel -------------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task The_sound_is_the_same_with_and_without_the_dissolve()
    {
        ProjectBuilder Build(bool dissolve)
        {
            var p = new ProjectBuilder(FrameRate.Ntsc30);
            var v1 = p.VideoTrack();
            var a = p.Video(v1, _media.Sync(FrameRate.Ntsc30), 0, 45);
            var b = p.Video(v1, _media.Sync(FrameRate.Ntsc30), 45, 90, sourceInFrame: 45);
            if (dissolve) Dissolve(p, v1, a, b, 20);
            return p;
        }

        var without = await Export(Build(false), "sound-without");
        var with = await Export(Build(true), "sound-with");

        Assert.Equal(without.Pcm, with.Pcm);
    }

    [FfmpegFact]
    public async Task Cancelling_inside_a_zone_leaves_nothing_behind()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var a = p.Video(v1, _media.Pattern(), 0, 40);
        var b = p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 40, 80, sourceInFrame: 20);
        Dissolve(p, v1, a, b, 20);
        p.Audio(p.AudioTrack(), _media.Tone(), 0, 80);
        var folder = _media.OutputFolder();
        var job = Preflight(p.Project, Path.Combine(folder, "cancel.mp4"));

        var cts = new CancellationTokenSource();
        var progress = new SyncProgress(r => { if (r is { Stage: ExportStage.Video, Done: 35 }) cts.Cancel(); });   // inside [30, 50)
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(EncoderHarness.Encoder()).ExportAsync(job, progress, cts.Token));

        Assert.Empty(Directory.GetFiles(folder));
        AssertNoFfmpegLeft();
    }
}
