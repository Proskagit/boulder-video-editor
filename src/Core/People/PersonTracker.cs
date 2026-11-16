using System.Collections.Immutable;
using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.People;

/// <summary>
/// Links the people of consecutive analysed frames into tracks (D061 §3.3) — a person keeps one id while it is seen. A
/// ByteTrack-like two-stage association, deterministic and greedy (a frame of a climbing video holds a handful of people):
/// <list type="number">
/// <item>every live track predicts its box (constant velocity of the centre, damped) at the frame's time;</item>
/// <item>confident detections (≥ <see cref="HighScore"/>) are matched to the predictions by overlap, centre distance and
/// appearance; a pair is allowed only when the boxes overlap or the centres are close relative to the person's size —
/// never across the frame;</item>
/// <item>weak detections (≥ <see cref="LowScore"/>) may only continue a track left unmatched (a partly hidden person), never
/// start one;</item>
/// <item>an unmatched confident detection starts a new track; a track unseen for longer than <see cref="MaxGap"/> ends.</item>
/// </list>
/// A track never jumps to another person within a frame — the matching is one-to-one — and a track that ended is never
/// revived here: bridging a longer loss is the job of <see cref="FollowTarget"/>, which decides with the appearance and
/// refuses when it is not sure.
/// </summary>
public sealed class PersonTracker
{
    public const float HighScore = 0.5f;
    public const float LowScore = 0.25f;
    public static readonly MediaTime MaxGap = MediaTime.FromSeconds(1.0);

    private sealed class Live
    {
        public int Id;
        public NormalizedBox Box;
        public double Vx, Vy;            // centre velocity, frame units per second
        public MediaTime LastSeen;
        public ImmutableArray<float> Appearance;
    }

    private readonly List<Live> _live = new();
    private int _nextId;

    /// <summary>Assigns track ids to the people of a frame (in time order); returns the id per detection (−1: a weak
    /// detection that continues no track).</summary>
    public ImmutableArray<int> Add(MediaTime time, IReadOnlyList<PersonDetection> people)
    {
        _live.RemoveAll(t => time - t.LastSeen > MaxGap);
        var ids = Enumerable.Repeat(-1, people.Count).ToArray();
        var free = new HashSet<Live>(_live);

        Match(time, people, ids, free, d => d.Score >= HighScore);
        Match(time, people, ids, free, d => d.Score >= LowScore && d.Score < HighScore);

        for (var i = 0; i < people.Count; i++)
        {
            if (ids[i] >= 0 || people[i].Score < HighScore) continue;
            var track = new Live { Id = _nextId++, Box = people[i].Box, LastSeen = time, Appearance = people[i].Appearance };
            _live.Add(track);
            ids[i] = track.Id;
        }
        return ImmutableArray.Create(ids);
    }

    private void Match(MediaTime time, IReadOnlyList<PersonDetection> people, int[] ids, HashSet<Live> free, Func<PersonDetection, bool> which)
    {
        var pairs = new List<(double Cost, Live Track, int Index)>();
        foreach (var track in free)
        {
            var predicted = Predict(track, time);
            for (var i = 0; i < people.Count; i++)
            {
                if (ids[i] >= 0 || !which(people[i])) continue;
                if (Cost(predicted, track, people[i]) is { } cost) pairs.Add((cost, track, i));
            }
        }
        foreach (var (_, track, index) in pairs.OrderBy(p => p.Cost).ThenBy(p => p.Track.Id).ThenBy(p => p.Index))
        {
            if (ids[index] >= 0 || !free.Contains(track)) continue;
            Update(track, people[index], time);
            ids[index] = track.Id;
            free.Remove(track);
        }
    }

    /// <summary>The cost of continuing <paramref name="track"/> with <paramref name="person"/>, or null when it is not
    /// plausible.</summary>
    internal static double? Cost(NormalizedBox predicted, ImmutableArray<float> trackAppearance, PersonDetection person)
    {
        var iou = predicted.IoU(person.Box);
        var size = Math.Max(0.05, Math.Max(predicted.Size, person.Box.Size));
        var (a, b) = (predicted.Center, person.Box.Center);
        var distance = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) / size;
        if (iou < 0.05 && distance > 0.6) return null;
        var similarity = PersonAppearance.Similarity(trackAppearance, person.Appearance);
        // An appearance that clearly does not match vetoes the match: a weak geometric one (two people side by side) below
        // 0.5, any one below 0.35 (somebody else in the climber's place — a passer-by in front of them, a person taking the
        // spot of one who left).
        if (!trackAppearance.IsDefaultOrEmpty && !person.Appearance.IsDefaultOrEmpty && (similarity < 0.35 || (similarity < 0.5 && iou < 0.5)))
            return null;
        return (1 - iou) + 0.5 * distance + 0.5 * (1 - similarity);
    }

    private static double? Cost(NormalizedBox predicted, Live track, PersonDetection person) => Cost(predicted, track.Appearance, person);

    private static NormalizedBox Predict(Live track, MediaTime time)
    {
        var dt = Math.Min((time - track.LastSeen).TotalSeconds, MaxGap.TotalSeconds);
        return track.Box.Shifted(track.Vx * dt * 0.7, track.Vy * dt * 0.7);
    }

    private static void Update(Live track, PersonDetection person, MediaTime time)
    {
        var dt = (time - track.LastSeen).TotalSeconds;
        if (dt > 0)
        {
            var (a, b) = (track.Box.Center, person.Box.Center);
            track.Vx = 0.5 * track.Vx + 0.5 * (b.X - a.X) / dt;
            track.Vy = 0.5 * track.Vy + 0.5 * (b.Y - a.Y) / dt;
        }
        track.Box = person.Box;
        track.LastSeen = time;
        // A cut-off person's histogram is less reliable: it changes the track's appearance more slowly.
        track.Appearance = PersonAppearance.Blend(track.Appearance, person.Appearance, person.Box.TouchesEdge() ? 0.05 : 0.2);
    }

    /// <summary>Removes duplicate boxes of one person (non-maximum suppression at <paramref name="iou"/>), the most confident
    /// first; the result keeps the detections' order of confidence.</summary>
    public static ImmutableArray<PersonDetection> Suppress(IEnumerable<PersonDetection> people, double iou = 0.7)
    {
        var kept = new List<PersonDetection>();
        foreach (var person in people.OrderByDescending(p => p.Score))
            if (kept.All(k => k.Box.IoU(person.Box) < iou)) kept.Add(person);
        return kept.ToImmutableArray();
    }
}
