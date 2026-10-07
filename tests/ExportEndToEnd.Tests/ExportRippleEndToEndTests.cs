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
/// Phase 12 Step 12.5 (D027 §2), its QG "an export after a ripple matching its Preview": three solids on V1 — red
/// [0, 10), green [10, 20), blue [20, 30) — the green one removed by the real <see cref="TimelineEditService.RippleDeleteClips"/>.
/// The export then has 20 frames, red then blue with no gap, and the Preview drawn from the export's snapshot shows the
/// same bytes (the existing canvas parity rule of D023; no new criterion).
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportRippleEndToEndTests
{
    private const string Red = "C83232";
    private const string Green = "20C040";
    private const string Blue = "3050D0";
    private readonly E2EMedia _media;

    public ExportRippleEndToEndTests(E2EMedia media) => _media = media;

    [FfmpegFact]
    public async Task An_export_after_a_ripple_delete_has_the_closed_timeline_and_matches_its_preview()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 0, 10);
        var green = p.Video(v1, _media.Solid(Green, FrameRate.Fps25), 10, 20);
        var blue = p.Video(v1, _media.Solid(Blue, FrameRate.Fps25), 20, 30);
        var edit = new TimelineEditService(new SceneProject(p.Project), new UndoRedoService(), NullLogger<TimelineEditService>.Instance);

        var ripple = edit.RippleDeleteClips(new[] { green.Id });

        Assert.True(ripple.Success, ripple.Message);
        Assert.Equal(p.F(10), blue.TimelineStart);

        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "ripple-delete.mp4"));
        AssertValidMp4(run);
        Assert.Equal(20L, run.Output.FrameCount);                          // the removed 10 frames are gone
        Assert.Equal(20, run.Canvases.Count);
        foreach (var (frame, isRed) in new[] { (0, true), (9, true), (10, false), (19, false) })
        {
            var (r, _, b) = Pixel(run.Canvases[frame], E2EMedia.Width, E2EMedia.Width / 2, E2EMedia.Height / 2);
            Assert.True(isRed ? r > b : b > r, $"frame {frame}: centre pixel R {r}, B {b}");
        }

        foreach (var n in new long[] { 9, 10 })                           // both sides of the new cut
        {
            var preview = await Preview(run.Job.Snapshot, n);
            var canvas = run.Canvases[(int)n];
            Assert.Equal(canvas.Length, preview.Pixels.Length);
            var differing = Enumerable.Range(0, canvas.Length).Count(i => preview.Pixels[i] != canvas[i]);
            Assert.True(differing == 0, $"frame {n}: {differing} bytes differ between the Preview and the export canvas");
        }
        AssertNoFfmpegLeft();
    }

    /// <summary>Phase 15 Step 15.4 (D030 §5): the same three solids; Shift+Q on green at frame 14 (green keeps its start,
    /// shows 6 frames, blue follows at 16), then Shift+W on red at frame 5 (red ends at 5, green and blue follow). The
    /// export has the rippled timeline and the Preview drawn from its snapshot shows the same bytes on both sides of
    /// each new cut.</summary>
    [FfmpegFact]
    public async Task An_export_after_ripple_trims_to_the_playhead_has_the_closed_timeline_and_matches_its_preview()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var red = p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 0, 10);
        var green = p.Video(v1, _media.Solid(Green, FrameRate.Fps25), 10, 20);
        var blue = p.Video(v1, _media.Solid(Blue, FrameRate.Fps25), 20, 30);
        var edit = new TimelineEditService(new SceneProject(p.Project), new UndoRedoService(), NullLogger<TimelineEditService>.Instance);

        var start = edit.TrimToPlayhead(new[] { green.Id }, ClipEdge.Start, p.F(14), ripple: true);
        Assert.True(start.Success, start.Message);
        Assert.Equal(p.F(10), start.Playhead);
        var end = edit.TrimToPlayhead(new[] { red.Id }, ClipEdge.End, p.F(5), ripple: true);
        Assert.True(end.Success, end.Message);
        Assert.Equal((p.F(5), p.F(11)), (green.TimelineStart, green.TimelineEnd));
        Assert.Equal((p.F(11), p.F(21)), (blue.TimelineStart, blue.TimelineEnd));

        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "ripple-trim.mp4"));
        AssertValidMp4(run);
        Assert.Equal(21L, run.Output.FrameCount);
        foreach (var (frame, expected) in new[] { (4, 'r'), (5, 'g'), (10, 'g'), (11, 'b'), (20, 'b') })
        {
            var (r, g, b) = Pixel(run.Canvases[frame], E2EMedia.Width, E2EMedia.Width / 2, E2EMedia.Height / 2);
            var actual = r > g && r > b ? 'r' : g > r && g > b ? 'g' : 'b';
            Assert.True(actual == expected, $"frame {frame}: expected {expected}, centre pixel R {r}, G {g}, B {b}");
        }

        foreach (var n in new long[] { 4, 5, 10, 11 })
        {
            var preview = await Preview(run.Job.Snapshot, n);
            var canvas = run.Canvases[(int)n];
            Assert.Equal(canvas.Length, preview.Pixels.Length);
            var differing = Enumerable.Range(0, canvas.Length).Count(i => preview.Pixels[i] != canvas[i]);
            Assert.True(differing == 0, $"frame {n}: {differing} bytes differ between the Preview and the export canvas");
        }
        AssertNoFfmpegLeft();
    }

    /// <summary>Phase 15 Step 15.6 (D030 §7): slip end to end. The A/V sync source (a white frame and a 1 kHz burst at
    /// every source frame n ≡ 15 mod 30) as one clip [0, 60) from source frame 10: white at timeline frames 5 and 35. A slip of
    /// +5 through the service shows source from frame 15: white at 0 and 30, the burst at 0 s and 1.2 s — the clip's place
    /// and length unchanged. The Preview drawn from the export's snapshot shows the same bytes.</summary>
    [FfmpegFact]
    public async Task An_export_after_a_slip_shows_the_slipped_source_in_the_same_place_and_matches_its_preview()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var clip = p.Video(v1, _media.Sync(FrameRate.Fps25), 0, 60, sourceInFrame: 10);
        var edit = new TimelineEditService(new SceneProject(p.Project), new UndoRedoService(), NullLogger<TimelineEditService>.Instance);

        var slip = edit.SlipClip(clip.Id, 5);
        Assert.True(slip.Success, slip.Message);
        Assert.Equal((p.F(0), p.F(60), p.F(15)), (clip.TimelineStart, clip.TimelineEnd, clip.SourceIn));

        var run = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "slip.mp4"));
        AssertValidMp4(run);
        Assert.Equal(60L, run.Output.FrameCount);
        foreach (var (frame, white) in new[] { (0, true), (5, false), (30, true), (35, false) })
        {
            var (r, g, b) = Pixel(run.Canvases[frame], E2EMedia.Width, E2EMedia.Width / 2, E2EMedia.Height / 2);
            Assert.True(white ? r > 200 && g > 200 && b > 200 : r < 50 && g < 50 && b < 50, $"frame {frame}: {r}, {g}, {b}");
        }
        var pcm = Left(run.Pcm);
        var rate = Core.Playback.AudioFormat.SampleRate;
        Assert.True(Rms(pcm.AsSpan(0, rate * 4 / 100)) > 0.3, "the burst at 0 s");
        Assert.True(Rms(pcm.AsSpan(rate * 20 / 100, rate * 4 / 100)) < 0.01, "no burst at 0.2 s any more");
        Assert.True(Rms(pcm.AsSpan(rate * 120 / 100, rate * 4 / 100)) > 0.3, "the burst at 1.2 s");

        foreach (var n in new long[] { 0, 5, 30 })
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
