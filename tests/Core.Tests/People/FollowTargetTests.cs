using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.People;
using Xunit;
using static AiVideoEditor.Core.Tests.People.PeopleTestData;

namespace AiVideoEditor.Core.Tests.People;

/// <summary>D061 §3.4: Follow follows the chosen person across losses — out of the frame, hidden, missed — and never switches
/// to another person silently: a coexisting track is somebody else, a different appearance is refused, two equally likely
/// candidates leave the person lost.</summary>
public class FollowTargetTests
{
    private static List<PersonDetection[]> Seen(int count, double cx, int colour, double cy = 0.5) =>
        Enumerable.Range(0, count).Select(_ => new[] { Person(cx, cy, colour: colour) }).ToList();

    private static List<PersonDetection[]> Nobody(int count) => Enumerable.Range(0, count).Select(_ => Array.Empty<PersonDetection>()).ToList();

    [Fact]
    public void The_picked_person_is_found_again_after_leaving_the_frame()
    {
        // Red climber 2 s, gone 3 s (out of the frame), back 2 s a little further right.
        var frames = Seen(20, 0.3, 0).Concat(Nobody(30)).Concat(Seen(20, 0.45, 0)).ToArray();
        var tracks = Tracks(frames);
        Assert.Equal(2, tracks.Tracks.Length);
        var trajectory = FollowTarget.Resolve(tracks, S(0.5), new NormalizedPoint(0.3, 0.5))!;
        Assert.Equal(2, trajectory.TrackIds.Length);
        Assert.True(trajectory.IsSeenAt(S(6)));
        Assert.False(trajectory.IsSeenAt(S(3.5)));
        var gap = Assert.Single(trajectory.Gaps(S(0), S(7), S(0.5)));
        Assert.Equal(S(2.0), gap.Start);
    }

    [Fact]
    public void Another_person_who_appears_while_the_climber_is_lost_is_not_followed()
    {
        // Red climber, gone; a blue person appears where the climber was; the red climber returns later.
        var frames = Seen(20, 0.3, 0).Concat(Nobody(5)).Concat(Seen(20, 0.32, 12)).Concat(Nobody(5)).Concat(Seen(20, 0.6, 0)).ToArray();
        var tracks = Tracks(frames);
        var trajectory = FollowTarget.Resolve(tracks, S(0.5), new NormalizedPoint(0.3, 0.5))!;
        Assert.False(trajectory.IsSeenAt(S(3)));   // the blue one is not the climber
        Assert.True(trajectory.IsSeenAt(S(6)));    // the red one is
        var blue = IdAt(tracks, 30, 0.32);
        Assert.DoesNotContain(blue, trajectory.TrackIds);
    }

    [Fact]
    public void A_person_seen_together_with_the_climber_is_never_the_climber()
    {
        // The climber and a spectator in the same colour are both seen; then only the spectator stays.
        var frames = Enumerable.Range(0, 20).Select(_ => new[] { Person(0.3, 0.5, colour: 0), Person(0.75, 0.5, colour: 0) })
            .Concat(Enumerable.Range(0, 20).Select(_ => new[] { Person(0.75, 0.5, colour: 0) })).ToArray();
        var tracks = Tracks(frames);
        var trajectory = FollowTarget.Resolve(tracks, S(0.5), new NormalizedPoint(0.3, 0.5))!;
        Assert.Single(trajectory.TrackIds);
        Assert.False(trajectory.IsSeenAt(S(3)));
    }

    [Fact]
    public void Two_equally_likely_candidates_leave_the_climber_lost()
    {
        // After the loss two red people appear together at the same distance: no guess.
        var frames = Seen(20, 0.5, 0).Concat(Nobody(10))
            .Concat(Enumerable.Range(0, 20).Select(_ => new[] { Person(0.35, 0.5, colour: 0), Person(0.65, 0.5, colour: 0) })).ToArray();
        var tracks = Tracks(frames);
        var trajectory = FollowTarget.Resolve(tracks, S(0.5), new NormalizedPoint(0.5, 0.5))!;
        Assert.Single(trajectory.TrackIds);
        Assert.False(trajectory.IsSeenAt(S(4)));
    }

    [Fact]
    public void The_climber_is_extended_backwards_before_the_picked_frame()
    {
        var frames = Seen(20, 0.4, 0).Concat(Nobody(15)).Concat(Seen(20, 0.42, 0)).ToArray();
        var tracks = Tracks(frames);
        var trajectory = FollowTarget.Resolve(tracks, S(4.5), new NormalizedPoint(0.42, 0.5))!;
        Assert.True(trajectory.IsSeenAt(S(0.5)));
    }

    [Fact]
    public void A_pick_on_nobody_resolves_to_nothing()
    {
        var tracks = Tracks(Seen(10, 0.3, 0).ToArray());
        Assert.Null(FollowTarget.Resolve(tracks, S(0.5), new NormalizedPoint(0.9, 0.1)));
    }

    [Fact]
    public void The_only_person_is_followed_without_a_pick_but_several_need_one()
    {
        var one = Tracks(Seen(30, 0.3, 0).ToArray());
        Assert.Equal(one.Tracks[0].Id, FollowTarget.AutoTrack(one, S(0), S(3)));

        var two = Tracks(Enumerable.Range(0, 30).Select(_ => new[] { Person(0.3, 0.5, colour: 0), Person(0.7, 0.5, colour: 12) }).ToArray());
        Assert.Null(FollowTarget.AutoTrack(two, S(0), S(3)));
    }

    [Fact]
    public void Small_spectators_far_behind_do_not_prevent_the_automatic_choice()
    {
        var tracks = Tracks(Enumerable.Range(0, 40).Select(_ => new[]
        {
            Person(0.4, 0.5, h: 0.5, colour: 0), Person(0.85, 0.6, w: 0.05, h: 0.1, colour: 12)
        }).ToArray());
        Assert.Equal(IdAt(tracks, 0, 0.4), FollowTarget.AutoTrack(tracks, S(0), S(4)));
    }

    [Fact]
    public void A_short_passer_by_does_not_prevent_the_automatic_choice()
    {
        var frames = Enumerable.Range(0, 40).Select(i => i is >= 10 and < 14
            ? new[] { Person(0.3, 0.5, colour: 0), Person(0.8, 0.5, colour: 12) }
            : new[] { Person(0.3, 0.5, colour: 0) }).ToArray();
        var tracks = Tracks(frames);
        Assert.Equal(IdAt(tracks, 0, 0.3), FollowTarget.AutoTrack(tracks, S(0), S(4)));
    }
}
