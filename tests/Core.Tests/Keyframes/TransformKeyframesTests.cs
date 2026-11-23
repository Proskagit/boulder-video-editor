using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.People;
using Xunit;

namespace AiVideoEditor.Core.Tests.Keyframes;

/// <summary>D063 §1–§5: the keyframes' interpolation (the transition lasts the time between the keyframes, the easings),
/// their edits, the Follow framing (D061 unchanged without keyframes, the part's scale clamped so the picture keeps covering
/// the canvas, no jump at a part's edges, the two systems independent).</summary>
public class TransformKeyframesTests
{
    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    private static TransformKeyframe Key(double seconds, double x, double y = 0, double scale = 1, KeyframeEasing easing = KeyframeEasing.Linear) =>
        new(Guid.NewGuid(), S(seconds), x, y, scale, easing);

    private static TransformKeyframes Keys(params TransformKeyframe[] keys) => new(keys.ToImmutableArray());

    [Fact]
    public void Between_two_keyframes_every_value_moves_over_exactly_the_time_between_them()
    {
        var keys = Keys(Key(1, 0, 100, 1), Key(6, 500, -100, 2));
        Assert.Equal(new TransformValue(0, 100, 1), keys.At(S(0)));             // before the first: held
        Assert.Equal(new TransformValue(0, 100, 1), keys.At(S(1)));             // exactly the keyframe
        Assert.Equal(new TransformValue(250, 0, 1.5), keys.At(S(3.5)));         // half of the five seconds
        Assert.Equal(new TransformValue(100, 60, 1.2), keys.At(S(2)));          // a fifth
        Assert.Equal(new TransformValue(500, -100, 2), keys.At(S(6)));
        Assert.Equal(new TransformValue(500, -100, 2), keys.At(S(60)));         // after the last: held

        // Twice the distance: half the speed.
        var slower = Keys(Key(1, 0), Key(11, 500));
        Assert.Equal(50, slower.At(S(2)).X, 9);
        Assert.Equal(100, keys.At(S(2)).X, 9);
    }

    [Theory]
    [InlineData(KeyframeEasing.Linear)]
    [InlineData(KeyframeEasing.EaseIn)]
    [InlineData(KeyframeEasing.EaseOut)]
    [InlineData(KeyframeEasing.EaseInOut)]
    public void Every_easing_starts_and_ends_on_the_keyframes_and_never_goes_back(KeyframeEasing easing)
    {
        var keys = Keys(Key(0, 0, easing: easing), Key(4, 100));
        Assert.Equal(0, keys.At(S(0)).X);
        Assert.Equal(100, keys.At(S(4)).X);
        var previous = 0.0;
        for (var t = 0.0; t <= 4; t += 0.04)
        {
            var x = keys.At(S(t)).X;
            Assert.True(x >= previous - 1e-9, $"{easing} goes back at {t}");
            Assert.InRange(x, 0, 100);
            previous = x;
        }
    }

    [Fact]
    public void Ease_in_is_slow_at_the_start_ease_out_at_the_end_and_in_out_symmetric()
    {
        double At(KeyframeEasing easing, double t) => Keys(Key(0, 0, easing: easing), Key(1, 100)).At(S(t)).X;
        Assert.True(At(KeyframeEasing.EaseIn, 0.25) < At(KeyframeEasing.Linear, 0.25));
        Assert.True(At(KeyframeEasing.EaseOut, 0.25) > At(KeyframeEasing.Linear, 0.25));
        Assert.Equal(50, At(KeyframeEasing.EaseInOut, 0.5), 9);
        Assert.Equal(100 - At(KeyframeEasing.EaseInOut, 0.2), At(KeyframeEasing.EaseInOut, 0.8), 9);
        // The easing is the outgoing segment's: the next segment is linear again.
        var keys = Keys(Key(0, 0, easing: KeyframeEasing.EaseIn), Key(1, 100), Key(2, 200));
        Assert.Equal(150, keys.At(S(1.5)).X, 9);
    }

