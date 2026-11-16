using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.People;

namespace AiVideoEditor.Core.Tests.People;

/// <summary>Synthetic people for the D061 tests: deterministic boxes, appearances by colour bin, tracks built by the
/// product's own tracker.</summary>
internal static class PeopleTestData
{
    public static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    /// <summary>An appearance concentrated in one colour bin (a red / blue / white shirt …).</summary>
    public static ImmutableArray<float> Colour(int bin)
    {
        var h = new float[PersonAppearance.Length];
        h[bin] = 0.8f;
        h[(bin + 1) % h.Length] = 0.2f;
        return ImmutableArray.Create(h);
    }

    public static PersonDetection Person(double cx, double cy, double w = 0.2, double h = 0.4, float score = 0.9f, int colour = 0,
        NormalizedPoint? torso = null) =>
        new(new NormalizedBox(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), score, torso, Colour(colour));

    /// <summary>Tracks of the frames <paramref name="frames"/> (one per 0.1 s from 0), linked by <see cref="PersonTracker"/>.</summary>
    public static PersonTracks Tracks(params PersonDetection[][] frames)
    {
        var tracker = new PersonTracker();
        var samples = ImmutableArray.CreateBuilder<PersonSample>();
        for (var i = 0; i < frames.Length; i++)
        {
            var time = S(i * 0.1);
            var people = PersonTracker.Suppress(frames[i]);
            samples.Add(new PersonSample(time, people, tracker.Add(time, people)));
        }
        return PersonTracks.Of(S(0.1), samples.ToImmutable(), "test");
    }

    /// <summary>The id of the track whose detection is nearest to <paramref name="cx"/> at frame <paramref name="frame"/>.</summary>
    public static int IdAt(PersonTracks tracks, int frame, double cx)
    {
        var sample = tracks.Samples[frame];
        var best = Enumerable.Range(0, sample.People.Length).MinBy(i => Math.Abs(sample.People[i].Box.Center.X - cx));
        return sample.TrackIdOf(best);
    }
}
