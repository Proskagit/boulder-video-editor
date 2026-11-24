using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.People;
using Xunit;
using static AiVideoEditor.Core.Tests.People.PeopleTestData;

namespace AiVideoEditor.Core.Tests.People;

/// <summary>D080 §3: Follow keeps the climber at a scale — a constant share while he climbs away, never a black border, smooth,
/// the automatic share (≤ 20 % over the starting frame, within the source's reserve), per part, off as before.</summary>
public class FollowZoomTests
{
    private static readonly FrameSize Landscape = new(1920, 1080);
    private static readonly FrameSize Portrait = new(1080, 1920);
    private static readonly FrameSize Source4K = new(3840, 2160);
    private static readonly FrameSize Source1080 = new(1920, 1080);

    /// <summary>A climber at x 0.5 climbing up (y 0.7 → 0.3) over 10 s, his box height <paramref name="height"/>(t).</summary>
    private static FollowTrajectory Climb(Func<double, double> height, int frames = 101) => new(
        Enumerable.Range(0, frames).Select(i =>
        {
            var t = i * 0.1;
            var (cx, cy, h) = (0.5, 0.7 - 0.04 * t, height(t));
            return new FollowPoint(S(t), new NormalizedPoint(cx, cy), new NormalizedBox(cx - h / 4, cy - h / 2, cx + h / 4, cy + h / 2));
        }).ToImmutableArray(), ImmutableArray.Create(0));

    /// <summary>He goes from 40 % of the source's height to 20 % (exponentially: the camera's log-scale is then linear).</summary>
    private static double Away(double t) => 0.4 * Math.Pow(0.5, t / 10);

    private static FollowZoomCurve Solve(FollowTrajectory climb, FrameSize canvas, FrameSize source, double? share, double baseScale = 1,
        FollowSmoothness smoothness = FollowSmoothness.Normal) =>
        FollowZoom.Solve(climb, S(0), S(10), canvas, source, VisualProperties.Default, baseScale, share, smoothness)!;

    [Fact]
    public void The_climber_keeps_his_share_of_the_frame_while_he_climbs_away()
    {
        var curve = Solve(Climb(Away), Landscape, Source4K, share: 0.4);
        Assert.False(curve.IsAuto);
        // The height on a 1080 canvas at scale 1 is the box's share of the source's height × 1080.
        foreach (var t in new[] { 2.0, 4.0, 5.0, 6.0, 8.0 })
        {
            var share = Away(t) * curve.At(S(t));
            Assert.InRange(share, 0.4 * 0.98, 0.4 * 1.02);
        }
        Assert.InRange(curve.At(S(8)), 1.6, 1.8);          // ≈ 0.4 / 0.23: twice nearer at the top
    }

    [Fact]
    public void The_scale_never_goes_below_the_fill_so_no_black_borders()
    {
        // The share asks for less than the fill (the climber close to the camera): the scale stops exactly at the fill.
        var curve = Solve(Climb(Away), Landscape, Source4K, share: 0.1);
        Assert.All(curve.Scales, s => Assert.True(s >= 1, $"{s}"));
        Assert.Equal(1, curve.At(S(1)), 9);
        // Portrait from landscape 4K: the fill is ≈ 3.16; nothing drawn below it, between the analysed frames too.
        var fill = CompositionMath.FillScale(Portrait, Source4K, VisualProperties.Default);
        var portrait = Solve(Climb(Away), Portrait, Source4K, share: 0.2);
        for (var t = 0.0; t <= 10; t += 1 / 60.0)
            Assert.True(portrait.At(S(t)) >= fill, $"{t:F3} s");
    }

    [Fact]
    public void The_scale_moves_smoothly_without_a_step()
    {
        // A sudden change of the height (the climber turns): the camera eases over it.
        var curve = Solve(Climb(t => t < 5 ? 0.4 : 0.2), Landscape, Source4K, share: 0.4);
        var previous = curve.At(S(0));
        for (var t = 0.0; t <= 10; t += 1 / 60.0)
        {
            var s = curve.At(S(t));
            Assert.True(Math.Abs(s - previous) < 0.02, $"step {s - previous:F4} at {t:F2} s");
            previous = s;
        }
    }

    [Fact]
    public void The_arms_up_for_a_moment_do_not_change_the_scale()
    {
        var curve = Solve(Climb(t => t is > 4 and < 4.6 ? 0.45 : 0.3), Landscape, Source4K, share: 0.45);
        Assert.Equal(curve.At(S(2)), curve.At(S(4.3)), 6);
    }