    [Fact]
    public void A_value_set_on_a_keyframes_time_changes_it_and_elsewhere_adds_one_with_the_split_segments_easing()
    {
        var first = Key(0, 0, easing: KeyframeEasing.EaseOut);
        var keys = Keys(first, Key(4, 100));
        var (changed, key) = keys.WithValueAt(S(0), new TransformValue(10, 20, 1.5));
        Assert.Equal(first.Id, key.Id);
        Assert.Equal(2, changed.Count);
        Assert.Equal(new TransformValue(10, 20, 1.5), changed.At(S(0)));

        var (added, newKey) = keys.WithValueAt(S(2), new TransformValue(-5, 0, 1));
        Assert.Equal(3, added.Count);
        Assert.Equal(KeyframeEasing.EaseOut, newKey.Easing);
        Assert.Equal(new[] { S(0), S(2), S(4) }, added.Keys.Select(k => k.Time));
        Assert.Null(keys.With(first with { Id = Guid.NewGuid() }));            // another keyframe at a taken time
        Assert.Null(Keys(first).Without(first.Id));                             // none left: no keyframes
    }

    [Fact]
    public void Only_sorted_distinct_in_range_keyframes_are_valid()
    {
        Assert.True(TransformKeyframes.AreValid(ImmutableArray.Create(Key(0, 0), Key(1, 0))));
        Assert.False(TransformKeyframes.AreValid(ImmutableArray<TransformKeyframe>.Empty));
        Assert.False(TransformKeyframes.AreValid(ImmutableArray.Create(Key(1, 0), Key(0, 0))));
        Assert.False(TransformKeyframes.AreValid(ImmutableArray.Create(Key(1, 0), Key(1, 5))));
        Assert.False(TransformKeyframes.AreValid(ImmutableArray.Create(Key(0, double.NaN))));
        Assert.False(TransformKeyframes.AreValid(ImmutableArray.Create(Key(0, 0, scale: 0))));
        Assert.False(TransformKeyframes.AreValid(ImmutableArray.Create(Key(0, 1e9))));
        var key = Key(0, 0);
        Assert.False(TransformKeyframes.AreValid(ImmutableArray.Create(key, key with { Time = S(1) })));   // one id twice
    }

    [Fact]
    public void Keyframes_are_equal_by_value()
    {
        var a = Key(0, 1);
        Assert.Equal(Keys(a), Keys(a));
        Assert.NotEqual(Keys(a), Keys(a with { X = 2 }));
        Assert.Equal(Keys(a).GetHashCode(), Keys(a).GetHashCode());
    }

    [Fact]
    public void Apply_replaces_only_the_position_and_the_scale()
    {
        var visual = new VisualProperties(5, 6, 1, 90, 0.5, new CropRect(0.1, 0, 0, 0));
        var applied = TransformKeyframes.Apply(visual, Keys(Key(0, 100, 200, 2)), S(3));
        Assert.Equal(visual with { PositionX = 100, PositionY = 200, Scale = 2 }, applied);
        Assert.Equal(visual, TransformKeyframes.Apply(visual, null, S(3)));
    }

    // --- Follow framing (D063 §4–§5) ----------------------------------------------------------------------------------

    private static readonly FrameSize Portrait = new(1080, 1920);
    private static readonly FrameSize Source = new(2160, 3840);

    private static FollowTrajectory Trajectory(int count, Func<int, NormalizedPoint> at) => new(
        Enumerable.Range(0, count).Select(i =>
        {
            var p = at(i);
            return new FollowPoint(S(i * 0.1), p, new NormalizedBox(p.X - 0.1, p.Y - 0.2, p.X + 0.1, p.Y + 0.2));
        }).ToImmutableArray(), ImmutableArray.Create(0));

