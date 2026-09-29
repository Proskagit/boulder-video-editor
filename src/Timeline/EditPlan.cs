using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Timeline.Commands;

namespace AiVideoEditor.Timeline;

/// <summary>
/// A proposed timeline change, described without touching the model: frame-rate
/// change, clip updates (move/trim/re-grid), inserts and removals, and transition changes (D025). The service
/// validates the resulting "effective" layout via <see cref="EffectiveClips"/> / <see cref="EffectiveTransitions"/>
/// and only then turns the plan into commands — so a rejected edit has no side effects.
/// </summary>
public sealed class EditPlan
{
    private readonly Sequence _sequence;
    private readonly ProjectSettings _settings;
    private readonly Dictionary<Clip, Track> _currentTrack = new();
    private readonly Dictionary<Clip, (Track To, ClipState After)> _updates = new();
    private readonly List<(Track Track, Clip Clip)> _inserts = new();
    private readonly Dictionary<Clip, Track> _removes = new();
    private readonly List<(Clip Clip, ClipPropertyValues After)> _properties = new();
    private readonly Dictionary<Transition, Track> _transitionTrack = new();
    private readonly Dictionary<Transition, TransitionState> _transitionUpdates = new();
    private readonly List<(Track Track, Transition Transition)> _transitionAdds = new();
    private readonly HashSet<Transition> _transitionRemoves = new();

    public EditPlan(Sequence sequence, ProjectSettings settings)
    {
        _sequence = sequence;
        _settings = settings;
        foreach (var track in AllTracks)
        {
            foreach (var clip in track.Clips)
                _currentTrack[clip] = track;
            foreach (var transition in track.Transitions)
                _transitionTrack[transition] = track;
        }
    }

    public FrameRate? NewFrameRate { get; private set; }
    public bool NewFrameRateLocked { get; private set; }

    /// <summary>The frame grid the planned result lives on.</summary>
    public FrameRate Rate => NewFrameRate ?? _settings.FrameRate;

    public IEnumerable<Track> AllTracks => _sequence.VideoTracks.Concat(_sequence.AudioTracks);

    public bool IsEmpty => NewFrameRate is null && _updates.Count == 0 && _inserts.Count == 0 && _removes.Count == 0 && _properties.Count == 0
        && _transitionUpdates.Count == 0 && _transitionAdds.Count == 0 && _transitionRemoves.Count == 0;

    /// <summary>Dissolves <see cref="ReconcileTransitions"/> removed because their cut no longer exists.</summary>
    public int AutoRemovedTransitions { get; private set; }

    /// <summary>True when the plan changes a transition.</summary>
    public bool ChangesTransitions => _transitionUpdates.Count > 0 || _transitionAdds.Count > 0 || _transitionRemoves.Count > 0;

    public Track TrackOf(Clip clip) => _currentTrack[clip];

    public void SetFrameRate(FrameRate rate, bool locked)
    {
        NewFrameRate = rate;
        NewFrameRateLocked = locked;
    }

    public void Update(Clip clip, Track toTrack, ClipState after) => _updates[clip] = (toTrack, after);

    public void Insert(Track track, Clip clip) => _inserts.Add((track, clip));

    public void Remove(Clip clip) => _removes[clip] = _currentTrack[clip];

    /// <summary>Sets non-timing properties of an existing clip as part of the edit (e.g. the fade a split takes off
    /// the left part, D025); only the groups given in <paramref name="after"/> change. Timing validation ignores them.</summary>
    public void SetProperties(Clip clip, ClipPropertyValues after) => _properties.Add((clip, after));

    // --- Transitions (D025) ------------------------------------------------------------------------------------------

    public void AddTransition(Track track, Transition transition) => _transitionAdds.Add((track, transition));

    public void RemoveTransition(Transition transition) => _transitionRemoves.Add(transition);

    public void UpdateTransition(Transition transition, TransitionState after) => _transitionUpdates[transition] = after;

    /// <summary>The transition as the plan would leave it (track, anchor, length).</summary>
    public TransitionState StateOf(Transition transition) =>
        _transitionUpdates.TryGetValue(transition, out var after) ? after
        : _transitionTrack.TryGetValue(transition, out var track) ? TransitionState.Capture(track, transition)
        : TransitionState.Capture(_transitionAdds.First(a => a.Transition == transition).Track, transition);

    /// <summary>Every transition the plan would leave, with its state.</summary>
    public IEnumerable<(Transition Transition, TransitionState State)> AllEffectiveTransitions() =>
        _transitionTrack.Keys.Concat(_transitionAdds.Select(a => a.Transition))
            .Where(t => !_transitionRemoves.Contains(t))
            .Select(t => (t, StateOf(t)));

