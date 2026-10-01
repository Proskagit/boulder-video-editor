using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Timeline.Commands;

/// <summary>Keeps <see cref="Track.Clips"/> sorted by start — an invariant every
/// command relies on (and the view renders from).</summary>
internal static class TrackClipList
{
    public static void InsertSorted(Track track, Clip clip)
    {
        var index = track.Clips.FindIndex(c => c.TimelineStart > clip.TimelineStart);
        track.Clips.Insert(index < 0 ? track.Clips.Count : index, clip);
    }

    public static void Remove(Track track, Clip clip)
    {
        if (!track.Clips.Remove(clip))
            throw new InvalidOperationException($"Clip {clip.Id} is not on track '{track.Name}'.");
    }
}

/// <summary>One clip's move/trim/re-grid: possibly to another track, from one absolute state to another.</summary>
public sealed record ClipChange(Clip Clip, Track FromTrack, ClipState Before, Track ToTrack, ClipState After);

/// <summary>Applies a set of <see cref="ClipChange"/>s. All clips are detached first
/// and re-inserted afterwards so intermediate orderings never matter.</summary>
public sealed class UpdateClipsCommand(string description, IReadOnlyList<ClipChange> changes) : IUndoableCommand
{
    public string Description { get; } = description;

    public IReadOnlyList<ClipChange> Changes { get; } = changes;

    public void Execute()
    {
        foreach (var c in Changes) TrackClipList.Remove(c.FromTrack, c.Clip);
        foreach (var c in Changes)
        {
            c.After.ApplyTo(c.Clip);
            TrackClipList.InsertSorted(c.ToTrack, c.Clip);
        }
    }

    public void Undo()
    {
        foreach (var c in Changes) TrackClipList.Remove(c.ToTrack, c.Clip);
        foreach (var c in Changes)
        {
            c.Before.ApplyTo(c.Clip);
            TrackClipList.InsertSorted(c.FromTrack, c.Clip);
        }
    }
}

/// <summary>Adds an already-constructed clip (its Id is fixed at construction, so
/// Redo recreates the very same clip and selections/references stay valid).</summary>
public sealed class InsertClipCommand(Track track, Clip clip, string description = "Add Clip") : IUndoableCommand
{
    public string Description { get; } = description;
    public Track Track { get; } = track;
    public Clip Clip { get; } = clip;

    public void Execute() => TrackClipList.InsertSorted(Track, Clip);
    public void Undo() => TrackClipList.Remove(Track, Clip);
}

/// <summary>Removes a clip; Undo puts the same object back.</summary>
public sealed class RemoveClipCommand(Track track, Clip clip) : IUndoableCommand
{
    public string Description => "Delete Clip";
    public Track Track { get; } = track;
    public Clip Clip { get; } = clip;

    public void Execute() => TrackClipList.Remove(Track, Clip);
    public void Undo() => TrackClipList.InsertSorted(Track, Clip);
}

/// <summary>Sets the project frame rate and its lock flag; Undo restores both.</summary>
public sealed class SetFrameRateCommand(
    ProjectSettings settings, FrameRate oldRate, bool oldLocked, FrameRate newRate, bool newLocked) : IUndoableCommand
{
    public string Description => "Set Project Frame Rate";

    public void Execute()
    {
        settings.FrameRate = newRate;
        settings.IsFrameRateLocked = newLocked;
    }

    public void Undo()
    {
        settings.FrameRate = oldRate;
        settings.IsFrameRateLocked = oldLocked;
    }
}

public sealed class AddTrackCommand(Sequence sequence, Track track) : IUndoableCommand
{
    public string Description => "Add Track";
    public Track Track { get; } = track;

    private List<Track> List => Track.Type == TrackType.Video ? sequence.VideoTracks : sequence.AudioTracks;

    public void Execute() => List.Add(Track);

    public void Undo()
    {
        if (!List.Remove(Track))
            throw new InvalidOperationException($"Track '{Track.Name}' is not in the sequence.");
    }
}