    [Fact]
    public void Without_keyframes_the_framing_is_exactly_the_D061_path()
    {
        var trajectory = Trajectory(100, i => new NormalizedPoint(0.3 + i * 0.004, 0.5));
        var visual = VisualProperties.Default with { Scale = 2 };
        var (path, _) = FollowCamera.Solve(trajectory, Portrait, Source, visual, FollowSmoothness.Normal, 40, -30);
        var withNull = FollowCamera.Solve(trajectory, Portrait, Source, visual, FollowSmoothness.Normal, 40, -30,
            _ => (new TransformValue(40, -30, 2), 0, 0)).Path!;
        var firstDiff = Enumerable.Range(0, path!.X.Length).FirstOrDefault(i => path.X[i] != withNull.X[i] || path.Y[i] != withNull.Y[i], -1);
        Assert.True(firstDiff < 0, firstDiff < 0 ? "" : $"{firstDiff}: {path.X[firstDiff]:R} / {withNull.X[firstDiff]:R}, {path.Y[firstDiff]:R} / {withNull.Y[firstDiff]:R}");

        var framing = new FollowFraming(new[] { new FollowFramingPart(0, path, S(0), S(10), 40, -30) }, S(0), S(10), visual, null, Portrait);
        for (var t = 0.0; t < 9.9; t += 0.37)
        {
            var (x, y) = path.At(S(t));
            Assert.Equal(visual with { PositionX = x, PositionY = y }, framing.At(S(t)));
        }
    }

    [Fact]
    public void A_parts_scale_keyframes_zoom_while_the_picture_keeps_covering_the_canvas_on_every_frame()
    {
        var trajectory = Trajectory(100, i => new NormalizedPoint(i % 2 == 0 ? 0.05 : 0.95, 0.5));   // the person at the edges
        var visual = VisualProperties.Default with { Scale = 1.2 };
        var keys = Keys(Key(0, 0, 0, 1.2), Key(9.9, 0, 0, 3));
        var part = new FollowPart(Keyframes: keys);
        var (path, reason) = FollowCamera.Solve(trajectory, Portrait, Source, visual, FollowSmoothness.Fast, framingAt: t => (part.FramingAt(t, 1.2), 0, 0));
        Assert.Equal(FollowUnavailable.None, reason);
        var framing = new FollowFraming(new[] { new FollowFramingPart(0, path!, S(0), S(10), Keyframes: keys) }, S(0), S(10), visual, null, Portrait);
        for (var t = 0.0; t < 9.9; t += 1 / 30.0)
        {
            var v = framing.At(S(t));
            Assert.Equal(keys.At(S(t)).Scale, v.Scale, 12);
            Assert.True(CompositionMath.Layout(Portrait, Source, v).CoversCanvas, $"a border at {t:F2} s (scale {v.Scale:F3})");
        }
    }

    [Fact]
    public void A_parts_framing_glides_in_and_out_of_the_clips_own_keyframed_transform_without_a_jump()
    {
        var times = Enumerable.Range(0, 101).Select(i => S(i * 0.1)).ToImmutableArray();
        var path = new FollowPath(times, times.Select(_ => -100.0).ToImmutableArray(), times.Select(_ => 0.0).ToImmutableArray())
        {
            MovesX = true, Extent = new FollowExtent(new RectD(-1000, -1000, 3080, 3920), 2)
        };
        var clipKeys = Keys(Key(0, 50, 0, 2), Key(10, 150, 0, 2.5));           // the clip moves and zooms on its own
        var partKeys = Keys(Key(2, 0, 0, 3));                                   // the part zooms to 3
        var framing = new FollowFraming(new[] { new FollowFramingPart(0, path, S(2), S(6), Keyframes: partKeys) }, S(0), S(10),
            VisualProperties.Default with { Scale = 2 }, clipKeys, Portrait);

        Assert.Equal(TransformKeyframes.Apply(VisualProperties.Default with { Scale = 2 }, clipKeys, S(1)), framing.At(S(1)));   // before the part
        Assert.Equal(TransformKeyframes.Apply(VisualProperties.Default with { Scale = 2 }, clipKeys, S(2)), framing.At(S(2)));   // its very start
        Assert.Equal(3, framing.At(S(4)).Scale, 9);                             // inside: the part's scale
        Assert.Equal(TransformKeyframes.Apply(VisualProperties.Default with { Scale = 2 }, clipKeys, S(8)), framing.At(S(8)));   // after
        var previous = framing.At(S(0));
        for (var t = 0.0; t <= 10; t += 1 / 30.0)
        {
            var v = framing.At(S(t));
            Assert.True(Math.Abs(v.PositionX - previous.PositionX) < 15, $"a position jump at {t:F2} s");
            Assert.True(Math.Abs(v.Scale - previous.Scale) < 0.08, $"a scale jump at {t:F2} s");
            previous = v;
        }
    }

