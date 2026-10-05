using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Timeline.Commands;
using AiVideoEditor.Video.Tests;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 12 Step 12.3 (D027 §3): the layer order is <see cref="Track.Order"/> alone. Two full-canvas solids on V1 and
/// V2: before the move the upper track's colour fills the canvas, after the move (the same order change the edit
/// service makes, <see cref="SetTrackOrderCommand"/>) the other one does — in the export and, byte for byte, in the
/// Preview drawn from the same snapshot. No new criterion: the existing canvas parity rule of D023.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportTrackOrderEndToEndTests
{
    private const string Red = "C83232";
    private const string Green = "20C040";
    private readonly E2EMedia _media;

    public ExportTrackOrderEndToEndTests(E2EMedia media) => _media = media;

    [FfmpegFact]
    public async Task A_reordered_track_is_drawn_in_its_new_place_in_the_export_and_the_preview()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var v2 = p.VideoTrack();
        p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 0, 10);
        p.Video(v2, _media.Solid(Green, FrameRate.Fps25), 0, 10);

        var before = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "track-order-before.mp4"));
        AssertValidMp4(before);
        AssertCentre(before, isGreen: true, "before the move (V2 on top)");

        new SetTrackOrderCommand(new[] { new TrackOrderChange(v1, v1.Order, v2.Order), new TrackOrderChange(v2, v2.Order, v1.Order) }).Execute();

        var after = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), "track-order-after.mp4"));
        AssertValidMp4(after);
        AssertCentre(after, isGreen: false, "after the move (V1 on top)");

        foreach (var n in new long[] { 0, 9 })
        {
            var preview = await Preview(after.Job.Snapshot, n);
            var canvas = after.Canvases[(int)n];
            Assert.Equal(canvas.Length, preview.Pixels.Length);
            var differing = Enumerable.Range(0, canvas.Length).Count(i => preview.Pixels[i] != canvas[i]);
            Assert.True(differing == 0, $"frame {n}: {differing} bytes differ between the Preview and the export canvas");
        }
        AssertNoFfmpegLeft();
    }

    private static void AssertCentre(ExportRun run, bool isGreen, string what)
    {
        var (r, g, _) = Pixel(run.Canvases[0], E2EMedia.Width, E2EMedia.Width / 2, E2EMedia.Height / 2);
        Assert.True(isGreen ? g > r : r > g, $"{what}: centre pixel R {r}, G {g}");
    }
}
