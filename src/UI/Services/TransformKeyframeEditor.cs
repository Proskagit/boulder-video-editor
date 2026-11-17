using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.Core.Composition;

namespace AiVideoEditor.UI.Services;

/// <summary>D063 §1: whose keyframes — a video clip's own transform, or one followed part's framing (its index in the
/// clip's Follow).</summary>
public sealed record KeyframeOwner(Guid ClipId, int? FollowPart)
{
    public bool IsFollow => FollowPart is not null;
}

/// <summary>One keyframe as the editor shows it: its owner, the keyframe and the timeline frame it lies on.</summary>
public sealed record KeyframeOnTimeline(KeyframeOwner Owner, TransformKeyframe Key, long Frame);

/// <summary>
/// D063 §2: the editing of the transform keyframes, shared by the timeline (its button and markers), the Inspector and the
/// Preview — every change one <see cref="ITimelineEditService"/> edit (one undo step; a Preview gesture's changes merge).
/// <list type="bullet">
/// <item><b>The owner at the playhead</b>: the followed part whose range holds the frame's source time, else the clip
/// (<see cref="OwnerAt"/>) — the transform that places the picture there.</item>
/// <item><b>Values</b>: the clip's Position X / Y and Scale; a part's climber offset X / Y and the picture's scale while it
/// follows. <see cref="ValueAt"/> is what the frame shows (the same interpolation as the composition).</item>
/// <item><b>An edit of a value</b> at the playhead (<see cref="SetValue"/>): with keyframes it changes the keyframe on that
/// frame — or adds one there; without keyframes the static values, as before D063.</item>
/// <item><b>The last keyframe removed</b>: its values become the static ones (the picture keeps them).</item>
/// </list>
/// Keyframe times are the video's source times (D063 §1); a frame's own source time is the D009 / D022 rule.
/// </summary>
public sealed class TransformKeyframeEditor
{
    private readonly ITimelineEditService _edit;
    private readonly Func<FrameRate> _rate;
    private readonly Func<IEnumerable<VideoClip>> _clips;

    /// <param name="rate">The project's frame rate now.</param>
    /// <param name="clips">The project's video clips now (for <see cref="Clip"/>).</param>
    public TransformKeyframeEditor(ITimelineEditService edit, Func<FrameRate> rate, Func<IEnumerable<VideoClip>>? clips = null)
    {
        _edit = edit;
        _rate = rate;
        _clips = clips ?? Enumerable.Empty<VideoClip>;
    }

    /// <summary>The editor of <paramref name="projects"/>' current project.</summary>
    public TransformKeyframeEditor(ITimelineEditService edit, IProjectService projects)
        : this(edit, () => projects.Current.Settings.FrameRate,
            () => projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>())
    {
    }

    /// <summary>New keyframe ids (tests).</summary>
    public Func<Guid> NewId { get; set; } = Guid.NewGuid;

    private FrameRate Rate => _rate();

    // ---- time --------------------------------------------------------------------------------------------------------

