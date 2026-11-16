using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.People;
using Xunit;
using static AiVideoEditor.Core.Tests.People.PeopleTestData;

namespace AiVideoEditor.Core.Tests.People;

/// <summary>D061 §3.5: the camera path — the person at the canvas centre, never a black border, a smooth path, losses
/// bridged without a jump, no room → no path, other rotations → no path.</summary>
public class FollowCameraTests
{
    private static readonly FrameSize Portrait = new(1080, 1920);
    private static readonly FrameSize Source = new(2160, 3840);

    private static FollowTrajectory Trajectory(params NormalizedPoint?[] anchors) => new(
        anchors.Select((a, i) => new FollowPoint(S(i * 0.1), a, a is { } p ? new NormalizedBox(p.X - 0.1, p.Y - 0.2, p.X + 0.1, p.Y + 0.2) : null)).ToImmutableArray(),
        ImmutableArray.Create(0));

    private static VisualProperties Zoomed(double scale) => VisualProperties.Default with { Scale = scale };

    [Fact]
    public void The_person_is_put_at_the_canvas_centre()
    {
        var anchors = Enumerable.Repeat<NormalizedPoint?>(new NormalizedPoint(0.4, 0.45), 30).ToArray();
        var (path, reason) = FollowCamera.Solve(Trajectory(anchors), Portrait, Source, Zoomed(2), FollowSmoothness.Normal);
        Assert.Equal(FollowUnavailable.None, reason);
        var (x, y) = path!.At(S(1.5));
        var g = CompositionMath.Layout(Portrait, Source, Zoomed(2) with { PositionX = x, PositionY = y });
        var anchor = g.Transform.Apply(new PointD(0.4 * Source.Width, 0.45 * Source.Height));
        Assert.Equal(540, anchor.X, 3);
        Assert.Equal(960, anchor.Y, 3);
    }

    [Fact]
    public void The_picture_always_covers_the_canvas_even_for_a_person_at_the_edge()
    {
        var anchors = Enumerable.Range(0, 60).Select(i => (NormalizedPoint?)new NormalizedPoint(i % 2 == 0 ? 0.0 : 1.0, i % 3 == 0 ? 0.0 : 1.0)).ToArray();
        var (path, _) = FollowCamera.Solve(Trajectory(anchors), Portrait, Source, Zoomed(1.5), FollowSmoothness.Fast);
        for (var i = 0; i < 60; i++)
        {
            var (x, y) = path!.At(S(i * 0.1));
            Assert.True(CompositionMath.Layout(Portrait, Source, Zoomed(1.5) with { PositionX = x, PositionY = y }).CoversCanvas, $"sample {i}");
        }
    }

    [Fact]
    public void Without_room_to_move_there_is_no_path()
    {
        var anchors = Enumerable.Repeat<NormalizedPoint?>(new NormalizedPoint(0.3, 0.3), 10).ToArray();
        var (path, reason) = FollowCamera.Solve(Trajectory(anchors), Portrait, Source, Zoomed(1), FollowSmoothness.Normal);
        Assert.Null(path);
        Assert.Equal(FollowUnavailable.NoRoom, reason);
    }

    [Fact]
    public void Only_the_axis_with_room_moves()
    {
        // A 16:9 source on a 9:16 canvas filled (Scale = Fill): room horizontally only.
        var source = new FrameSize(3840, 2160);
        var fill = CompositionMath.FillScale(Portrait, source, VisualProperties.Default);
        var anchors = Enumerable.Repeat<NormalizedPoint?>(new NormalizedPoint(0.3, 0.2), 10).ToArray();
        var (path, _) = FollowCamera.Solve(Trajectory(anchors), Portrait, source, Zoomed(fill), FollowSmoothness.Normal);
        Assert.True(path!.MovesX);
        Assert.False(path.MovesY);
        Assert.Equal(0, path.At(S(0.5)).Y);
        Assert.NotEqual(0, path.At(S(0.5)).X);
    }

    [Fact]
    public void An_odd_rotation_has_no_path()
    {
        var anchors = Enumerable.Repeat<NormalizedPoint?>(new NormalizedPoint(0.5, 0.5), 10).ToArray();
        var (_, reason) = FollowCamera.Solve(Trajectory(anchors), Portrait, Source, Zoomed(2) with { RotationDegrees = 10 }, FollowSmoothness.Normal);
        Assert.Equal(FollowUnavailable.Rotation, reason);
    }

    [Fact]
    public void A_quarter_turn_still_follows()
    {
        var anchors = Enumerable.Repeat<NormalizedPoint?>(new NormalizedPoint(0.4, 0.6), 10).ToArray();
        var (path, _) = FollowCamera.Solve(Trajectory(anchors), Portrait, Source, Zoomed(2) with { RotationDegrees = 90 }, FollowSmoothness.Normal);
        Assert.NotNull(path);
    }

