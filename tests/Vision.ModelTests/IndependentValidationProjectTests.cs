using System.Security.Cryptography;
using System.Text.Json;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
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
/// D048 independent validation (<c>artifacts/d048-colour-audit/independent-validation/PLAN.md</c>, pilot as amended by
/// the owner on 2026-10-28: one chosen frame per clip, no click transfer): the marking project. Each clip is cut to an
/// 8 s window around its frame (60 fps locked, one after another on V1), with one marker «кадр разметки» exactly at the
/// frame. Built with the app's own services, saved, reopened and checked; a manifest with the frame's source time as
/// requested and as decoded by <see cref="RouteSelectionFrameSource"/> (the app's frame in the 1920 × 1080 box). Nothing
/// is searched. Inputs: <c>AIVE_IV_PROJECT</c> (an empty folder), <c>AIVE_IV_SELECTION</c> (JSON: case, file, frame_s).
/// </summary>
public sealed class IndependentValidationProjectTests(ITestOutputHelper output)
{
    private const int W = 1920, H = 1080;

    [Fact]
    public async Task Build_the_marking_project()
    {
        var folder = Environment.GetEnvironmentVariable("AIVE_IV_PROJECT");
        var selection = Environment.GetEnvironmentVariable("AIVE_IV_SELECTION");
        if (folder is null || selection is null || !File.Exists(selection))
        {
            output.WriteLine("SKIPPED: AIVE_IV_PROJECT / AIVE_IV_SELECTION not set");
            return;
        }
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(), $"{folder} is not empty");
        using var chosen = JsonDocument.Parse(await File.ReadAllTextAsync(selection));
        var cases = chosen.RootElement.EnumerateArray().Select(c => (Case: c.GetProperty("case").GetString()!, File: c.GetProperty("file").GetString()!,
            Frame: c.GetProperty("frame_s").GetDouble())).ToList();

        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        Assert.True(edit.SetFrameRate(FrameRate.Fps60).Success);
        var rate = projects.Current.Settings.FrameRate;
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var probe = new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance), NullLogger<FfprobeMediaAnalysisService>.Instance);
        var track = projects.Current.Timeline.VideoTracks[0];
        var at = MediaTime.Zero;
        var placed = new List<(string Case, Guid Clip, double From)>();
        foreach (var c in cases)
        {
            var metadata = (await probe.AnalyzeAsync(c.File)).Metadata!;
            Assert.Null(HdrColor.Classify(MediaKind.Video, metadata).Conversion);                  // SDR set
            var asset = new MediaAsset { FilePath = c.File, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
            projects.AddMediaAssets(new[] { asset });
            Assert.True(edit.AddClip(asset.Id, track.Id, at).Success);
            var clip = track.Clips.OfType<VideoClip>().Single(v => v.MediaAssetId == asset.Id);
            // An 8 s window on whole seconds containing the frame, inside the file.
            var from = Math.Max(0, Math.Min(Math.Floor(c.Frame) - 4, Math.Floor(metadata.Duration.TotalSeconds) - 8));
            Assert.True(c.Frame > from && c.Frame < from + 8, $"{c.Case}: the frame is outside its window");
            Assert.True(edit.TrimClip(clip.Id, ClipEdge.End, at + MediaTime.FromSeconds(from + 8)).Success);
            if (from > 0)
            {
                Assert.True(edit.TrimClip(clip.Id, ClipEdge.Start, at + MediaTime.FromSeconds(from)).Success);
                Assert.True(edit.MoveClips(new[] { clip.Id }, -MediaTime.FromSeconds(from).ToNearestFrame(rate)).Success);
            }
            Assert.Equal((at, MediaTime.FromSeconds(from), MediaTime.FromSeconds(from + 8)), (clip.TimelineStart, clip.SourceIn, clip.SourceOut));
            var result = edit.AddMarker(at + MediaTime.FromSeconds(c.Frame - from));
            Assert.True(result.Success, result.Message);
            projects.Current.Timeline.Markers.Single(m => m.Id == result.MarkerId).Label =
                $"{c.Case} · {Path.GetFileNameWithoutExtension(c.File)} · кадр разметки · source {c.Frame:0.00} s";
            placed.Add((c.Case, clip.Id, from));
            at = clip.TimelineEnd;
        }
        await projects.SaveAsAsync(folder);

        // Reopened by a new service; the manifest from the file.
        var project = await new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance).OpenAsync(folder);
        Assert.Equal((FrameRate.Fps60, true), (project.Settings.FrameRate, project.Settings.IsFrameRateLocked));
        var clips = project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().OrderBy(v => v.TimelineStart).ToList();
        Assert.Equal(cases.Count, clips.Count);
        Assert.Equal(cases.Count, project.Timeline.Markers.Count);
        var frames = new RouteSelectionFrameSource(new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance));
        var manifest = new List<object>();
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i]; var clip = clips[i]; var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            Assert.Equal(c.File, asset.FilePath);
            var marker = project.Timeline.Markers.Single(m => m.Label.StartsWith($"{c.Case} ·"));
            var source = clip.SourceIn + (marker.Position - clip.TimelineStart);              // speed 1
            Assert.Equal(ClipSpeed.Normal, clip.Speed);
            Assert.Equal(MediaTime.FromSeconds(c.Frame), source);                               // exact in ticks
            var m = asset.Metadata!;
            var frame = await frames.DecodeAsync(asset, source, Math.Min(m.DisplayWidth ?? m.Width ?? W, W), Math.Min(m.DisplayHeight ?? m.Height ?? H, H), default);
            manifest.Add(new
            {
                @case = c.Case, clip_id = clip.Id, file = c.File, source_range_s = new[] { clip.SourceIn.TotalSeconds, clip.SourceOut.TotalSeconds },
                timeline_start_ticks = clip.TimelineStart.Ticks,
                marker = new { id = marker.Id, position_ticks = marker.Position.Ticks, label = marker.Label },
                frame_source = new { requested_ticks = source.Ticks, decoded_ticks = frame.SourceTime.Ticks },
                frame_size = new[] { frame.Frame.Width, frame.Frame.Height }, display_size = new[] { m.DisplayWidth, m.DisplayHeight },
                route = (string?)null, ambiguous_clip_id = (Guid?)null
            });
            output.WriteLine($"{c.Case} {Path.GetFileName(c.File)}: clip {clip.TimelineStart}–{clip.TimelineEnd}, source {clip.SourceIn}–{clip.SourceOut}, " +
                             $"marker {marker.Position} → source {source} (decoded {frame.SourceTime.Ticks / 1e7:0.000000} s, {frame.Frame.Width}×{frame.Frame.Height})");
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new
        {
            purpose = "D048 independent validation pilot: marking project, one frame per clip; the reference is marked by the owner before any proposal is shown",
            project_sha256_at_build = Sha256(Path.Combine(folder, "project.json")), cases = manifest
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
