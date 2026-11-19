using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// The fixed curves of the library's transitions on the frames of a zone (D044 §2). A zone of <c>F</c> frames has
/// <c>hB = ⌊F/2⌋</c> frames of A before the cut and <c>hA = ⌈F/2⌉</c> of B after it; <c>j</c> is the zone frame
/// (<c>0 ≤ j &lt; F</c>). The Dissolve keeps D025's ramp (<see cref="FadeRule.Ramp"/>).
/// </summary>
public static class TransitionPresentation
{
    /// <summary>Zoom's peak scale (D044 §2, accepted in Step 28.1).</summary>
    public const double ZoomPeak = 1.25;

    /// <summary>The colour of a fill transition: black for Dip to black, white for Flash.</summary>
    public static string FillColor(string typeId) => typeId == TransitionRules.Flash ? "#FFFFFF" : "#000000";

    /// <summary>True when zone frame <paramref name="j"/> lies before the cut — A's own frame for the types without
    /// handles.</summary>
    public static bool BeforeCut(long j, long frames) => j < frames / 2;

    /// <summary>Dip to black / Flash: the fill's opacity — <c>(j + 1)/(hB + 1)</c> on A's frames, <c>1 − i/hA</c> on B's
    /// (<c>i = j − hB</c>): exactly one frame fully covered, the cut frame (B's first).</summary>
    public static double FillOpacity(long j, long frames)
    {
        var (hB, hA) = TransitionRules.Zone(frames);
        return j < hB ? (j + 1) / (double)(hB + 1) : 1.0 - (j - hB) / (double)hA;
    }

    /// <summary>Zoom: the scale about the canvas centre — <c>1 + (K − 1)·t²</c> with <c>t = (j + ½)/hB</c> on A's frames,
    /// <c>1 + (K − 1)·(1 − t)²</c> with <c>t = (i + ½)/hA</c> on B's: one bump peaking at the cut.</summary>
    public static double ZoomScale(long j, long frames)
    {
        var (hB, hA) = TransitionRules.Zone(frames);
        if (j < hB)
        {
            var t = (j + 0.5) / hB;
            return 1 + (ZoomPeak - 1) * t * t;
        }
        var u = 1 - (j - hB + 0.5) / hA;
        return 1 + (ZoomPeak - 1) * u * u;
    }

    // --- D069 §8 -------------------------------------------------------------------------------------------------------

    /// <summary>Размытие: the peak blur radius, a fraction of the picture's short side.</summary>
    public const double BlurPeak = 0.04;

    /// <summary>Отдаление: the smallest scale (at the cut).</summary>
    public const double ZoomOutLow = 0.6;

    /// <summary>Вращение: the turn at the cut and the enlargement that keeps the corners covered.</summary>
    public const double SpinDegrees = 90;
    public const double SpinGrow = 0.8;

    /// <summary>Смахивание: the peak motion blur, a fraction of the picture's short side.</summary>
    public const double WhipBlurPeak = 0.06;

    /// <summary>The one bump of the own-frame types (Zoom's curve, 0…1): <c>t²</c> with <c>t = (j + ½)/hB</c> on A's frames,
    /// <c>(1 − t)²</c> with <c>t = (i + ½)/hA</c> on B's — peaking at the cut.</summary>
    public static double Bump(long j, long frames)
    {
        var (hB, hA) = TransitionRules.Zone(frames);
        if (j < hB)
        {
            var t = (j + 0.5) / hB;
            return t * t;
        }
        var u = 1 - (j - hB + 0.5) / hA;
        return u * u;
    }

    /// <summary>Отдаление: the scale about the canvas centre — <c>1 − (1 − L)·bump</c>.</summary>
    public static double ZoomOutScale(long j, long frames) => 1 - (1 - ZoomOutLow) * Bump(j, frames);

    /// <summary>Вращение: A turns clockwise into the cut, B comes out of the opposite turn; enlarged with the turn.</summary>
    public static CanvasMotion SpinMotion(long j, long frames)
    {
        var bump = Bump(j, frames);
        var angle = SpinDegrees * bump * (BeforeCut(j, frames) ? 1 : -1);
        return new CanvasMotion(1 + SpinGrow * bump, 0, 0, angle);
    }