    /// <summary>The transitions <paramref name="track"/> would hold after the plan is applied.</summary>
    public IEnumerable<(Transition Transition, TransitionState State)> EffectiveTransitions(Track track) =>
        AllEffectiveTransitions().Where(t => t.State.Track == track);

    /// <summary>True when the plan creates or changes the transition or changes one of its clips: its source handles
    /// are then checked (D025 §4: only when an edit creates or keeps a dissolve).</summary>
    public bool IsTouched(Transition transition)
    {
        if (_transitionUpdates.ContainsKey(transition) || _transitionAdds.Any(a => a.Transition == transition)) return true;
        var state = StateOf(transition);
        return Changes(state.LeftClipId) || Changes(state.RightClipId);

        bool Changes(Guid clipId) =>
            _updates.Keys.Any(c => c.Id == clipId) || _inserts.Any(i => i.Clip.Id == clipId);
    }

    /// <summary>
    /// Brings every transition in line with the planned clips (D025 §5): a dissolve whose clips were removed, ended up
    /// on different tracks or no longer touch is removed (counted in <see cref="AutoRemovedTransitions"/>); one whose
    /// two clips moved together to another track moves with them. What stays is validated afterwards (zone, handles).
    /// </summary>
    public void ReconcileTransitions()
    {
        var clips = new Dictionary<Guid, (Track Track, ClipState State)>();
        foreach (var track in AllTracks)
            foreach (var (clip, state) in EffectiveClips(track))
                clips[clip.Id] = (track, state);

        foreach (var (transition, state) in AllEffectiveTransitions().ToList())
        {
            if (!clips.TryGetValue(state.LeftClipId, out var left) || !clips.TryGetValue(state.RightClipId, out var right)
                || left.Track != right.Track || left.State.End != right.State.Start)
            {
                _transitionRemoves.Add(transition);
                AutoRemovedTransitions++;
            }
            else if (left.Track != state.Track)
            {
                _transitionUpdates[transition] = state with { Track = left.Track };
            }
        }
    }

    /// <summary>Tracks whose transitions the plan may change or that hold clips it changes.</summary>
    public IEnumerable<Track> TransitionTracks =>
        AffectedTracks
            .Concat(_removes.Values)
            .Concat(_transitionAdds.Select(a => a.Track))
            .Concat(_transitionUpdates.Values.Select(s => s.Track))
            .Concat(_transitionUpdates.Keys.Where(_transitionTrack.ContainsKey).Select(t => _transitionTrack[t]))
            .Distinct();

    /// <summary>The commands for the transition changes alone (removes, updates, adds, in that order).</summary>
    public List<IUndoableCommand> BuildTransitionCommands(string updateDescription = "Update Dissolve")
    {
        var commands = new List<IUndoableCommand>();
        foreach (var transition in _transitionRemoves.Where(_transitionTrack.ContainsKey))
            commands.Add(new RemoveTransitionCommand(_transitionTrack[transition], transition));
        foreach (var (transition, after) in _transitionUpdates)
        {
            if (_transitionRemoves.Contains(transition) || !_transitionTrack.TryGetValue(transition, out var track)) continue;
            var before = TransitionState.Capture(track, transition);
            if (before != after) commands.Add(new UpdateTransitionCommand(transition, before, after, updateDescription));
        }
        foreach (var (track, transition) in _transitionAdds)
        {
            if (_transitionRemoves.Contains(transition)) continue;
            var target = track;
            if (_transitionUpdates.TryGetValue(transition, out var after))
            {
                after.ApplyTo(transition);   // not in the model yet: its final state is the one added
                target = after.Track;
            }
            commands.Add(new AddTransitionCommand(target, transition));
        }
        return commands;
    }

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

        foreach (var (clip, after) in _properties)
        {
            var before = ClipPropertyValues.Capture(clip);
            var full = new ClipPropertyValues(after.Visual ?? before.Visual, after.Audio ?? before.Audio, after.Text ?? before.Text,
                after.Fade ?? before.Fade);
            var fields = ClipPropertyValues.Diff(before, full);
            if (fields != ClipPropertyFields.None)
                commands.Add(new SetClipPropertiesCommand(clip, before, full, fields));
        }

        commands.AddRange(BuildTransitionCommands());

        if (commands.Count == 0)
            throw new InvalidOperationException("Cannot build a command from an empty plan.");

        return commands.Count == 1 ? commands[0] : new CompositeCommand(description, commands);
    }
}
