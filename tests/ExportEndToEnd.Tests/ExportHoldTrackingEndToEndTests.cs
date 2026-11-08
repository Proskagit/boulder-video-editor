using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Video.Tests;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// R2 → E, E-5: the hold tracking through the whole export with real parts (preflight with the camera-motion tracks →
/// ffmpeg decoding → the shared draw plan → the Avalonia rasterizer → H.264) against the Preview's own path (its snapshot
/// built by <see cref="PlaybackSnapshotBuilder"/> from the same tracks, its own pipeline and control). Compared byte for
/// byte — the criterion: both rasterize the same draw plan with the same rasterizer at the canvas size, so any difference
/// is a defect — on frames where the contour is carried by the track, between two samples (a blend), where it is hidden
/// (an unreliable stretch), and outside the track; the contour is at the place the track puts it (its edge moved by the
/// expected pixels); after "export without compensation" (a failed analysis, the user went on) both draw it where it was
/// marked; the MP4 matches the canvases within the codec's error.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportHoldTrackingEndToEndTests(E2EMedia media, ITestOutputHelper output)
{
    private const int Frames = 50;   // 2 s at 25 fps, 320 × 180

    private static ImmutableArray<NormalizedPoint> Box(double x0, double y0, double x1, double y1) => ImmutableArray.Create(
        new NormalizedPoint(x0, y0), new NormalizedPoint(x1, y0), new NormalizedPoint(x1, y1), new NormalizedPoint(x0, y1));

    private (ProjectBuilder Builder, VideoClip Clip) Build()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var clip = p.Video(p.VideoTrack(), media.Solid("C83232", FrameRate.Fps25), 0, Frames);
        clip.HoldMarkup = new HoldMarkup
        {
            StrokeWidth1080 = 32, ColorHex = "#FFFFFF", Keyline = KeylineMode.Off, KeylineActive = false,
            Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(), Box(0.1, 0.3, 0.3, 0.7), default, default, MediaTime.Zero))
        };
        return (p, clip);
    }

    /// <summary>The camera pans: the wall moves right by 0.1 of the frame per second (32 px of 320), analysed every 0.1 s
    /// up to 1.6 s; the samples at 1.2 and 1.3 s are unreliable (the contour hidden from 1.1 to 1.4 s); nothing after 1.6 s.</summary>
    private static CameraMotionTrack Pan() => new(MediaTime.Zero, CameraMotionModel.Similarity,
        Enumerable.Range(0, 17).Select(i => new CameraMotionSample(MediaTime.FromSeconds(i / 10.0),
            new WallTransform(1, 0, 0.01 * i, 0, 1, 0, 0, 0, 1), i is not (12 or 13))).ToImmutableArray());

    private async Task<ExportRun> Export(ProjectBuilder p, string name, ExportCameraMotion motion)
    {
        var path = Path.Combine(media.OutputFolder(), name);
        var result = ExportPreflight.Check(p.Project, path, EndToEnd.Environment with { CameraMotion = motion });
        Assert.True(result.CanExport, string.Join("; ", result.Errors.Select(e => e.Message)));
        var encoder = new RecordingEncoder(EncoderHarness.Encoder());
        var progress = new List<ExportProgress>();
        await Service(encoder).ExportAsync(result.Job!, new SyncProgress(progress.Add));
        return new ExportRun(path, result.Job!, encoder.Canvases, encoder.Pcm.ToArray(), progress) { EncoderSettings = encoder.Settings };
    }

    /// <summary>How white the column <paramref name="x"/> is at mid-height (the wall is red C83232; the stroke white).</summary>
    private static double White(byte[] canvas, int width, int x) => (Pixel(canvas, width, x, 90).G - 0x32) / (255.0 - 0x32);

    [FfmpegFact]
    public async Task The_preview_and_the_export_draw_the_tracked_contour_alike_on_every_kind_of_frame()
    {
        var (p, clip) = Build();
        var track = Pan();
        var tracks = new Dictionary<Guid, CameraMotionTrack> { [clip.Id] = track };
        var run = await Export(p, "tracked.mp4", new ExportCameraMotion(tracks, new HashSet<Guid>()));
        var w = run.Output.Size.Width;
        var previewSnapshot = PlaybackSnapshotBuilder.Build(p.Project, 1, tracks.ToImmutableDictionary());

        // 0: the reference; 10: carried 0.04 s·… (sample 0.4 s); 12: between two samples (0.48 s, a blend); 30, 32: hidden
        // (unreliable from 1.1 to 1.4 s); 38: carried again; 45: after the analysed range — drawn as marked.
        foreach (var n in new long[] { 0, 10, 12, 30, 32, 38, 45 })
        {
            var preview = await Preview(previewSnapshot, n);
            var differing = Enumerable.Range(0, preview.Pixels.Length).Count(i => preview.Pixels[i] != run.Canvases[(int)n][i]);
            Assert.True(differing == 0, $"frame {n}: {differing} bytes differ between the Preview and the export canvas");
        }

        // Where the contour is: its left edge is at x = 32 px as marked, moved by 32 px per second while carried.
        Assert.True(White(run.Canvases[0], w, 32) > 0.9);
        Assert.True(White(run.Canvases[10], w, 32 + 13) > 0.9);              // 0.4 s → 12.8 px
        Assert.True(White(run.Canvases[10], w, 32) < 0.1);                   // nothing left at the marked place
        Assert.True(White(run.Canvases[12], w, 32 + 15) > 0.9);              // 0.48 s, blended → 15.4 px
        Assert.True(White(run.Canvases[38], w, 32 + 49) > 0.9);              // 1.52 s → 48.6 px
        foreach (var n in new[] { 30, 32 })                                  // hidden: no outline anywhere on the row
            Assert.All(Enumerable.Range(0, w), x => Assert.True(White(run.Canvases[n], w, x) < 0.1, $"frame {n}, x {x}"));
        Assert.True(White(run.Canvases[45], w, 32) > 0.9);                   // past the analysed range: as marked

        var errors = FrameErrors(run);
        output.WriteLine($"{run.Output.FrameCount} frames, MP4 vs canvas mean |Δ| max {errors.Max():0.00}, avg {errors.Average():0.00}");
        Assert.All(errors, e => Assert.InRange(e, 0, 3.0));
        AssertValidMp4(run);
    }

    [FfmpegFact]
    public async Task Export_without_compensation_after_a_failed_analysis_draws_the_contour_as_marked_like_the_preview()
    {
        var (p, clip) = Build();
        var motion = new ExportCameraMotion(new Dictionary<Guid, CameraMotionTrack>(), new HashSet<Guid>()) { Failed = new HashSet<Guid> { clip.Id } };
        var check = ExportPreflight.Check(p.Project, Path.Combine(media.OutputFolder(), "x.mp4"), EndToEnd.Environment with { CameraMotion = motion });
        Assert.Single(check.Warnings, i => i.Kind == ExportIssueKind.HoldTrackingFailed);   // the user is told, then goes on

        var run = await Export(p, "untracked.mp4", motion);
        var w = run.Output.Size.Width;
        var previewSnapshot = PlaybackSnapshotBuilder.Build(p.Project, 1, ImmutableDictionary<Guid, CameraMotionTrack>.Empty);
        foreach (var n in new long[] { 0, 25, 49 })
        {
            var preview = await Preview(previewSnapshot, n);
            Assert.Equal(0, Enumerable.Range(0, preview.Pixels.Length).Count(i => preview.Pixels[i] != run.Canvases[(int)n][i]));
            Assert.True(White(run.Canvases[(int)n], w, 32) > 0.9);           // as marked, on every frame
        }
        AssertValidMp4(run);
    }
}
