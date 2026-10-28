using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// D048 stage 1, the held-out check (local, outside the solution; research only): clips that took no part in tuning the
/// split / chroma variant (<c>artifacts/d048-stage1-heldout/PROTOCOL.md</c>).
/// <list type="bullet">
/// <item><see cref="Check_the_candidates"/> — for each candidate clip and 8 s window, what the app would do there: the clean
/// frame and the frame at <c>T</c> decoded by <see cref="RouteSelectionFrameSource"/>, the click transfer by
/// <see cref="RouteSelectionTransfer"/> (a click at the frame's centre), the camera motion from the clean frame to every
/// second up to <c>T</c>; the clean frames written raw (BGRA) for the owner's contact sheets.</item>
/// <item><see cref="Build_the_heldout_project"/> — the reference project of the chosen windows, built exactly as
/// <see cref="ReferenceProjectBuildTests"/> (60 fps locked, the clips one after another on V1, markers «clean frame» and
/// <c>T</c>), saved, reopened and checked, with a manifest skeleton (the control clicks are recorded once the owner has
/// chosen the routes).</item>
/// </list>
/// </summary>
public sealed class ReferenceHeldoutTests(ITestOutputHelper output)
{
    private const int W = 1920, H = 1080;

    /// <summary>The candidates, in order of preference per source (replacements after the first four / three).</summary>
    private static readonly (string Source, string File)[] Candidates =
    [
        ("2709", @"D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_100628.mp4"),
        ("2709", @"D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_101229.mp4"),
        ("2709", @"D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_101712.mp4"),
        ("2709", @"D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_103026.mp4"),
        ("2709", @"D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_104651.mp4"),
        ("2709", @"D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_105713.mp4"),
        ("2709", @"D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_110133.mp4"),
        ("2709", @"D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_111424.mp4"),
        ("2709", @"D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_112606.mp4"),
        ("2709", @"D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_102533.mp4"),
        ("1705", @"D:\Времянка для монтажа\уку\video_20260517_134900.mp4"),
        ("1705", @"D:\Времянка для монтажа\уку\video_20260517_140207.mp4"),
        ("1705", @"D:\Времянка для монтажа\уку\video_20260517_140610.mp4"),
        ("1705", @"D:\Времянка для монтажа\уку\video_20260517_133557.mp4"),
        ("1705", @"D:\Времянка для монтажа\уку\video_20260517_133830.mp4"),
        ("1705", @"D:\Времянка для монтажа\уку\video_20260517_135227.mp4"),
        ("1705", @"D:\Времянка для монтажа\уку\video_20260517_140812.mp4"),
        ("1705", @"D:\Времянка для монтажа\уку\video_20260517_125453.mp4"),
    ];

    /// <summary>The 8 s windows tried: [from, from + 8), the clean frame at from + 1 s, <c>T</c> at from + 6 s.</summary>
    private static readonly double[] WindowStarts = [0, 5, 10, 20];