    [Fact]
    public void A_short_miss_is_glided_through_and_a_long_loss_holds_then_eases()
    {
        var shortMiss = new NormalizedPoint?[] { new(0.2, 0.5), null, null, null, null, new(0.6, 0.5) };   // 0.5 s
        var filled = FollowCamera.Filled(Trajectory(shortMiss).Points);
        Assert.Equal(0.2 + 0.4 * 0.4, filled[2].X, 9);   // linear by time

        var longLoss = new List<NormalizedPoint?> { new NormalizedPoint(0.2, 0.5) };
        longLoss.AddRange(Enumerable.Repeat<NormalizedPoint?>(null, 49));                       // 5 s lost
        longLoss.Add(new NormalizedPoint(0.8, 0.5));
        var held = FollowCamera.Filled(Trajectory(longLoss.ToArray()).Points);
        Assert.Equal(0.2, held[30].X, 9);                 // holds the last seen point …
        Assert.InRange(held[45].X, 0.2, 0.8);              // … and eases to the reappearance during the last second
        Assert.True(held[45].X > 0.2 && held[48].X > held[45].X);
        for (var i = 1; i < held.Length; i++) Assert.True(Math.Abs(held[i].X - held[i - 1].X) < 0.1, $"jump at {i}");
    }

    [Fact]
    public void Before_the_first_and_after_the_last_sighting_the_camera_holds()
    {
        var anchors = new NormalizedPoint?[] { null, null, new(0.3, 0.5), new(0.35, 0.5), null, null };
        var filled = FollowCamera.Filled(Trajectory(anchors).Points);
        Assert.Equal(0.3, filled[0].X, 9);
        Assert.Equal(0.35, filled[5].X, 9);
    }

    [Fact]
    public void The_smoothing_removes_the_jitter_of_the_detections()
    {
        var random = new Random(7);
        var anchors = Enumerable.Range(0, 100).Select(_ => (NormalizedPoint?)new NormalizedPoint(0.5 + (random.NextDouble() - 0.5) * 0.04, 0.5)).ToArray();
        var (path, _) = FollowCamera.Solve(Trajectory(anchors), Portrait, Source, Zoomed(2), FollowSmoothness.Normal);
        var steps = Enumerable.Range(21, 58).Select(i => Math.Abs(path!.X[i] - path.X[i - 1])).ToList();
        var rawSteps = Enumerable.Range(21, 58).Select(i => Math.Abs(anchors[i]!.Value.X - anchors[i - 1]!.Value.X) * Source.Width * 2 * Portrait.Width / Source.Width).ToList();
        Assert.True(steps.Average() < rawSteps.Average() / 5, $"{steps.Average()} vs {rawSteps.Average()}");
    }

    [Fact]
    public void A_softer_setting_gives_a_calmer_camera()
    {
        var anchors = Enumerable.Range(0, 100).Select(i => (NormalizedPoint?)new NormalizedPoint(i % 20 < 10 ? 0.3 : 0.7, 0.5)).ToArray();
        double Roughness(FollowSmoothness s)
        {
            var (path, _) = FollowCamera.Solve(Trajectory(anchors), Portrait, Source, Zoomed(2), s);
            return Enumerable.Range(1, 99).Sum(i => Math.Abs(path!.X[i] - path.X[i - 1]));
        }
        Assert.True(Roughness(FollowSmoothness.Soft) < Roughness(FollowSmoothness.Normal));
        Assert.True(Roughness(FollowSmoothness.Normal) < Roughness(FollowSmoothness.Fast));
    }