    [Fact]
    public void The_clips_keyframes_never_change_a_fully_followed_frame_and_the_parts_never_change_the_clips_own()
    {
        var times = Enumerable.Range(0, 101).Select(i => S(i * 0.1)).ToImmutableArray();
        var path = new FollowPath(times, times.Select(_ => -100.0).ToImmutableArray(), times.Select(_ => 0.0).ToImmutableArray())
        {
            MovesX = true, Extent = new FollowExtent(new RectD(-1000, -1000, 3080, 3920), 2)
        };
        var partKeys = Keys(Key(2, 0, 0, 2.5));
        FollowFraming With(TransformKeyframes? clipKeys) => new(new[] { new FollowFramingPart(0, path, S(2), S(6), Keyframes: partKeys) },
            S(0), S(10), VisualProperties.Default with { Scale = 2 }, clipKeys, Portrait);
        var plain = With(null);
        var keyed = With(Keys(Key(0, 300, 40, 1.1), Key(10, -300, 40, 4)));
        // Inside the part: the part's framing — the camera's X and the part's scale; the camera does not move vertically here
        // (no room on that axis), so Y is the clip's own there, as D061 keeps it.
        Assert.Equal((plain.At(S(4)).PositionX, plain.At(S(4)).Scale), (keyed.At(S(4)).PositionX, keyed.At(S(4)).Scale));
        Assert.Equal(40, keyed.At(S(4)).PositionY);
        Assert.Equal(new TransformValue(300, 40, 1.1), new TransformValue(keyed.At(S(0)).PositionX, keyed.At(S(0)).PositionY, keyed.At(S(0)).Scale));
        Assert.Equal(VisualProperties.Default with { Scale = 2 }, plain.At(S(9)));   // outside: the clip's own, the part's keyframes unused
    }

    // --- Canvas snap (D063 §B) -----------------------------------------------------------------------------------------

    private static readonly FrameSize Landscape = new(1920, 1080);

    [Theory]
    [InlineData(1.2, 0, 0, 0)]
    [InlineData(1.137, 0, 0.1, 0)]                                              // a cropped picture
    [InlineData(1.3, 0, 0, 90)]
    [InlineData(1.0731, 0.07, 0, 180)]
    public void A_move_snaps_the_edge_onto_the_canvas_edge_exactly(double scale, double cropLeft, double cropTop, double rotation)
    {
        var source = new FrameSize(1920, 1080);
        var visual = new VisualProperties(0, 0, scale, rotation, 1, new CropRect(cropLeft, cropTop, 0, 0));
        var bounds = CompositionMath.Layout(Landscape, source, visual).Bounds;
        foreach (var (raw, edge) in new[]
                 {
                     (visual with { PositionX = -bounds.X - 5.3 }, CropEdges.Left),              // the left edge 5.3 px inside
                     (visual with { PositionX = Landscape.Width - bounds.Right + 3.9 }, CropEdges.Right),
                     (visual with { PositionY = -bounds.Y + 2.2 }, CropEdges.Top),               // past the top edge by 2.2 px
                     (visual with { PositionY = Landscape.Height - bounds.Bottom - 7.7 }, CropEdges.Bottom)
                 })
        {
            var snapped = CanvasSnap.Move(Landscape, source, raw, threshold: 8);
            Assert.True(snapped.Edges.HasFlag(edge), $"{edge} not snapped");
            var b = CompositionMath.Layout(Landscape, source, snapped.Visual).Bounds;
            var distance = edge switch
            {
                CropEdges.Left => b.X,
                CropEdges.Right => Landscape.Width - b.Right,
                CropEdges.Top => b.Y,
                _ => Landscape.Height - b.Bottom
            };
            // On the edge to the last bits, never short of it (the covering side).
            Assert.InRange(distance, -1e-9, 1e-12);
        }
    }

