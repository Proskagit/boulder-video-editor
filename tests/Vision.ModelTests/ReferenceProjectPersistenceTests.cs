using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
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
/// D048 stage 1, the owner's reference project (local, outside the solution): before the six cases are prepared, one test
/// clip shows what a saved project keeps of a reference marking. A 8 s range of 100405 (source 5–13 s) on the timeline,
/// a labelled marker at <c>T</c>; "Place holds" with the real model at <c>T</c> = source 11 s (the clean frame at 6 s, the
/// research frame); then the owner's kinds of edits through the same service calls the UI makes — a contour corrected
/// (D035), a proposal removed, a hold added by hand (D034); saved with the standard <c>SaveAsAsync</c>, opened by a new
/// project service, every field compared. Writes into <c>AIVE_PERSIST_OUT</c> (default: the temp folder) — never into a
/// working project. This is a test marking, not a reference.
/// </summary>
public sealed class ReferenceProjectPersistenceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_reference_marking_survives_save_close_and_open()
    {
        var path = @"D:\Времянка для монтажа\Боулдер 27.09.2026\video_20260927_100405.mp4";
        if (!File.Exists(path))
        {
            output.WriteLine("SKIPPED: the clip is not available");
            return;
        }
        var folder = Environment.GetEnvironmentVariable("AIVE_PERSIST_OUT") is { Length: > 0 } dir
            ? dir : Path.Combine(Path.GetTempPath(), "aive-d048-reference-persistence");
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(), $"{folder} is not empty");

        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var metadata = (await new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance),
            NullLogger<FfprobeMediaAnalysisService>.Instance).AnalyzeAsync(path)).Metadata!;
        var asset = new MediaAsset { FilePath = path, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
        projects.AddMediaAssets(new[] { asset });
        var track = projects.Current.Timeline.VideoTracks[0];
        Assert.True(edit.AddClip(asset.Id, track.Id, MediaTime.Zero).Success);
        var rate = projects.Current.Settings.FrameRate;                                    // set from the first clip
        var clip = track.Clips.OfType<VideoClip>().Single();
        Assert.True(edit.TrimClip(clip.Id, ClipEdge.End, MediaTime.FromSeconds(13)).Success);
        Assert.True(edit.TrimClip(clip.Id, ClipEdge.Start, MediaTime.FromSeconds(5)).Success);
        Assert.True(edit.MoveClips(new[] { clip.Id }, -MediaTime.FromSeconds(5).ToNearestFrame(rate)).Success);
        Assert.Equal((MediaTime.Zero, MediaTime.FromSeconds(5)), (clip.TimelineStart, clip.SourceIn));
        var t = MediaTime.FromSeconds(6).SnapToFrame(rate);                              // source 11 s; the clean frame 6 s
        var marker = edit.AddMarker(t);
        Assert.True(marker.Success);
        projects.Current.Timeline.Markers.Single().Label = "TEST 01 · 100405 · T (clean frame = source 6 s)";

        // "Place holds" exactly as in the app, with the real model and decoder.
        using var service = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        var decoder = new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance);
        var status = new StatusService();
        var markup = new HoldMarkupViewModel(edit, projects, status, service, new HoldMarkupFrameSource(decoder),
            routeSelection: new RouteSelectionService(new RouteSelectionFrameSource(decoder))) { Playhead = () => t };
        markup.SetClip(clip);
        markup.PlaceHoldsCommand.Execute(null);
        var click = new NormalizedPoint(0.451, 0.715);
        await markup.OnClickAsync(click, additive: false);
        Assert.True(markup.IsRouteReview, status.Message);
        var controlProposal = markup.Proposals[0].Proposal;                               // the review lists the control first
        await markup.AcceptRouteCommand.ExecuteAsync(null);
        var accepted = clip.HoldMarkup!.Holds;
        output.WriteLine($"accepted {accepted.Length} holds at {accepted[0].SourceTime}; control proposal {controlProposal.Id}");
        Assert.Equal(controlProposal.Id, accepted[0].Id);                                // the control hold is the first hold

        // The owner's kinds of edits (the UI calls exactly these).
        var corrected = accepted[1];
        var moved = corrected.Points.SetItem(0, new NormalizedPoint(corrected.Points[0].X + 0.003, corrected.Points[0].Y - 0.002));
        Assert.True(edit.ReplaceHoldContour(clip.Id, corrected.Id, moved).Success);
        Assert.True(edit.RemoveHold(clip.Id, accepted[2].Id).Success);
        var manual = new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0.10, 0.10), new NormalizedPoint(0.12, 0.10),
            new NormalizedPoint(0.12, 0.13), new NormalizedPoint(0.10, 0.13)), accepted[3].SurfaceLab, accepted[3].WallLab, accepted[0].SourceTime);
        Assert.True(edit.AddHold(clip.Id, manual).Success);
        var before = clip.HoldMarkup!;

        // Save, close (a new service), open.
        await projects.SaveAsAsync(folder);
        var reopened = new ProjectService(new UndoRedoService(), NullLogger<ProjectService>.Instance);
        var project = await reopened.OpenAsync(folder);
        var clip2 = project.Timeline.VideoTracks.SelectMany(tr => tr.Clips).OfType<VideoClip>().Single();
        var after = clip2.HoldMarkup!;

        Assert.Equal(before.Holds.Length, after.Holds.Length);
        for (var i = 0; i < before.Holds.Length; i++) Assert.True(before.Holds[i].SameAs(after.Holds[i]), $"hold {i} differs");
        Assert.Equal(before.Holds.Select(h => h.Id), after.Holds.Select(h => h.Id));     // the order too: the control first
        Assert.Equal((before.ColorHex, before.ColorMode, before.StrokeWidth1080, before.ModelId), (after.ColorHex, after.ColorMode, after.StrokeWidth1080, after.ModelId));
        Assert.Equal((clip.TimelineStart, clip.Duration, clip.SourceIn, clip.SourceOut, clip.Speed),
            (clip2.TimelineStart, clip2.Duration, clip2.SourceIn, clip2.SourceOut, clip2.Speed));
        var asset2 = project.MediaAssets.Single();
        Assert.Equal((asset.FilePath, metadata.DisplayWidth ?? metadata.Width, metadata.DisplayHeight ?? metadata.Height),
            (asset2.FilePath, asset2.Metadata?.DisplayWidth ?? asset2.Metadata?.Width, asset2.Metadata?.DisplayHeight ?? asset2.Metadata?.Height));
        var marker2 = project.Timeline.Markers.Single();
        Assert.Equal((t, projects.Current.Timeline.Markers.Single().Label), (marker2.Position, marker2.Label));
        output.WriteLine($"saved and reopened: {after.Holds.Length} holds identical (ids, order, contours, samples, source time), " +
                         $"clip range, media path and size, marker; project at {folder}");
    }
}