    /// <summary>The source time shown on timeline frame <paramref name="frame"/> of <paramref name="clip"/>.</summary>
    public MediaTime SourceAt(VideoClip clip, long frame) =>
        HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, frame, Rate);

    /// <summary>The timeline frame showing source time <paramref name="source"/> of <paramref name="clip"/> (nearest).</summary>
    public long FrameOf(VideoClip clip, MediaTime source)
    {
        long a = clip.Speed.Numerator, b = clip.Speed.Denominator;
        var offset = new MediaTime((long)((Int128)(source - clip.SourceIn).Ticks * b / a));
        return (clip.TimelineStart + offset).ToNearestFrame(Rate);
    }

    /// <summary>The first and the last timeline frame of the clip.</summary>
    public (long First, long Last) FramesOf(Clip clip) =>
        (clip.TimelineStart.ToFrameFloor(Rate), Math.Max(clip.TimelineStart.ToFrameFloor(Rate), clip.TimelineEnd.ToFrameFloor(Rate) - 1));

    // ---- owners and values ---------------------------------------------------------------------------------------------

    public VideoClip? Clip(Guid clipId) => _clips().FirstOrDefault(c => c.Id == clipId);

    /// <summary>The owner whose transform places <paramref name="clip"/>'s picture on <paramref name="frame"/>.</summary>
    public KeyframeOwner OwnerAt(VideoClip clip, long frame)
    {
        var index = clip.Follow?.IndexAt(SourceAt(clip, frame)) ?? -1;
        return new KeyframeOwner(clip.Id, index >= 0 ? index : null);
    }

    /// <summary>The keyframes of <paramref name="owner"/> (null: none).</summary>
    public static TransformKeyframes? KeysOf(VideoClip clip, KeyframeOwner owner) =>
        owner.FollowPart is { } part
            ? clip.Follow is { } follow && part < follow.Parts.Length ? follow.Parts[part].Keyframes : null
            : clip.Keyframes;

    /// <summary>The owner's values on <paramref name="frame"/>: the clip's position and scale, or the part's offset and scale
    /// (the clip's own scale there without the part's keyframes).</summary>
    public TransformValue ValueAt(VideoClip clip, KeyframeOwner owner, long frame)
    {
        var source = SourceAt(clip, frame);
        var own = VisualProperties.At(clip, source)!.Value;
        if (owner.FollowPart is { } part && clip.Follow is { } follow && part < follow.Parts.Length)
            return follow.Parts[part].FramingAt(source, own.Scale);
        return new TransformValue(own.PositionX, own.PositionY, own.Scale);
    }

    /// <summary>The owner's keyframe on <paramref name="frame"/>, or null.</summary>
    public TransformKeyframe? KeyOnFrame(VideoClip clip, KeyframeOwner owner, long frame) =>
        KeysOf(clip, owner)?.Keys.FirstOrDefault(k => FrameOf(clip, k.Time) == frame);

    /// <summary>The owner's keyframes the clip shows (on its frames; for a part, within the part), in time order.</summary>
    public IReadOnlyList<KeyframeOnTimeline> Shown(VideoClip clip, KeyframeOwner owner)
    {
        if (KeysOf(clip, owner) is not { } keys) return Array.Empty<KeyframeOnTimeline>();
        var (first, last) = FramesOf(clip);
        var range = owner.FollowPart is { } p && clip.Follow is { } follow ? follow.Parts[p].RangeIn(clip.SourceIn, clip.SourceOut) : (clip.SourceIn, clip.SourceOut);
        var result = new List<KeyframeOnTimeline>();
        foreach (var key in keys.Keys)
        {
            // A part is [From, To): a keyframe on its end belongs to what follows.
            if (range is not { } r || key.Time < r.Item1 || (owner.IsFollow ? key.Time >= r.Item2 : key.Time > r.Item2)) continue;
            var frame = FrameOf(clip, key.Time);
            if (frame < first || frame > last) continue;
            result.Add(new KeyframeOnTimeline(owner, key, frame));
        }
        return result;
    }

    /// <summary>Every owner's shown keyframes of <paramref name="clip"/> (the clip's, then each part's).</summary>
    public IReadOnlyList<KeyframeOnTimeline> AllShown(VideoClip clip)
    {
        var all = new List<KeyframeOnTimeline>(Shown(clip, new KeyframeOwner(clip.Id, null)));
        if (clip.Follow is { } follow)
            for (var i = 0; i < follow.Parts.Length; i++) all.AddRange(Shown(clip, new KeyframeOwner(clip.Id, i)));
        return all;
    }

    // ---- edits -----------------------------------------------------------------------------------------------------------

    /// <summary>The keyframe button (D063 §1): on <paramref name="frame"/> the owner's keyframe is removed when there is one,
    /// else one is added with the values the frame shows (the picture does not change).</summary>
    public (TimelineEditResult Result, Guid? Added) Toggle(VideoClip clip, long frame)
    {
        var owner = OwnerAt(clip, frame);
        if (KeyOnFrame(clip, owner, frame) is { } existing) return (Delete(clip, owner, existing.Id), null);
        var source = SourceAt(clip, frame);
        var value = ValueAt(clip, owner, frame);
        var keys = KeysOf(clip, owner);
        var (next, added) = keys is null
            ? (TransformKeyframes.Single(source, value, NewId), (TransformKeyframe?)null)
            : keys.WithValueAt(source, value, NewId);
        added ??= next.Keys[0];
        return (Write(clip, owner, next, new ClipPropertyGesture("Add Keyframe")), added.Id);
    }

    /// <summary>
    /// D063 §2: the owner's values on <paramref name="frame"/> set to <paramref name="value"/> — the keyframe on that frame
    /// changed (or one added there) when the owner has keyframes; otherwise the static values (the clip's Position X / Y and
    /// Scale; a part's offset — its scale is then the clip's, so a part's scale without keyframes goes to the clip).
    /// </summary>
    public TimelineEditResult SetValue(VideoClip clip, KeyframeOwner owner, long frame, TransformValue value, ClipPropertyGesture? gesture = null)
    {
        var source = SourceAt(clip, frame);
        if (KeysOf(clip, owner) is not null)
        {
            var keys = KeysWithValue(clip, owner, frame, value);
            return Write(clip, owner, keys, gesture ?? StepFor(clip, owner, frame, keys));
        }
        if (owner.FollowPart is { } part && clip.Follow is { } follow)
        {
            var p = follow.Parts[part];
            var own = VisualProperties.At(clip, source)!.Value;
            TimelineEditResult? result = null;
            if (p.OffsetX != value.X || p.OffsetY != value.Y)
                result = _edit.SetClipFollow(clip.Id, follow.WithPart(part, p with { OffsetX = value.X, OffsetY = value.Y }), gesture: gesture);
            if (result is { Success: false }) return result;
            if (own.Scale != value.Scale)
                return SetValue(clip, new KeyframeOwner(clip.Id, null), frame, new TransformValue(own.PositionX, own.PositionY, value.Scale), gesture);
            return result ?? TimelineEditResult.Unchanged();
        }
        var visual = VisualProperties.Of(clip)!.Value;
        return _edit.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Visual = visual with { PositionX = value.X, PositionY = value.Y, Scale = value.Scale }, Gesture = gesture
        });
    }

    /// <summary>The last value edit without a gesture (the Inspector's fields): its owner, its keyframe and its undo step.</summary>
    private (KeyframeOwner Owner, Guid Key, ClipPropertyGesture Step)? _lastStep;

    /// <summary>
    /// D063 §2: the undo step of a value edit without a gesture — the edits of one keyframe in a row (a typed value, a scrub)
    /// are one step; another keyframe, or a keyframe added, starts a new one (never one step over two keyframes).
    /// </summary>
    private ClipPropertyGesture StepFor(VideoClip clip, KeyframeOwner owner, long frame, TransformKeyframes keys)
    {
        var key = keys.Keys.First(k => FrameOf(clip, k.Time) == frame || k.Time == SourceAt(clip, frame));
        if (_lastStep is { } last && last.Owner == owner && last.Key == key.Id && KeysOf(clip, owner)?.Find(key.Id) is not null)
            return last.Step;
        var added = KeysOf(clip, owner)?.Find(key.Id) is null;
        var step = new ClipPropertyGesture(added ? "Add Keyframe" : "Change Keyframe");
        _lastStep = (owner, key.Id, step);
        return step;
    }

    /// <summary>The time an edit of the owner's values on <paramref name="frame"/> writes: the keyframe's own time when one lies on
    /// the frame (after a speed change between two source times), else the frame's source time.</summary>
    public MediaTime EditTimeAt(VideoClip clip, KeyframeOwner owner, long frame) => KeyOnFrame(clip, owner, frame)?.Time ?? SourceAt(clip, frame);

    /// <summary>D063 §5: the time Fill / Fit writes on a keyframed clip — the playhead's frame when it lies on the clip; null
    /// otherwise (or without keyframes).</summary>
    public MediaTime? FramingTimeAt(Clip clip, long frame)
    {
        if (clip is not VideoClip { Keyframes: not null } video) return null;
        var (first, last) = FramesOf(video);
        return frame >= first && frame <= last ? EditTimeAt(video, new KeyframeOwner(video.Id, null), frame) : null;
    }

    /// <summary>The owner's keyframes with the values on <paramref name="frame"/> set to <paramref name="value"/> — the keyframe
    /// on that frame changed, or one added at the frame's source time (the owner must have keyframes).</summary>
    public TransformKeyframes KeysWithValue(VideoClip clip, KeyframeOwner owner, long frame, TransformValue value)
    {
        var keys = KeysOf(clip, owner) ?? throw new InvalidOperationException("The owner has no keyframes.");
        var target = KeyOnFrame(clip, owner, frame) is { } onFrame ? onFrame.Time : SourceAt(clip, frame);
        return keys.WithValueAt(target, value, NewId).Keys;
    }

    /// <summary>Removes the owner's keyframe <paramref name="id"/>; the last one leaves its values as the static ones.</summary>
    public TimelineEditResult Delete(VideoClip clip, KeyframeOwner owner, Guid id)
    {
        if (KeysOf(clip, owner) is not { } keys || keys.Find(id) is not { } key) return TimelineEditResult.Unchanged();
        var gesture = new ClipPropertyGesture("Delete Keyframe");
        if (keys.Without(id) is { } rest) return Write(clip, owner, rest, gesture);
        if (owner.FollowPart is { } part && clip.Follow is { } follow)
            return _edit.SetClipFollow(clip.Id, follow.WithPart(part, follow.Parts[part] with { Keyframes = null, OffsetX = key.X, OffsetY = key.Y }),
                gesture: gesture);
        var visual = VisualProperties.Of(clip)!.Value;
        return _edit.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Visual = visual with { PositionX = key.X, PositionY = key.Y, Scale = key.Scale },
            Keyframes = new KeyframeValues(null),
            Gesture = gesture
        });
    }

    /// <summary>Moves the owner's keyframe <paramref name="id"/> to timeline frame <paramref name="frame"/> (within the clip,
    /// and for a part within the part); refused when another keyframe is on that frame.</summary>
    public TimelineEditResult Move(VideoClip clip, KeyframeOwner owner, Guid id, long frame)
    {
        if (KeysOf(clip, owner) is not { } keys || keys.Find(id) is not { } key) return TimelineEditResult.Unchanged();
        var (first, last) = FramesOf(clip);
        frame = Math.Clamp(frame, first, last);
        var source = SourceAt(clip, frame);
        if (owner.FollowPart is { } part && clip.Follow?.Parts[part].RangeIn(clip.SourceIn, clip.SourceOut) is { } range)
            source = source < range.From ? range.From : source >= range.To ? SourceAt(clip, FrameOf(clip, range.To) - 1) : source;
        if (source == key.Time) return TimelineEditResult.Unchanged();
        if (keys.Keys.Any(k => k.Id != id && FrameOf(clip, k.Time) == FrameOf(clip, source)))
            return TimelineEditResult.Fail(Core.Localization.Messages.Keyframe_Exists);
        return Write(clip, owner, keys.With(key with { Time = source })!, new ClipPropertyGesture("Move Keyframe"));
    }

    /// <summary>Sets the easing of the owner's keyframe <paramref name="id"/> (its outgoing segment).</summary>
    public TimelineEditResult SetEasing(VideoClip clip, KeyframeOwner owner, Guid id, KeyframeEasing easing)
    {
        if (KeysOf(clip, owner) is not { } keys || keys.Find(id) is not { } key || key.Easing == easing) return TimelineEditResult.Unchanged();
        return Write(clip, owner, keys.With(key with { Easing = easing })!, new ClipPropertyGesture("Change Keyframe Easing"));
    }

    private TimelineEditResult Write(VideoClip clip, KeyframeOwner owner, TransformKeyframes keys, ClipPropertyGesture? gesture)
    {
        if (owner.FollowPart is { } part)
        {
            if (clip.Follow is not { } follow || part >= follow.Parts.Length) return TimelineEditResult.Fail(Strings.Inspector_ClipNotChanged);
            return _edit.SetClipFollow(clip.Id, follow.WithPart(part, follow.Parts[part] with { Keyframes = keys }), gesture: gesture);
        }
        return _edit.SetClipProperties(clip.Id, new ClipPropertyChange { Keyframes = new KeyframeValues(keys), Gesture = gesture });
    }
}