    [Fact]
    public void A_snapped_picture_that_reaches_every_edge_provably_covers_the_canvas()
    {
        // 0.3 % short of the canvas on every side: a hairline black border before the snap.
        var source = new FrameSize(3840, 2160);
        var visual = VisualProperties.Default with { Scale = 0.997 };
        Assert.False(CompositionMath.Layout(Landscape, source, visual).CoversCanvas);
        var snapped = CanvasSnap.Scale(Landscape, source, visual, threshold: 8);
        Assert.True(snapped.Snapped);
        Assert.True(CompositionMath.Layout(Landscape, source, snapped.Visual).CoversCanvas);
        Assert.Equal(1.0, snapped.Visual.Scale);                                // exactly the canvas
    }

    [Fact]
    public void Beyond_the_threshold_nothing_snaps_and_the_snap_depends_on_the_raw_value_alone()
    {
        var visual = VisualProperties.Default with { Scale = 1.2 };
        var bounds = CompositionMath.Layout(Landscape, Landscape, visual).Bounds;
        var far = visual with { PositionX = -bounds.X - 8.5 };
        Assert.Equal(new CanvasSnapResult(far, CropEdges.None), CanvasSnap.Move(Landscape, Landscape, far, threshold: 8));
        var near = visual with { PositionX = -bounds.X - 7.5 };
        Assert.Equal(CanvasSnap.Move(Landscape, Landscape, near, 8), CanvasSnap.Move(Landscape, Landscape, near, 8));   // stable
        Assert.False(CanvasSnap.Move(Landscape, Landscape, near, threshold: 0).Snapped);                                // off
    }

    [Fact]
    public void A_scale_snap_keeps_the_aspect_and_the_centre()
    {
        var visual = new VisualProperties(30, -12, 1.03, 0, 1, CropRect.None);
        var bounds = CompositionMath.Layout(Landscape, Landscape, visual).Bounds;
        var snapped = CanvasSnap.Scale(Landscape, Landscape, visual, threshold: 40);
        Assert.True(snapped.Snapped);
        Assert.Equal((visual.PositionX, visual.PositionY, visual.RotationDegrees, visual.Crop), (snapped.Visual.PositionX, snapped.Visual.PositionY,
            snapped.Visual.RotationDegrees, snapped.Visual.Crop));
        var b = CompositionMath.Layout(Landscape, Landscape, snapped.Visual).Bounds;
        var nearest = new[] { Math.Abs(b.X), Math.Abs(b.Right - 1920), Math.Abs(b.Y), Math.Abs(b.Bottom - 1080) }.Min();
        Assert.InRange(nearest, 0, 1e-9);
        Assert.True(bounds.Width / bounds.Height - b.Width / b.Height < 1e-9);
    }

    // --- The corner overlays (D063 §C) ---------------------------------------------------------------------------------

    [Fact]
    public void The_sizes_follow_the_canvas_shape_the_landscape_heart_rate_one_and_a_half_times_smaller()
    {
        Assert.Equal(1, CornerOverlayLayout.PlateFactor(new FrameSize(1920, 1080)));                       // the plate as before
        Assert.Equal(1 / 1.5, CornerOverlayLayout.HeartFactor(new FrameSize(3840, 2160)), 12);             // amendment 1
        Assert.Equal(CornerOverlayLayout.PortraitPlateFactor, CornerOverlayLayout.PlateFactor(new FrameSize(1080, 1920)), 12);
        Assert.Equal(CornerOverlayLayout.PortraitHeartFactor, CornerOverlayLayout.HeartFactor(new FrameSize(2160, 3840)), 12);
        var fourFive = new FrameSize(1080, 1350);                               // between: in proportion to the shape
        Assert.InRange(CornerOverlayLayout.PlateFactor(fourFive), 1.01, CornerOverlayLayout.PortraitPlateFactor - 0.01);
        Assert.Equal(CornerOverlayLayout.PlateFactor(new FrameSize(720, 1280)), CornerOverlayLayout.PlateFactor(new FrameSize(2160, 3840)), 12);
    }

