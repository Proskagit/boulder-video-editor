using AiVideoEditor.Core.People;
using Xunit;
using static AiVideoEditor.Core.Tests.People.PeopleTestData;

namespace AiVideoEditor.Core.Tests.People;

/// <summary>D061 §3.3: people keep their identity from frame to frame; a track never jumps to another person; weak
/// detections continue a track but never start one; a long loss ends it; duplicates are suppressed.</summary>
public class PersonTrackerTests
{
    [Fact]
    public void A_person_keeps_one_id_while_moving()
    {
        var frames = Enumerable.Range(0, 30).Select(i => new[] { Person(0.2 + i * 0.01, 0.5) }).ToArray();
        var tracks = Tracks(frames);
        Assert.Single(tracks.Tracks);
        Assert.All(tracks.Samples, s => Assert.Equal(tracks.Tracks[0].Id, s.TrackIds[0]));
    }

    [Fact]
    public void Two_people_side_by_side_keep_their_ids_when_they_pass_each_other()
    {
        // A red climber moves right, a blue one left; they cross in the middle.
        var frames = Enumerable.Range(0, 41).Select(i => new[]
        {
            Person(0.2 + i * 0.015, 0.5, colour: 0),
            Person(0.8 - i * 0.015, 0.55, colour: 12)
        }).ToArray();
        var tracks = Tracks(frames);
        var red = IdAt(tracks, 0, 0.2);
        var blue = IdAt(tracks, 0, 0.8);
        Assert.NotEqual(red, blue);
        // At the end the red one is on the right, the blue one on the left — and they kept their ids.
        Assert.Equal(red, IdAt(tracks, 40, 0.8));
        Assert.Equal(blue, IdAt(tracks, 40, 0.2));
    }

    [Fact]
    public void A_weak_detection_continues_a_track_but_never_starts_one()
    {
        var frames = new[]
        {
            new[] { Person(0.5, 0.5) },
            new[] { Person(0.51, 0.5, score: 0.3f), Person(0.1, 0.5, score: 0.3f) }
        };
        var tracks = Tracks(frames);
        Assert.Equal(tracks.Samples[0].TrackIds[0], tracks.Samples[1].TrackIdOf(tracks.Samples[1].People.ToList().FindIndex(p => p.Box.Center.X > 0.4)));
        Assert.Contains(-1, tracks.Samples[1].TrackIds);   // the weak one far away belongs to nobody
    }

    [Fact]
    public void A_loss_longer_than_the_gap_ends_the_track()
    {
        var frames = new List<PersonDetection[]>();
        for (var i = 0; i < 5; i++) frames.Add(new[] { Person(0.5, 0.5) });
        for (var i = 0; i < 15; i++) frames.Add(Array.Empty<PersonDetection>());   // 1.5 s away
        for (var i = 0; i < 5; i++) frames.Add(new[] { Person(0.5, 0.5) });
        var tracks = Tracks(frames.ToArray());
        Assert.Equal(2, tracks.Tracks.Length);
    }

    [Fact]
    public void A_short_miss_is_bridged()
    {
        var frames = new List<PersonDetection[]>();
        for (var i = 0; i < 5; i++) frames.Add(new[] { Person(0.5, 0.5) });
        for (var i = 0; i < 5; i++) frames.Add(Array.Empty<PersonDetection>());   // 0.5 s
        for (var i = 0; i < 5; i++) frames.Add(new[] { Person(0.52, 0.5) });
        Assert.Single(Tracks(frames.ToArray()).Tracks);
    }

    [Fact]
    public void A_person_never_jumps_across_the_frame()
    {
        var frames = new[] { new[] { Person(0.15, 0.5) }, new[] { Person(0.85, 0.5) } };
        Assert.Equal(2, Tracks(frames).Tracks.Length);
    }

    [Fact]
    public void Duplicate_boxes_of_one_person_are_suppressed()
    {
        var people = PersonTracker.Suppress(new[] { Person(0.5, 0.5, score: 0.9f), Person(0.505, 0.5, score: 0.7f), Person(0.2, 0.5, score: 0.6f) });
        Assert.Equal(2, people.Length);
        Assert.Equal(0.9f, people[0].Score);
    }

    [Fact]
    public void A_cut_off_person_at_the_frame_edge_is_still_tracked()
    {
        // The climber leaves through the left edge: the box shrinks against the edge.
        var frames = Enumerable.Range(0, 10).Select(i =>
        {
            var w = Math.Max(0.04, 0.2 - i * 0.02);
            return new[] { Person(w / 2, 0.5, w: w) };
        }).ToArray();
        Assert.Single(Tracks(frames).Tracks);
    }
}
