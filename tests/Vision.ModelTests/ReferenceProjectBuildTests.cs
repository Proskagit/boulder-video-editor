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
/// D048 stage 1, the owner's reference project (local, outside the solution): six cases of the 2026-09-27 session as clips
/// of one project, one after another on V1 — the given source ranges, 60 fps locked first (integer seconds exact on its
/// grid), two labelled markers per case (the clean frame and <c>T</c>); saved with the standard <c>SaveAsAsync</c> into
/// <c>AIVE_REFERENCE_OUT</c> (an empty folder), then opened by a new project service and checked: ranges, positions, markers,
/// <c>T</c> and <c>T − 5 s</c> through the route selection's own locator (exact ticks), and the source frames the app's frame
/// source decodes there. No hold, no marking: the project is for the owner.
/// </summary>
public sealed class ReferenceProjectBuildTests(ITestOutputHelper output)
{
    private static readonly (string Case, string File, double Clean, double T, double From, double To, string? Note)[] Cases =
    {
        ("01", "video_20260927_100405.mp4", 6, 11, 5, 13, null),
        ("02", "video_20260927_103744.mp4", 6, 11, 5, 13, "some targets ambiguous — the owner decides (see README)"),
        ("03", "video_20260927_101034.mp4", 1, 6, 0, 8, null),
        ("05", "video_20260927_104110.mp4", 1, 6, 0, 8, null),
        ("06", "video_20260927_105438.mp4", 1, 6, 0, 8, "some targets ambiguous — the owner decides (see README)"),
        ("07", "video_20260927_112854.mp4", 1, 6, 0, 8, null),
    };

    [Fact]
    public async Task Build_and_reopen_the_reference_project()
    {
        var clipsDir = @"D:\Времянка для монтажа\Боулдер 27.09.2026";
        var folder = Environment.GetEnvironmentVariable("AIVE_REFERENCE_OUT");
        if (folder is null || !Directory.Exists(clipsDir))
        {
            output.WriteLine("SKIPPED: AIVE_REFERENCE_OUT not set or the clips are not available");
            return;
        }
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(), $"{folder} is not empty");

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
        foreach (var c in Cases)
        {
            var path = Path.Combine(clipsDir, c.File);
            var metadata = (await probe.AnalyzeAsync(path)).Metadata!;
            var asset = new MediaAsset { FilePath = path, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
            projects.AddMediaAssets(new[] { asset });
            Assert.True(edit.AddClip(asset.Id, track.Id, at).Success);
            var clip = track.Clips.OfType<VideoClip>().Single(v => v.MediaAssetId == asset.Id);
            // The clip at `at` shows source s at `at + s`: cut to [From, To), then moved back to `at`.
            Assert.True(edit.TrimClip(clip.Id, ClipEdge.End, at + MediaTime.FromSeconds(c.To)).Success);
            if (c.From > 0)
            {
                Assert.True(edit.TrimClip(clip.Id, ClipEdge.Start, at + MediaTime.FromSeconds(c.From)).Success);
                Assert.True(edit.MoveClips(new[] { clip.Id }, -MediaTime.FromSeconds(c.From).ToNearestFrame(rate)).Success);
            }
            Assert.Equal((at, MediaTime.FromSeconds(c.From), MediaTime.FromSeconds(c.To)), (clip.TimelineStart, clip.SourceIn, clip.SourceOut));
            foreach (var (time, what) in new[] { (c.Clean, "clean frame (T − 5 s)"), (c.T, "T — click the control hold here") })
            {
                var position = at + MediaTime.FromSeconds(time - c.From);
                var result = edit.AddMarker(position);
                Assert.True(result.Success, result.Message);
                projects.Current.Timeline.Markers.Single(m => m.Id == result.MarkerId).Label =
                    $"{c.Case} · {Path.GetFileNameWithoutExtension(c.File)} · {what} · source {time:0} s" + (c.Note is null ? "" : $" · {c.Note}");
            }
            placed.Add((c.Case, clip.Id, at));
            at = clip.TimelineEnd;
        }

        await projects.SaveAsAsync(folder);

        // Closed and opened again by a new service: everything checked from the file.
        var reopened = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        var project = await reopened.OpenAsync(folder);
        Assert.Equal((FrameRate.Fps60, true), (project.Settings.FrameRate, project.Settings.IsFrameRateLocked));
        var clips = project.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().OrderBy(v => v.TimelineStart).ToList();
        Assert.Equal(Cases.Length, clips.Count);
        Assert.Equal(Cases.Length * 2, project.Timeline.Markers.Count);
        var decoder = new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance);
        var frames = new RouteSelectionFrameSource(decoder);
        for (var i = 0; i < Cases.Length; i++)
        {
            var c = Cases[i];
            var clip = clips[i];
            var asset = project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
            Assert.Equal(Path.Combine(clipsDir, c.File), asset.FilePath);
            Assert.Equal((placed[i].Start, MediaTime.FromSeconds(c.From), MediaTime.FromSeconds(c.To), ClipSpeed.Normal),
                (clip.TimelineStart, clip.SourceIn, clip.SourceOut, clip.Speed));
            var tMarker = project.Timeline.Markers.Single(m => m.Label.StartsWith($"{c.Case} ·") && m.Label.Contains("· T "));
            var cleanMarker = project.Timeline.Markers.Single(m => m.Label.StartsWith($"{c.Case} ·") && m.Label.Contains("clean frame"));
            var located = RouteSelectionLocator.Locate(clip, tMarker.Position.ToNearestFrame(project.Settings.FrameRate), project.Settings.FrameRate)!.Value;
            Assert.Equal(MediaTime.FromSeconds(c.T), located.TargetSourceTime);              // exact in ticks
            Assert.Equal(MediaTime.FromSeconds(c.Clean), located.CleanSourceTime);
            Assert.False(located.CleanAtFileStart);
            Assert.Equal(clip.TimelineStart + MediaTime.FromSeconds(c.Clean - c.From), cleanMarker.Position);   // the clean frame is inside the clip
            var cleanFrame = await frames.DecodeAsync(asset, located.CleanSourceTime, 1920, 1080, default);
            var tFrame = await frames.DecodeAsync(asset, located.TargetSourceTime, 960, 960, default);
            output.WriteLine($"{c.Case} {c.File}: clip {clip.TimelineStart}–{clip.TimelineEnd} source {clip.SourceIn}–{clip.SourceOut}; " +
                             $"T marker {tMarker.Position} → source {located.TargetSourceTime} (decoded frame {tFrame.SourceTime.Ticks / 1e7:0.000000} s); " +
                             $"clean marker {cleanMarker.Position} → source {located.CleanSourceTime} (decoded frame {cleanFrame.SourceTime.Ticks / 1e7:0.000000} s, " +
                             $"{cleanFrame.Frame.Width}×{cleanFrame.Frame.Height})");
        }
        output.WriteLine($"saved, closed and reopened: {folder}");
    }
}
