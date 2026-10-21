using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 28 Step 28.3 (D044 §2–§4, §10): the library's transitions end to end with real decoders, the Avalonia
/// rasterizer and the ffmpeg encoder. The Preview (its own pipeline at the canvas size) draws exactly the export's canvas
/// on the zone frames of every type and direction — also with rotation, scale, crop, opacity, Fill / Fit, a hold outline,
/// a vertical canvas and lower / upper tracks — under the D023 Step 8 criteria, no new tolerance. Independently of the app:
/// a fill frame is ffmpeg's source frame blended towards black / white at the fill's opacity (the cut frame fully
/// covered), a zoom frame is the source scaled about the centre, a push frame is the two sources shifted by whole pixels.
/// Handles missing at render time hold the first frame in both (D025 §4); a source that vanishes fails the export with
/// DecodeFailed and a cancel inside a zone leaves nothing behind.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportTransitionLibraryEndToEndTests
{
    private const string Red = "C83232";
    private readonly E2EMedia _media;
    private readonly ITestOutputHelper _output;

    public ExportTransitionLibraryEndToEndTests(E2EMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
    }

    private static Transition Add(ProjectBuilder p, Track track, Clip a, Clip b, string type, long frames, TransitionDirection? direction = null)
    {
        var t = new Transition
        {
            TransitionTypeId = type, Duration = p.F(frames), LeftClipId = a.Id, RightClipId = b.Id,
            Direction = direction ?? TransitionRules.DefaultDirection(type)
        };
        track.Transitions.Add(t);
        return t;
    }

    private async Task<ExportRun> Export(ProjectBuilder p, string name)
    {
        foreach (var track in p.Project.Timeline.VideoTracks)
            Assert.Null(TransitionRules.ValidateTrack(track, p.Rate));
        Assert.Null(TransitionRules.ValidateFillPeaks(p.Project.Timeline.VideoTracks));
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

    private static byte[] SourceFrame(string path, long index, string? filter = null) =>
        EncoderHarness.Run(FfmpegTools.Ffmpeg!, "-v", "error", "-i", path, "-vf", $"select=eq(n\\,{index}){(filter is null ? "" : "," + filter)}",
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "-");

    /// <summary>The canvas is <paramref name="source"/> blended towards <paramref name="fill"/> at <paramref name="alpha"/>,
    /// to rounding.</summary>
    private void AssertFilled(byte[] canvas, byte[] source, byte fill, double alpha, string what)
    {
        var max = 0;
        for (var i = 0; i < canvas.Length; i += 4)
            for (var c = 0; c < 3; c++)
                max = Math.Max(max, (int)Math.Abs(canvas[i + c] - (source[i + c] * (1 - alpha) + fill * alpha)));
        _output.WriteLine($"{what}: α {alpha:0.####}, max |canvas − blend| = {max}");
        Assert.True(max <= 3, $"{what}: max {max}");
    }

    private static int MaxDifference(byte[] a, int ax, int ay, byte[] b, int bx, int by, int width)
    {
        var max = 0;
        var (ia, ib) = ((ay * width + ax) * 4, (by * width + bx) * 4);
        for (var c = 0; c < 3; c++) max = Math.Max(max, Math.Abs(a[ia + c] - b[ib + c]));
        return max;
    }

    // --- Dip to black and Flash ---------------------------------------------------------------------------------------

    [FfmpegTheory]
    [InlineData(TransitionRules.DipToBlack, (byte)0)]
    [InlineData(TransitionRules.Flash, (byte)255)]
    public async Task A_fill_blends_each_clips_own_frames_towards_its_colour_and_covers_the_cut_frame(string type, byte fill)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var pattern = _media.Pattern();
        var red = _media.Solid(Red, FrameRate.Fps25);
        var a = p.Video(v1, pattern, 0, 40);
        var b = p.Video(v1, red, 40, 80);                                           // whole sources: no handles needed
        Add(p, v1, a, b, type, 15);                                                 // zone [33, 48): 7 in A, 8 in B
        var run = await Export(p, "fill-" + type);

        await AssertPreviewEqualsExport(run, type, 32, 33, 36, 39, 40, 41, 47, 48);

        var redFrame = SourceFrame(red.FilePath, 0);
        foreach (var n in new long[] { 33, 36, 39 })                                // A's own frames
            AssertFilled(run.Canvases[(int)n], SourceFrame(pattern.FilePath, n), fill, TransitionPresentation.FillOpacity(n - 33, 15), $"{type} A {n}");
        foreach (var n in new long[] { 40, 43, 47 })                                // B's own frames; 40 fully covered
            AssertFilled(run.Canvases[(int)n], redFrame, fill, TransitionPresentation.FillOpacity(n - 33, 15), $"{type} B {n}");
        AssertFilled(run.Canvases[48], redFrame, fill, 0, "after the zone");
    }

    [FfmpegFact]
    public async Task A_dip_on_a_lower_track_keeps_the_title_above_readable_at_the_cut()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var v2 = p.VideoTrack();
        var a = p.Video(v1, _media.Pattern(), 0, 40);
        var b = p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 40, 80);
        Add(p, v1, a, b, TransitionRules.DipToBlack, 11);
        var title = p.Text(v2, "Маршрут 6B", 30, 50);
        (title.FontSize, title.Bold) = (40, true);
        var run = await Export(p, "dip-under-title");

        await AssertPreviewEqualsExport(run, "dip under a title", 35, 40, 45);
        var bright = 0;
        for (var i = 0; i < run.Canvases[40].Length; i += 4) if (run.Canvases[40][i] > 200) bright++;
        Assert.True(bright > 100, $"the title's pixels at the black frame: {bright}");
    }

    // --- Zoom -------------------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task A_zoom_scales_each_clips_own_frames_about_the_centre()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var first = _media.Pattern();
        var second = _media.Pattern(FrameRate.Fps25, seconds: 5);
        var a = p.Video(v1, first, 0, 40);
        var b = p.Video(v1, second, 40, 80);
        Add(p, v1, a, b, TransitionRules.Zoom, 8);                                  // zone [36, 44): 4 in A, 4 in B
        var run = await Export(p, "zoom");

        await AssertPreviewEqualsExport(run, "zoom", 35, 36, 38, 39, 40, 41, 43, 44);

        // Independently: the canvas is far closer to ffmpeg's frame scaled by k about the centre than to the plain frame.
        foreach (var (n, path, source) in new[] { (39L, first.FilePath, 39L), (40L, second.FilePath, 0L) })
        {
            var k = TransitionPresentation.ZoomScale(n - 36, 8);
            var w = (int)Math.Round(E2EMedia.Width * k / 2) * 2;
            var h = (int)Math.Round(E2EMedia.Height * k / 2) * 2;
            var scaled = SourceFrame(path, source, $"scale={w}:{h}:flags=bilinear,crop={E2EMedia.Width}:{E2EMedia.Height}");
            var plain = SourceFrame(path, source);
            var (toScaled, toPlain) = (MeanAbs(run.Canvases[(int)n], scaled), MeanAbs(run.Canvases[(int)n], plain));
            _output.WriteLine($"zoom frame {n}: k {k:0.###}, mean |canvas − scaled| {toScaled:0.##}, |canvas − plain| {toPlain:0.##}");
            Assert.True(toScaled < 6 && toScaled < toPlain / 3, $"frame {n}");
        }
        Assert.True(MeanAbs(run.Canvases[44], SourceFrame(second.FilePath, 4)) < 1.5, "after the zone the frame is plain");
    }

    // --- Push -------------------------------------------------------------------------------------------------------

    [FfmpegTheory]
    [InlineData(TransitionDirection.FromRight)]
    [InlineData(TransitionDirection.FromLeft)]
    [InlineData(TransitionDirection.FromTop)]
    [InlineData(TransitionDirection.FromBottom)]
    public async Task A_push_shifts_both_sources_by_whole_pixels_from_their_handles(TransitionDirection direction)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var red = _media.Solid(Red, FrameRate.Fps25);
        var pattern = _media.Pattern();
        var a = p.Video(v1, red, 0, 40);                                            // 60 frames of source after A's end
        var b = p.Video(v1, pattern, 40, 80, sourceInFrame: 20);                    // 20 before B's start
        Add(p, v1, a, b, TransitionRules.Push, 8, direction);                       // zone [36, 44)
        var run = await Export(p, "push-" + direction);

        await AssertPreviewEqualsExport(run, $"push {direction}", 35, 36, 38, 39, 40, 43, 44);

        var (w, h) = (E2EMedia.Width, E2EMedia.Height);
        foreach (var n in new long[] { 37, 40, 42 })
        {
            var (_, motionB) = TransitionPresentation.PushMotions(n - 36, 8, direction, new FrameSize(w, h));
            var (dx, dy) = ((int)motionB.OffsetX, (int)motionB.OffsetY);
            var source = SourceFrame(pattern.FilePath, 20 + n - 40);                // B from its handle before the cut
            var max = 0;
            for (var y = Math.Max(0, dy) + 2; y < Math.Min(h, h + dy) - 2; y += 7)
                for (var x = Math.Max(0, dx) + 2; x < Math.Min(w, w + dx) - 2; x += 7)
                    max = Math.Max(max, MaxDifference(run.Canvases[(int)n], x, y, source, x - dx, y - dy, w));
            _output.WriteLine($"push {direction} frame {n}: B shifted by ({dx}, {dy}), max |canvas − source| {max}");
            Assert.True(max <= 3, $"frame {n}: {max}");
        }
    }

    // --- composition ------------------------------------------------------------------------------------------------

    private static ImmutableArray<NormalizedPoint> Box(double x0, double y0, double x1, double y1) => ImmutableArray.Create(
        new NormalizedPoint(x0, y0), new NormalizedPoint(x1, y0), new NormalizedPoint(x1, y1), new NormalizedPoint(x0, y1));

    private static HoldMarkup Outline() => new()
    {
        StrokeWidth1080 = 24, ColorHex = "#FFD600", Keyline = KeylineMode.On, KeylineActive = true,
        Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(), Box(0.3, 0.3, 0.6, 0.6), default, default, MediaTime.Zero))
    };

    [FfmpegTheory]
    [InlineData(TransitionRules.Push)]
    [InlineData(TransitionRules.Zoom)]
    [InlineData(TransitionRules.Flash)]
    public async Task Every_type_composes_with_transforms_an_outline_and_lower_and_upper_tracks_on_a_vertical_canvas(string type)
    {
        var p = new ProjectBuilder(FrameRate.Fps25, 180, 320);                      // a vertical canvas
        var v1 = p.VideoTrack();
        var v2 = p.VideoTrack();
        var v3 = p.VideoTrack();
        p.Image(v1, _media.Jpeg(), 0, 80);                                          // a lower track
        var a = p.Video(v2, _media.Pattern(), 0, 40);
        (a.RotationDegrees, a.Scale, a.Opacity, a.Crop) = (15, 0.9, 0.85, new CropRect(0.05, 0.1, 0.1, 0.05));
        a.HoldMarkup = Outline();
        var b = p.Video(v2, _media.Solid(Red, FrameRate.Fps25), 40, 80, sourceInFrame: 20);
        b.Scale = CompositionMath.FillScale(new FrameSize(180, 320), new FrameSize(E2EMedia.Width, E2EMedia.Height), VisualProperties.Default);   // Fill (D041)
        Add(p, v2, a, b, type, 10, type == TransitionRules.Push ? TransitionDirection.FromBottom : null);   // zone [35, 45)
        var title = p.Text(v3, "6B+", 20, 60);                                      // an upper track
        title.FontSize = 36;
        var run = await Export(p, "compose-" + type);

        await AssertPreviewEqualsExport(run, $"{type} composed", 34, 35, 37, 39, 40, 42, 44, 45);
    }

    // --- failures and missing media ----------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task Handles_missing_at_render_time_refuse_the_export_of_a_push_while_the_Preview_holds_the_first_frame()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var pattern = _media.Pattern();
        var a = p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 0, 40);
        var b = p.Video(v1, pattern, 40, 80);                                       // SourceIn 0: no handle before B
        Add(p, v1, a, b, TransitionRules.Push, 10);                                 // B shown from 35 anyway (media changed since)
        var destination = Path.Combine(_media.OutputFolder(), "push-no-handles.mp4");
        var job = Preflight(p.Project, destination);

        // D025 §4 as changed in Phase 28: refused before rendering — no file.
        var error = await Assert.ThrowsAsync<ExportException>(() =>
            Service(EncoderHarness.Encoder()).ExportAsync(job, new SyncProgress(_ => { }), CancellationToken.None));
        Assert.Equal(ExportFailure.TransitionSourceMissing, error.Failure);
        Assert.Equal(new MissingTransitionSource(TransitionRules.Push, v1.Id, a.Id, b.Id, p.F(40), BeforeRendering: true), error.MissingSource);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!));
        AssertNoFfmpegLeft();

        // The Preview still holds B's first frame before its first source frame (D025 §4).
        var (_, motionB) = TransitionPresentation.PushMotions(2, 10, TransitionDirection.FromRight, new FrameSize(E2EMedia.Width, E2EMedia.Height));
        var dx = (int)motionB.OffsetX;
        var first = SourceFrame(pattern.FilePath, 0);
        Assert.True(MaxDifference((await Preview(job.Snapshot, 37)).Pixels, dx + 10, 90, first, 10, 90, E2EMedia.Width) <= 3);
    }

    [FfmpegFact]
    public async Task A_source_that_vanishes_after_the_preflight_fails_the_export_inside_a_zone()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var a = p.Video(v1, _media.Pattern(), 0, 40);
        var vanishing = _media.CopyOf(_media.Solid(Red, FrameRate.Fps25));
        var b = p.Video(v1, vanishing, 40, 80, sourceInFrame: 20);
        Add(p, v1, a, b, TransitionRules.Push, 10);
        var folder = _media.OutputFolder();
        var destination = Path.Combine(folder, "vanished.mp4");
        var job = Preflight(p.Project, destination);
        File.Delete(vanishing.FilePath);

        var error = await Assert.ThrowsAsync<ExportException>(() =>
            Service(EncoderHarness.Encoder()).ExportAsync(job, new SyncProgress(_ => { }), CancellationToken.None));

        Assert.Equal(ExportFailure.DecodeFailed, error.Failure);
        Assert.False(File.Exists(destination));
        AssertNoFfmpegLeft();
    }

    [FfmpegTheory]
    [InlineData(TransitionRules.Zoom)]
    [InlineData(TransitionRules.Flash)]
    [InlineData(TransitionRules.Push)]
    public async Task Cancelling_inside_a_zone_leaves_nothing_behind(string type)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var a = p.Video(v1, _media.Pattern(), 0, 40);
        var b = p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 40, 80, sourceInFrame: 20);
        Add(p, v1, a, b, type, 10);                                                 // zone [35, 45)
        p.Audio(p.AudioTrack(), _media.Tone(), 0, 80);
        var folder = _media.OutputFolder();
        var job = Preflight(p.Project, Path.Combine(folder, "cancel.mp4"));

        var cts = new CancellationTokenSource();
        var progress = new SyncProgress(r => { if (r is { Stage: ExportStage.Video, Done: 39 }) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(EncoderHarness.Encoder()).ExportAsync(job, progress, cts.Token));

        Assert.Empty(Directory.GetFiles(folder));
        AssertNoFfmpegLeft();
    }

    [FfmpegFact]
    public async Task The_sound_is_the_same_with_and_without_a_library_transition()
    {
        ProjectBuilder Build(string? type)
        {
            var p = new ProjectBuilder(FrameRate.Ntsc30);
            var v1 = p.VideoTrack();
            var a = p.Video(v1, _media.Sync(FrameRate.Ntsc30), 0, 45);
            var b = p.Video(v1, _media.Sync(FrameRate.Ntsc30), 45, 90, sourceInFrame: 45);
            if (type is not null) Add(p, v1, a, b, type, 12);
            return p;
        }

        var without = await Export(Build(null), "sound-plain");
        foreach (var type in new[] { TransitionRules.DipToBlack, TransitionRules.Zoom, TransitionRules.Push })
            Assert.Equal(without.Pcm, (await Export(Build(type), "sound-" + type)).Pcm);   // PO-5: a hard cut of the sound
    }
}
