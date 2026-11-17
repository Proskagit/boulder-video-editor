using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Composition;

/// <summary>D063 §3: how the values move from a keyframe to the next one — the curve of the segment that starts at the
/// keyframe (its outgoing segment). The last keyframe's easing is kept but unused.</summary>
public enum KeyframeEasing
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut
}

/// <summary>The three animated transform values (D063 §1): a position in canvas pixels from the canvas centre (+X right,
/// +Y down) and a scale — of a video clip (its Position X / Y and Scale) or of a followed part (the climber's place in the
/// frame, its offset X / Y, and the picture's scale while following).</summary>
public readonly record struct TransformValue(double X, double Y, double Scale);

/// <summary>
/// D063 §1: one transform keyframe — its <see cref="Time"/> in the video's source time (like a followed part's range: Trim,
/// Split, Slip, Speed and moving the clip keep it on the same frame), its values and the easing of the segment to the next
/// keyframe. Its owner is where it is stored: <see cref="VideoClip.Keyframes"/> (the clip's transform) or
/// <see cref="FollowPart.Keyframes"/> (that followed part's framing). <see cref="Id"/> identifies it in the editor (the
/// selection, a drag).
/// </summary>
public sealed record TransformKeyframe(Guid Id, MediaTime Time, double X, double Y, double Scale, KeyframeEasing Easing = KeyframeEasing.Linear)
{
    public TransformValue Value => new(X, Y, Scale);

    public TransformKeyframe WithValue(TransformValue value) => this with { X = value.X, Y = value.Y, Scale = value.Scale };
}

/// <summary>
/// D063: an owner's transform keyframes — sorted by time, never two at one time, at least one. Immutable and equal by value
/// (replaced as a whole by every edit, like <see cref="FollowSettings"/>); read from any thread (the export's parallel
/// rasterizers). <see cref="At"/> is the one interpolation the Preview, the export and the editor share.
/// </summary>
public sealed record TransformKeyframes
{
    public TransformKeyframes(ImmutableArray<TransformKeyframe> keys)
    {
        Keys = keys.IsDefault ? ImmutableArray<TransformKeyframe>.Empty : keys;
    }

    public ImmutableArray<TransformKeyframe> Keys { get; }

    public int Count => Keys.Length;

    /// <summary>The keyframes of <paramref name="keys"/> sorted; null when there are none (no keyframes — the static values).</summary>
    public static TransformKeyframes? Of(IEnumerable<TransformKeyframe> keys)
    {
        var sorted = keys.OrderBy(k => k.Time).ToImmutableArray();
        return sorted.IsEmpty ? null : new TransformKeyframes(sorted);
    }

    /// <summary>Whether <paramref name="keys"/> are what a project accepts: at least one, sorted, every time distinct, ids
    /// distinct, every value finite and within the limits of a position and a scale.</summary>
    public static bool AreValid(ImmutableArray<TransformKeyframe> keys)
    {
        if (keys.IsDefaultOrEmpty) return false;
        var ids = new HashSet<Guid>();
        for (var i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            if (!ids.Add(k.Id)) return false;
            if (i > 0 && keys[i - 1].Time >= k.Time) return false;
            if (!double.IsFinite(k.X) || !double.IsFinite(k.Y) || !double.IsFinite(k.Scale)) return false;
            if (Math.Abs(k.X) > ClipPropertyLimits.MaxPositionMagnitude || Math.Abs(k.Y) > ClipPropertyLimits.MaxPositionMagnitude) return false;
            if (k.Scale < ClipPropertyLimits.MinScale || k.Scale > ClipPropertyLimits.MaxScale) return false;
            if (!Enum.IsDefined(k.Easing)) return false;
        }
        return true;
    }

