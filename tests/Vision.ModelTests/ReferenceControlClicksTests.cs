using System.Security.Cryptography;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Motion;
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
/// D048 stage 1, the reference project's control clicks (local, outside the solution). The six clicks were made in the Debug
/// app through "Place holds" on each case's <c>T</c> marker (2026-10-27, Computer Use); their screenshot positions and the
/// Preview picture's measured rectangle give the click in the tool's own coordinates — a point normalized to the clip's
/// source frame (the overlay's <c>NormalizedPoint</c>, display-oriented, independent of the window). Here they are replayed
/// through the same code the UI runs — <see cref="RouteSelectionService"/> (the transfer to the clean frame),
/// <see cref="RouteHoldFinder"/> with its defaults (the reference colour and the control hold's mask) and the markup view
/// model (the control proposal the review showed) — and written with every parameter a later run needs into
/// <c>control-clicks.json</c> beside the project. The project is opened, never saved.
/// </summary>
public sealed class ReferenceControlClicksTests(ITestOutputHelper output)
{
    /// <summary>The Preview's picture in the screenshots' coordinate frame (1568 × 656, the 3440 × 1440 screen scaled),
    /// measured on the edges: left, top, right, bottom.</summary>
    private const double Left = 437.0, Top = 64.09, Right = 1108.0, Bottom = 440.91;

    private static readonly (string Case, double X, double Y, string Hold, string Ui)[] Clicks =
    {
        ("01", 746, 329, "small coral hold, the owner's arrow 9", "mcp-computer-use-blob-1793133051820-7hathl.jpg"),
        ("02", 751, 294, "blue pentagon hold right of the black bucket, the owner's arrow 13", "mcp-computer-use-blob-1793133180535-46sg7p.jpg"),
        ("03", 758, 150, "green hold at the top of the teal panel, the owner's arrow 2", "mcp-computer-use-blob-1793133252596-649xzt.jpg"),
        ("05", 729, 205, "L-shaped dark-green hold on the teal panel, the owner's arrow 4", "mcp-computer-use-blob-1793133326889-ei5kqk.jpg"),
        ("06", 807, 326, "dark-blue hold right of the yellow bar, the owner's arrows 15–16", "mcp-computer-use-blob-1793133412923-liewc0.jpg"),
        ("07", 594, 222, "olive hold, the owner's arrow 4", "mcp-computer-use-blob-1793133494940-r4zha5.jpg"),
    };

