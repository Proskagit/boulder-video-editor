using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Export;

/// <summary>
/// D077 §2: the size each video clip of an export is decoded at — no larger than it is ever drawn on the output. A 4K
/// source in a Full HD export was decoded, carried and copied at 4K and only the painter shrank it (D060 §3); decoded at
/// its drawn size it costs a quarter of the memory traffic (measured ×2.2 «Быстрый», ×1.6 «Средний»,
/// <c>docs/EXPORT_PERFORMANCE.md</c> §9.3). The draw plan does not depend on the decoded size (the crop is normalized, the
/// local rectangle is in source pixels — the Preview draws its reduced frames the same way), so only the pixels the painter
/// samples change.
/// <para>
/// The rule: for every output frame the clip is shown on, its picture's transform onto the output (the canvas-to-target
/// stretch, the motion, the keyframed geometry — <see cref="CompositionDrawPlan.Operation"/> itself) gives how many output
/// pixels one source pixel covers at most (the largest singular value); the clip is decoded at its source size times the
/// largest such factor over all its frames, rounded up, never above the source size. Kept at full resolution: still images
/// (decoded once), clips with a Follow camera (its framing is evaluated per source frame), clips whose source size is unknown.
/// </para>
/// </summary>
public static class ExportDecodeSizes
{
    /// <summary>Below this many output pixels per source pixel being saved, the source is decoded as it is (a scaler for a
    /// few per cent less is not worth its own resampling).</summary>
    public const double MinimumReduction = 0.95;

    /// <summary>The decode bound per clip id for the clips to decode smaller; clips not listed are decoded at full
    /// resolution.</summary>
    public static IReadOnlyDictionary<Guid, FrameSize> For(PlaybackSnapshot snapshot, ExportOutput output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(output);
        var canvas = snapshot.Canvas;
        var target = output.Size.IsValid ? output.Size : canvas;
        var canvasToTarget = target == canvas ? Affine2D.Identity : CompositionDrawPlan.Stretch(canvas, target);

        var largest = new Dictionary<Guid, (FrameSize Source, double Scale)>();
        var full = new HashSet<Guid>();
        for (var n = 0L; n < output.FrameCount; n++)
        {
            if ((n & 255) == 0) ct.ThrowIfCancellationRequested();
            var time = MediaTime.FromFrame(output.FirstFrame + n, snapshot.FrameRate);
            foreach (var layer in snapshot.LayersAt(time))
            {
                if (layer is not PictureLayer picture || full.Contains(picture.ClipId)) continue;
                if (picture.Span.Status != SpanStatus.Video || picture.Span.Follow is not null ||
                    picture.Span.SourceSize is not { IsValid: true } source || picture.Geometry is not { } geometry)
                {
                    full.Add(picture.ClipId);
                    continue;
                }
                // CompositionDrawPlan.Operation's transform of a picture without a Follow camera: its local units are source
                // pixels (the local rectangle is the crop in source pixels), whatever size the frame is decoded at.
                var transform = canvasToTarget.Compose(picture.Motion.Apply(canvas, geometry.Transform));
                var scale = LargestScale(transform);
                largest[picture.ClipId] = (source, Math.Max(largest.TryGetValue(picture.ClipId, out var seen) ? seen.Scale : 0, scale));
            }
        }

        var sizes = new Dictionary<Guid, FrameSize>();
        foreach (var (clip, (source, scale)) in largest)
        {
            if (full.Contains(clip) || !double.IsFinite(scale) || scale <= 0 || scale >= MinimumReduction) continue;
            sizes[clip] = new FrameSize(Math.Max(2, (int)Math.Ceiling(source.Width * scale)), Math.Max(2, (int)Math.Ceiling(source.Height * scale)));
        }
        return sizes;
    }

    /// <summary>The largest factor <paramref name="t"/> stretches a length by (its largest singular value).</summary>
    public static double LargestScale(Affine2D t)
    {
        var s = t.A * t.A + t.B * t.B + t.C * t.C + t.D * t.D;
        var det = t.A * t.D - t.B * t.C;
        return Math.Sqrt((s + Math.Sqrt(Math.Max(0, s * s - 4 * det * det))) / 2);
    }
}
