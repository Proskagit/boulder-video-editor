using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 21 (D036 §4): the animation in <see cref="PlaybackSnapshot.LayersAt"/> — the one place the Preview and the export
/// take a frame from. Hidden before the start, the holds appear one by one, stay, vanish together (or stay to the clip's
/// end); the draw plan carries each shown contour's opacity on top of the layer's (clip opacity × fade / dissolve) and
/// leaves hidden ones out; without animation nothing changes; an animation change is presentation-only; inside a
/// dissolve the frames run on; the source anchor survives trim, slip and speed.
/// </summary>
public class HoldAnimationSnapshotTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };   // 1920 × 1080
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };

    public HoldAnimationSnapshotTests() => _project.Timeline.VideoTracks.Add(_v1);

    private static ImmutableArray<NormalizedPoint> Square(double x, double y, double r = 0.03) => ImmutableArray.Create(
        new NormalizedPoint(x - r, y - r), new NormalizedPoint(x + r, y - r), new NormalizedPoint(x + r, y + r), new NormalizedPoint(x - r, y + r));

    /// <summary>Three holds, marked top-down (so bottom to top reverses them): appear at 0, 0.5, 1 s = frames 0, 12/13, 25.</summary>
    private static HoldMarkup Markup(HoldAnimation? animation) => RouteColor.WithResolved(new HoldMarkup
    {
        Holds = ImmutableArray.Create(
            new Hold(Guid.NewGuid(), Square(0.5, 0.2), RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero),
            new Hold(Guid.NewGuid(), Square(0.5, 0.5), RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero),
            new Hold(Guid.NewGuid(), Square(0.5, 0.8), RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero)),
        Animation = animation
    });

    private VideoClip Video(long start, long end, HoldMarkup? markup, long sourceIn = 0, double opacity = 1)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(600), FrameRate = Rate,
                Width = 1920, Height = 1080, DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080
            }
        };
        _project.MediaAssets.Add(asset);
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(end) - F(start), HoldMarkup = markup, Opacity = opacity,
            SourceIn = F(sourceIn)
        };
        clip.SourceOut = clip.SourceIn + clip.Duration;
        _v1.Clips.Add(clip);
        _v1.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private PlaybackSnapshot Snapshot() => PlaybackSnapshotBuilder.Build(_project, 1);

    private static DecodedFrame Frame() => new(1920, 1080, 1920 * 4, new byte[1920 * 1080 * 4], new SourceTimestamp(0, new TimeBase(1, 25)));

    private OutlineDraw? OutlineAt(long frame, Guid clipId, PlaybackSnapshot? snapshot = null)
    {
        var layers = (snapshot ?? Snapshot()).LayersAt(F(frame)).Select(l => new ResolvedLayer(l, Frame()));
        return CompositionDrawPlan.Build(new FrameSize(1920, 1080), Affine2D.Identity, layers)
            .Operations.OfType<OutlineDraw>().SingleOrDefault(o => o.ClipId == clipId);
    }

    /// <summary>The opacity of each hold (by index) drawn on <paramref name="frame"/>: 0 when left out.</summary>
    private double[] Shown(long frame, VideoClip clip)
    {
        var markup = clip.HoldMarkup!;
        var result = new double[markup.Holds.Length];
        if (OutlineAt(frame, clip.Id) is not { } draw) return result;
        // Shown polygons keep the markup order; match them by their first point.
        for (int i = 0, j = 0; i < markup.Holds.Length && j < draw.Polygons.Length; i++)
        {
            var p = markup.Holds[i].Points[0];
            if (Math.Abs(draw.Polygons[j][0].X - p.X * 1920) < 1e-6 && Math.Abs(draw.Polygons[j][0].Y - p.Y * 1080) < 1e-6)
                result[i] = draw.PolygonOpacity(j++);
        }
        return result;
    }

    [Fact]
    public void Hidden_before_the_start_then_one_by_one_bottom_up_they_stay_and_vanish_together()
    {
        // Start 2 s into the source at frame 50 of the clip; 0.5 s interval (12.5 → 13 frames), 0.25 s fades (6 frames),
        // 3 s stay (75), 0.5 s disappear (13).
        var clip = Video(0, 500, Markup(new HoldAnimation { StartSourceTime = F(50) }));
        Assert.Null(OutlineAt(49, clip.Id));                                        // all hidden before the start

        Assert.Equal(new[] { 0, 0, 1 / 7.0 }, Shown(50, clip));                     // the lowest first, fading in
        Assert.Equal(new[] { 0, 0, 1.0 }, Shown(56, clip));
        Assert.Equal(new[] { 0, 1 / 7.0, 1.0 }, Shown(63, clip));                   // 50 + round(12.5) = 63
        Assert.Equal(new[] { 1 / 7.0, 1.0, 1.0 }, Shown(75, clip));                 // 50 + 25
        var allShown = 75 + 6;
        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, Shown(allShown, clip));
        Assert.Empty(OutlineAt(allShown, clip.Id)!.PolygonOpacities);              // all at 1: drawn as without animation

        var d = allShown + 75;
        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, Shown(d - 1, clip));
        Assert.Equal(new[] { 13 / 14.0, 13 / 14.0, 13 / 14.0 }, Shown(d, clip));   // together
        Assert.Equal(new[] { 1 / 14.0, 1 / 14.0, 1 / 14.0 }, Shown(d + 12, clip));
        Assert.Null(OutlineAt(d + 13, clip.Id));
        Assert.Null(OutlineAt(499, clip.Id));
    }

    [Fact]
    public void Until_clip_end_keeps_every_contour_and_without_animation_nothing_changes()
    {
        var stays = Video(0, 500, Markup(new HoldAnimation { StartSourceTime = F(10), StayUntilClipEnd = true }));
        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, Shown(499, stays));

        var still = Video(500, 600, Markup(null));
        var layer = Snapshot().LayersAt(F(500)).OfType<PictureLayer>().Single();
        Assert.True(layer.OutlineOpacities.IsDefault);
        var draw = OutlineAt(500, still.Id)!;
        Assert.Equal(3, draw.Polygons.Length);
        Assert.Empty(draw.PolygonOpacities);
        Assert.Single(draw.OpacityGroups());
    }

    [Fact]
    public void The_contour_opacity_sits_on_top_of_the_clip_opacity_and_its_fade()
    {
        var clip = Video(0, 500, Markup(new HoldAnimation { StartSourceTime = F(0) }), opacity: 0.6);
        clip.FadeIn = F(10);
        var draw = OutlineAt(2, clip.Id)!;                                  // the lowest hold's fade-in frame 2: 3/7
        Assert.Equal(0.6 * 3 / 11.0, draw.Opacity, 12);                     // the layer: clip opacity × its fade (D025)
        Assert.Equal(3 / 7.0, draw.PolygonOpacity(0), 12);                  // the hold's own, multiplied by the renderer
    }

    [Fact]
    public void An_animation_change_is_presentation_only()
    {
        var clip = Video(0, 500, Markup(null));
        var before = Snapshot();
        clip.HoldMarkup = clip.HoldMarkup! with { Animation = new HoldAnimation { StartSourceTime = F(30) } };
        var after = Snapshot();
        Assert.True(before.DiffersOnlyInPresentation(after));
        Assert.NotNull(after.VideoLayers[0].Spans[0].Outline!.Schedule);
    }

    [Fact]
    public void Inside_a_dissolve_the_outgoing_clips_frames_run_on_past_its_end()
    {
        var a = Video(0, 100, Markup(new HoldAnimation { StartSourceTime = F(90), StayUntilClipEnd = true }));
        var b = Video(100, 200, null, sourceIn: 300);
        b.SourceOut = b.SourceIn + b.Duration;
        _v1.Transitions.Add(new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(20), LeftClipId = a.Id, RightClipId = b.Id });

        var snapshot = Snapshot();
        Assert.NotEmpty(snapshot.VideoLayers[0].Dissolves);
        // Frame 105 lies in the zone [90, 110) after A's end: A's holds go on appearing on the timeline frame grid.
        var schedule = snapshot.VideoLayers[0].Spans.Single(s => s.ClipId == a.Id).Outline!.Schedule!;
        var draw = OutlineAt(105, a.Id, snapshot)!;
        var expected = schedule.OpacitiesAt(105).Where(o => o > 0).ToArray();
        Assert.Equal(expected, Enumerable.Range(0, draw.Polygons.Length).Select(draw.PolygonOpacity).ToArray());
        Assert.True(draw.Opacity < 1 || expected.Length > 0);
    }

    [Fact]
    public void The_animation_stays_on_the_source_moment_after_trim_slip_and_a_speed_change()
    {
        var clip = Video(100, 400, Markup(new HoldAnimation { StartSourceTime = F(120) }), sourceIn: 50);
        // Source frame 120 is shown at timeline frame 100 + (120 − 50) = 170.
        Assert.Equal(170, Snapshot().VideoLayers[0].Spans[0].Outline!.Schedule!.StartFrame);

        // Trim the start by 20 frames (source in 70, timeline start 120): still timeline frame 170.
        clip.TimelineStart = F(120); clip.SourceIn = F(70); clip.Duration = F(280);
        Assert.Equal(170, Snapshot().VideoLayers[0].Spans[0].Outline!.Schedule!.StartFrame);

        // Slip by +30 frames of source: source frame 120 comes 30 frames earlier on the timeline.
        clip.SourceIn = F(100);
        Assert.Equal(140, Snapshot().VideoLayers[0].Spans[0].Outline!.Schedule!.StartFrame);

        // 2×: (120 − 100) source frames take 10 timeline frames; the interval stays output time (13 frames).
        clip.Speed = ClipSpeed.FromSteps(40);
        var schedule = Snapshot().VideoLayers[0].Spans[0].Outline!.Schedule!;
        Assert.Equal(130, schedule.StartFrame);
        Assert.Equal(new long[] { 130 + 25, 130 + 13, 130 }, schedule.AppearFrames.ToArray());
    }
}
