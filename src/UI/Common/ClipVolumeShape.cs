using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// D064 §2: what a clip's volume line shows on the timeline — the clip's volume × its points' curve over the clip's pixels, the
/// points inside the clip, the sound fades' widths — and the geometry the timeline's hit test and drags share with the drawing.
/// Pixel <c>x</c> (from the clip's left edge) plays source time <c>SourceIn + x / pixelsPerSecond · speed</c>. The line lies in
/// the clip's sound area (its lower half on a video clip, as the waveform; all of an audio clip): 0 at its bottom, 100 % at its
/// middle, 200 % at its top. Immutable and equal by value (the view redraws only what changed).
/// </summary>
public sealed record ClipVolumeShape(double Volume, VolumeEnvelope? Points, MediaTime SourceIn, double Speed, double PixelsPerSecond,
    double Width, bool LowerHalf, double FadeInWidth, double FadeOutWidth, bool IsMuted, bool IsActive, Guid? SelectedPoint)
{
    /// <summary>The handles' and points' reach in pixels.</summary>
    public const double HitPixels = 6;

    /// <summary>The line's reach in pixels above and below it.</summary>
    public const double LineHitPixels = 4;

    /// <summary>The top of the sound area in a clip <paramref name="height"/> high, and its height.</summary>
    public (double Top, double Height) Area(double height) => LowerHalf ? (height / 2, height / 2) : (0, height);

    /// <summary>The source time played at pixel <paramref name="x"/>.</summary>
    public MediaTime SourceAt(double x) => SourceIn + new MediaTime((long)Math.Round(x / PixelsPerSecond * Speed * TimeSpan.TicksPerSecond));

    /// <summary>The pixel of source time <paramref name="time"/>.</summary>
    public double XOf(MediaTime time) => (time - SourceIn).Ticks / (double)TimeSpan.TicksPerSecond / Speed * PixelsPerSecond;

    /// <summary>The points' curve alone (1 without points) at pixel <paramref name="x"/>.</summary>
    public double CurveAt(double x) => Points?.At(SourceAt(x)) ?? 1;

    /// <summary>The whole gain (volume × curve, 0–2) at pixel <paramref name="x"/>.</summary>
    public double GainAt(double x) => Math.Clamp(Volume * CurveAt(x), 0, 2);

    /// <summary>The line's y at a gain in a clip <paramref name="height"/> high.</summary>
    public double YOf(double gain, double height)
    {
        var (top, h) = Area(height);
        return top + h * (1 - Math.Clamp(gain, 0, 2) / 2);
    }

    /// <summary>The gain a y stands for (0–2).</summary>
    public double GainOfY(double y, double height)
    {
        var (top, h) = Area(height);
        return Math.Clamp(2 * (1 - (y - top) / h), 0, 2);
    }

    /// <summary>The points inside the clip with their pixels.</summary>
    public IEnumerable<(VolumePoint Point, double X)> ShownPoints()
    {
        if (Points is null) yield break;
        foreach (var point in Points.Points)
        {
            var x = XOf(point.Time);
            if (x >= -0.5 && x <= Width + 0.5) yield return (point, x);
        }
    }

    /// <summary>What lies under (<paramref name="x"/>, <paramref name="y"/>) in a clip <paramref name="height"/> high: a fade
    /// handle (the top corners at the fades' ends), a point, the line — in that order — or nothing.</summary>
    public VolumeHit HitTest(double x, double y, double height)
    {
        var (top, _) = Area(height);
        if (IsActive && Math.Abs(y - (top + 3)) <= HitPixels)
        {
            if (Math.Abs(x - FadeInWidth) <= HitPixels && x <= Width / 2 + HitPixels) return new VolumeHit(VolumeHitKind.FadeIn, null);
            if (Math.Abs(x - (Width - FadeOutWidth)) <= HitPixels && x >= Width / 2 - HitPixels) return new VolumeHit(VolumeHitKind.FadeOut, null);
        }
        if (IsActive)
            foreach (var (point, px) in ShownPoints())
                if (Math.Abs(px - x) <= HitPixels && Math.Abs(YOf(Volume * point.Gain, height) - y) <= HitPixels)
                    return new VolumeHit(VolumeHitKind.Point, point.Id);
        if (IsActive && x >= 0 && x <= Width && Math.Abs(YOf(GainAt(x), height) - y) <= LineHitPixels)
            return new VolumeHit(VolumeHitKind.Line, null);
        return new VolumeHit(VolumeHitKind.None, null);
    }

    /// <summary>A gain as the user reads it: dB (−∞ at 0) and per cent.</summary>
    public static string Describe(double gain) => gain <= 0
        ? $"−∞ {Strings.Audio_DecibelUnit}"
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{20 * Math.Log10(gain):+0.0;−0.0;0.0} {Strings.Audio_DecibelUnit} ({gain * 100:0} %)");
}

public enum VolumeHitKind { None, Line, Point, FadeIn, FadeOut }

public readonly record struct VolumeHit(VolumeHitKind Kind, Guid? PointId);
