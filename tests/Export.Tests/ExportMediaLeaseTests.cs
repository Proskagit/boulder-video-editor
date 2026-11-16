using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Export;
using Xunit;

namespace AiVideoEditor.Export.Tests;

/// <summary>
/// D060 §2: while an export runs, its source files stay as they are — they can be read (the decoders, the Preview,
/// thumbnails) but not deleted, moved or overwritten; nothing is copied. A file that is already gone is left out, not a new
/// failure (reading it fails the export as before). D060 §3: an export frame is drawn at the output's size.
/// </summary>
public sealed class ExportMediaLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));

    public ExportMediaLeaseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private PlaybackSnapshot SnapshotOf(params string[] paths)
    {
        var project = new Core.Entities.Project { Settings = { FrameRate = FrameRate.Fps25 } };
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(track);
        var at = 0L;
        foreach (var path in paths)
        {
            var asset = new MediaAsset
            {
                FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
                Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = FrameRate.Fps25, Width = 64, Height = 36 }
            };
            project.MediaAssets.Add(asset);
            track.Clips.Add(new VideoClip
            {
                MediaAssetId = asset.Id, TimelineStart = MediaTime.FromFrame(at, FrameRate.Fps25),
                Duration = MediaTime.FromFrame(25, FrameRate.Fps25), SourceOut = MediaTime.FromFrame(25, FrameRate.Fps25)
            });
            at += 25;
        }
        return PlaybackSnapshotBuilder.Build(project, 0);
    }

    private string File(string name)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, "source");
        return path;
    }

    [Fact]
    public void A_leased_source_can_be_read_but_not_deleted_moved_or_overwritten()
    {
        var path = File("a.mp4");
        using (var lease = ExportMediaLease.Acquire(SnapshotOf(path)))
        {
            Assert.Equal(new[] { path }, lease.Paths);
            Assert.Equal("source", System.IO.File.ReadAllText(path));                       // readers as before
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }  // like ffmpeg opens it
            Assert.ThrowsAny<IOException>(() => System.IO.File.Delete(path));
            Assert.ThrowsAny<IOException>(() => System.IO.File.Move(path, Path.Combine(_root, "moved.mp4")));
            Assert.ThrowsAny<IOException>(() => System.IO.File.WriteAllText(path, "replaced"));
        }
        // Released with the export.
        System.IO.File.Move(path, Path.Combine(_root, "moved.mp4"));
    }

    [Fact]
    public void A_file_already_gone_is_left_out_and_the_others_are_held()
    {
        var present = File("b.mp4");
        var gone = Path.Combine(_root, "gone.mp4");
        using var lease = ExportMediaLease.Acquire(SnapshotOf(gone, present));
        Assert.Equal(new[] { present }, lease.Paths);
    }

    [Fact]
    public void A_file_used_twice_is_held_once()
    {
        var path = File("c.mp4");
        using var lease = ExportMediaLease.Acquire(SnapshotOf(path, path));
        Assert.Single(lease.Paths);
    }

    [Fact]
    public void An_export_frame_is_drawn_at_the_output_size_and_at_the_canvas_size_exactly_as_before()
    {
        var canvas = new FrameSize(3840, 2160);
        var native = new ExportFrame(0, MediaTime.Zero, canvas, ImmutableArray<ResolvedLayer>.Empty).DrawPlan();
        Assert.Equal(Affine2D.Identity, native.CanvasToTarget);
        Assert.Null(native.TargetSize);

        var fhd = new ExportFrame(0, MediaTime.Zero, canvas, ImmutableArray<ResolvedLayer>.Empty) { Target = new FrameSize(1920, 1080) }.DrawPlan();
        Assert.Equal(new FrameSize(1920, 1080), fhd.TargetSize);
        Assert.Equal(canvas, fhd.Canvas);
        Assert.Equal(new RectD(0, 0, 1920, 1080), fhd.CanvasBounds);
    }
}