    [Fact]
    public async Task Check_the_candidates()
    {
        var dir = Environment.GetEnvironmentVariable("AIVE_HELDOUT_CHECK");
        if (dir is null)
        {
            output.WriteLine("SKIPPED: AIVE_HELDOUT_CHECK not set");
            return;
        }
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        Assert.False(File.Exists(Path.Combine(dir, "candidates.json")), "candidates.json exists — results are never overwritten");
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var probe = new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance), NullLogger<FfprobeMediaAnalysisService>.Instance);
        var frames = Frames(options);

        var results = new List<object>();
        foreach (var (source, file) in Candidates)
        {
            var metadata = (await probe.AnalyzeAsync(file)).Metadata!;
            var asset = new MediaAsset { FilePath = file, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
            var name = Path.GetFileNameWithoutExtension(file);
            var windows = new List<object>();
            foreach (var from in WindowStarts.Where(f => f + 8 <= metadata.Duration.TotalSeconds))
            {
                var clean = MediaTime.FromSeconds(from + 1);
                var t = MediaTime.FromSeconds(from + 6);
                var cleanSmall = await frames.DecodeAsync(asset, clean, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                var tSmall = await frames.DecodeAsync(asset, t, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                var transfer = RouteSelectionTransfer.Transfer(GrayImage.FromBgra(cleanSmall.Frame), cleanSmall.SourceTime, GrayImage.FromBgra(tSmall.Frame), tSmall.SourceTime, new NormalizedPoint(0.5, 0.5));

                // The camera from the clean frame to every second up to T (and T itself).
                var estimator = new CameraMotionEstimator(GrayImage.FromBgra(cleanSmall.Frame), cleanSmall.SourceTime);
                var times = new List<MediaTime>();
                for (var s = from + 2; s < from + 6; s++)
                {
                    var f = await frames.DecodeAsync(asset, MediaTime.FromSeconds(s), RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
                    estimator.Add(GrayImage.FromBgra(f.Frame), f.SourceTime);
                    times.Add(f.SourceTime);
                }
                estimator.Add(GrayImage.FromBgra(tSmall.Frame), tSmall.SourceTime);
                times.Add(tSmall.SourceTime);
                var built = estimator.Build();
                double maxShift = 0;
                var allReliable = true;
                foreach (var sample in built.Samples.Where(s => times.Contains(s.SourceTime)))
                {
                    allReliable &= sample.IsReliable;
                    foreach (var gx in new[] { 0.05, 0.5, 0.95 })
                    foreach (var gy in new[] { 0.05, 0.5, 0.95 })
                    {
                        var (x, y) = sample.ReferenceToFrame.Apply(gx, gy);
                        maxShift = Math.Max(maxShift, Math.Sqrt(Math.Pow((x - gx) * W, 2) + Math.Pow((y - gy) * H, 2)));
                    }
                }

                var full = await frames.DecodeAsync(asset, clean, Math.Min(metadata.DisplayWidth ?? metadata.Width ?? W, W), Math.Min(metadata.DisplayHeight ?? metadata.Height ?? H, H), default);
                var raw = $"{name}_from{from:0}.bgra";
                await File.WriteAllBytesAsync(Path.Combine(dir, "raw", raw), full.Frame.Pixels.ToArray());
                windows.Add(new
                {
                    from, clean_s = from + 1, t_s = from + 6, clean_decoded_s = cleanSmall.SourceTime.Ticks / 1e7, t_decoded_s = tSmall.SourceTime.Ticks / 1e7,
                    transfer_ok = transfer.Succeeded, motion_all_reliable = allReliable, motion_max_shift_px_1920 = maxShift,
                    raw, raw_size = new[] { full.Frame.Width, full.Frame.Height, full.Frame.Stride }
                });
                output.WriteLine($"{name} [{from}–{from + 8}): transfer {(transfer.Succeeded ? "ok" : "FAILED")}, motion ≤ {maxShift:0.0} px (all reliable {allReliable})");
            }
            results.Add(new
            {
                source, file, name, duration_s = metadata.Duration.TotalSeconds, width = metadata.Width, height = metadata.Height,
                frame_rate = metadata.FrameRate?.ToString(), hdr = metadata.ColorTransfer, windows
            });
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "candidates.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task Build_the_heldout_project()
    {
        var folder = Environment.GetEnvironmentVariable("AIVE_HELDOUT_PROJECT");
        var selection = Environment.GetEnvironmentVariable("AIVE_HELDOUT_SELECTION");
        if (folder is null || selection is null || !File.Exists(selection))
        {
            output.WriteLine("SKIPPED: AIVE_HELDOUT_PROJECT / AIVE_HELDOUT_SELECTION not set");
            return;
        }
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(), $"{folder} is not empty");
        using var chosen = JsonDocument.Parse(await File.ReadAllTextAsync(selection));
        var cases = chosen.RootElement.EnumerateArray().Select(c => (Case: c.GetProperty("case").GetString()!, File: c.GetProperty("file").GetString()!,
            From: c.GetProperty("from").GetDouble())).ToList();

        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        Assert.True(edit.SetFrameRate(FrameRate.Fps60).Success);                         // locked: a first clip no longer sets it
        var rate = projects.Current.Settings.FrameRate;
        Assert.True(projects.Current.Settings.IsFrameRateLocked);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var probe = new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance), NullLogger<FfprobeMediaAnalysisService>.Instance);
        var track = projects.Current.Timeline.VideoTracks[0];
        var at = MediaTime.Zero;
        var placed = new List<(string Case, Guid Clip, MediaTime Start)>();
        foreach (var c in cases)
        {
            var metadata = (await probe.AnalyzeAsync(c.File)).Metadata!;
            var asset = new MediaAsset { FilePath = c.File, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
            projects.AddMediaAssets(new[] { asset });
            Assert.True(edit.AddClip(asset.Id, track.Id, at).Success);
            var clip = track.Clips.OfType<VideoClip>().Single(v => v.MediaAssetId == asset.Id);
            // The clip at `at` shows source s at `at + s`: cut to [From, From + 8), then moved back to `at`.
            Assert.True(edit.TrimClip(clip.Id, ClipEdge.End, at + MediaTime.FromSeconds(c.From + 8)).Success);
            if (c.From > 0)
            {
                Assert.True(edit.TrimClip(clip.Id, ClipEdge.Start, at + MediaTime.FromSeconds(c.From)).Success);
                Assert.True(edit.MoveClips(new[] { clip.Id }, -MediaTime.FromSeconds(c.From).ToNearestFrame(rate)).Success);
            }
            Assert.Equal((at, MediaTime.FromSeconds(c.From), MediaTime.FromSeconds(c.From + 8)), (clip.TimelineStart, clip.SourceIn, clip.SourceOut));
            foreach (var (time, what) in new[] { (c.From + 1, "clean frame (T − 5 s)"), (c.From + 6, "T — click the control hold here") })
            {
                var result = edit.AddMarker(at + MediaTime.FromSeconds(time - c.From));
                Assert.True(result.Success, result.Message);
                projects.Current.Timeline.Markers.Single(m => m.Id == result.MarkerId).Label =
                    $"{c.Case} · {Path.GetFileNameWithoutExtension(c.File)} · {what} · source {time:0} s";
            }
            placed.Add((c.Case, clip.Id, at));
            at = clip.TimelineEnd;
        }
        await projects.SaveAsAsync(folder);

        // Closed and opened again by a new service: everything checked from the file; the manifest skeleton from it.
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(folder);
        Assert.Equal((FrameRate.Fps60, true), (project.Settings.FrameRate, project.Settings.IsFrameRateLocked));
        var clips = project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().OrderBy(v => v.TimelineStart).ToList();
        Assert.Equal(cases.Count, clips.Count);
        Assert.Equal(cases.Count * 2, project.Timeline.Markers.Count);
        var frames = Frames(options);
        var manifest = new List<object>();
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            var clip = clips[i];
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            Assert.Equal(c.File, asset.FilePath);
            Assert.Equal((placed[i].Start, MediaTime.FromSeconds(c.From), MediaTime.FromSeconds(c.From + 8), ClipSpeed.Normal),
                (clip.TimelineStart, clip.SourceIn, clip.SourceOut, clip.Speed));
            var tMarker = project.Timeline.Markers.Single(m => m.Label.StartsWith($"{c.Case} ·") && m.Label.Contains("· T "));
            var cleanMarker = project.Timeline.Markers.Single(m => m.Label.StartsWith($"{c.Case} ·") && m.Label.Contains("clean frame"));
            var tFrame = tMarker.Position.ToNearestFrame(project.Settings.FrameRate);
            var located = RouteSelectionLocator.Locate(clip, tFrame, project.Settings.FrameRate)!.Value;
            Assert.Equal(MediaTime.FromSeconds(c.From + 6), located.TargetSourceTime);        // exact in ticks
            Assert.Equal(MediaTime.FromSeconds(c.From + 1), located.CleanSourceTime);
            Assert.False(located.CleanAtFileStart);
            Assert.Equal(clip.TimelineStart + MediaTime.FromSeconds(1), cleanMarker.Position);
            var metadata = asset.Metadata!;
            var cleanFrame = await frames.DecodeAsync(asset, located.CleanSourceTime, Math.Min(metadata.DisplayWidth ?? metadata.Width ?? W, W), Math.Min(metadata.DisplayHeight ?? metadata.Height ?? H, H), default);
            var atT = await frames.DecodeAsync(asset, located.TargetSourceTime, RouteSelectionService.AnalysisWidth, RouteSelectionService.AnalysisWidth, default);
            manifest.Add(new
            {
                @case = c.Case, clip_id = clip.Id, file = c.File, source_range_s = new[] { c.From, c.From + 8 },
                timeline_start_ticks = clip.TimelineStart.Ticks,
                clean_marker = new { id = cleanMarker.Id, position_ticks = cleanMarker.Position.Ticks, label = cleanMarker.Label },
                t_marker = new { id = tMarker.Id, position_ticks = tMarker.Position.Ticks, timeline_frame = tFrame, label = tMarker.Label },
                clean_source = new { requested_ticks = located.CleanSourceTime.Ticks, decoded_ticks = cleanFrame.SourceTime.Ticks },
                t_source = new { requested_ticks = located.TargetSourceTime.Ticks, decoded_ticks = atT.SourceTime.Ticks },
                clean_frame_size = new[] { cleanFrame.Frame.Width, cleanFrame.Frame.Height },
                source_size = new[] { metadata.Width, metadata.Height }, colour_transfer = metadata.ColorTransfer,
                route = (string?)null, click = (object?)null       // filled once the owner has chosen the route
            });
            output.WriteLine($"{c.Case} {Path.GetFileName(c.File)}: clip {clip.TimelineStart}–{clip.TimelineEnd}, source {clip.SourceIn}–{clip.SourceOut}; " +
                             $"T → {located.TargetSourceTime} (decoded {atT.SourceTime.Ticks / 1e7:0.000000} s), clean → {located.CleanSourceTime} " +
                             $"(decoded {cleanFrame.SourceTime.Ticks / 1e7:0.000000} s, {cleanFrame.Frame.Width}×{cleanFrame.Frame.Height})");
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "control-clicks.json"), JsonSerializer.Serialize(new
        {
            note = "Held-out check of the D048 stage 1 split / chroma variant. Project built before any run on these clips; the control clicks " +
                   "are recorded at T once the owner has chosen each route. The variant is frozen in artifacts/d048-stage1-heldout/PROTOCOL.md.",
            project_sha256_at_build = Sha256(Path.Combine(folder, "project.json")),
            cases = manifest
        }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"saved, closed and reopened: {folder}");
    }

    /// <summary>
    /// The frames at <c>T</c> of the held-out project, display-oriented in the 1920 × 1080 box (the picture the click is made
    /// on, normalized to it), written raw (BGRA) to <c>AIVE_HELDOUT_TFRAMES</c> for choosing the control holds. The project is
    /// opened, never saved.
    /// </summary>
    [Fact]
    public async Task Dump_the_frames_at_T()
    {
        var folder = Environment.GetEnvironmentVariable("AIVE_HELDOUT_PROJECT");
        var dir = Environment.GetEnvironmentVariable("AIVE_HELDOUT_TFRAMES");
        if (folder is null || dir is null || !File.Exists(Path.Combine(folder, "project.json")))
        {
            output.WriteLine("SKIPPED: AIVE_HELDOUT_PROJECT / AIVE_HELDOUT_TFRAMES not set");
            return;
        }
        Directory.CreateDirectory(dir);
        var hash = Sha256(Path.Combine(folder, "project.json"));
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(folder);
        var frames = Frames(Microsoft.Extensions.Options.Options.Create(new FfmpegOptions()));
        var list = new List<object>();
        foreach (var clip in project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().OrderBy(v => v.TimelineStart))
        {
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var tMarker = project.Timeline.Markers.Where(m => m.Label.Contains("· T ")).Single(m => m.Position >= clip.TimelineStart && m.Position < clip.TimelineEnd);
            var caseId = tMarker.Label.Split(" · ")[0];
            var located = RouteSelectionLocator.Locate(clip, tMarker.Position.ToNearestFrame(project.Settings.FrameRate), project.Settings.FrameRate)!.Value;
            var m = asset.Metadata!;
            var f = await frames.DecodeAsync(asset, located.TargetSourceTime, Math.Min(m.DisplayWidth ?? m.Width ?? W, W), Math.Min(m.DisplayHeight ?? m.Height ?? H, H), default);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"{caseId}_T.bgra"), f.Frame.Pixels.ToArray());
            list.Add(new { @case = caseId, raw = $"{caseId}_T.bgra", size = new[] { f.Frame.Width, f.Frame.Height, f.Frame.Stride }, decoded_s = f.SourceTime.Ticks / 1e7,
                display_size = new[] { m.DisplayWidth ?? m.Width, m.DisplayHeight ?? m.Height } });
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "tframes.json"), JsonSerializer.Serialize(list));
        Assert.Equal(hash, Sha256(Path.Combine(folder, "project.json")));
    }

    /// <summary>
    /// The control clicks of the owner's routes (2026-10-28): one point per case on the frame at <c>T</c>, at the centre of a
    /// hold of the route's colour inside the first grid cell the owner named (`artifacts/d048-stage1-heldout/route-choice`,
    /// 8 × 6 cells landscape, 4 × 8 portrait), in pixels of the frame at <c>T</c> as the app shows it (display-oriented, in
    /// the 1920 × 1080 box). Chosen by Claude on <see cref="Dump_the_frames_at_T"/>'s frames rather than clicked on screen:
    /// the screen click only turns into this same normalized point.
    /// </summary>
    private static readonly (string Case, string Route, string Cell, double X, double Y, string Hold)[] Clicks =
    [
        ("H1", "red: E5, E3 (extra F3)", "E5", 1030, 805, "the large red hold in E5"),
        ("H2", "yellow: E6, D4 (extra D1)", "D4", 765, 635, "the small yellow hold at the left of D4 (E6 holds only a yellow volume)"),
        ("H3", "blue: D3, C6 (extra D2)", "D3", 757, 478, "the light-blue hold at the left of D3 (D2 shows the same light blue)"),
        ("H4", "yellow: C6, D3 (extra E2)", "D3", 752, 465, "the elongated yellow hold at the left of D3"),
        ("H5", "purple: B3, C7 (extra B1)", "B3", 172, 320, "the magenta-purple hold at the left of B3 (B1 shows the same colour)"),
        ("H6", "pink: A4, B5 (extra C1)", "A4", 55, 456, "the long pink hold in A4"),
        ("H7", "green: B3, C7 (extra C4)", "B3", 218, 372, "the green hold in B3"),
    ];

    /// <summary>
    /// Records the control clicks into the held-out project's manifest (<c>control-clicks.json</c>, the skeleton kept in
    /// <c>artifacts/d048-stage1-heldout/project-at-build</c>): each click replayed through the code the UI runs —
    /// <see cref="RouteSelectionService"/> (the transfer to the clean frame), <see cref="RouteHoldFinder"/> with its defaults
    /// (the reference colour, the control hold's mask) and the markup view model's «Разместить зацепки» (its review's first
    /// proposal must be the same hold; cancelled, nothing added). The project is opened, never saved (its hash checked).
    /// </summary>
    [Fact]
    public async Task Record_the_control_clicks()
    {
        var folder = Environment.GetEnvironmentVariable("AIVE_HELDOUT_PROJECT");
        if (folder is null || !File.Exists(Path.Combine(folder, "control-clicks.json")))
        {
            output.WriteLine("SKIPPED: AIVE_HELDOUT_PROJECT not set");
            return;
        }
        var projectHash = Sha256(Path.Combine(folder, "project.json"));
        using var skeleton = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "control-clicks.json")));
        Assert.Equal(projectHash, skeleton.RootElement.GetProperty("project_sha256_at_build").GetString());
        Assert.All(skeleton.RootElement.GetProperty("cases").EnumerateArray(), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("click").ValueKind));

        var projects = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        var project = await projects.OpenAsync(folder);
        var edit = new TimelineEditService(projects, new UndoRedoService(), NullLogger<TimelineEditService>.Instance);
        var rate = project.Settings.FrameRate;
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var decoder = Decoder(options);
        var frames = new RouteSelectionFrameSource(decoder);
        var routeSelection = new RouteSelectionService(frames);
        using var model = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await model.EnsureReadyAsync()).IsReady);
        var search = new RouteHoldSearchOptions();

        var cases = new List<object>();
        foreach (var s in skeleton.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = s.GetProperty("case").GetString()!;
            var c = Clicks.Single(k => k.Case == caseId);
            var clip = project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single(v => v.Id == s.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var tMarker = project.Timeline.Markers.Single(m => m.Id == s.GetProperty("t_marker").GetProperty("id").GetGuid());
            var tFrame = tMarker.Position.ToNearestFrame(rate);
            var atT = await frames.DecodeAsync(asset, new MediaTime(s.GetProperty("t_source").GetProperty("requested_ticks").GetInt64()),
                Math.Min(asset.Metadata!.DisplayWidth ?? asset.Metadata.Width ?? W, W), Math.Min(asset.Metadata.DisplayHeight ?? asset.Metadata.Height ?? H, H), default);
            var click = new NormalizedPoint(c.X / atT.Frame.Width, c.Y / atT.Frame.Height);
            int width = asset.Metadata.DisplayWidth ?? asset.Metadata.Width!.Value, height = asset.Metadata.DisplayHeight ?? asset.Metadata.Height!.Value;

            var prepared = (await routeSelection.PrepareAsync(clip, asset, tFrame, rate, click, default)).Prepared;
            Assert.NotNull(prepared);
            Assert.Equal(s.GetProperty("clean_source").GetProperty("decoded_ticks").GetInt64(), prepared!.Clean.SourceTime.Ticks);
            using var encoded = await model.PrepareFrameAsync(prepared.Clean.Frame);
            var found = await RouteHoldFinder.FindAsync(encoded, prepared.Clean.Frame, prepared.ControlPoint,
                Array.Empty<ImmutableArray<NormalizedPoint>>(), search);
            Assert.True(found.Search is not null, found.Error);
            var control = found.Search!.Candidates.Single(k => k.IsControl);

            // The UI's own path: «Разместить зацепки», the click, the review's first proposal; then cancelled.
            var status = new AiVideoEditor.UI.Services.StatusService();
            var markup = new AiVideoEditor.UI.ViewModels.Panels.HoldMarkupViewModel(edit, projects, status, model,
                new AiVideoEditor.UI.Services.HoldMarkupFrameSource(decoder), routeSelection: routeSelection) { Playhead = () => tMarker.Position };
            markup.SetClip(clip);
            markup.PlaceHoldsCommand.Execute(null);
            await markup.OnClickAsync(click, additive: false);
            Assert.True(markup.IsRouteReview, status.Message);
            Assert.True(control.Contour.SequenceEqual(markup.Proposals[0].Proposal.Contour), $"{caseId}: the UI path made another control hold");
            markup.CancelRouteCommand.Execute(null);
            Assert.Null(clip.HoldMarkup);

            var r = found.Search.Reference;
            cases.Add(new
            {
                @case = caseId, clip_id = clip.Id, file = asset.FilePath, display_size = new[] { width, height },
                source_range_s = new[] { clip.SourceIn.TotalSeconds, clip.SourceOut.TotalSeconds },
                t_marker = new { id = tMarker.Id, timeline_s = tMarker.Position.TotalSeconds, timeline_frame = tFrame, label = tMarker.Label },
                clean_marker = s.GetProperty("clean_marker"),
                t_source = new { requested_ticks = prepared.Frames.TargetSourceTime.Ticks, decoded_ticks = atT.SourceTime.Ticks },
                clean_source = new { requested_ticks = prepared.Frames.CleanSourceTime.Ticks, decoded_ticks = prepared.Clean.SourceTime.Ticks,
                    decoded_size = new[] { prepared.Clean.Frame.Width, prepared.Clean.Frame.Height } },
                route = new { owner_choice = c.Route, cell_used = c.Cell, hold = c.Hold },
                click = new
                {
                    on_frame = "T", frame_px = new[] { c.X, c.Y }, frame_size = new[] { atT.Frame.Width, atT.Frame.Height },
                    normalized = new[] { click.X, click.Y }, source_px = new[] { click.X * width, click.Y * height },
                    how = "chosen on the decoded frame at T (the picture the Preview shows), replayed through the UI code path"
                },
                control_on_clean_frame = new
                {
                    normalized = new[] { prepared.ControlPoint.X, prepared.ControlPoint.Y },
                    mask_contour_normalized = control.Contour.Select(p => new[] { p.X, p.Y }),
                    mask_area_fraction = control.AreaFraction, mask_score = control.Score
                },
                reference_oklab = new[] { r.L, r.A, r.B }, reference_chroma = Math.Sqrt(r.A * r.A + r.B * r.B)
            });
            output.WriteLine($"{caseId}: click ({click.X:0.0000}, {click.Y:0.0000}) → clean ({prepared.ControlPoint.X:0.0000}, {prepared.ControlPoint.Y:0.0000}), " +
                             $"mask {control.AreaFraction:P3} score {control.Score:0.00}, OKLab ({r.L:0.000}, {r.A:0.000}, {r.B:0.000}); UI path the same control");
        }

        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        await File.WriteAllTextAsync(Path.Combine(folder, "control-clicks.json"), JsonSerializer.Serialize(new
        {
            purpose = "D048 stage 1 held-out check: the control clicks of the owner's routes (not a reference marking)",
            project_folder = folder, project_json_sha256_when_recorded = projectHash, project_frame_rate = $"{rate.Numerator}/{rate.Denominator}",
            recorded = DateTimeOffset.Now, protocol = "artifacts/d048-stage1-heldout/PROTOCOL.md (frozen before these clips were prepared)",
            coordinates = "normalized to the clip's display-oriented frame (the overlay's NormalizedPoint); source px = normalized × display size",
            algorithm = new
            {
                source_sha256 = new[] { "Core/Holds/RouteHoldClassification.cs", "Core/Holds/RouteHoldFinder.cs", "Core/Holds/RouteSelection.cs",
                    "Core/Holds/HoldColorSampler.cs", "Core/Holds/HoldCandidateRules.cs", "Core/Motion/CameraMotionEstimator.cs" }
                    .ToDictionary(f => f, f => Sha256(Path.Combine(src, f.Replace('/', Path.DirectorySeparatorChar)))),
                model = new { id = HoldMarkup.CurrentModelId, files = Directory.GetFiles(ReferenceData.ModelDir).ToDictionary(Path.GetFileName, Sha256), device = model.Device.ToString() },
                hdr = "HLG → SDR by the app's rule (D033), the LUT of HdrLut.Generate"
            },
            cases
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(projectHash, Sha256(Path.Combine(folder, "project.json")));          // the project never written
    }

    /// <summary>
    /// The protocol's one run (<c>artifacts/d048-stage1-heldout/PROTOCOL.md</c>) on the owner's reference
    /// (<c>AIVE_HELDOUT_REFERENCE_SHA</c> = the hash fixed before the run). Per case, exactly as the split experiment: the
    /// frame and control point of <see cref="RouteSelectionService.PrepareAsync"/> (the reference holds must lie on that frame,
    /// in ticks), encoded once; the production <see cref="RouteHoldFinder.FindAsync"/> (baseline) and the frozen research copy
    /// <c>ReferenceSplitExperimentTests.ResearchFindAsync</c> with <c>split: true</c> (one level, 0.06, ×4, no grid), called
    /// unchanged by reflection, the order alternating per case; then <see cref="RouteHoldSelector"/> at τ 0.08 — for the
    /// variant after the chroma gate 0.3. On the first case the copy (unsplit) is checked against production. Writes
    /// <c>baseline/run.json</c>, <c>split-chroma/run.json</c> (the split experiment's format) and the raw frames to
    /// <c>AIVE_HELDOUT_RUN</c>; never overwrites, never writes the project.
    /// </summary>
    [Fact]
    public async Task Run_the_protocol_on_the_heldout_reference()
    {
        var folder = Environment.GetEnvironmentVariable("AIVE_HELDOUT_PROJECT");
        var dir = Environment.GetEnvironmentVariable("AIVE_HELDOUT_RUN");
        var expectedHash = Environment.GetEnvironmentVariable("AIVE_HELDOUT_REFERENCE_SHA");
        if (folder is null || dir is null || expectedHash is null)
        {
            output.WriteLine("SKIPPED: AIVE_HELDOUT_PROJECT / AIVE_HELDOUT_RUN / AIVE_HELDOUT_REFERENCE_SHA not set");
            return;
        }
        const double chromaRatio = 0.3;
        string[] variants = ["baseline", "split-chroma"];
        foreach (var v in variants)
            Assert.False(File.Exists(Path.Combine(dir, v, "run.json")), $"{v}/run.json exists — results are never overwritten");
        Assert.Equal(expectedHash, Sha256(Path.Combine(folder, "project.json")));
        var research = typeof(ReferenceSplitExperimentTests).GetMethod("ResearchFindAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Task<(RouteHoldSearch? Search, IReadOnlyList<(AiVideoEditor.Core.Composition.PointD Point, string Source)> Prompts)> Research(
            IHoldFrame encoded, DecodedFrame frame, NormalizedPoint control, RouteHoldSearchOptions o, bool split) =>
            (Task<(RouteHoldSearch?, IReadOnlyList<(AiVideoEditor.Core.Composition.PointD, string)>)>)research.Invoke(null, [encoded, frame, control, o, split, null, null])!;

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "control-clicks.json")));
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(folder);
        var rate = project.Settings.FrameRate;
        var frames = Frames(Microsoft.Extensions.Options.Options.Create(new FfmpegOptions()));
        var routeSelection = new RouteSelectionService(frames);
        using var model = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        Assert.True((await model.EnsureReadyAsync()).IsReady);
        var search = new RouteHoldSearchOptions();
        Directory.CreateDirectory(Path.Combine(dir, "frames"));

        var results = variants.ToDictionary(v => v, _ => new List<object>());
        var index = 0;
        foreach (var m in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = m.GetProperty("case").GetString()!;
            var clip = project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(c => c.Id == m.GetProperty("clip_id").GetGuid());
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            var holds = clip.HoldMarkup?.Holds ?? ImmutableArray<Hold>.Empty;
            Assert.NotEmpty(holds);
            var tFrame = m.GetProperty("t_marker").GetProperty("timeline_frame").GetInt64();
            var n = m.GetProperty("click").GetProperty("normalized");
            var click = new NormalizedPoint(n[0].GetDouble(), n[1].GetDouble());

            var sw = Stopwatch.StartNew();
            var prepared = (await routeSelection.PrepareAsync(clip, asset, tFrame, rate, click, default)).Prepared!;
            var prepareMs = sw.ElapsedMilliseconds;
            var cp = m.GetProperty("control_on_clean_frame").GetProperty("normalized");
            Assert.Equal(cp[0].GetDouble(), prepared.ControlPoint.X, 9);
            Assert.Equal(cp[1].GetDouble(), prepared.ControlPoint.Y, 9);
            // The algorithm runs on the frame the reference was marked on — exactly, in ticks; no neighbour substituted.
            Assert.Equal(prepared.Clean.SourceTime, Assert.Single(holds.Select(h => h.SourceTime).Distinct()));
            var frame = prepared.Clean;
            var control = prepared.ControlPoint;
            int w = frame.Frame.Width, h = frame.Frame.Height;
            await File.WriteAllBytesAsync(Path.Combine(dir, "frames", $"{caseId}.bgra"), frame.Frame.Pixels.ToArray());

            sw.Restart();
            using var encoded = await model.PrepareFrameAsync(frame.Frame);
            var encodeMs = sw.ElapsedMilliseconds;

            async Task<RouteHoldSearch> Production() => (await RouteHoldFinder.FindAsync(encoded, frame.Frame, control, ImmutableArray<ImmutableArray<NormalizedPoint>>.Empty, search)).Search!;
            if (index == 0)
            {
                var reference = await Production();
                var copy = (await Research(encoded, frame.Frame, control, search, false)).Search!;
                Assert.Equal(reference.Prompts, copy.Prompts);
                Assert.Equal(reference.Candidates.Count, copy.Candidates.Count);
                var a = reference.Select(RouteHoldColor.DefaultTolerance).Select(c => string.Join(";", c.Contour)).Order().ToList();
                var b = copy.Select(RouteHoldColor.DefaultTolerance).Select(c => string.Join(";", c.Contour)).Order().ToList();
                Assert.Equal(a, b);
                output.WriteLine($"{caseId}: research copy reproduces production ({copy.Candidates.Count} candidates, {a.Count} proposals)");
            }

            var runs = new Dictionary<string, (RouteHoldSearch Search, long SearchMs, IReadOnlyList<(AiVideoEditor.Core.Composition.PointD Point, string Source)> Prompts)>();
            async Task RunProduction()
            {
                var watch = Stopwatch.StartNew();
                var s = await Production();
                var ms = watch.ElapsedMilliseconds;
                var prompts = ColorPromptPlanner.Plan(frame.Frame, s.Reference, search.MapThreshold, maxPrompts: search.MaxColorPrompts).Select(p => (p, "colour"))
                    .Concat(HoldProposalService.GridPoints(w, h, search.GridDivisions).Select(p => (p, "grid"))).ToList();
                Assert.Equal(s.Prompts, prompts.Count);
                runs["baseline"] = (s, ms, prompts);
            }
            async Task RunSplit()
            {
                var watch = Stopwatch.StartNew();
                var (s, prompts) = await Research(encoded, frame.Frame, control, search, true);
                runs["split-chroma"] = (s!, watch.ElapsedMilliseconds, prompts);
            }
            if (index++ % 2 == 0) { await RunProduction(); await RunSplit(); }
            else { await RunSplit(); await RunProduction(); }

            foreach (var variant in variants)
            {
                var (s, searchMs, prompts) = runs[variant];
                var gate = variant == "split-chroma";
                static double Chroma(OkLabColor c) => Math.Sqrt(c.A * c.A + c.B * c.B);
                var refChroma = Chroma(s.Reference);
                var watch = Stopwatch.StartNew();
                var pool = gate ? s.Candidates.Where(c => c.IsControl || Chroma(c.Surface) >= chromaRatio * refChroma).ToList() : s.Candidates.ToList();
                var accepted = RouteHoldSelector.Select(pool, RouteHoldColor.DefaultTolerance);
                var selectMs = watch.Elapsed.TotalMilliseconds;
                var chromaRejected = gate ? s.Candidates.Where(c => !c.IsControl && Chroma(c.Surface) < chromaRatio * refChroma).Select(c => c.Id).ToArray() : [];
                output.WriteLine($"{caseId} {variant}: {prompts.Count} prompts, {s.Candidates.Count} candidates, {accepted.Count} proposals; " +
                                 $"prepare {prepareMs} + encode {encodeMs} + search {searchMs} + select {selectMs:0} ms");
                results[variant].Add(new
                {
                    @case = caseId, frame_s = frame.SourceTime.Ticks / 1e7, frame_ticks = frame.SourceTime.Ticks, on_reference_frame = true,
                    frame = new[] { w, h }, raw = $"frames/{caseId}.bgra", stride = frame.Frame.Stride,
                    timing_ms = new { decode_and_transfer = prepareMs, encode = encodeMs, search = searchMs, select = selectMs },
                    parameters = new
                    {
                        tau = RouteHoldColor.DefaultTolerance, search.MapThreshold, search.MaxColorPrompts, grid = !gate,
                        split_distance = gate ? 0.06 : (double?)null, large_region_factor = gate ? 4.0 : (double?)null,
                        chroma_ratio = gate ? chromaRatio : (double?)null, reference_chroma = refChroma
                    },
                    reference = holds.Select(x => new { id = x.Id, surface = new[] { x.SurfaceLab.L, x.SurfaceLab.A, x.SurfaceLab.B },
                        contour = x.Points.Select(p => new[] { p.X, p.Y }) }),
                    runs = new[]
                    {
                        new
                        {
                            label = variant, control = new[] { control.X, control.Y }, search_ms = searchMs,
                            reference_oklab = new[] { s.Reference.L, s.Reference.A, s.Reference.B }, prompts = prompts.Count,
                            prompt_points = prompts.Select(p => new { x = p.Point.X / w, y = p.Point.Y / h, source = p.Source }),
                            candidates = s.Candidates.Count,
                            predictions = accepted.Select(c => new { id = c.Id, is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                                contour = c.Contour.Select(p => new[] { p.X, p.Y }) }),
                            all_candidates = s.Candidates.Select(c => new { id = c.Id, is_control = c.IsControl, score = c.Score, distance = c.Distance, area = c.AreaFraction,
                                surface = new[] { c.Surface.L, c.Surface.A, c.Surface.B }, contour = c.Contour.Select(p => new[] { p.X, p.Y }) }),
                            chroma_rejected = chromaRejected
                        }
                    }
                });
            }
        }

        foreach (var v in variants)
        {
            Directory.CreateDirectory(Path.Combine(dir, v));
            await File.WriteAllTextAsync(Path.Combine(dir, v, "run.json"), JsonSerializer.Serialize(results[v]));
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "environment.json"), JsonSerializer.Serialize(new
        {
            run = DateTimeOffset.Now, reference_sha256 = expectedHash, device = model.Device.ToString(), model = HoldMarkup.CurrentModelId
        }));
        Assert.Equal(expectedHash, Sha256(Path.Combine(folder, "project.json")));          // the reference never written
    }

    private static FfmpegVideoDecoder Decoder(Microsoft.Extensions.Options.IOptions<FfmpegOptions> options)
    {
        var luts = Environment.GetEnvironmentVariable("AIVE_HELDOUT_LUTS") ?? throw new InvalidOperationException("AIVE_HELDOUT_LUTS not set");
        Directory.CreateDirectory(luts);
        return new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance, luts: new LutStore(luts));
    }

    /// <summary>The app's frame source with the app's HDR → SDR rule (D033): the decoder gets an <see cref="IHdrLutStore"/> as in the
    /// app's composition — here <see cref="LutStore"/>, the same LUT text (<see cref="AiVideoEditor.Core.Color.HdrLut.Generate"/>)
    /// in <c>AIVE_HELDOUT_LUTS</c>, not the app's cache.</summary>
    private static RouteSelectionFrameSource Frames(Microsoft.Extensions.Options.IOptions<FfmpegOptions> options)
    {
        var luts = Environment.GetEnvironmentVariable("AIVE_HELDOUT_LUTS") ?? throw new InvalidOperationException("AIVE_HELDOUT_LUTS not set");
        Directory.CreateDirectory(luts);
        return new RouteSelectionFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance),
            NullLogger<FfmpegVideoDecoder>.Instance, luts: new LutStore(luts)));
    }

    /// <summary>The app's <c>HdrLutStore</c> reduced to what a test needs (the Media assembly isn't referenced here): the
    /// <c>.cube</c> of <see cref="AiVideoEditor.Core.Color.HdrLut.Generate"/> under <see cref="AiVideoEditor.Core.Color.HdrLut.FileName"/>.</summary>
    private sealed class LutStore(string folder) : IHdrLutStore
    {
        public Task<string> GetLutPathAsync(AiVideoEditor.Core.Color.HdrConversion conversion, CancellationToken ct = default)
        {
            var path = Path.Combine(folder, AiVideoEditor.Core.Color.HdrLut.FileName(conversion));
            var text = AiVideoEditor.Core.Color.HdrLut.Generate(conversion);
            if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            return Task.FromResult(path);
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
