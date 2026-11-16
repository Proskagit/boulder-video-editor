using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.People;

/// <summary>
/// A box in normalized coordinates of the display-oriented source frame (D061 §2): <c>0..1</c> on both axes, origin at the
/// top-left — the space of the hold contours (<see cref="Hold.Points"/>), so a box, a contour and a mask meet without any
/// conversion, whatever the clip's crop, scale or canvas.
/// </summary>
public readonly record struct NormalizedBox(double X0, double Y0, double X1, double Y1)
{
    public double Width => Math.Max(0, X1 - X0);
    public double Height => Math.Max(0, Y1 - Y0);
    public double Area => Width * Height;
    public NormalizedPoint Center => new((X0 + X1) / 2, (Y0 + Y1) / 2);

    /// <summary>The box's size as one number (the diagonal) — the unit of "near" for a person.</summary>
    public double Size => Math.Sqrt(Width * Width + Height * Height);

    public bool Contains(NormalizedPoint p) => p.X >= X0 && p.X <= X1 && p.Y >= Y0 && p.Y <= Y1;

    public NormalizedBox Clamped() => new(Math.Clamp(X0, 0, 1), Math.Clamp(Y0, 0, 1), Math.Clamp(X1, 0, 1), Math.Clamp(Y1, 0, 1));

    public NormalizedBox Shifted(double dx, double dy) => new(X0 + dx, Y0 + dy, X1 + dx, Y1 + dy);

    /// <summary>Intersection over union; 0 for disjoint or empty boxes.</summary>
    public double IoU(NormalizedBox other)
    {
        var w = Math.Min(X1, other.X1) - Math.Max(X0, other.X0);
        var h = Math.Min(Y1, other.Y1) - Math.Max(Y0, other.Y0);
        if (w <= 0 || h <= 0) return 0;
        var inter = w * h;
        var union = Area + other.Area - inter;
        return union > 0 ? inter / union : 0;
    }

    /// <summary>Whether the box touches an edge of the frame (within <paramref name="margin"/>): a person cut by the frame.</summary>
    public bool TouchesEdge(double margin = 0.01) => X0 <= margin || Y0 <= margin || X1 >= 1 - margin || Y1 >= 1 - margin;
}

/// <summary>
/// One person found on one analysed frame: the detector's box and score, the torso centre from the pose (the middle of the
/// shoulders and hips; null when the pose is not confident — the box centre is used then) and an appearance descriptor
/// (<see cref="PersonAppearance"/>: a colour histogram of the inner box, L1-normalized) for re-identification after a loss.
/// </summary>
public sealed record PersonDetection(NormalizedBox Box, float Score, NormalizedPoint? Torso, ImmutableArray<float> Appearance)
{
    /// <summary>The point the camera follows (D061 §3): the torso centre, or the box centre without a confident pose.</summary>
    public NormalizedPoint Anchor => Torso ?? Box.Center;
}

/// <summary>The people of one analysed frame at source time <see cref="Time"/> (100 ns ticks from the file's start), with the
/// track each belongs to (<see cref="TrackIds"/>, by index; −1 — not part of any track).</summary>
public sealed record PersonSample(MediaTime Time, ImmutableArray<PersonDetection> People, ImmutableArray<int> TrackIds)
{
    public int TrackIdOf(int index) => index < TrackIds.Length ? TrackIds[index] : -1;
}