    [Fact]
    public void A_bottom_plate_stands_one_plate_height_above_its_margin_and_a_top_one_where_it_was()
    {
        // D079: one plate's height higher than D063 amendment 1's place in a bottom corner (YouTube's description).
        var (x, top, height) = CornerOverlayLayout.PlateRow(new FrameSize(1920, 1080), OverlayCorner.BottomLeft);
        Assert.Equal((60.0, 1080 - 64 - 62.0 - 62.0, 62.0), (x, top, height));
        var (rx, rtop, _) = CornerOverlayLayout.PlateRow(new FrameSize(1920, 1080), OverlayCorner.BottomRight);
        Assert.Equal((1920 - 60.0, top), (rx, rtop));
        var (vx, vtop, vheight) = CornerOverlayLayout.PlateRow(new FrameSize(1080, 1920), OverlayCorner.BottomLeft);
        Assert.Equal(48, vx, 9);
        Assert.Equal(Math.Round(1920 - 176 - 62 * CornerOverlayLayout.PortraitPlateFactor) - vheight, vtop, 9);   // its bottom on a whole pixel
        Assert.Equal(62 * CornerOverlayLayout.PortraitPlateFactor, vheight, 9);
        var (_, k4top, k4height) = CornerOverlayLayout.PlateRow(new FrameSize(3840, 2160), OverlayCorner.BottomLeft);
        Assert.Equal(2160 - 2 * (64 + 62.0 + 62.0), k4top, 9);                   // in 1080-units on any resolution
        Assert.Equal(124, k4height, 9);
        // The top corners are unchanged.
        Assert.Equal(64.0, CornerOverlayLayout.PlateRow(new FrameSize(1920, 1080), OverlayCorner.TopLeft).Top, 9);
        Assert.Equal(200.0, CornerOverlayLayout.PlateRow(new FrameSize(1080, 1920), OverlayCorner.TopRight).Top, 9);
    }