    [Fact]
    public void A_followed_part_glides_in_and_out_of_the_clips_own_framing_without_a_jump()
    {
        // The person far right all the time: the camera sits at x = -100 inside the part; the clip's own position is 0.
        var times = Enumerable.Range(0, 101).Select(i => MediaTime.FromSeconds(i * 0.1)).ToImmutableArray();
        var path = new FollowPath(times, times.Select(_ => -100.0).ToImmutableArray(), times.Select(_ => 0.0).ToImmutableArray()) { MovesX = true };
        var limited = FollowCamera.Limit(path, MediaTime.FromSeconds(2), MediaTime.FromSeconds(6), MediaTime.Zero, MediaTime.FromSeconds(10), 0, 0);

        Assert.Equal(0, limited.At(MediaTime.FromSeconds(1)).X);                 // before the part: the clip's own framing
        Assert.Equal(0, limited.At(MediaTime.FromSeconds(2)).X);                 // the glide starts exactly at the part's start
        Assert.Equal(-100, limited.At(MediaTime.FromSeconds(4)).X, 6);          // inside: following
        Assert.Equal(0, limited.At(MediaTime.FromSeconds(6)).X);                 // back at the part's end
        Assert.Equal(0, limited.At(MediaTime.FromSeconds(8)).X);
        // No jump: neighbouring frames (1/30 s) differ by far less than the whole move.
        var previous = limited.At(MediaTime.Zero).X;
        for (var t = 0.0; t <= 10; t += 1 / 30.0)
        {
            var x = limited.At(MediaTime.FromSeconds(t)).X;
            Assert.True(Math.Abs(x - previous) < 15, $"jump at {t:F2} s");
            previous = x;
        }

        // The whole clip as the part: the path unchanged; the part from the clip's start: no glide in.
        Assert.Same(path, FollowCamera.Limit(path, MediaTime.Zero, MediaTime.FromSeconds(10), MediaTime.Zero, MediaTime.FromSeconds(10), 0, 0));
        var fromStart = FollowCamera.Limit(path, MediaTime.FromSeconds(1), MediaTime.FromSeconds(6), MediaTime.FromSeconds(1), MediaTime.FromSeconds(10), 0, 0);
        Assert.Equal(-100, fromStart.At(MediaTime.FromSeconds(1)).X, 6);
        // Before the clip's start (a Dissolve's frames past the cut) the open side keeps following — no jump at the cut.
        Assert.Equal(-100, fromStart.At(MediaTime.FromSeconds(0.5)).X, 6);
        Assert.Equal(0, fromStart.At(MediaTime.FromSeconds(7)).X);
        var toEnd = FollowCamera.Limit(path, MediaTime.FromSeconds(3), MediaTime.FromSeconds(8), MediaTime.Zero, MediaTime.FromSeconds(8), 0, 0);
        Assert.Equal(-100, toEnd.At(MediaTime.FromSeconds(8.5)).X, 6);     // past the clip's end
        Assert.Equal(0, toEnd.At(MediaTime.FromSeconds(2)).X);
    }

    [Fact]
    public void Between_two_touching_parts_the_camera_passes_from_one_person_to_the_other_without_returning()
    {
        var times = Enumerable.Range(0, 101).Select(i => MediaTime.FromSeconds(i * 0.1)).ToImmutableArray();
        FollowPath At(double x) => new(times, times.Select(_ => x).ToImmutableArray(), times.Select(_ => 0.0).ToImmutableArray()) { MovesX = true };
        var combined = FollowCamera.Combine(new[] { (At(-100), MediaTime.FromSeconds(2), MediaTime.FromSeconds(5)), (At(-300), MediaTime.FromSeconds(5), MediaTime.FromSeconds(8)) },
            MediaTime.Zero, MediaTime.FromSeconds(10), 0, 0);
        Assert.Equal(0, combined.At(MediaTime.FromSeconds(1)).X);
        Assert.Equal(-100, combined.At(MediaTime.FromSeconds(4)).X, 6);
        Assert.Equal(-200, combined.At(MediaTime.FromSeconds(5)).X, 6);     // half way at the boundary — never back to 0
        Assert.Equal(-300, combined.At(MediaTime.FromSeconds(6.5)).X, 6);
        Assert.Equal(0, combined.At(MediaTime.FromSeconds(9)).X);
        var previous = combined.At(MediaTime.Zero).X;
        for (var t = 0.0; t <= 10; t += 1 / 30.0)
        {
            var x = combined.At(MediaTime.FromSeconds(t)).X;
            Assert.True(x <= 1e-9, $"back to the clip's framing at {t:F2} s");
            Assert.True(Math.Abs(x - previous) < 25, $"jump at {t:F2} s");
            previous = x;
        }
    }

    [Fact]
    public void A_part_that_cannot_follow_is_reported_while_another_one_follows()
    {
        // One person in the first 5 s; two people as near in the last 5 s.
        var frames = Enumerable.Range(0, 101).Select(i => i < 50
            ? new[] { Person(0.3, 0.5, colour: 0) }
            : new[] { Person(0.3, 0.5, colour: 0), Person(0.7, 0.5, colour: 12) }).ToArray();
        var clip = new VideoClip { MediaAssetId = Guid.NewGuid(), Duration = MediaTime.FromSeconds(10), SourceOut = MediaTime.FromSeconds(10), Scale = 2 };
        var follow = new FollowSettings(ImmutableArray.Create(new FollowPart(MediaTime.FromSeconds(0.5), MediaTime.FromSeconds(4)),
            new FollowPart(MediaTime.FromSeconds(6), MediaTime.FromSeconds(9))));
        var resolution = FollowResolution.For(clip, follow, Tracks(frames), Portrait, Source);
        Assert.Equal(FollowState.Following, resolution.State);
        Assert.Equal(FollowState.NeedsTarget, resolution.Problem!.State);
        Assert.Equal(MediaTime.FromSeconds(6), resolution.Problem.From);
    }