    /// <summary>Вытеснение, Круг, Шторки: the part of the canvas B is revealed through at the eased progress
    /// (<see cref="PushProgress"/>): an edge from the direction's side, a circle from the centre up to the corners, a band from
    /// the centre to both sides.</summary>
    public static RevealShape Reveal(string typeId, long j, long frames, TransitionDirection direction, FrameSize canvas)
    {
        var e = PushProgress(j, frames);
        double w = canvas.Width, h = canvas.Height;
        switch (typeId)
        {
            case TransitionRules.Circle:
            {
                var r = e * Math.Sqrt(w * w + h * h) / 2;
                return new RevealShape(true, new RectD(w / 2 - r, h / 2 - r, 2 * r, 2 * r));
            }
            case TransitionRules.Doors:
                return new RevealShape(false, new RectD(w / 2 * (1 - e), 0, w * e, h));
            default:   // the wipe
                return new RevealShape(false, direction switch
                {
                    TransitionDirection.FromLeft => new RectD(0, 0, w * e, h),
                    TransitionDirection.FromTop => new RectD(0, 0, w, h * e),
                    TransitionDirection.FromBottom => new RectD(0, h * (1 - e), w, h * e),
                    _ => new RectD(w * (1 - e), 0, w * e, h)
                });
        }
    }

    /// <summary>Смахивание: the push's shifts on a sharper (cubic) ease and the motion blur along it, strongest midway.</summary>
    public static (CanvasMotion A, CanvasMotion B, LayerEffect Blur) WhipMotions(long j, long frames, TransitionDirection direction, FrameSize canvas)
    {
        var p = FadeRule.Ramp(j, frames);
        var e = p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;
        var (w, h) = (canvas.Width, canvas.Height);
        var (bx, by, ax, ay) = direction switch
        {
            TransitionDirection.FromLeft => (-(1 - e) * w, 0.0, e * w, 0.0),
            TransitionDirection.FromTop => (0.0, -(1 - e) * h, 0.0, e * h),
            TransitionDirection.FromBottom => (0.0, (1 - e) * h, 0.0, -e * h),
            _ => ((1 - e) * w, 0.0, -e * w, 0.0)
        };
        var axis = direction is TransitionDirection.FromTop or TransitionDirection.FromBottom ? BlurAxis.Vertical : BlurAxis.Horizontal;
        var blur = new LayerEffect(WhipBlurPeak * Math.Sin(Math.PI * p), axis, 0, 0);
        return (new CanvasMotion(1, Whole(ax), Whole(ay)), new CanvasMotion(1, Whole(bx), Whole(by)), blur);

        static double Whole(double v) => Math.Round(v, MidpointRounding.AwayFromZero) + 0.0;
    }

    /// <summary>Глитч: the strength (the bump) and, at a strength, a jerk of the picture by up to 2 % of the canvas, both
    /// chosen by the zone frame — the same frame always looks the same.</summary>
    public static (CanvasMotion Motion, LayerEffect Effect) GlitchAt(long j, long frames, FrameSize canvas)
    {
        var strength = Bump(j, frames);
        var hash = unchecked((uint)(j * 2654435761L) ^ 0x9E3779B9u);
        double Unit(int shift) => ((hash >> shift) & 0xFF) / 255.0 * 2 - 1;
        var dx = Math.Round(strength * 0.02 * canvas.Width * Unit(0), MidpointRounding.AwayFromZero) + 0.0;
        var dy = Math.Round(strength * 0.01 * canvas.Height * Unit(8), MidpointRounding.AwayFromZero) + 0.0;
        return (new CanvasMotion(1, dx, dy), new LayerEffect(0, BlurAxis.Both, strength, (int)(hash & 0x7FFFFFFF)));
    }

    /// <summary>Push: the eased progress <c>e = smoothstep((j + 1)/(F + 1))</c>.</summary>
    public static double PushProgress(long j, long frames)
    {
        var p = FadeRule.Ramp(j, frames);
        return p * p * (3 - 2 * p);
    }

    /// <summary>Push: the shifts of A and B in whole canvas pixels — B by <c>(1 − e)</c> of the canvas side from its side,
    /// A by <c>e</c> towards the other side.</summary>
    public static (CanvasMotion A, CanvasMotion B) PushMotions(long j, long frames, TransitionDirection direction, FrameSize canvas)
    {
        var e = PushProgress(j, frames);
        var (w, h) = (canvas.Width, canvas.Height);
        var (bx, by, ax, ay) = direction switch
        {
            TransitionDirection.FromLeft => (-(1 - e) * w, 0.0, e * w, 0.0),
            TransitionDirection.FromTop => (0.0, -(1 - e) * h, 0.0, e * h),
            TransitionDirection.FromBottom => (0.0, (1 - e) * h, 0.0, -e * h),
            _ => ((1 - e) * w, 0.0, -e * w, 0.0)          // from the right (the default)
        };
        return (new CanvasMotion(1, Whole(ax), Whole(ay)), new CanvasMotion(1, Whole(bx), Whole(by)));

        static double Whole(double v) => Math.Round(v, MidpointRounding.AwayFromZero) + 0.0;   // + 0.0: never −0
    }
}
