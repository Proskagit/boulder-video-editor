using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Timeline;
using AiVideoEditor.Video.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 13 Step 13.4 (D028, CS-1 B): a 320 × 180 project — a red full-canvas solid on V1, a quarter-size green solid
/// moved right and down on V2, a moved title on V3 — whose canvas is changed by the real
/// <see cref="TimelineEditService.SetCanvasSize"/> to a portrait and to a square canvas. The export has the new size,
/// the green box lands where the scaled position puts it, and every checked frame of the export canvas matches the
/// Preview drawn from the same snapshot byte for byte (the existing canvas parity rule of D023; no new criterion).
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportCanvasChangeEndToEndTests
{
    private const string Red = "C83232";
    private const string Green = "20C040";
    private readonly E2EMedia _media;

    public ExportCanvasChangeEndToEndTests(E2EMedia media) => _media = media;

    [FfmpegTheory]
    [InlineData(180, 320)]    // portrait
    [InlineData(240, 240)]    // square
    public async Task An_export_after_a_canvas_change_has_the_new_size_the_scaled_layout_and_matches_its_preview(int width, int height)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var v2 = p.VideoTrack();
        var v3 = p.VideoTrack();
        p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 0, 10);
        var box = p.Video(v2, _media.Solid(Green, FrameRate.Fps25), 0, 10);
        (box.Scale, box.PositionX, box.PositionY) = (0.25, 100, 50);
        var title = p.Text(v3, "13.4", 0, 10);
        (title.FontSize, title.PositionX, title.PositionY) = (24, -100, -60);
        var edit = new TimelineEditService(new SceneProject(p.Project), new UndoRedoService(), NullLogger<TimelineEditService>.Instance);

        var result = edit.SetCanvasSize(width, height);

        Assert.True(result.Success, result.Message);
        var s = Math.Min((double)width / E2EMedia.Width, (double)height / E2EMedia.Height);
        Assert.Equal((100 * s, 50 * s), (box.PositionX, box.PositionY));
        Assert.Equal(24 * s, title.FontSize);

        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), $"canvas-{width}x{height}.mp4"));
        AssertValidMp4(run);
        Assert.Equal(new FrameSize(width, height), run.Output.Size);
        Assert.Equal(new FrameSize(width, height), run.Job.Snapshot.Canvas);
        Assert.Equal(width * height * 4, run.Canvases[0].Length);

        // The green box's centre: the canvas centre plus the scaled position.
        var (gx, gy) = ((int)Math.Round(width / 2.0 + 100 * s), (int)Math.Round(height / 2.0 + 50 * s));
        var (r, g, _) = Pixel(run.Canvases[0], width, gx, gy);
        Assert.True(g > r, $"box centre ({gx}, {gy}): R {r}, G {g}");
        // The red solid, contained: present at the canvas centre's row left of the box, black above it in the portrait case.
        var (rr, rg, _) = Pixel(run.Canvases[0], width, width / 8, height / 2);
        Assert.True(rr > rg, $"red solid at ({width / 8}, {height / 2}): R {rr}, G {rg}");
        if (height > width)
        {
            var (br, bg, bb) = Pixel(run.Canvases[0], width, width / 2, 4);
            Assert.True(br < 16 && bg < 16 && bb < 16, $"letterbox at the top: {br}, {bg}, {bb}");
        }

        foreach (var n in new long[] { 0, 9 })
        {
            var preview = await Preview(run.Job.Snapshot, n);
            var canvas = run.Canvases[(int)n];
            Assert.Equal(canvas.Length, preview.Pixels.Length);
            var differing = Enumerable.Range(0, canvas.Length).Count(i => preview.Pixels[i] != canvas[i]);
            Assert.True(differing == 0, $"frame {n}: {differing} bytes differ between the Preview and the export canvas");
        }
        AssertNoFfmpegLeft();
    }

    /// <summary>What the edit service needs from the project service: the scene's project and the notifications.</summary>
    private sealed class SceneProject(Core.Entities.Project project) : IProjectService
    {
        public Core.Entities.Project Current { get; } = project;
        public event EventHandler? ProjectChanged { add { } remove { } }
        public event EventHandler? MediaAssetsChanged { add { } remove { } }
        public event EventHandler? TimelineChanged { add { } remove { } }
        public event EventHandler? SaveStateChanged { add { } remove { } }
        public event EventHandler? ProjectSaved { add { } remove { } }
        public event EventHandler<MediaAvailabilityChangedEventArgs>? MediaAvailabilityChanged { add { } remove { } }
        public event EventHandler<MediaRelinkedEventArgs>? MediaRelinked { add { } remove { } }
        public Core.Entities.Project CreateNew(string name, ProjectSettings? settings = null) => throw new NotSupportedException();
        public Task<Core.Entities.Project> OpenAsync(string projectFolderPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Core.Entities.Project> RestoreRecoveryAsync(string recoveryFilePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecheckMediaAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void NotifyMediaRelinked(IReadOnlyList<MediaFileReplacement> replacements) { }
        public MediaAddResult AddMediaAssets(IEnumerable<MediaAsset> assets) => throw new NotSupportedException();
        public void NotifyMediaAssetsChanged() { }
        public void NotifyTimelineChanged() { }
    }
}
