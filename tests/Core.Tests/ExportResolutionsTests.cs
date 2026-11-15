using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>D060 §3: the export resolution presets — the short side fixed, the canvas's aspect ratio kept, even sides, always
/// a size the encoders accept; the canvas itself unless a preset was chosen.</summary>
public class ExportResolutionsTests
{
    private static FrameSize S(int w, int h) => new(w, h);

    [Theory]
    [InlineData(ExportResolution.Uhd4K, 3840, 2160)]
    [InlineData(ExportResolution.FullHd, 1920, 1080)]
    [InlineData(ExportResolution.Hd, 1280, 720)]
    [InlineData(ExportResolution.Sd, 854, 480)]
    public void A_16_9_canvas_gets_the_standard_sizes(ExportResolution preset, int width, int height)
    {
        Assert.Equal(S(width, height), ExportResolutions.SizeFor(preset, S(1920, 1080)));
        Assert.Equal(S(width, height), ExportResolutions.SizeFor(preset, S(3840, 2160)));
        Assert.Equal(S(width, height), ExportResolutions.SizeFor(preset, S(1280, 720)));
    }

    [Theory]
    [InlineData(ExportResolution.Uhd4K, 2160, 3840)]
    [InlineData(ExportResolution.FullHd, 1080, 1920)]
    [InlineData(ExportResolution.Hd, 720, 1280)]
    [InlineData(ExportResolution.Sd, 480, 854)]
    public void A_vertical_9_16_canvas_gets_the_same_sizes_standing(ExportResolution preset, int width, int height) =>
        Assert.Equal(S(width, height), ExportResolutions.SizeFor(preset, S(1080, 1920)));

    [Theory]
    [InlineData(1080, 1080, ExportResolution.FullHd, 1080, 1080)]   // square
    [InlineData(1440, 1080, ExportResolution.Hd, 960, 720)]         // 4:3
    [InlineData(1080, 1350, ExportResolution.FullHd, 1080, 1350)]   // 4:5
    [InlineData(2560, 1440, ExportResolution.FullHd, 1920, 1080)]   // 16:9 that is no preset
    [InlineData(2560, 1080, ExportResolution.FullHd, 2560, 1080)]   // 64:27
    [InlineData(1000, 562, ExportResolution.Sd, 854, 480)]          // nearly 16:9, rounded to even
    public void Other_aspect_ratios_keep_their_ratio_with_even_sides(int cw, int ch, ExportResolution preset, int width, int height) =>
        Assert.Equal(S(width, height), ExportResolutions.SizeFor(preset, S(cw, ch)));

    [Fact]
    public void A_4K_too_large_for_the_encoders_is_reduced_keeping_the_aspect_ratio()
    {
        var size = ExportResolutions.SizeFor(ExportResolution.Uhd4K, S(2560, 1080));   // 5120 × 2160 would be too wide
        Assert.Null(ProjectSettingsRules.CanvasError(size.Width, size.Height));
        Assert.Equal(4096, size.Width);
        Assert.InRange(size.Width / (double)size.Height, 2560 / 1080.0 - 0.01, 2560 / 1080.0 + 0.01);
    }

    [Fact]
    public void Every_preset_of_every_valid_canvas_is_an_encodable_size_of_its_aspect_ratio()
    {
        var canvases = new[] { S(1920, 1080), S(1080, 1920), S(1080, 1080), S(1440, 1080), S(2560, 1080), S(4096, 2160), S(4096, 64),
            S(64, 4096), S(3840, 2160), S(1000, 562), S(1366, 768), S(720, 1280), S(2048, 858) };
        foreach (var canvas in canvases)
        {
            Assert.Null(ProjectSettingsRules.CanvasError(canvas.Width, canvas.Height));
            foreach (var preset in ExportResolutions.Presets)
            {
                var size = ExportResolutions.SizeFor(preset, canvas);
                Assert.True(ProjectSettingsRules.CanvasError(size.Width, size.Height) is null, $"{preset} of {canvas}: {size}");
                Assert.Equal(canvas.Width >= canvas.Height, size.Width >= size.Height);
                // The ratio within one pixel of the long side (rounding to even).
                var expectedLong = (double)Math.Max(canvas.Width, canvas.Height) / Math.Min(canvas.Width, canvas.Height) * Math.Min(size.Width, size.Height);
                Assert.True(Math.Abs(Math.Max(size.Width, size.Height) - expectedLong) <= 1.0001, $"{preset} of {canvas}: {size}");
            }
        }
    }

    [Fact]
    public void Project_is_the_canvas_and_the_matching_preset_is_found()
    {
        Assert.Equal(S(2560, 1440), ExportResolutions.SizeFor(ExportResolution.Project, S(2560, 1440)));
        Assert.Equal(ExportResolution.FullHd, ExportResolutions.PresetMatching(S(1920, 1080)));
        Assert.Equal(ExportResolution.FullHd, ExportResolutions.PresetMatching(S(1080, 1920)));
        Assert.Equal(ExportResolution.Uhd4K, ExportResolutions.PresetMatching(S(3840, 2160)));
        Assert.Null(ExportResolutions.PresetMatching(S(2560, 1440)));
    }

    [Fact]
    public void A_preset_of_the_canvas_size_is_stored_as_the_canvas()
    {
        var fhd = ExportEncoding.Default with { Resolution = ExportResolution.FullHd };
        Assert.Equal(ExportEncoding.Default, ExportResolutions.Normalize(fhd, S(1920, 1080)));
        Assert.Equal(fhd, ExportResolutions.Normalize(fhd, S(3840, 2160)));
    }

    [Fact]
    public void The_job_s_output_has_the_chosen_size_and_the_snapshot_keeps_the_canvas()
    {
        var project = new Project { Settings = { FrameRate = FrameRate.Fps25, FrameWidth = 3840, FrameHeight = 2160 } };
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(track);
        track.Clips.Add(new TextClip { Text = "x", TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(25, FrameRate.Fps25) });
        var snapshot = PlaybackSnapshotBuilder.Build(project, 0);

        var job = new ExportJob(snapshot, @"C:\out.mp4", ExportEncoding.Default with { Resolution = ExportResolution.FullHd });
        Assert.Equal(S(1920, 1080), job.Output.Size);
        Assert.Equal(S(3840, 2160), job.Snapshot.Canvas);
        Assert.Equal(S(3840, 2160), new ExportJob(snapshot, @"C:\out.mp4").Output.Size);   // unchanged default
        Assert.Equal(25, job.Output.FrameCount);
    }

    [Fact]
    public void Stretch_maps_the_canvas_exactly_onto_the_target()
    {
        Assert.Equal(Affine2D.Identity, CompositionDrawPlan.Stretch(S(1920, 1080), S(1920, 1080)));
        var plan = CompositionDrawPlan.Build(S(1920, 1080), CompositionDrawPlan.Stretch(S(1920, 1080), S(854, 480)), Array.Empty<ResolvedLayer>());
        Assert.Equal(new RectD(0, 0, 854, 480), plan.CanvasBounds);
    }

    [Fact]
    public void An_unknown_resolution_is_not_valid() =>
        Assert.NotNull((ExportEncoding.Default with { Resolution = (ExportResolution)42 }).Validate());
}