    [Fact]
    public async Task Record_the_control_clicks_beside_the_reference_project()
    {
        var folder = Environment.GetEnvironmentVariable("AIVE_REFERENCE_OUT");
        var uiShots = Environment.GetEnvironmentVariable("AIVE_UI_SHOTS");
        if (folder is null || !File.Exists(Path.Combine(folder, "project.json")))
        {
            output.WriteLine("SKIPPED: AIVE_REFERENCE_OUT has no project");
            return;
        }
        var projectHash = Sha256(Path.Combine(folder, "project.json"));
        var projects = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        var project = await projects.OpenAsync(folder);
        var edit = new TimelineEditService(projects, new UndoRedoService(), NullLogger<TimelineEditService>.Instance);
        var rate = project.Settings.FrameRate;
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var decoder = new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance);
        var frames = new RouteSelectionFrameSource(decoder);
        var routeSelection = new RouteSelectionService(frames);
        using var model = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await model.EnsureReadyAsync()).IsReady);
        var search = new RouteHoldSearchOptions();
        var clips = project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().OrderBy(v => v.TimelineStart).ToList();

        var cases = new List<object>();
        for (var i = 0; i < Clicks.Length; i++)
        {
            var c = Clicks[i];
            var clip = clips[i];
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var tMarker = project.Timeline.Markers.Single(m => m.Label.StartsWith($"{c.Case} ·") && m.Label.Contains("· T "));
            var cleanMarker = project.Timeline.Markers.Single(m => m.Label.StartsWith($"{c.Case} ·") && m.Label.Contains("clean frame"));
            var tFrame = tMarker.Position.ToNearestFrame(rate);
            var click = new NormalizedPoint((c.X - Left) / (Right - Left), (c.Y - Top) / (Bottom - Top));
            int width = asset.Metadata!.DisplayWidth ?? asset.Metadata.Width!.Value, height = asset.Metadata.DisplayHeight ?? asset.Metadata.Height!.Value;

            // The transfer and the clean frame — RouteSelectionService, as the view model calls it.
            var prepared = (await routeSelection.PrepareAsync(clip, asset, tFrame, rate, click, default)).Prepared;
            Assert.NotNull(prepared);
            var atT = await frames.DecodeAsync(asset, prepared!.Frames.TargetSourceTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);

            // The reference colour and the control hold's mask — RouteHoldFinder with its defaults on the encoded clean frame.
            using var encoded = await model.PrepareFrameAsync(prepared.Clean.Frame);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var found = await RouteHoldFinder.FindAsync(encoded, prepared.Clean.Frame, prepared.ControlPoint,
                Array.Empty<System.Collections.Immutable.ImmutableArray<NormalizedPoint>>(), search);
            var searchMs = sw.ElapsedMilliseconds;
            Assert.True(found.Search is not null, found.Error);
            var control = found.Search!.Candidates.Single(k => k.IsControl);

            // The UI's own path once more: the markup view model's review lists the control proposal first.
            var status = new StatusService();
            var markup = new HoldMarkupViewModel(edit, projects, status, model, new HoldMarkupFrameSource(decoder),
                routeSelection: routeSelection) { Playhead = () => tMarker.Position };
            markup.SetClip(clip);
            markup.PlaceHoldsCommand.Execute(null);
            await markup.OnClickAsync(click, additive: false);
            Assert.True(markup.IsRouteReview, status.Message);
            var uiControl = markup.Proposals[0].Proposal;
            Assert.True(control.Contour.SequenceEqual(uiControl.Contour), "the UI path made another control hold");   // the same hold
            markup.CancelRouteCommand.Execute(null);
            Assert.Null(clip.HoldMarkup);

            cases.Add(new
            {
                @case = c.Case, clip_id = clip.Id, clip_index_on_V1 = i, file = asset.FilePath, display_size = new[] { width, height },
                clip_timeline = new { start_s = clip.TimelineStart.TotalSeconds, end_s = clip.TimelineEnd.TotalSeconds },
                clip_source = new { in_s = clip.SourceIn.TotalSeconds, out_s = clip.SourceOut.TotalSeconds, speed = "1/1" },
                t_marker = new { id = tMarker.Id, timeline_s = tMarker.Position.TotalSeconds, timeline_frame = tFrame, label = tMarker.Label },
                clean_marker = new { id = cleanMarker.Id, timeline_s = cleanMarker.Position.TotalSeconds, label = cleanMarker.Label },
                t_source = new { requested_ticks = prepared.Frames.TargetSourceTime.Ticks, decoded_frame_s = atT.SourceTime.Ticks / 1e7 },
                clean_source = new { requested_ticks = prepared.Frames.CleanSourceTime.Ticks, decoded_frame_s = prepared.Clean.SourceTime.Ticks / 1e7,
                    decoded_size = new[] { prepared.Clean.Frame.Width, prepared.Clean.Frame.Height }, at_file_start = prepared.Frames.CleanAtFileStart },
                click = new
                {
                    hold = c.Hold, on_frame = "T",
                    normalized = new[] { click.X, click.Y },
                    source_px = new[] { click.X * width, click.Y * height },
                    screen = new { x = c.X, y = c.Y, frame = "screenshot 1568 × 656 of the 3440 × 1440 screen", preview_picture = new[] { Left, Top, Right, Bottom } },
                    uncertainty_source_px = 0.5 / (Right - Left) * width
                },
                control_on_clean_frame = new
                {
                    normalized = new[] { prepared.ControlPoint.X, prepared.ControlPoint.Y },
                    source_px = new[] { prepared.ControlPoint.X * width, prepared.ControlPoint.Y * height },
                    mask_contour_normalized = control.Contour.Select(p => new[] { p.X, p.Y }),
                    mask_area_fraction = control.AreaFraction, mask_score = control.Score,
                    centroid_normalized = new[] { control.Centroid.X, control.Centroid.Y }
                },
                reference_oklab = new[] { found.Search.Reference.L, found.Search.Reference.A, found.Search.Reference.B },
                ui_evidence = $"ui/{c.Case}-review.jpg",
                replay_search_ms = searchMs
            });
            if (uiShots is not null && File.Exists(Path.Combine(uiShots, c.Ui)))
            {
                Directory.CreateDirectory(Path.Combine(folder, "ui"));
                File.Copy(Path.Combine(uiShots, c.Ui), Path.Combine(folder, "ui", $"{c.Case}-review.jpg"), overwrite: false);
            }
            output.WriteLine($"{c.Case}: click ({click.X:0.00000}, {click.Y:0.00000}) → clean ({prepared.ControlPoint.X:0.00000}, {prepared.ControlPoint.Y:0.00000}), " +
                             $"reference OKLab ({found.Search.Reference.L:0.000}, {found.Search.Reference.A:0.000}, {found.Search.Reference.B:0.000}), " +
                             $"mask {control.AreaFraction:P3}, score {control.Score:0.00}; UI path the same control");
        }

        var modelDir = ReferenceData.ModelDir;
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var manifest = new
        {
            purpose = "D048 stage 1: the original control clicks of the reference project (not a reference marking)",
            project_folder = folder, project_json_sha256_when_recorded = projectHash, project_frame_rate = $"{rate.Numerator}/{rate.Denominator}",
            recorded = DateTimeOffset.Now,
            coordinates = "normalized to the clip's source frame, display-oriented (the overlay's NormalizedPoint): x = px / display width, " +
                          "y = px / display height; source px = normalized × display size; the clean-frame point is the click carried by the transfer",
            how_clicked = "Debug app, the case's T marker, Inspector «Разместить зацепки», one click on the Preview; the review's first proposal " +
                          "(yellow) checked to be the clicked hold; cancelled (the project unchanged)",
            algorithm = new
            {
                code = "branch docs/d048-route-timing, HEAD 024f3fb with uncommitted changes (D048 stages 1–3); source hashes below",
                source_sha256 = new[] { "Core/Holds/RouteHoldClassification.cs", "Core/Holds/RouteHoldFinder.cs", "Core/Holds/RouteSelection.cs",
                    "Core/Holds/HoldColorSampler.cs", "Core/Holds/HoldCandidateRules.cs", "Core/Motion/CameraMotionEstimator.cs" }
                    .ToDictionary(f => f, f => Sha256(Path.Combine(src, f.Replace('/', Path.DirectorySeparatorChar)))),
                model = new { id = HoldMarkup.CurrentModelId, folder = modelDir, files = Directory.GetFiles(modelDir).ToDictionary(Path.GetFileName, Sha256), device = model.Device.ToString() },
                clean_offset_s = RouteSelectionLocator.CleanOffset.TotalSeconds,
                frame_choice = "the last source frame at or before the time (RouteSelectionFrameSource)",
                transfer = new { analysis_width = RouteSelectionService.AnalysisWidth, CameraMotionEstimator.MedianLimit1920, CameraMotionEstimator.P95Limit1920,
                    CameraMotionEstimator.InlierThreshold1920, CameraMotionEstimator.MinInliers, CameraMotionEstimator.MinInlierShare },
                clean_frame_box = new[] { HoldProposalGenerator.DefaultMaxWidth, HoldProposalGenerator.DefaultMaxHeight },
                control_mask = new { rule = "HoldCandidateRules.Choose, largest component", HoldCandidateRules.MaxAreaFraction,
                    HoldCandidateRules.MaxConfidentAreaFraction, MinConfidentScore = (double)HoldCandidateRules.MinConfidentScore },
                reference_colour = new { rule = "HoldColorSampler surface (OKLab median)", HoldColorSampler.ErodeFraction, HoldColorSampler.MinErode,
                    HoldColorSampler.TrimFraction },
                search = new { search.MapThreshold, map_width = ColorPromptPlanner.MapWidth, search.MaxColorPrompts, search.UseGrid, search.GridDivisions,
                    search.NmsIouThreshold },
                selection = new { tolerance = RouteHoldColor.DefaultTolerance, RouteHoldColor.MinScore, RouteHoldColor.LightnessWeight,
                    RouteHoldSelector.FragmentOverlap }
            },
            cases
        };
        await File.WriteAllTextAsync(Path.Combine(folder, "control-clicks.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(projectHash, Sha256(Path.Combine(folder, "project.json")));          // the project never written
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
