using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// What a timeline clip's waveform shows (D024 Step 9.5, PO-W1 / PO-W2): the asset's <see cref="Waveform"/> (source
/// time), the clip's timing on the timeline and in the source (with its speed, D022), its volume, whether it is silent
/// (a muted clip or a clip on a muted track — drawn dimmed, same shape), and whether it takes the lower half of the clip
/// (a video clip) or all of it (an audio clip); <see cref="PixelsPerSecond"/> is the zoom it is drawn at. An immutable
/// value: any change is a new one, and the view redraws.
/// </summary>
public sealed record ClipWaveform(
    Waveform Data, MediaTime TimelineStart, MediaTime TimelineEnd, MediaTime SourceIn, ClipSpeed Speed,
    double Volume, bool IsMuted, bool LowerHalf, double PixelsPerSecond);

/// <summary>
/// The display rule of timeline waveforms (D024 Step 9.5), independent of drawing: a clip-local pixel column covers
/// timeline samples <c>[⌊c · 48000 / pps⌋, ⌊(c + 1) · 48000 / pps⌋)</c> from the clip's first sample; they map to the
/// source by the clip's audio placement — the one rule of playback and export (<see cref="AudioPlacement"/>, D013 /
/// D022), so a trimmed clip shows exactly its source range and a clip at another speed its source range stretched or
/// compressed onto its length. The column shows the largest peak of that source range (<see cref="Waveform.MaxPeak"/>,
/// <c>max(|L|, |R|)</c>), scaled linearly by the clip's volume: a full-scale peak reaches the full height at the maximum
/// volume (200 %, <see cref="ClipPropertyLimits.MaxVolume"/>) and half of it at 100 %.
/// </summary>
public static class WaveformLayout
{
    /// <summary>The share of the waveform's height (0–1, of the half above or below the centre line) a peak takes at
    /// <paramref name="volume"/>: <c>peak / 255 · volume / 2</c>, capped at 1.</summary>
    public static double Fraction(byte peak, double volume) =>
        Math.Clamp(peak / (double)Waveform.FullScale * volume / ClipPropertyLimits.MaxVolume, 0, 1);

    /// <summary>The number of pixel columns the clip covers at its zoom.</summary>
    public static int ColumnCount(ClipWaveform clip) =>
        (int)Math.Ceiling(TimelineCoordinateMapper.TimeToX(clip.TimelineEnd - clip.TimelineStart, clip.PixelsPerSecond));

    /// <summary>The height fraction of clip-local column <paramref name="column"/>; 0 outside the clip or where the
    /// source has no sound.</summary>
    public static double Column(ClipWaveform clip, int column)
    {
        if (column < 0 || clip.PixelsPerSecond <= 0) return 0;
        var placement = AudioPlacement.Of(clip.TimelineStart, clip.TimelineEnd, clip.SourceIn, clip.Speed);
        var from = placement.FirstSample + SamplesBefore(column, clip.PixelsPerSecond);
        if (from >= placement.EndSample) return 0;
        var to = Math.Min(placement.FirstSample + SamplesBefore(column + 1, clip.PixelsPerSecond), placement.EndSample);

        var sourceFrom = AudioTiming.NearestSample(placement.SourcePositionAt(from));
        var sourceTo = Math.Max(AudioTiming.NearestSample(placement.SourcePositionAt(to)), sourceFrom + 1);
        return Fraction(clip.Data.MaxPeak(sourceFrom, sourceTo), clip.Volume);
    }

    /// <summary>Timeline samples before clip-local x = <paramref name="column"/>.</summary>
    private static long SamplesBefore(int column, double pixelsPerSecond) =>
        (long)Math.Floor(column * (double)AudioFormat.SampleRate / pixelsPerSecond);
}
