using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
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
/// Phase 19 Step 19.8, local: the markup mode's view model with the real model and decoder on the E1 clip c6, driven like
/// a quick user — a click, Enter 0.7 s later, the next click 0.6 s after that, never waiting for the confirm — every
/// confirmed hold is kept (found in the real-app run: a click during a running confirm used to drop it).
/// </summary>
public sealed class MarkupScenarioTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Quick_clicks_and_enters_keep_every_confirmed_hold()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new FfmpegOptions());
        var path = ReferenceData.Path_("clips/12x10/1014(6).mp4");
        var metadata = (await new FfprobeMediaAnalysisService(new FfprobeLocator(options, NullLogger<FfprobeLocator>.Instance),
            NullLogger<FfprobeMediaAnalysisService>.Instance).AnalyzeAsync(path)).Metadata!;
        var asset = new MediaAsset { FilePath = path, Kind = MediaKind.Video, Metadata = metadata, AnalysisStatus = MediaAnalysisStatus.Completed };
        projects.AddMediaAssets(new[] { asset });
        Assert.True(edit.AddClip(asset.Id, projects.Current.Timeline.VideoTracks[0].Id, MediaTime.Zero).Success);
        var clip = projects.Current.Timeline.VideoTracks[0].Clips.OfType<VideoClip>().Single();

        using var service = new OnnxHoldSegmentationService(ReferenceData.ModelDir);
        var decoder = new FfmpegVideoDecoder(new FfmpegLocator(options, NullLogger<FfmpegLocator>.Instance), NullLogger<FfmpegVideoDecoder>.Instance);
        var status = new StatusService();
        var markup = new HoldMarkupViewModel(edit, projects, status, service, new HoldMarkupFrameSource(decoder))
        {
            Playhead = () => MediaTime.FromSeconds(0.5)
        };
        markup.SetClip(clip);
        var sw = Stopwatch.StartNew();
        await markup.StartCommand.ExecuteAsync(null);
        Assert.True(markup.IsActive, status.Message);
        output.WriteLine($"prepared in {sw.ElapsedMilliseconds} ms");

        var clicks = ReferenceData.Clicks().Where(c => c.Clip == "c6").Take(6).ToList();
        var w = metadata.Width!.Value;
        var h = metadata.Height!.Value;
        var pending = new List<Task>();
        foreach (var click in clicks)
        {
            pending.Add(markup.OnClickAsync(new NormalizedPoint(click.X / w, click.Y / h), additive: false));
            await Task.Delay(700);
            sw.Restart();
            var confirm = markup.ConfirmCommand.ExecuteAsync(null);
            pending.Add(confirm);
            await Task.Delay(600);
            output.WriteLine($"click #{click.Id}: confirm {(confirm.IsCompleted ? "done" : "still running")} after 600 ms; holds {clip.HoldMarkup?.Holds.Length ?? 0}; {status.Message}");
        }
        await Task.WhenAll(pending);
        output.WriteLine($"holds: {clip.HoldMarkup?.Holds.Length ?? 0} of {clicks.Count}");
        Assert.Equal(clicks.Count, clip.HoldMarkup?.Holds.Length ?? 0);
    }
}
