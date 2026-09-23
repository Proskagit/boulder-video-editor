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
public sealed class InsertClipCommand(Track track, Clip clip) : IUndoableCommand
{
    public string Description => "Add Clip";
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
        _ => "Change Clip Properties"
    };
}

/// <summary>Top-level wrapper: runs the inner command and then raises one
/// timeline-changed notification, for Execute, Undo and Redo alike. Mergeable when the inner
/// command is: the merged step is wrapped again with the same notification.</summary>
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