/// <summary>
/// Sets a clip's non-timing properties from one absolute snapshot to another. Execute writes
/// <see cref="After"/>, Undo writes <see cref="Before"/> — the captured values themselves, never
/// recomputed. Consecutive changes of the same <see cref="Fields"/> of the same clip merge into
/// one step whose Undo still restores the state before the first of them.
/// </summary>
public sealed class SetClipPropertiesCommand(Clip clip, ClipPropertyValues before, ClipPropertyValues after, ClipPropertyFields fields)
    : IMergeableCommand
{
    public Clip Clip { get; } = clip;
    public ClipPropertyValues Before { get; } = before;
    public ClipPropertyValues After { get; } = after;

    /// <summary>The properties this step changes (fixed by its first edit when merged).</summary>
    public ClipPropertyFields Fields { get; } = fields;

    public string Description => Describe(Fields);

    public void Execute() => After.ApplyTo(Clip);
    public void Undo() => Before.ApplyTo(Clip);

    public bool TryMerge(IUndoableCommand next, out IUndoableCommand? merged)
    {
        merged = null;
        if (next is not SetClipPropertiesCommand n || n.Clip != Clip || n.Fields != Fields || n.Before != After)
            return false;

        if (n.After != Before)
            merged = new SetClipPropertiesCommand(Clip, Before, n.After, Fields);
        return true; // merged stays null when the second edit restored the first one's "before"
    }

    private static string Describe(ClipPropertyFields fields) => fields switch
    {
        ClipPropertyFields.PositionX or ClipPropertyFields.PositionY => "Change Position",
        ClipPropertyFields.Scale => "Change Scale",
        ClipPropertyFields.Rotation => "Change Rotation",
        ClipPropertyFields.Opacity => "Change Opacity",
        _ when (fields & ~ClipPropertyFields.Crop) == 0 => "Change Crop",
        ClipPropertyFields.Volume => "Change Volume",
        ClipPropertyFields.Mute => "Mute Clip",
        ClipPropertyFields.Text => "Edit Text",
        ClipPropertyFields.FontFamily => "Change Font",
        ClipPropertyFields.FontSize => "Change Font Size",
        ClipPropertyFields.Color => "Change Text Color",
        ClipPropertyFields.Alignment => "Change Text Alignment",
        ClipPropertyFields.FadeIn => "Change Fade In",
        ClipPropertyFields.FadeOut => "Change Fade Out",
        ClipPropertyFields.FadeIn | ClipPropertyFields.FadeOut => "Change Fades",
        _ => "Change Clip Properties"
    };
}

/// <summary>Top-level wrapper: runs the inner command and then raises one
/// timeline-changed notification, for Execute, Undo and Redo alike. Mergeable when the inner
/// command is: the merged step is wrapped again with the same notification.</summary>
/// <summary>
/// Changes a clip's speed together with the timing it implies (D022): absolute before/after
/// snapshots, the clip stays where it is on its track (same start). Consecutive speed changes of the
/// same clip merge into one Undo step (like property edits, D017); a change back to where the step
/// started removes it.
/// </summary>
/// <remarks>
/// <paramref name="changes"/>: what the speed change does besides the timing — dissolves it removed because their cut
/// opened (D025 §5) and fades it cut to the shorter clip (§2). They belong to the same step: Execute runs them after
/// the clip changes, Undo undoes them (in reverse) before the clip goes back. Merged steps keep every one of them, so
/// one Undo after any chain of speed changes restores the clip, its fades and every dissolve it lost; a chain that
/// returns to the starting speed but made such a change is kept as a step (it would otherwise be left without an undo).
/// </remarks>
public sealed class SetClipSpeedCommand(Clip clip, ClipState before, ClipState after,
    IReadOnlyList<IUndoableCommand>? changes = null) : IMergeableCommand
{
    public Clip Clip { get; } = clip;
    public ClipState Before { get; } = before;
    public ClipState After { get; } = after;
    public IReadOnlyList<IUndoableCommand> Changes { get; } = changes ?? Array.Empty<IUndoableCommand>();

    public string Description => "Change Speed";

    public void Execute()
    {
        After.ApplyTo(Clip);
        foreach (var change in Changes) change.Execute();
    }

    public void Undo()
    {
        for (var i = Changes.Count - 1; i >= 0; i--) Changes[i].Undo();
        Before.ApplyTo(Clip);
    }

    public bool TryMerge(IUndoableCommand next, out IUndoableCommand? merged)
    {
        merged = null;
        if (next is not SetClipSpeedCommand n || n.Clip != Clip || n.Before != After)
            return false;

        var changes = Changes.Concat(n.Changes).ToList();
        if (n.After != Before || changes.Count > 0)
            merged = new SetClipSpeedCommand(Clip, Before, n.After, changes);
        return true;
    }
}

