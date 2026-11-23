using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.People;
using Xunit;

namespace AiVideoEditor.Core.Tests.People;

/// <summary>D078 §3: the lift-off candidates and the falls of a climber's skeleton (synthetic climbs; the rules' accuracy
/// on real footage is the research's, `docs/ATTEMPT_EVENTS_RESEARCH.md`).</summary>
public class AttemptEventsTests
{
    private const double Dt = 0.1;

    /// <summary>A pose: hip, the two ankles (image y), the box from the head to the lower foot.</summary>
    private static PersonDetection Pose(double x, double hip, double leftAnkle, double rightAnkle)
    {
        var k = new float[PersonDetection.KeypointCount * 3];
        void Set(int index, double px, double py) { k[index * 3] = (float)px; k[index * 3 + 1] = (float)py; k[index * 3 + 2] = 0.9f; }
        Set(CocoKeypoint.LeftHip, x - 0.02, hip);
        Set(CocoKeypoint.RightHip, x + 0.02, hip);
        Set(CocoKeypoint.LeftAnkle, x - 0.03, leftAnkle);
        Set(CocoKeypoint.RightAnkle, x + 0.03, rightAnkle);
        Set(CocoKeypoint.LeftShoulder, x - 0.04, hip - 0.15);
        Set(CocoKeypoint.RightShoulder, x + 0.04, hip - 0.15);
        var top = hip - 0.3;
        return new PersonDetection(new NormalizedBox(x - 0.08, top, x + 0.08, Math.Max(leftAnkle, rightAnkle) + 0.02), 0.9f, null,
            ImmutableArray<float>.Empty) { Keypoints = ImmutableArray.Create(k) };
    }

    /// <summary>A climb: 6 s standing at the wall (floor 0.9), a foot put on a hold at 6.0 s, the lift-off at 7.0 s, 10 s up the
    /// wall (the hip from 0.6 to 0.25), a 1 s hang, the fall at 18.0 s (the hip drops 0.35 in 0.5 s) and the landing; 4 s on the
    /// floor after. Plus a person walking by at the floor level the whole time (not a climber).</summary>
    private static PersonTracks Climb(bool withWalker = true)
    {
        var tracker = new PersonTracker();
        var samples = new List<PersonSample>();
        for (var i = 0; i <= 220; i++)
        {
            var t = i * Dt;
            double hip, left, right;
            if (t < 6.0) (hip, left, right) = (0.6, 0.9, 0.9);
            else if (t < 7.0) (hip, left, right) = (0.6, 0.7, 0.9);                       // the left foot on a hold
            else if (t < 17.0)
            {
                var u = (t - 7.0) / 10.0;                                                  // climbing
                hip = 0.6 - 0.35 * u;
                left = 0.7 - 0.35 * u;
                right = 0.85 - 0.35 * u;
            }
            else if (t < 18.0) (hip, left, right) = (0.25, 0.35, 0.5);                    // hanging at the top
            else if (t < 18.5)
            {
                var u = (t - 18.0) / 0.5;                                                  // falling
                (hip, left, right) = (0.25 + 0.35 * u, 0.35 + 0.57 * u, 0.5 + 0.42 * u);
            }
            else (hip, left, right) = (0.62, 0.92, 0.92);                                 // landed
            var people = new List<PersonDetection> { Pose(0.4, hip, left, right) };
            if (withWalker) people.Add(Pose(0.75 + 0.002 * i % 0.1, 0.62, 0.93 - (i % 6 < 3 ? 0.03 : 0), 0.93));
            var arr = people.ToImmutableArray();
            samples.Add(new PersonSample(MediaTime.FromSeconds(t), arr, tracker.Add(MediaTime.FromSeconds(t), arr)));
        }
        return PersonTracks.Of(MediaTime.FromSeconds(Dt), samples.ToImmutableArray(), "test");
    }

    [Fact]
    public void A_climb_gives_its_lift_off_and_its_fall()
    {
        var events = AttemptEvents.Find(Climb(), MediaTime.Zero, MediaTime.FromSeconds(22));

        var lift = AttemptEventSet.Nearest(events.LiftOffs, MediaTime.FromSeconds(7.0), MediaTime.FromSeconds(3));
        Assert.NotNull(lift);
        Assert.InRange(lift!.Value.TotalSeconds, 6.4, 7.6);
        var fall = Assert.Single(events.Falls);
        Assert.InRange(fall.TotalSeconds, 17.6, 18.4);
    }

    [Fact]
    public void Nobody_climbing_gives_no_events()
    {
        var tracker = new PersonTracker();
        var samples = Enumerable.Range(0, 200).Select(i =>
        {
            var people = ImmutableArray.Create(Pose(0.3 + i * 0.002, 0.6, 0.9 - (i % 6 < 3 ? 0.03 : 0), 0.9));
            return new PersonSample(MediaTime.FromSeconds(i * Dt), people, tracker.Add(MediaTime.FromSeconds(i * Dt), people));
        }).ToImmutableArray();
        var events = AttemptEvents.Find(PersonTracks.Of(MediaTime.FromSeconds(Dt), samples, "test"), MediaTime.Zero, MediaTime.FromSeconds(20));
        Assert.Empty(events.Falls);
        Assert.Empty(events.LiftOffs);
    }

    [Fact]
    public void Only_the_window_is_read_and_a_short_window_gives_nothing()
    {
        Assert.Empty(AttemptEvents.Find(Climb(), MediaTime.FromSeconds(19), MediaTime.FromSeconds(21)).Falls);
        Assert.Same(AttemptEventSet.Empty, AttemptEvents.Find(Climb(), MediaTime.FromSeconds(5), MediaTime.FromSeconds(4)));
    }

    [Fact]
    public void A_pose_without_keypoints_gives_no_events()
    {
        var tracker = new PersonTracker();
        var samples = Enumerable.Range(0, 200).Select(i =>
        {
            var people = ImmutableArray.Create(new PersonDetection(new NormalizedBox(0.3, 0.6 - i * 0.002, 0.4, 0.95 - i * 0.002), 0.9f, null, ImmutableArray<float>.Empty));
            return new PersonSample(MediaTime.FromSeconds(i * Dt), people, tracker.Add(MediaTime.FromSeconds(i * Dt), people));
        }).ToImmutableArray();
        Assert.Empty(AttemptEvents.Find(PersonTracks.Of(MediaTime.FromSeconds(Dt), samples, "test"), MediaTime.Zero, MediaTime.FromSeconds(20)).Falls);
    }

    [Fact]
    public void The_nearest_event_is_taken_within_the_radius_only()
    {
        var events = ImmutableArray.Create(MediaTime.FromSeconds(10), MediaTime.FromSeconds(14));
        Assert.Equal(MediaTime.FromSeconds(14), AttemptEventSet.Nearest(events, MediaTime.FromSeconds(12.5), MediaTime.FromSeconds(3)));
        Assert.Equal(MediaTime.FromSeconds(10), AttemptEventSet.Nearest(events, MediaTime.FromSeconds(12), MediaTime.FromSeconds(3)));   // a tie: the earlier
        Assert.Null(AttemptEventSet.Nearest(events, MediaTime.FromSeconds(20), MediaTime.FromSeconds(3)));
    }
}
