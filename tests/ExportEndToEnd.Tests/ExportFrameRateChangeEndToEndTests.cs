using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Timeline;
using AiVideoEditor.Video.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 13 Step 13.5 (D028 §4): a 25 fps project — red [0, 10) and green [10, 20) on V1, a title on V2 — re-gridded to
/// 30 fps by the real <see cref="TimelineEditService.SetFrameRate"/>. The export runs at 30 fps with the cut at its
/// nearest 30 fps frame (0.4 s → 12) and 24 frames (0.8 s), and the export canvas matches the Preview drawn from the same
/// snapshot byte for byte on both sides of the cut and at the ends (the existing canvas parity rule of D023).
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportFrameRateChangeEndToEndTests
{
    private const string Red = "C83232";
    private const string Green = "20C040";
    private readonly E2EMedia _media;

    public ExportFrameRateChangeEndToEndTests(E2EMedia media) => _media = media;

    [FfmpegFact]
    public async Task An_export_after_a_rate_change_runs_on_the_new_grid_and_matches_its_preview()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var v2 = p.VideoTrack();
        p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 0, 10);
        var green = p.Video(v1, _media.Solid(Green, FrameRate.Fps25), 10, 20);
        p.Text(v2, "13.5", 3, 17);
        var edit = new TimelineEditService(new SceneProject(p.Project), new UndoRedoService(), NullLogger<TimelineEditService>.Instance);

        var result = edit.SetFrameRate(FrameRate.Fps30);

        Assert.True(result.Success, result.Message);
        Assert.Equal(MediaTime.FromFrame(12, FrameRate.Fps30), green.TimelineStart);

        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "rate-25-to-30.mp4"));
        AssertValidMp4(run);
        Assert.Equal(FrameRate.Fps30, run.Output.FrameRate);
        Assert.Equal(24L, run.Output.FrameCount);
        foreach (var (frame, isRed) in new[] { (0, true), (11, true), (12, false), (23, false) })
        {
            var (r, g, _) = Pixel(run.Canvases[frame], E2EMedia.Width, E2EMedia.Width / 8, E2EMedia.Height / 8);
            Assert.True(isRed ? r > g : g > r, $"frame {frame}: R {r}, G {g}");
        }

        foreach (var n in new long[] { 0, 11, 12, 23 })
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
