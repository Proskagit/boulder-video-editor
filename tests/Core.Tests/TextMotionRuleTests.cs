using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 27 (D043 §1): <see cref="TextMotionRule"/> and its use in <see cref="PlaybackSnapshot.LayersAt"/> — on timeline
/// frames over the D025 fade ramps: the eased progress, Slide toward each side by 8 % of the short side along the canvas
/// axis, Zoom 70 → 100 % around the box centre, both edges, a short clip, no motion on an edge with a dissolve, a text
/// without motion keeping exactly its D018 transform, the opacity still D025's ramp.
/// </summary>
public class TextMotionRuleTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly FrameSize Canvas = new(1080, 1920);   // short side 1080 → d = 86.4 px

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    [Theory]
    [InlineData(0, 4, 1 - 0.8 * 0.8 * 0.8)]        // p = 1/5
    [InlineData(1, 4, 1 - 0.6 * 0.6 * 0.6)]
    [InlineData(3, 4, 1 - 0.2 * 0.2 * 0.2)]
    [InlineData(4, 4, 1.0)]                        // past the ramp: at rest
    [InlineData(-1, 4, 1.0)]
    [InlineData(0, 0, 1.0)]                        // no fade: no motion
    public void The_progress_is_the_fade_ramp_eased_out(long k, long steps, double expected) =>
        Assert.Equal(expected, TextMotionRule.Progress(k, steps), 12);

    private static TextProperties Text(TextMotion motionIn, TextMotion? motionOut = null) =>
        new("Title", "Segoe UI", 48, "#FFFFFF", TextAlignment.Center) { MotionIn = motionIn, MotionOut = motionOut ?? TextMotion.None };

    [Theory]
    [InlineData(TextMotionSide.Bottom, 0, 1)]
    [InlineData(TextMotionSide.Top, 0, -1)]
    [InlineData(TextMotionSide.Left, -1, 0)]
    [InlineData(TextMotionSide.Right, 1, 0)]
    public void A_slide_comes_from_its_side_along_the_canvas_axis(TextMotionSide side, int sx, int sy)
    {
        var rotated = CompositionMath.TextTransform(Canvas, VisualProperties.Default with { RotationDegrees = 30, PositionX = 15 });
        var text = Text(new TextMotion(TextMotionKind.Slide, side));

        // Clip [100, 200), fade in 4 frames: frame 100 is k = 0.
        var moved = TextMotionRule.Apply(rotated, text, Canvas, 100, 100, 200, 4, 0);
        var d = (1 - TextMotionRule.Progress(0, 4)) * 0.08 * 1080;
        var origin = rotated.Apply(new PointD(0, 0));
        var shifted = moved.Apply(new PointD(0, 0));
        Assert.Equal(origin.X + sx * d, shifted.X, 9);
        Assert.Equal(origin.Y + sy * d, shifted.Y, 9);
        Assert.Equal(rotated.Determinant, moved.Determinant, 12);              // no scale, no extra rotation
    }

    [Fact]
    public void A_zoom_scales_around_the_box_centre_from_70_percent()
    {
        var t = CompositionMath.TextTransform(Canvas, VisualProperties.Default with { PositionY = 500, Scale = 2 });
        var zoomed = TextMotionRule.Apply(t, Text(new TextMotion(TextMotionKind.Zoom)), Canvas, 10, 10, 50, 3, 0);
        var factor = 0.7 + 0.3 * TextMotionRule.Progress(0, 3);
        Assert.Equal(t.Apply(new PointD(0, 0)), zoomed.Apply(new PointD(0, 0)));       // the centre stays
        Assert.Equal(t.Determinant * factor * factor, zoomed.Determinant, 9);
    }

    [Fact]
    public void Out_goes_toward_its_side_and_ends_moved_on_the_last_frame()
    {
        var t = CompositionMath.TextTransform(Canvas, VisualProperties.Default);
        var text = Text(TextMotion.None, new TextMotion(TextMotionKind.Slide, TextMotionSide.Bottom));
        // Clip [0, 50), fade out 5: the last frame 49 is j = 0 (most moved), frame 44 is at rest.
        var last = TextMotionRule.Apply(t, text, Canvas, 49, 0, 50, 0, 5).Apply(new PointD(0, 0));
        var rest = TextMotionRule.Apply(t, text, Canvas, 44, 0, 50, 0, 5);
        Assert.True(last.Y > t.Apply(new PointD(0, 0)).Y);
        Assert.Equal(t, rest);
    }

    [Fact]
    public void Both_edges_of_a_short_clip_apply_together()
    {
        var t = CompositionMath.TextTransform(Canvas, VisualProperties.Default);
        var text = Text(new TextMotion(TextMotionKind.Slide, TextMotionSide.Left), new TextMotion(TextMotionKind.Slide, TextMotionSide.Top));
        var p = TextMotionRule.Apply(t, text, Canvas, 1, 0, 3, 3, 3).Apply(new PointD(0, 0));   // in k = 1, out j = 1
        var c = t.Apply(new PointD(0, 0));
        Assert.True(p.X < c.X && p.Y < c.Y);
    }

    [Fact]
    public void A_text_without_motion_keeps_exactly_its_transform()
    {
        var t = CompositionMath.TextTransform(Canvas, VisualProperties.Default with { RotationDegrees = 12.5, Scale = 1.3, PositionX = -7.25 });
        Assert.Equal(t, TextMotionRule.Apply(t, Text(TextMotion.None), Canvas, 0, 0, 10, 5, 5));
    }

    // --- In the snapshot ----------------------------------------------------------------------------------------------

    private static (Project Project, Track Track) Project()
    {
        var project = new Project { Settings = { FrameRate = Rate, FrameWidth = 1080, FrameHeight = 1920 } };
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(track);
        return (project, track);
    }

    private static TextLayer Layer(Project project, long frame) =>
        Assert.IsType<TextLayer>(Assert.Single(PlaybackSnapshotBuilder.Build(project, 1).LayersAt(F(frame))));

    [Fact]
    public void The_snapshot_moves_the_text_over_its_fade_and_keeps_the_fade_opacity()
    {
        var (project, track) = Project();
        var clip = new TextClip
        {
            Text = "Title", TimelineStart = F(10), Duration = F(40), FadeIn = F(4), FadeOut = F(2),
            MotionIn = new TextMotion(TextMotionKind.Slide, TextMotionSide.Bottom)
        };
        track.Clips.Add(clip);

        var still = CompositionMath.TextTransform(Canvas, VisualProperties.Of(clip)!.Value);
        var first = Layer(project, 10);
        Assert.Equal(1.0 / 5, first.Opacity, 12);                                   // D025's ramp, unchanged
        Assert.Equal(still.Apply(new PointD(0, 0)).Y + (1 - TextMotionRule.Progress(0, 4)) * 86.4, first.Transform.Apply(new PointD(0, 0)).Y, 9);
        Assert.Equal(still, Layer(project, 14).Transform);                          // at rest after the ramp
        Assert.Equal(still, Layer(project, 49).Transform);                          // Out has no motion
    }

    [Fact]
    public void An_edge_with_a_dissolve_has_no_fade_and_no_motion()
    {
        var (project, track) = Project();
        var asset = new MediaAsset
        {
            FilePath = @"C:\media\a.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(60), FrameRate = Rate, Width = 1080, Height = 1920, DisplayRotation = 0, DisplayWidth = 1080, DisplayHeight = 1920 }
        };
        project.MediaAssets.Add(asset);
        var video = new VideoClip { MediaAssetId = asset.Id, TimelineStart = F(0), Duration = F(50), SourceIn = F(10), SourceOut = F(60) };
        var text = new TextClip
        {
            Text = "Title", TimelineStart = F(50), Duration = F(50), FadeIn = F(10),
            MotionIn = new TextMotion(TextMotionKind.Zoom)
        };
        track.Clips.AddRange(new Clip[] { video, text });
        track.Transitions.Add(new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(4), LeftClipId = video.Id, RightClipId = text.Id });

        var still = CompositionMath.TextTransform(Canvas, VisualProperties.Of(text)!.Value);
        var layers = PlaybackSnapshotBuilder.Build(project, 1).LayersAt(F(52));      // inside the zone, after the cut
        Assert.Equal(still, layers.OfType<TextLayer>().Single().Transform);
    }

    [Fact]
    public void Durations_follow_the_fades_in_time_at_another_rate()
    {
        var (project, track) = Project();
        project.Settings.FrameRate = FrameRate.Fps60;
        track.Clips.Add(new TextClip
        {
            Text = "Title", TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(120, FrameRate.Fps60),
            FadeIn = MediaTime.FromFrame(24, FrameRate.Fps60), MotionIn = new TextMotion(TextMotionKind.Zoom)
        });
        var snapshot = PlaybackSnapshotBuilder.Build(project, 1);
        var moving = snapshot.LayersAt(MediaTime.FromFrame(23, FrameRate.Fps60)).OfType<TextLayer>().Single().Transform;
        var rest = snapshot.LayersAt(MediaTime.FromFrame(24, FrameRate.Fps60)).OfType<TextLayer>().Single().Transform;
        Assert.NotEqual(rest, moving);
        Assert.Equal(CompositionMath.TextTransform(Canvas, VisualProperties.Default), rest);
    }
}