    [Fact]
    public void A_lost_climber_keeps_his_last_height()
    {
        var climb = Climb(_ => 0.3);
        var points = climb.Points.Select((p, i) => i is > 40 and < 60 ? p with { Anchor = null, Box = null } : p).ToImmutableArray();
        var curve = FollowZoom.Solve(climb with { Points = points }, S(0), S(10), Landscape, Source4K, VisualProperties.Default, 1, 0.45,
            FollowSmoothness.Normal)!;
        Assert.Equal(1.5, curve.At(S(5)), 6);
    }

    [Fact]
    public void A_long_loss_holds_the_height_like_the_position_then_eases_to_the_new_one()
    {
        // Seen at 30 % until 3 s, lost 3–7 s, seen again at 15 %: the scale holds through the loss (the camera's position holds
        // too) and moves only over the last second before the climber is seen again — never a slow zoom on a stale point.
        var climb = Climb(t => t < 5 ? 0.3 : 0.15);
        var points = climb.Points.Select((p, i) => i is > 30 and < 70 ? p with { Anchor = null, Box = null } : p).ToImmutableArray();
        var curve = FollowZoom.Solve(climb with { Points = points }, S(0), S(10), Landscape, Source4K, VisualProperties.Default, 1, 0.45,
            FollowSmoothness.Fast)!;
        Assert.Equal(1.5, curve.At(S(4.5)), 3);
        Assert.Equal(1.5, curve.At(S(5.5)), 3);
        Assert.InRange(curve.At(S(9)), 2.9, 3.1);
    }

    [Fact]
    public void The_automatic_share_zooms_in_by_at_most_twenty_percent_over_the_starting_frame()
    {
        var curve = Solve(Climb(Away), Landscape, Source4K, share: null);
        Assert.True(curve.IsAuto);
        Assert.All(curve.Scales, s => Assert.InRange(s, 1, FollowZoom.AutoMaxZoom + 1e-9));
        Assert.True(curve.Scales.Max() > 1.17);                 // the top of the wall reaches the 20 %
        // His small end (the 10th percentile: ≈ 21.5 % of the source) × 1.2.
        Assert.InRange(curve.Share, 0.24, 0.27);
    }

    [Fact]
    public void The_automatic_share_stays_within_the_source_reserve()
    {
        // Portrait from landscape 4K: one source pixel per canvas pixel at ≈ 3.56, the fill ≈ 3.16 → only ≈ 1.125× over the fill.
        var fill = CompositionMath.FillScale(Portrait, Source4K, VisualProperties.Default);
        var native = 3840.0 / 1080;
        var portrait = Solve(Climb(Away), Portrait, Source4K, share: null);
        Assert.All(portrait.Scales, s => Assert.InRange(s, fill, native + 1e-9));
        // 1080p into 1080p: no reserve at all — the automatic scale is the fill throughout (as before D080).
        var flat = Solve(Climb(Away), Landscape, Source1080, share: null);
        Assert.All(flat.Scales, s => Assert.Equal(1, s, 9));
    }

    [Fact]
    public void A_part_with_the_dynamic_scale_is_drawn_at_it_and_always_covers_the_canvas()
    {
        var frames = Enumerable.Range(0, 101).Select(i => new[] { Person(0.5, 0.7 - 0.004 * i, w: Away(i * 0.1) / 2, h: Away(i * 0.1)) }).ToArray();
        var clip = new VideoClip { MediaAssetId = Guid.NewGuid(), Duration = MediaTime.FromSeconds(10), SourceOut = MediaTime.FromSeconds(10) };
        var follow = new FollowSettings(ImmutableArray.Create(new FollowPart(DynamicScale: true, ScaleShare: 0.4)));
        var resolution = FollowResolution.For(clip, follow, Tracks(frames), Landscape, Source4K);
        Assert.Equal(FollowState.Following, resolution.State);
        Assert.Equal(0.4, resolution.Parts[0].Zoom!.Share);
        for (var t = 0.0; t < 10; t += 1 / 30.0)
        {
            var v = resolution.Framing!.At(S(t));
            Assert.True(CompositionMath.Layout(Landscape, Source4K, v).CoversCanvas, $"{t:F2} s");
        }
        Assert.InRange(resolution.Framing!.At(S(8)).Scale, 1.6, 1.8);
        Assert.True(resolution.Framing.HasPartScale);
    }

    [Fact]
    public void Without_the_dynamic_scale_a_part_is_framed_exactly_as_before()
    {
        var frames = Enumerable.Range(0, 101).Select(i => new[] { Person(0.5, 0.7 - 0.004 * i, w: Away(i * 0.1) / 2, h: Away(i * 0.1)) }).ToArray();
        var clip = new VideoClip { MediaAssetId = Guid.NewGuid(), Duration = MediaTime.FromSeconds(10), SourceOut = MediaTime.FromSeconds(10), Scale = 1.5 };
        var resolution = FollowResolution.For(clip, FollowSettings.Whole(), Tracks(frames), Landscape, Source4K);
        Assert.Null(resolution.Parts[0].Zoom);
        Assert.Equal(1.5, resolution.Framing!.At(S(8)).Scale);
        Assert.False(resolution.Framing.HasPartScale);
    }

