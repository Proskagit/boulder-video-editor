using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Playback;

/// <summary>
/// The fade rule (D025 §2), shared by the Preview and the export. A clip covers timeline frames <c>[s, e)</c>,
/// <c>N = e − s</c>; its effective fade lengths are <c>Fin = min(FadeIn.ToNearestFrame(rate), N)</c> and likewise
/// <c>Fout</c> — 0 on an edge that has a dissolve (PO-8). A ramp of <c>F</c> steps is
/// <c>ramp(k, F) = (k + 1) / (F + 1)</c> for <c>0 ≤ k &lt; F</c>, 1 otherwise; the picture of clip frame
/// <c>i = n − s</c> is multiplied by <c>ramp(i, Fin) · ramp(N − 1 − i, Fout)</c>, and the clip's sound by the same
/// ramps over the samples of those frames (<see cref="AudioFadeEnvelope"/>).
/// </summary>
public static class FadeRule
{
    /// <summary><c>(k + 1) / (F + 1)</c> for <c>0 ≤ k &lt; F</c>, else exactly 1.</summary>
    public static double Ramp(long k, long steps) => k >= 0 && k < steps ? (double)(k + 1) / (steps + 1) : 1.0;

    /// <summary>The effective fade lengths of <paramref name="clip"/> in frames: each stored fade as whole frames,
    /// clamped to the clip's length; an edge with a dissolve (<paramref name="fadeInSuppressed"/> /
    /// <paramref name="fadeOutSuppressed"/>, PO-8) has none.</summary>
    public static (long In, long Out) EffectiveFrames(Clip clip, FrameRate rate, bool fadeInSuppressed = false, bool fadeOutSuppressed = false)
    {
        var frames = TransitionRules.ClipFrames(clip, rate);
        var fadeIn = fadeInSuppressed ? 0 : Math.Clamp(TransitionRules.Frames(clip.FadeIn, rate), 0, frames);
        var fadeOut = fadeOutSuppressed ? 0 : Math.Clamp(TransitionRules.Frames(clip.FadeOut, rate), 0, frames);
        return (fadeIn, fadeOut);
    }

    /// <summary>The picture factor of timeline frame <paramref name="frame"/> of a clip covering frames
    /// <c>[startFrame, endFrame)</c> with effective fades <paramref name="fadeIn"/> / <paramref name="fadeOut"/>:
    /// exactly 1 outside both ramps.</summary>
    public static double PictureFactor(long frame, long startFrame, long endFrame, long fadeIn, long fadeOut)
    {
        var i = frame - startFrame;
        return Ramp(i, fadeIn) * Ramp(endFrame - 1 - frame, fadeOut);
    }

    /// <summary>The frames where a clip's picture factor reaches or leaves 1 — <c>s + Fin</c> and <c>e − Fout</c>
    /// as timeline times (only those strictly inside the clip). Visibility of the layers below can change there.</summary>
    public static IEnumerable<MediaTime> RampEdges(MediaTime start, MediaTime end, long fadeIn, long fadeOut, FrameRate rate)
    {
        if (fadeIn == 0 && fadeOut == 0) yield break;
        var startFrame = start.ToFrameFloor(rate);
        var endFrame = end.ToFrameFloor(rate);
        if (fadeIn > 0 && startFrame + fadeIn < endFrame) yield return MediaTime.FromFrame(startFrame + fadeIn, rate);
        if (fadeOut > 0 && endFrame - fadeOut > startFrame) yield return MediaTime.FromFrame(endFrame - fadeOut, rate);
    }
}

