using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Timeline.Commands;

namespace AiVideoEditor.Timeline;

/// <summary>
/// A proposed timeline change, described without touching the model: frame-rate
/// change, clip updates (move/trim/re-grid), inserts and removals. The service
/// validates the resulting "effective" layout via <see cref="EffectiveClips"/> and
/// only then turns the plan into commands — so a rejected edit has no side effects.
/// </summary>
public sealed class EditPlan
{
    private readonly Sequence _sequence;
    private readonly ProjectSettings _settings;
    private readonly Dictionary<Clip, Track> _currentTrack = new();
    private readonly Dictionary<Clip, (Track To, ClipState After)> _updates = new();
    private readonly List<(Track Track, Clip Clip)> _inserts = new();
    private readonly Dictionary<Clip, Track> _removes = new();

    public EditPlan(Sequence sequence, ProjectSettings settings)
    {
        _sequence = sequence;
        _settings = settings;
        foreach (var track in AllTracks)
            foreach (var clip in track.Clips)
                _currentTrack[clip] = track;
    }

    public FrameRate? NewFrameRate { get; private set; }
    public bool NewFrameRateLocked { get; private set; }

    /// <summary>The frame grid the planned result lives on.</summary>
    public FrameRate Rate => NewFrameRate ?? _settings.FrameRate;

    public IEnumerable<Track> AllTracks => _sequence.VideoTracks.Concat(_sequence.AudioTracks);

    public bool IsEmpty => NewFrameRate is null && _updates.Count == 0 && _inserts.Count == 0 && _removes.Count == 0;

    public Track TrackOf(Clip clip) => _currentTrack[clip];

    public void SetFrameRate(FrameRate rate, bool locked)
    {
        NewFrameRate = rate;
        NewFrameRateLocked = locked;
    }

    public void Update(Clip clip, Track toTrack, ClipState after) => _updates[clip] = (toTrack, after);

    public void Insert(Track track, Clip clip) => _inserts.Add((track, clip));

    public void Remove(Clip clip) => _removes[clip] = _currentTrack[clip];

    /// <summary>Tracks whose contents change (all tracks when the frame rate changes).</summary>
    public IEnumerable<Track> AffectedTracks
    {
        get
        {
            if (NewFrameRate is not null) return AllTracks;
            return _updates.Keys.Select(TrackOf)
                .Concat(_updates.Values.Select(u => u.To))
                .Concat(_inserts.Select(i => i.Track))
                .Distinct();
        }
    }

    /// <summary>What <paramref name="track"/> would contain after the plan is applied.</summary>
    public IEnumerable<(Clip Clip, ClipState State)> EffectiveClips(Track track)
    {
        foreach (var (clip, current) in _currentTrack)
        {
            if (_removes.ContainsKey(clip)) continue;

            if (_updates.TryGetValue(clip, out var update))
            {
                if (update.To == track) yield return (clip, update.After);
            }
            else if (current == track)
            {
                yield return (clip, ClipState.Capture(clip));
            }
        }

        foreach (var (insertTrack, clip) in _inserts)
            if (insertTrack == track)
                yield return (clip, ClipState.Capture(clip));
    }

    /// <summary>Builds the single undoable command that applies this plan.</summary>
    public IUndoableCommand BuildCommand(string description)
    {
        var commands = new List<IUndoableCommand>();

        if (NewFrameRate is { } rate)
            commands.Add(new SetFrameRateCommand(_settings, _settings.FrameRate, _settings.IsFrameRateLocked, rate, NewFrameRateLocked));

        foreach (var (clip, track) in _removes)
            commands.Add(new RemoveClipCommand(track, clip));

        var changes = _updates
            .Select(u => new ClipChange(u.Key, TrackOf(u.Key), ClipState.Capture(u.Key), u.Value.To, u.Value.After))
            .ToList();
        if (changes.Count > 0)
            commands.Add(new UpdateClipsCommand(description, changes));

        foreach (var (track, clip) in _inserts)
            commands.Add(new InsertClipCommand(track, clip, description));

        if (commands.Count == 0)
            throw new InvalidOperationException("Cannot build a command from an empty plan.");

        return commands.Count == 1 ? commands[0] : new CompositeCommand(description, commands);
    }
}