    [Fact]
    public void Two_parts_keep_their_own_shares()
    {
        var frames = Enumerable.Range(0, 101).Select(i => new[] { Person(0.5, 0.5, w: 0.15, h: 0.3) }).ToArray();
        var clip = new VideoClip { MediaAssetId = Guid.NewGuid(), Duration = MediaTime.FromSeconds(10), SourceOut = MediaTime.FromSeconds(10) };
        var follow = new FollowSettings(ImmutableArray.Create(
            new FollowPart(null, S(5), DynamicScale: true, ScaleShare: 0.45),
            new FollowPart(S(5), null, DynamicScale: true, ScaleShare: 0.6)));
        var resolution = FollowResolution.For(clip, follow, Tracks(frames), Landscape, Source4K);
        Assert.Equal(1.5, resolution.Framing!.At(S(2)).Scale, 2);
        Assert.Equal(2.0, resolution.Framing.At(S(8)).Scale, 2);
    }

    [Theory]
    [InlineData(1.0, false)]     // «Вписать»
    [InlineData(1.5, true)]
    public void A_clip_scaled_by_hand_starts_Follow_without_the_dynamic_scale(double scale, bool hand)
    {
        var clip = new VideoClip { MediaAssetId = Guid.NewGuid(), Duration = MediaTime.FromSeconds(10), SourceOut = MediaTime.FromSeconds(10), Scale = scale };
        Assert.Equal(hand, FollowZoom.IsHandScaled(clip, Landscape, Source4K));
        var fill = CompositionMath.FillScale(Portrait, Source4K, VisualProperties.Default);
        clip.Scale = fill;
        Assert.False(FollowZoom.IsHandScaled(clip, Portrait, Source4K));
        // Keyframes that change the scale are a scale set by hand; keyframes of the position only are not.
        clip.Scale = 1;
        clip.Keyframes = new TransformKeyframes(ImmutableArray.Create(new TransformKeyframe(Guid.NewGuid(), S(0), 0, 0, 1), new TransformKeyframe(Guid.NewGuid(), S(5), 0, 0, 1.4)));
        Assert.True(FollowZoom.IsHandScaled(clip, Landscape, Source4K));
        clip.Keyframes = new TransformKeyframes(ImmutableArray.Create(new TransformKeyframe(Guid.NewGuid(), S(0), 0, 0, 1), new TransformKeyframe(Guid.NewGuid(), S(5), 200, 0, 1)));
        Assert.False(FollowZoom.IsHandScaled(clip, Landscape, Source4K));   // «Заполнить»
    }

    [Fact]
    public void A_part_with_place_keyframes_and_the_dynamic_scale_takes_the_curves_scale_and_the_keys_place()
    {
        var frames = Enumerable.Range(0, 101).Select(i => new[] { Person(0.5, 0.5, w: 0.15, h: 0.3) }).ToArray();
        var clip = new VideoClip { MediaAssetId = Guid.NewGuid(), Duration = MediaTime.FromSeconds(10), SourceOut = MediaTime.FromSeconds(10) };
        var keys = new TransformKeyframes(ImmutableArray.Create(new TransformKeyframe(Guid.NewGuid(), S(0), 100, 0, 3)));
        var follow = new FollowSettings(ImmutableArray.Create(new FollowPart(Keyframes: keys, DynamicScale: true, ScaleShare: 0.45)));
        var resolution = FollowResolution.For(clip, follow, Tracks(frames), Landscape, Source4K);
        var v = resolution.Framing!.At(S(5));
        Assert.Equal(1.5, v.Scale, 3);                                    // the curve's, not the keyframe's 3
        // The climber (x 0.5 of the source) stands 100 px right of the canvas centre, as the key puts him.
        var g = CompositionMath.Layout(Landscape, Source4K, v);
        Assert.Equal(960 + 100, g.Transform.Apply(new PointD(0.5 * Source4K.Width, 0.5 * Source4K.Height)).X, 0);
    }

    [Fact]
    public void A_share_out_of_its_limits_is_not_a_valid_part()
    {
        Assert.False(FollowSettings.AreValid(ImmutableArray.Create(new FollowPart(DynamicScale: true, ScaleShare: 1.5))));
        Assert.True(FollowSettings.AreValid(ImmutableArray.Create(new FollowPart(DynamicScale: true, ScaleShare: 0.4))));
    }
}
