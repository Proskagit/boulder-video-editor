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
    public void The_path_interpolates_between_samples_and_holds_outside()
    {
        var path = new FollowPath(ImmutableArray.Create(S(1), S(2)), ImmutableArray.Create(0.0, 10.0), ImmutableArray.Create(5.0, 5.0));
        Assert.Equal((0, 5), path.At(S(0)));
        Assert.Equal((5, 5), path.At(S(1.5)));
        Assert.Equal((10, 5), path.At(S(3)));
    }
}
