using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Hold tracking (R2 → E) in the shared draw plan: with a camera-motion track the contours follow the wall on the shown
/// frame (by its own timestamp — the Preview and the export alike); on an unreliable frame a contour is left out, never
/// drawn off its hold; outside the analysed range, and without a track, everything is exactly as before; the track is
/// presentation only; the range a clip needs.
/// </summary>
public class HoldTrackingCompositionTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };   // 1920 × 1080
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };

    public HoldTrackingCompositionTests() => _project.Timeline.VideoTracks.Add(_v1);

    private static readonly ImmutableArray<NormalizedPoint> Square = ImmutableArray.Create(
        new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.6, 0.4), new NormalizedPoint(0.6, 0.6), new NormalizedPoint(0.4, 0.6));

    private static Hold HoldAt(MediaTime markedAt, ImmutableArray<NormalizedPoint>? points = null) =>
        new(Guid.NewGuid(), points ?? Square, RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#EEEEEE"), markedAt);

    private VideoClip Video(params Hold[] holds)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Duration = S(60), FrameRate = Rate, StartTime = S(0),
                Width = 1920, Height = 1080, DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080
            }
        };
        _project.MediaAssets.Add(asset);
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = F(0), Duration = S(10),
            HoldMarkup = RouteColor.WithResolved(new HoldMarkup { Holds = holds.ToImmutableArray() })
        };
        clip.SourceOut = clip.Duration;
        _v1.Clips.Add(clip);
        return clip;
    }

    private static CameraMotionTrack ShiftTrack(params (double Seconds, double Dx, bool Reliable)[] samples) =>
        new(MediaTime.Zero, CameraMotionModel.Similarity,
            samples.Select(s => new CameraMotionSample(S(s.Seconds), new WallTransform(1, 0, s.Dx, 0, 1, 0, 0, 0, 1), s.Reliable)).ToImmutableArray());

    /// <summary>The outline drawn on the frame whose timestamp is <paramref name="frameSeconds"/> (25 fps time base).</summary>
    private OutlineDraw? OutlineOn(double frameSeconds, IReadOnlyDictionary<Guid, CameraMotionTrack>? motion)
    {
        var snapshot = PlaybackSnapshotBuilder.Build(_project, 1, motion);
        var frame = new DecodedFrame(1920, 1080, 1920 * 4, new byte[1920 * 1080 * 4],
            new SourceTimestamp((long)Math.Round(frameSeconds * 25), new TimeBase(1, 25)));
        var layers = snapshot.LayersAt(F(0)).Select(l => new ResolvedLayer(l, frame));
        return CompositionDrawPlan.Build(new FrameSize(1920, 1080), Affine2D.Identity, layers).Operations.OfType<OutlineDraw>().SingleOrDefault();
    }

    [Fact]
    public void The_contours_follow_the_wall_on_the_shown_frame()
    {
        var clip = Video(HoldAt(S(0)));
        var motion = new Dictionary<Guid, CameraMotionTrack> { [clip.Id] = ShiftTrack((0, 0, true), (1, 0.1, true)) };
        var outline = OutlineOn(1, motion)!;
        Assert.Equal((0.4 + 0.1) * 1920, outline.Polygons[0][0].X, 6);
        Assert.Equal(0.4 * 1080, outline.Polygons[0][0].Y, 6);
    }

    [Fact]
    public void A_contour_marked_on_another_frame_is_carried_from_there()
    {
        var clip = Video(HoldAt(S(0)), HoldAt(S(1)));
        var motion = new Dictionary<Guid, CameraMotionTrack> { [clip.Id] = ShiftTrack((0, 0, true), (1, 0.1, true), (2, 0.15, true)) };
        var outline = OutlineOn(2, motion)!;
        Assert.Equal((0.4 + 0.15) * 1920, outline.Polygons[0][0].X, 6);   // marked at the reference
        Assert.Equal((0.4 + 0.05) * 1920, outline.Polygons[1][0].X, 6);   // marked where the wall had moved by 0.1
    }

    [Fact]
    public void On_an_unreliable_frame_the_contours_are_left_out()
    {
        var clip = Video(HoldAt(S(0)));
        var motion = new Dictionary<Guid, CameraMotionTrack> { [clip.Id] = ShiftTrack((0, 0, true), (1, 0.1, false)) };
        Assert.Null(OutlineOn(1, motion));
    }

    [Fact]
    public void Outside_the_analysed_range_and_without_a_track_nothing_changes()
    {
        var clip = Video(HoldAt(S(0)));
        var before = OutlineOn(5, null)!;
        var motion = new Dictionary<Guid, CameraMotionTrack> { [clip.Id] = ShiftTrack((0, 0, true), (1, 0.1, true)) };
        var outside = OutlineOn(5, motion)!;
        Assert.Equal(before.Polygons[0].ToArray(), outside.Polygons[0].ToArray());
        Assert.Equal(0.4 * 1920, before.Polygons[0][0].X, 9);
        Assert.Null(PlaybackSnapshotBuilder.Build(_project, 1).VideoLayers.Single().Spans.Single().Outline!.Motion);
    }

    [Fact]
    public void A_track_is_presentation_only()
    {
        var clip = Video(HoldAt(S(0)));
        var before = PlaybackSnapshotBuilder.Build(_project, 1);
        var after = PlaybackSnapshotBuilder.Build(_project, 2,
            new Dictionary<Guid, CameraMotionTrack> { [clip.Id] = ShiftTrack((0, 0, true), (1, 0.1, true)) });
        Assert.True(after.DiffersOnlyInPresentation(before));
    }

    [Fact]
    public void The_range_is_the_clips_source_widened_to_the_marking_times()
    {
        var clip = Video(HoldAt(S(12)), HoldAt(S(4)));
        clip.SourceIn = S(5);
        clip.SourceOut = S(15);
        var range = CameraMotionRange.For(clip, Rate)!.Value;
        Assert.Equal(S(12), range.ReferenceTime);
        Assert.Equal(S(4), range.Start);
        Assert.Equal(S(15), range.End);
        Assert.Null(CameraMotionRange.For(new VideoClip { MediaAssetId = Guid.NewGuid() }, Rate));
    }

    [Fact]
    public void With_the_animation_the_range_is_where_the_contours_are_shown()
    {
        var clip = Video(HoldAt(S(2)), HoldAt(S(2)));
        clip.HoldMarkup = clip.HoldMarkup! with
        {
            Animation = new HoldAnimation
            {
                StartSourceTime = S(2), Interval = S(1), AppearFade = MediaTime.Zero, StayAfterLast = S(3), DisappearFade = S(0.4)
            }
        };
        var range = CameraMotionRange.For(clip, Rate)!.Value;
        Assert.Equal(S(2), range.Start);
        Assert.Equal(S(2 + 1 + 3 + 0.4), range.End);   // the last hold at +1 s, stays 3 s, fades 0.4 s (10 frames)
    }
}
