using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.People;

/// <summary>One track of <see cref="PersonTracks"/>: a person seen continuously (gaps up to <see cref="PersonTracker.MaxGap"/>)
/// from <see cref="Start"/> to <see cref="End"/> on <see cref="Count"/> analysed frames.</summary>
public sealed record PersonTrackSpan(int Id, MediaTime Start, MediaTime End, int Count)
{
    public MediaTime Duration => End - Start;
}

/// <summary>
/// The people of a video (D061 §2): the result of the detection pass over the whole file — every analysed frame
/// (<see cref="Interval"/> apart, source time from the file's start) with its people and their tracks. Derived from the
/// video alone, never part of the project: kept in the application's cache by the file's content, the model and
/// <see cref="AnalysisVersion"/>. Immutable, read from any thread.
/// </summary>
public sealed record PersonTracks(MediaTime Interval, ImmutableArray<PersonSample> Samples)
{
    /// <summary>Changes whenever the detection, the pose, the tracking or the stored data change their meaning: a cached
    /// analysis of another version is never used.</summary>
    public const int AnalysisVersion = 2;   // 2: the weak-detection overlap rule (PersonTracker.WeakMinIoU)

    /// <summary>The model set the analysis was made with (<c>PeopleModel.Id</c>).</summary>
    public string ModelId { get; init; } = "";

    /// <summary>The tracks, by id, with their extent.</summary>
    public ImmutableArray<PersonTrackSpan> Tracks { get; init; } = ImmutableArray<PersonTrackSpan>.Empty;

    /// <summary>The source time the analysis covers (the last analysed frame).</summary>
    public MediaTime End => Samples.IsDefaultOrEmpty ? MediaTime.Zero : Samples[^1].Time;

    /// <summary>The same samples with the track extents computed from them.</summary>
    public static PersonTracks Of(MediaTime interval, ImmutableArray<PersonSample> samples, string modelId)
    {
        var spans = new Dictionary<int, (MediaTime Start, MediaTime End, int Count)>();
        foreach (var sample in samples)
        {
            for (var i = 0; i < sample.People.Length; i++)
            {
                var id = sample.TrackIdOf(i);
                if (id < 0) continue;
                spans[id] = spans.TryGetValue(id, out var s) ? (s.Start, sample.Time, s.Count + 1) : (sample.Time, sample.Time, 1);
            }
        }
        return new PersonTracks(interval, samples)
        {
            ModelId = modelId,
            Tracks = spans.OrderBy(p => p.Key).Select(p => new PersonTrackSpan(p.Key, p.Value.Start, p.Value.End, p.Value.Count)).ToImmutableArray()
        };
    }

    /// <summary>The index of the sample nearest to <paramref name="time"/>; −1 without samples.</summary>
    public int NearestIndex(MediaTime time)
    {
        if (Samples.IsDefaultOrEmpty) return -1;
        int lo = 0, hi = Samples.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Samples[mid].Time < time) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && (time - Samples[lo - 1].Time).Ticks <= (Samples[lo].Time - time).Ticks) lo--;
        return lo;
    }

    /// <summary>The sample nearest to <paramref name="time"/> when it is within one interval of it (the people "on this
    /// frame"); null otherwise.</summary>
    public PersonSample? SampleAt(MediaTime time)
    {
        var index = NearestIndex(time);
        if (index < 0) return null;
        var sample = Samples[index];
        return Math.Abs((sample.Time - time).Ticks) <= Interval.Ticks ? sample : null;
    }

    /// <summary>The track of the person at <paramref name="point"/> on the frame nearest to <paramref name="time"/> — the
    /// box containing it (the smallest, when boxes overlap), or else the nearest box within half its size. Null: nobody
    /// there.</summary>
    public int? TrackAt(MediaTime time, NormalizedPoint point)
    {
        if (SampleAt(time) is not { } sample) return null;
        int? best = null;
        var bestScore = double.MaxValue;
        for (var i = 0; i < sample.People.Length; i++)
        {
            var id = sample.TrackIdOf(i);
            if (id < 0) continue;
            var box = sample.People[i].Box;
            var c = box.Center;
            var distance = Math.Sqrt((c.X - point.X) * (c.X - point.X) + (c.Y - point.Y) * (c.Y - point.Y));
            var score = box.Contains(point) ? box.Area : distance <= box.Size / 2 ? 10 + distance : double.MaxValue;
            if (score < bestScore) (bestScore, best) = (score, id);
        }
        return best;
    }

    /// <summary>The tracks that last at least <paramref name="minimum"/> and overlap <c>[from, to]</c>.</summary>
    public IEnumerable<PersonTrackSpan> TracksIn(MediaTime from, MediaTime to, MediaTime minimum) =>
        Tracks.Where(t => t.End >= from && t.Start <= to && t.Duration >= minimum);
}
