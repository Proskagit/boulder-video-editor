using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Rendering;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Hold tracking (R2 → E): the Preview's draw plan moves the contours by the shown frame's own timestamp exactly as the
/// export's plan does — the same contour on the same frame, only through the Preview's viewport — and leaves them out on
/// an unreliable frame like the export.
/// </summary>
public class HoldTrackingPreviewPlanTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly FrameSize Canvas = new(1920, 1080);

    private static (PlaybackSnapshot Snapshot, Guid ClipId) Snapshot(bool reliable)
    {
        var project = new AiVideoEditor.Core.Entities.Project { Settings = { FrameRate = Rate } };
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(track);
        var asset = new MediaAsset
        {
            FilePath = @"C:\media\wall.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(10), FrameRate = Rate, StartTime = MediaTime.Zero,
                Width = 1920, Height = 1080, DisplayWidth = 1920, DisplayHeight = 1080
            }
        };
        project.MediaAssets.Add(asset);
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(10),
            HoldMarkup = RouteColor.WithResolved(new HoldMarkup
            {
                Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(),
                    ImmutableArray.Create(new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.6, 0.4), new NormalizedPoint(0.5, 0.6)),
                    RouteColor.HexToOkLab("#1E88E5"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero))
            })
        };
        clip.SourceOut = clip.Duration;
        track.Clips.Add(clip);
        var motion = new CameraMotionTrack(MediaTime.Zero, CameraMotionModel.Similarity, ImmutableArray.Create(
            new CameraMotionSample(MediaTime.Zero, WallTransform.Identity, true),
            new CameraMotionSample(MediaTime.FromSeconds(1), new WallTransform(1, 0, 0.05, 0, 1, 0.02, 0, 0, 1), reliable)));
        return (PlaybackSnapshotBuilder.Build(project, 1, new Dictionary<Guid, CameraMotionTrack> { [clip.Id] = motion }), clip.Id);
    }

    private static DecodedFrame FrameAt(double seconds) =>
        new(1920, 1080, 1920 * 4, new byte[1920 * 1080 * 4], new SourceTimestamp((long)(seconds * 25), new TimeBase(1, 25)));

    [Fact]
    public void The_preview_moves_the_contours_like_the_export()
    {
        var (snapshot, _) = Snapshot(reliable: true);
        var layer = snapshot.LayersAt(MediaTime.FromSeconds(1)).Single();
        var frame = FrameAt(1);

        var export = CompositionDrawPlan.Build(Canvas, Affine2D.Identity, new[] { new ResolvedLayer(layer, frame) })
            .Operations.OfType<OutlineDraw>().Single();
        var preview = PreviewDrawPlan.Build(Canvas, 1920, 1080, new[] { new LayerPicture(layer, LayerPictureState.Frame, frame) })
            .Operations.OfType<OutlineDraw>().Single();

        Assert.Equal(export.Polygons[0].ToArray(), preview.Polygons[0].ToArray());   // the same contour, layer-local
        Assert.Equal((0.4 + 0.05) * 1920, export.Polygons[0][0].X, 6);
        Assert.Equal((0.4 + 0.02) * 1080, export.Polygons[0][0].Y, 6);
    }

    [Fact]
    public void On_an_unreliable_frame_neither_plan_draws_the_contours()
    {
        var (snapshot, _) = Snapshot(reliable: false);
        var layer = snapshot.LayersAt(MediaTime.FromSeconds(1)).Single();
        var frame = FrameAt(1);
        Assert.Empty(CompositionDrawPlan.Build(Canvas, Affine2D.Identity, new[] { new ResolvedLayer(layer, frame) }).Operations.OfType<OutlineDraw>());
        Assert.Empty(PreviewDrawPlan.Build(Canvas, 1920, 1080, new[] { new LayerPicture(layer, LayerPictureState.Frame, frame) })
            .Operations.OfType<OutlineDraw>());
    }
}