public sealed class NotifyingCommand(IUndoableCommand inner, Action notify) : IMergeableCommand
{
    public string Description => Inner.Description;
    public IUndoableCommand Inner { get; } = inner;

    public bool TryMerge(IUndoableCommand next, out IUndoableCommand? merged)
    {
        merged = null;
        if (Inner is not IMergeableCommand mergeable || next is not NotifyingCommand n
            || !mergeable.TryMerge(n.Inner, out var inner))
            return false;

        merged = inner is null ? null : new NotifyingCommand(inner, notify);
        return true;
    }

    public void Execute()
    {
        Inner.Execute();
        notify();
    }

    public void Undo()
    {
        Inner.Undo();
        notify();
    }
}

/// <summary>Where a transition is and what it joins (D025): its track, the clips left and right of the cut and its
/// length. Commands store absolute before / after states.</summary>
public readonly record struct TransitionState(Track Track, Guid LeftClipId, Guid RightClipId, MediaTime Duration)
{
    public static TransitionState Capture(Track track, Transition t) => new(track, t.LeftClipId, t.RightClipId, t.Duration);

    public void ApplyTo(Transition t)
    {
        t.LeftClipId = LeftClipId;
        t.RightClipId = RightClipId;
        t.Duration = Duration;
    }
}

/// <summary>Adds an already-constructed transition (its Id is fixed, so Redo recreates the very same one).</summary>
public sealed class AddTransitionCommand(Track track, Transition transition, string description = "Add Dissolve") : IUndoableCommand
{
    public string Description { get; } = description;
    public Track Track { get; } = track;
    public Transition Transition { get; } = transition;

    public void Execute() => Track.Transitions.Add(Transition);

    public void Undo()
    {
        if (!Track.Transitions.Remove(Transition))
            throw new InvalidOperationException($"Transition {Transition.Id} is not on track '{Track.Name}'.");
    }
}

/// <summary>Removes a transition; Undo puts the same object back where it was.</summary>
public sealed class RemoveTransitionCommand(Track track, Transition transition, string description = "Remove Dissolve") : IUndoableCommand
{
    private int _index = -1;

    public string Description { get; } = description;
    public Track Track { get; } = track;
    public Transition Transition { get; } = transition;

    public void Execute()
    {
        _index = Track.Transitions.IndexOf(Transition);
        if (_index < 0) throw new InvalidOperationException($"Transition {Transition.Id} is not on track '{Track.Name}'.");
        Track.Transitions.RemoveAt(_index);
    }

    public void Undo() => Track.Transitions.Insert(Math.Min(_index, Track.Transitions.Count), Transition);
}

/// <summary>
/// Moves a transition from one absolute state to another: another anchor (a split), another track (both clips moved
/// together) or another length. Consecutive length changes of the same transition merge into one Undo step (like clip
/// properties, D017); a change back to where the step started removes it.
/// </summary>
public sealed class UpdateTransitionCommand(Transition transition, TransitionState before, TransitionState after,
    string description = "Change Dissolve Duration") : IMergeableCommand
{
    public string Description { get; } = description;
    public Transition Transition { get; } = transition;
    public TransitionState Before { get; } = before;
    public TransitionState After { get; } = after;

    public void Execute() => Apply(Before, After);
    public void Undo() => Apply(After, Before);

    private void Apply(TransitionState from, TransitionState to)
    {
        if (from.Track != to.Track)
        {
            if (!from.Track.Transitions.Remove(Transition))
                throw new InvalidOperationException($"Transition {Transition.Id} is not on track '{from.Track.Name}'.");
            to.Track.Transitions.Add(Transition);
        }
        to.ApplyTo(Transition);
    }

    private bool IsLengthOnly => Before with { Duration = After.Duration } == After;

    public bool TryMerge(IUndoableCommand next, out IUndoableCommand? merged)
    {
        merged = null;
        if (next is not UpdateTransitionCommand n || n.Transition != Transition || n.Before != After || !IsLengthOnly || !n.IsLengthOnly)
            return false;

        if (n.After != Before)
            merged = new UpdateTransitionCommand(Transition, Before, n.After, Description);
        return true;
    }
}