    [Fact]
    public void The_heart_rate_beside_a_bottom_plate_goes_up_with_it_and_stays_side_by_side()
    {
        var canvas = new FrameSize(1920, 1080);
        var (outerX, top, plateHeight) = CornerOverlayLayout.PlateRow(canvas, OverlayCorner.BottomRight);
        var heart = CornerOverlayLayout.HeartRate(canvas, OverlayCorner.BottomRight, 200, 65, plateCorner: OverlayCorner.BottomRight);
        Assert.Equal(top - CornerOverlayLayout.StackGapUnits - 65, heart.Y, 9);   // right above the raised plate
        Assert.Equal(outerX - 200, heart.X, 9);
        Assert.Equal(1080 - 64 - 2 * 62.0, top, 9);
        // Alone in a bottom corner: on the raised plate's line.
        var alone = CornerOverlayLayout.HeartRate(canvas, OverlayCorner.BottomLeft, 200, 65, plateCorner: OverlayCorner.TopLeft);
        Assert.Equal(top + plateHeight / 2, alone.Y + alone.Height / 2, 9);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1080, 1920)]
    [InlineData(1080, 1080)]
    public void In_one_corner_the_heart_rate_stands_beside_the_plate_and_in_another_on_the_plates_line(int width, int height)
    {
        var canvas = new FrameSize(width, height);
        foreach (var corner in Enum.GetValues<OverlayCorner>())
        {
            var (outerX, top, plateHeight) = CornerOverlayLayout.PlateRow(canvas, corner);
            var heart = CornerOverlayLayout.HeartRate(canvas, corner, 300, 98, plateCorner: corner);
            Assert.True(heart.Bottom <= top || heart.Y >= top + plateHeight, $"{corner}: over the plate");
            Assert.True(heart.X >= 0 && heart.Right <= width && heart.Y >= 0 && heart.Bottom <= height, $"{corner}: outside");
            var plateCentreY = top + plateHeight / 2;
            Assert.True(Math.Abs(heart.Y + heart.Height / 2 - height / 2.0) < Math.Abs(plateCentreY - height / 2.0), $"{corner}: not nearer the centre");
            Assert.Equal(CornerOverlayLayout.IsLeft(corner) ? outerX : outerX - 300, heart.X, 9);

            // The plate in the other corner of the same side (bottom left / bottom right …): both on one line.
            var neighbour = corner switch
            {
                OverlayCorner.BottomLeft => OverlayCorner.BottomRight,
                OverlayCorner.BottomRight => OverlayCorner.BottomLeft,
                OverlayCorner.TopLeft => OverlayCorner.TopRight,
                _ => OverlayCorner.TopLeft
            };
            var alone = CornerOverlayLayout.HeartRate(canvas, corner, 300, 98, plateCorner: neighbour);
            Assert.Equal(CornerOverlayLayout.HeartRate(canvas, corner, 300, 98, plateCorner: null), alone);
            var (_, neighbourTop, neighbourHeight) = CornerOverlayLayout.PlateRow(canvas, neighbour);
            Assert.Equal(neighbourTop + neighbourHeight / 2, alone.Y + alone.Height / 2, 9);
            Assert.Equal(CornerOverlayLayout.IsLeft(corner) ? outerX : outerX - 300, alone.X, 9);
        }
    }

    // --- The corner scale (D063 amendment 1) ---------------------------------------------------------------------------

    [Fact]
    public void A_corner_scale_holds_the_opposite_corner_and_grows_towards_the_dragged_one()
    {
        // A half-size picture put bottom left; its top-right corner dragged up and right.
        var visual = new VisualProperties(-480, 270, 0.5, 0, 1, CropRect.None);
        var frame = PreviewTransformMath.PictureFrame(CompositionMath.Layout(Landscape, Landscape, visual));
        var anchor = frame.HandlePoint(CropEdges.Left | CropEdges.Bottom);
        Assert.Equal(new PointD(0, 1080), anchor);
        var corner = frame.HandlePoint(CropEdges.Right | CropEdges.Top);
        var dragged = PreviewTransformMath.ScaleFromAnchor(visual, anchor, corner,
            new PointD(anchor.X + 1.5 * (corner.X - anchor.X), anchor.Y + 1.5 * (corner.Y - anchor.Y)));
        Assert.Equal(0.75, dragged.Scale, 9);
        var b = CompositionMath.Layout(Landscape, Landscape, dragged).Bounds;
        Assert.Equal(0, b.X, 9);                                               // the bottom-left corner held
        Assert.Equal(1080, b.Bottom, 9);
        Assert.Equal(1440, b.Right, 9);                                        // grown up and right
        Assert.Equal(270, b.Y, 9);
    }

    [Fact]
    public void A_picture_in_a_corner_stretched_to_the_opposite_corner_snaps_to_fill_the_canvas_exactly()
    {
        var source = new FrameSize(3840, 2160);
        var start = new VisualProperties(-480, 270, 0.5, 0, 1, CropRect.None);   // bottom left, exactly on the edges
        var frame = PreviewTransformMath.PictureFrame(CompositionMath.Layout(Landscape, source, start));
        var anchor = frame.HandlePoint(CropEdges.Left | CropEdges.Bottom);
        var corner = frame.HandlePoint(CropEdges.Right | CropEdges.Top);
        var raw = PreviewTransformMath.ScaleFromAnchor(start, anchor, corner, new PointD(1913.7, 4.6));   // a hair short of the top right
        var snapped = CanvasSnap.ScaleFromAnchor(Landscape, source, start, raw, threshold: 8);
        Assert.True(snapped.Snapped);
        var geometry = CompositionMath.Layout(Landscape, source, snapped.Visual);
        Assert.True(geometry.CoversCanvas);                                    // no black line at any edge
        Assert.InRange(geometry.Bounds.X, -1e-9, 0);
        Assert.InRange(geometry.Bounds.Bottom - 1080, 0, 1e-9);

        // Beyond the threshold: the picture grows freely, the held corner still exactly on the canvas corner.
        var free = CanvasSnap.ScaleFromAnchor(Landscape, source, start, PreviewTransformMath.ScaleFromAnchor(start, anchor, corner, new PointD(1500, 300)), 8);
        Assert.False(free.Snapped);
        var fb = CompositionMath.Layout(Landscape, source, free.Visual).Bounds;
        Assert.InRange(fb.X, -1e-9, 0);
        Assert.InRange(fb.Bottom - 1080, 0, 1e-9);
    }
}
