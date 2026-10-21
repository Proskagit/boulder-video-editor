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