    [Fact]
    public void The_masks_and_tracks_reach_past_the_cut_as_far_as_a_transition_shows_the_clip()
    {
        var clip = new VideoClip { MediaAssetId = Guid.NewGuid(), Duration = MediaTime.FromSeconds(10), SourceIn = MediaTime.FromSeconds(5), SourceOut = MediaTime.FromSeconds(15),
            Follow = FollowSettings.Whole() };
        var dissolve = new Transition { TransitionTypeId = "dissolve", Duration = MediaTime.FromSeconds(3), RightClipId = clip.Id };
        var (before, after) = PeopleAnalysisNeeds.PastTheCut(clip, new[] { dissolve });
        Assert.Equal(MediaTime.FromSeconds(1.5), before);
        Assert.Equal(MediaTime.Zero, after);
        var span = PeopleAnalysisNeeds.FollowSpans(clip, before, after).Single();
        Assert.Equal(MediaTime.FromSeconds(2.5), span.From);                // 5 − 1 (margin) − 1.5 (the dissolve's half)
        Assert.Equal(MediaTime.FromSeconds(16), span.To);
    }

    [Fact]
    public void A_part_shorter_than_two_glides_eases_over_half_of_it()
    {
        var times = Enumerable.Range(0, 101).Select(i => MediaTime.FromSeconds(i * 0.1)).ToImmutableArray();
        var path = new FollowPath(times, times.Select(_ => -100.0).ToImmutableArray(), times.Select(_ => 0.0).ToImmutableArray()) { MovesX = true };
        var limited = FollowCamera.Limit(path, MediaTime.FromSeconds(4), MediaTime.FromSeconds(5), MediaTime.Zero, MediaTime.FromSeconds(10), 0, 0);
        Assert.Equal(-100, limited.At(MediaTime.FromSeconds(4.5)).X, 6);   // the top reached in the middle
        Assert.Equal(0, limited.At(MediaTime.FromSeconds(4)).X);
        Assert.Equal(0, limited.At(MediaTime.FromSeconds(5)).X);
    }

    [Fact]
    public void An_offset_places_the_person_off_the_centre_while_the_picture_still_covers_the_canvas()
    {
        var trajectory = Trajectory(new NormalizedPoint(0.5, 0.5), new NormalizedPoint(0.5, 0.5));
        var centred = FollowCamera.Solve(trajectory, Portrait, Source, Zoomed(1.5), FollowSmoothness.Normal).Path!;
        var right = FollowCamera.Solve(trajectory, Portrait, Source, Zoomed(1.5), FollowSmoothness.Normal, offsetX: 150, offsetY: -60).Path!;
        Assert.Equal(centred.X[0] + 150, right.X[0], 6);         // the person 150 px right of the centre
        Assert.Equal(centred.Y[0] - 60, right.Y[0], 6);
        var far = FollowCamera.Solve(trajectory, Portrait, Source, Zoomed(1.5), FollowSmoothness.Normal, offsetX: 5000).Path!;
        Assert.True(far.X[0] < centred.X[0] + 5000);             // clamped: no black border
    }

    [Fact]
    public void A_followed_part_maps_to_each_clip_that_shows_it()
    {
        var follow = FollowSettings.Of(new FollowPart(MediaTime.FromSeconds(3), MediaTime.FromSeconds(7)));
        Assert.Equal((MediaTime.FromSeconds(3), MediaTime.FromSeconds(7)), follow.Parts[0].RangeIn(MediaTime.Zero, MediaTime.FromSeconds(10)));
        Assert.Equal((MediaTime.FromSeconds(5), MediaTime.FromSeconds(7)), follow.Parts[0].RangeIn(MediaTime.FromSeconds(5), MediaTime.FromSeconds(10))); // split half
        Assert.Null(follow.Parts[0].RangeIn(MediaTime.FromSeconds(7), MediaTime.FromSeconds(10)));                                                        // none of it
        Assert.Equal((MediaTime.FromSeconds(2), MediaTime.FromSeconds(4)), FollowSettings.Whole().Parts[0].RangeIn(MediaTime.FromSeconds(2), MediaTime.FromSeconds(4)));
    }

    [Fact]
    public void The_path_interpolates_between_samples_and_holds_outside()
    {
        var path = new FollowPath(ImmutableArray.Create(S(1), S(2)), ImmutableArray.Create(0.0, 10.0), ImmutableArray.Create(5.0, 5.0));
        Assert.Equal((0, 5), path.At(S(0)));
        Assert.Equal((5, 5), path.At(S(1.5)));
        Assert.Equal((10, 5), path.At(S(3)));
    }
}
