using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Video.Tests;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// D060 with real parts: an export at a resolution preset gives a valid MP4 of exactly the preset's size, frame rate, frame
/// count, audio and duration, drawn by the same pipeline; the canvas's own size is drawn byte for byte as the plain export;
/// a source file cannot be deleted while the export reads it and can again afterwards; no ffmpeg is left.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportResolutionEndToEndTests(E2EMedia media)
{
    private ProjectBuilder Scene(MediaAsset? video = null)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);   // 320 × 180 (16:9)
        var v1 = p.VideoTrack();
        p.Video(v1, video ?? media.Pattern(), 0, 25);
        var title = p.Text(p.VideoTrack(), "D060", 5, 20);
        title.Outline = new TextOutline(true, "#000000", TextOutline.DefaultThickness);
        p.Template(p.VideoTrack(), TemplateKind.Vhs, 10, 25);
        p.Audio(p.AudioTrack(), media.Tone(), 0, 25);
        return p;
    }

    [FfmpegTheory]
    [InlineData(ExportResolution.Sd, 854, 480)]
    [InlineData(ExportResolution.Hd, 1280, 720)]
    public async Task A_preset_export_is_a_valid_mp4_of_the_preset_s_size(ExportResolution resolution, int width, int height)
    {
        var p = Scene();
        p.Project.Settings.Export = ExportEncoding.Default with { Resolution = resolution };

        var run = await ExportProject(p.Project, Path.Combine(media.OutputFolder(), $"res-{resolution}.mp4"));

        Assert.Equal((width, height), (run.Output.Size.Width, run.Output.Size.Height));
        Assert.Equal((320, 180), (run.Job.Snapshot.Canvas.Width, run.Job.Snapshot.Canvas.Height));
        AssertValidMp4(run);                                             // size, rate, frames, audio, duration, no ffmpeg left
        Assert.All(run.Canvases, c => Assert.Equal(width * height * 4, c.Length));
        // The encoded file against the canvases: tight before the VHS (frames 0–9); its noise and scan lines cost libx264 more.
        var errors = FrameErrors(run);
        Assert.All(errors.Take(10), e => Assert.True(e < 4, $"mean error {e}"));
        Assert.All(errors, e => Assert.True(e < 8, $"mean error {e}"));
    }

    [FfmpegFact]
    public async Task The_canvas_s_own_size_draws_the_same_canvases_as_the_plain_export()
    {
        var plain = await ExportProject(Scene().Project, Path.Combine(media.OutputFolder(), "plain.mp4"));
        var p = Scene();
        p.Project.Settings.Export = ExportEncoding.Default with { Resolution = ExportResolution.Project };
        var project = await ExportProject(p.Project, Path.Combine(media.OutputFolder(), "project.mp4"));

        Assert.Equal(plain.Canvases.Count, project.Canvases.Count);
        Assert.True(Enumerable.Range(0, plain.Canvases.Count).All(n => plain.Canvases[n].AsSpan().SequenceEqual(project.Canvases[n])));
    }

    [FfmpegFact]
    public async Task A_source_cannot_be_deleted_while_the_export_reads_it()
    {
        var folder = media.OutputFolder();
        var original = media.Pattern();
        var copy = Path.Combine(folder, "source.mp4");
        File.Copy(original.FilePath, copy);
        var source = new MediaAsset
        {
            FilePath = copy, Kind = original.Kind, AnalysisStatus = original.AnalysisStatus, Metadata = original.Metadata
        };
        var p = Scene(source);
        var job = Preflight(p.Project, Path.Combine(media.OutputFolder(), "lease.mp4"));
        Exception? deleteDuringExport = null;
        var tried = false;
        var progress = new SyncProgress(report =>
        {
            if (tried || report.Stage != ExportStage.Video) return;
            tried = true;
            try { File.Delete(copy); } catch (Exception ex) { deleteDuringExport = ex; }
        });

        await Service(new RecordingEncoder(EncoderHarness.Encoder())).ExportAsync(job, progress);

        Assert.True(tried);
        Assert.IsAssignableFrom<IOException>(deleteDuringExport);
        Assert.True(File.Exists(copy));
        File.Delete(copy);                                               // released with the export
        AssertNoFfmpegLeft();
    }
}
