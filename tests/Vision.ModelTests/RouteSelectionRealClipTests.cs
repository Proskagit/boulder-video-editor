using System.Collections.Immutable;
using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// D048 stage 1, local, on the owner's clips of 2026-09-27 (<c>AIVE_BOULDER_DIR</c>, default
/// <c>D:\Времянка для монтажа\Боулдер 27.09.2026</c>): "Place holds" through the markup view model with the real model,
/// FFmpeg decoder and the route selection service — the click on the control hold at <c>T</c>, the transfer to the clean
/// frame 5 s earlier, the search, the review at τ = 0.06 / 0.08 / 0.10, Accept = one step at <c>t_C</c>, back to <c>T</c>.
/// Writes the clean frame with the proposals (<c>AIVE_ROUTE_OUT</c>) for the owner's review. Not an acceptance by itself:
/// whether the proposals are the route's holds is the owner's call (D048 §5 stage 1, 2 and 7).
/// </summary>
public sealed class RouteSelectionRealClipTests(ITestOutputHelper output)
{
    private static string ClipsDir => Environment.GetEnvironmentVariable("AIVE_BOULDER_DIR") is { Length: > 0 } dir
        ? dir : @"D:\Времянка для монтажа\Боулдер 27.09.2026";

    private static string OutDir => Environment.GetEnvironmentVariable("AIVE_ROUTE_OUT") is { Length: > 0 } dir
        ? dir : Path.Combine(Path.GetTempPath(), "aive-route-selection");

    [Theory]
    [InlineData("video_20260927_100405.mp4", 11.0, 0.451, 0.715)]   // the coral route: a small coral hold, lower middle
    [InlineData("video_20260927_103744.mp4", 11.0, 0.589, 0.848)]   // the blue route: the blue sloper, lower middle
    public async Task Place_holds_on_an_owner_clip(string file, double tSeconds, double x, double y)
    {
        var path = Path.Combine(ClipsDir, file);
        if (!File.Exists(path))
        {
            output.WriteLine($"SKIPPED: {path} not found");
            return;
        }

        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var metadata = (await new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance),
            NullLogger<FfprobeMediaAnalysisService>.Instance).AnalyzeAsync(path)).Metadata!;
        var asset = new MediaAsset { FilePath = path, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
        projects.AddMediaAssets(new[] { asset });
        Assert.True(edit.AddClip(asset.Id, projects.Current.Timeline.VideoTracks[0].Id, MediaTime.Zero).Success);
        var clip = projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single();
        var steps = 0;
        undo.StateChanged += (_, _) => steps++;

        using var service = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        var decoder = new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance);
        var status = new StatusService();
        var t = MediaTime.FromSeconds(tSeconds).SnapToFrame(projects.Current.Settings.FrameRate);
        var seeks = new List<MediaTime>();
        var markup = new HoldMarkupViewModel(edit, projects, status, service, new HoldMarkupFrameSource(decoder),
            routeSelection: new RouteSelectionService(new RouteSelectionFrameSource(decoder)))
        {
            Playhead = () => t,
            SeekTo = seeks.Add
        };
        markup.SetClip(clip);
        output.WriteLine($"{file}: {metadata.DisplayWidth ?? metadata.Width}×{metadata.DisplayHeight ?? metadata.Height}, " +
                         $"{metadata.ColorTransfer ?? "?"}, T = {t}, click ({x}, {y})");

        Assert.True(markup.PlaceHoldsCommand.CanExecute(null));
        markup.PlaceHoldsCommand.Execute(null);
        var sw = Stopwatch.StartNew();
        await markup.OnClickAsync(new NormalizedPoint(x, y), additive: false);
        output.WriteLine($"click → review: {sw.ElapsedMilliseconds} ms; review {markup.IsRouteReview}; status: {status.Message}");
        if (!markup.IsRouteReview)
        {
            Assert.Null(clip.HoldMarkup);                                           // a failure changes nothing
            Assert.Equal(new[] { t }, seeks);
            return;
        }

        var control = markup.Proposals[0].Proposal;
        output.WriteLine($"control hold on the clean frame: centre ({control.Centroid.X * 1920:0}, {control.Centroid.Y * 1080:0}) at the 1920 scale, area {control.AreaFraction:P2}");
        foreach (var tolerance in new[] { 0.06m, 0.08m, 0.10m })
        {
            sw.Restart();
            markup.RouteTolerance = tolerance;
            output.WriteLine($"τ = {tolerance}: {markup.Proposals.Count} proposals ({sw.ElapsedMilliseconds} ms, no model call)");
        }
        markup.RouteTolerance = 0.08m;
        var name = Path.GetFileNameWithoutExtension(file);
        Directory.CreateDirectory(OutDir);
        var png = Path.Combine(OutDir, $"{name}-clean-proposals.png");
        Draw(markup.MarkupFrame!, markup.ProposalContours, png);
        output.WriteLine($"overlay: {png}");

        await markup.AcceptRouteCommand.ExecuteAsync(null);
        var holds = clip.HoldMarkup!.Holds;
        output.WriteLine($"accepted: {holds.Length} holds at {holds[0].SourceTime} in {steps} step(s); seeks {string.Join(", ", seeks)}; {status.Message}");
        Assert.Equal(1, steps);
        Assert.All(holds, h => Assert.Equal(holds[0].SourceTime, h.SourceTime));
        Assert.Equal(new[] { t }, seeks);
        undo.Undo();
        Assert.Null(clip.HoldMarkup);
    }

    /// <summary>The frame with the contours (yellow, 3 px) as a PNG through the FFmpeg CLI.</summary>
    private static void Draw(DecodedFrame frame, ImmutableArray<ImmutableArray<NormalizedPoint>> contours, string png)
    {
        int w = frame.Width, h = frame.Height;
        var pixels = new byte[w * h * 4];
        for (var row = 0; row < h; row++) frame.Pixels.Span.Slice(row * frame.Stride, w * 4).CopyTo(pixels.AsSpan(row * w * 4));
        void Dot(int px, int py)
        {
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int qx = px + dx, qy = py + dy;
                if (qx < 0 || qy < 0 || qx >= w || qy >= h) continue;
                var i = (qy * w + qx) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2]) = (0, 234, 255);
            }
        }
        foreach (var contour in contours)
            for (var k = 0; k < contour.Length; k++)
            {
                var (a, b) = (contour[k], contour[(k + 1) % contour.Length]);
                var n = (int)Math.Ceiling(Math.Max(Math.Abs(a.X - b.X) * w, Math.Abs(a.Y - b.Y) * h)) + 1;
                for (var s = 0; s <= n; s++)
                    Dot((int)((a.X + (b.X - a.X) * s / n) * w), (int)((a.Y + (b.Y - a.Y) * s / n) * h));
            }
        var raw = png + ".bgra";
        File.WriteAllBytes(raw, pixels);
        using var process = Process.Start(new ProcessStartInfo("ffmpeg", $"-v error -y -f rawvideo -pix_fmt bgra -s {w}x{h} -i \"{raw}\" \"{png}\"")
            { UseShellExecute = false })!;
        process.WaitForExit();
        File.Delete(raw);
    }
}
