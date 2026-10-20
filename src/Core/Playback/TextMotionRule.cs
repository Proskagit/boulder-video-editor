using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// The appearance / disappearance motion of a text (D043 §1), on timeline frames like the fades (D025 §2) — the one rule the
/// Preview and the export take a frame from (<see cref="PlaybackSnapshot.LayersAt"/>). A text covering frames
/// <c>[s, e)</c> with effective fades <c>Fin</c> / <c>Fout</c> (D025: whole frames, clamped to the clip, none on a dissolve
/// edge): on clip frame <c>i = n − s &lt; Fin</c> the In progress is the fade's ramp <c>p = (i + 1)/(Fin + 1)</c>, eased
/// <c>e = 1 − (1 − p)³</c>; on <c>j = e − 1 − n &lt; Fout</c> the Out progress likewise. The opacity stays the D025 fade
/// (applied elsewhere); the motion only adds, for a progress <c>e</c> (1 = at rest):
/// <list type="bullet">
/// <item>Slide: a shift of <c>(1 − e) · d</c>, <c>d = </c><see cref="SlideFactor"/> × the canvas's short side, toward the
/// motion's side along the canvas axis (not rotated with the text) — In comes from that side, Out goes to it;</item>
/// <item>Zoom: a scale of <c>0.7 + 0.3 · e</c> around the text box's centre (the text's local origin).</item>
/// </list>
/// A text without motion, or at rest, keeps exactly its transform (no arithmetic is applied to it).
/// </summary>
public static class TextMotionRule
{
    /// <summary>The slide distance, × the canvas's short side.</summary>
    public const double SlideFactor = 0.08;

    /// <summary>The scale a Zoom starts from (In) or ends at (Out).</summary>
    public const double ZoomFrom = 0.7;

    /// <summary>The eased progress of a ramp of <paramref name="steps"/> frames at its frame <paramref name="k"/>: 1 outside
    /// it (at rest).</summary>
    public static double Progress(long k, long steps)
    {
        var p = FadeRule.Ramp(k, steps);
        if (p >= 1) return 1;
        var rest = 1 - p;
        return 1 - rest * rest * rest;
    }

    /// <summary><paramref name="transform"/> (the text's D018 transform) with the motion of <paramref name="text"/> on
    /// timeline frame <paramref name="frame"/> of a clip covering <c>[startFrame, endFrame)</c> with effective fades
    /// <paramref name="fadeIn"/> / <paramref name="fadeOut"/> frames, on <paramref name="canvas"/>.</summary>
    public static Affine2D Apply(Affine2D transform, TextProperties text, FrameSize canvas, long frame, long startFrame, long endFrame,
        long fadeIn, long fadeOut)
    {
        var result = transform;
        if (text.MotionIn.Kind != TextMotionKind.None)
            result = Move(result, text.MotionIn, Progress(frame - startFrame, fadeIn), canvas);
        if (text.MotionOut.Kind != TextMotionKind.None)
            result = Move(result, text.MotionOut, Progress(endFrame - 1 - frame, fadeOut), canvas);
        return result;
    }

    private static Affine2D Move(Affine2D transform, TextMotion motion, double progress, FrameSize canvas)
    {
        if (progress >= 1) return transform;
        switch (motion.Kind)
        {
            case TextMotionKind.Slide:
                var d = (1 - progress) * SlideFactor * Math.Min(canvas.Width, canvas.Height);
                var (dx, dy) = motion.Side switch
                {
                    TextMotionSide.Top => (0.0, -d),
                    TextMotionSide.Left => (-d, 0.0),
                    TextMotionSide.Right => (d, 0.0),
                    _ => (0.0, d)
                };
                return Affine2D.Translation(dx, dy).Compose(transform);
            case TextMotionKind.Zoom:
                return transform.Compose(Affine2D.Scaling(ZoomFrom + (1 - ZoomFrom) * progress));
            default:
                return transform;
        }
    }
}