    /// <summary>
    /// The values at source time <paramref name="time"/> (D063 §3): before the first keyframe its values, after the last the
    /// last's; between two the segment's easing applied to the share of the time between them (so the transition lasts
    /// exactly the distance between the keyframes) and every value interpolated linearly by it. Exactly a keyframe's values
    /// on its own time.
    /// </summary>
    public TransformValue At(MediaTime time)
    {
        if (Keys.IsDefaultOrEmpty) throw new InvalidOperationException("No keyframes.");
        if (time <= Keys[0].Time) return Keys[0].Value;
        if (time >= Keys[^1].Time) return Keys[^1].Value;
        int lo = 0, hi = Keys.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (Keys[mid].Time <= time) lo = mid; else hi = mid;
        }
        var (a, b) = (Keys[lo], Keys[hi]);
        if (time == a.Time) return a.Value;
        var f = Ease(a.Easing, (time - a.Time).Ticks / (double)(b.Time - a.Time).Ticks);
        return new TransformValue(Lerp(a.X, b.X, f), Lerp(a.Y, b.Y, f), Lerp(a.Scale, b.Scale, f));
    }

    /// <summary>The keyframe at exactly <paramref name="time"/>, or null.</summary>
    public TransformKeyframe? KeyAt(MediaTime time)
    {
        foreach (var key in Keys)
            if (key.Time == time) return key;
        return null;
    }

    public TransformKeyframe? Find(Guid id)
    {
        foreach (var key in Keys)
            if (key.Id == id) return key;
        return null;
    }

    /// <summary>These keyframes with <paramref name="key"/> added or — the same id — replaced, sorted; null when another
    /// keyframe already lies at its time.</summary>
    public TransformKeyframes? With(TransformKeyframe key)
    {
        if (Keys.Any(k => k.Id != key.Id && k.Time == key.Time)) return null;
        return Of(Keys.Where(k => k.Id != key.Id).Append(key))!;
    }

    /// <summary>These keyframes without <paramref name="id"/>; null when none is left.</summary>
    public TransformKeyframes? Without(Guid id) => Of(Keys.Where(k => k.Id != id));

    /// <summary>
    /// D063 §2: the values at <paramref name="time"/> set to <paramref name="value"/> — the keyframe there changed, or (none
    /// there) a new keyframe added there with the easing of the segment it splits (an edit away from every keyframe adds one,
    /// as in the common editors). Returns the keyframes and the keyframe written.
    /// </summary>
    public (TransformKeyframes Keys, TransformKeyframe Key) WithValueAt(MediaTime time, TransformValue value, Func<Guid>? newId = null)
    {
        if (KeyAt(time) is { } existing)
        {
            var changed = existing.WithValue(value);
            return (With(changed)!, changed);
        }
        var before = Keys.LastOrDefault(k => k.Time < time);
        var added = new TransformKeyframe((newId ?? Guid.NewGuid)(), time, value.X, value.Y, value.Scale, before?.Easing ?? KeyframeEasing.Linear);
        return (With(added)!, added);
    }

    /// <summary>A keyframe list from nothing: one keyframe at <paramref name="time"/>.</summary>
    public static TransformKeyframes Single(MediaTime time, TransformValue value, Func<Guid>? newId = null) =>
        new(ImmutableArray.Create(new TransformKeyframe((newId ?? Guid.NewGuid)(), time, value.X, value.Y, value.Scale)));

    /// <summary>The easing curve: 0 → 0, 1 → 1, monotonic (cubic ease in, out, in-out).</summary>
    public static double Ease(KeyframeEasing easing, double f)
    {
        f = Math.Clamp(f, 0, 1);
        return easing switch
        {
            KeyframeEasing.EaseIn => f * f * f,
            KeyframeEasing.EaseOut => 1 - Math.Pow(1 - f, 3),
            KeyframeEasing.EaseInOut => f < 0.5 ? 4 * f * f * f : 1 - Math.Pow(-2 * f + 2, 3) / 2,
            _ => f
        };
    }

    private static double Lerp(double a, double b, double f) => f <= 0 ? a : f >= 1 ? b : a + (b - a) * f;

    public bool Equals(TransformKeyframes? other) => other is not null && Keys.SequenceEqual(other.Keys);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in Keys) hash.Add(key);
        return hash.ToHashCode();
    }

    /// <summary>
    /// D063 §1: <paramref name="visual"/> with its position and scale at source time <paramref name="time"/> from
    /// <paramref name="keys"/> (the rotation, opacity and crop stay the clip's own); <paramref name="visual"/> itself without
    /// keyframes.
    /// </summary>
    public static VisualProperties Apply(VisualProperties visual, TransformKeyframes? keys, MediaTime time)
    {
        if (keys is null || keys.Keys.IsDefaultOrEmpty) return visual;
        var v = keys.At(time);
        return visual with { PositionX = v.X, PositionY = v.Y, Scale = v.Scale };
    }
}