/// <summary>
/// The fade of one clip's sound in timeline samples (D025 §2): the clip owns <c>[FirstSample, EndSample)</c>
/// (<see cref="AudioPlacement"/>); the fade in owns <c>[FirstSample, FadeInEnd)</c> with
/// <c>FadeInEnd = ⌈FromFrame(s + Fin) · 48000 / 10⁷⌉</c>, the fade out <c>[FadeOutStart, EndSample)</c> with
/// <c>FadeOutStart = ⌈FromFrame(e − Fout) · 48000 / 10⁷⌉</c>. Sample <c>k</c> gets
/// <c>ramp(k − FirstSample, FadeInEnd − FirstSample) · ramp(EndSample − 1 − k, EndSample − FadeOutStart)</c>.
/// A crossfade (D032 R9) adds its ramps over its zone's samples <c>[Z0, Z1)</c>, <c>M = Z1 − Z0</c>: the clip fading in
/// (B) gets <c>ramp(k − Z0, M) = (k − Z0 + 1)/(M + 1)</c>, the clip fading out (A) <c>1 − ramp(k − Z0, M)</c> — linear, the
/// two factors of a sample summing to 1 (no equal-power curve). The default value fades nothing.
/// </summary>
public readonly record struct AudioFadeEnvelope(long FirstSample, long FadeInEnd, long FadeOutStart, long EndSample)
{
    /// <summary>No fade: every gain is exactly 1.</summary>
    public static readonly AudioFadeEnvelope None = default;

    /// <summary>The crossfade-in zone's samples <c>[Z0, Z1)</c> (empty: none).</summary>
    public long CrossfadeInStart { get; init; }
    public long CrossfadeInEnd { get; init; }

    /// <summary>The crossfade-out zone's samples (empty: none).</summary>
    public long CrossfadeOutStart { get; init; }
    public long CrossfadeOutEnd { get; init; }

    public bool HasFade => FadeInEnd > FirstSample || FadeOutStart < EndSample
        || CrossfadeInEnd > CrossfadeInStart || CrossfadeOutEnd > CrossfadeOutStart;

    /// <summary>The envelope of <paramref name="span"/> at the project rate <paramref name="rate"/>.</summary>
    public static AudioFadeEnvelope Of(AudioSpan span, FrameRate rate)
    {
        if (span.FadeInFrames == 0 && span.FadeOutFrames == 0 && span.CrossfadeIn is null && span.CrossfadeOut is null) return None;
        var startFrame = span.TimelineStart.ToFrameFloor(rate);
        var endFrame = span.TimelineEnd.ToFrameFloor(rate);
        var first = AudioTiming.CeilingSample(span.TimelineStart);
        var end = AudioTiming.CeilingSample(span.TimelineEnd);
        return new AudioFadeEnvelope(first,
            AudioTiming.CeilingSample(MediaTime.FromFrame(startFrame + span.FadeInFrames, rate)),
            AudioTiming.CeilingSample(MediaTime.FromFrame(endFrame - span.FadeOutFrames, rate)),
            end)
        {
            CrossfadeInStart = span.CrossfadeIn is { } zoneIn ? AudioTiming.CeilingSample(zoneIn.Start) : 0,
            CrossfadeInEnd = span.CrossfadeIn is { } zoneInEnd ? AudioTiming.CeilingSample(zoneInEnd.End) : 0,
            CrossfadeOutStart = span.CrossfadeOut is { } zoneOut ? AudioTiming.CeilingSample(zoneOut.Start) : 0,
            CrossfadeOutEnd = span.CrossfadeOut is { } zoneOutEnd ? AudioTiming.CeilingSample(zoneOutEnd.End) : 0
        };
    }

    /// <summary>The gain factor of timeline sample <paramref name="sample"/> (1 outside the ramps). A sample before the
    /// clip's first or after its last (a crossfade's extension) is outside its fades.</summary>
    public double Gain(long sample)
    {
        var gain = FadeRule.Ramp(sample - FirstSample, FadeInEnd - FirstSample) * FadeRule.Ramp(EndSample - 1 - sample, EndSample - FadeOutStart);
        if (sample >= CrossfadeInStart && sample < CrossfadeInEnd)
            gain *= FadeRule.Ramp(sample - CrossfadeInStart, CrossfadeInEnd - CrossfadeInStart);
        if (sample >= CrossfadeOutStart && sample < CrossfadeOutEnd)
            gain *= 1 - FadeRule.Ramp(sample - CrossfadeOutStart, CrossfadeOutEnd - CrossfadeOutStart);
        return gain;
    }

    /// <summary>True when a sample in <c>[from, until)</c> lies in a ramp.</summary>
    public bool Affects(long from, long until) =>
        HasFade && ((from < FadeInEnd && until > FirstSample) || (until > FadeOutStart && from < EndSample)
                    || (from < CrossfadeInEnd && until > CrossfadeInStart) || (from < CrossfadeOutEnd && until > CrossfadeOutStart));
}
