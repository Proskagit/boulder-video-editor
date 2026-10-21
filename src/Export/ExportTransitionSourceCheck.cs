using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Export;

/// <summary>
/// The check before the first frame (D025 §4 as changed in Phase 28, D044): every Dissolve and Push whose zone meets the
/// exported frames must have, in the media as they are now, the source frames its zone needs beyond its clips. The
/// metadata are not trusted for it — a relinked or replaced file, a video stream shorter than its container, a file cut
/// short: the decoder is asked for the farthest such frame on each side (A's last one after the cut, B's first one
/// before it, within the exported range) by the export's own reader, which fails on a frame it could only hold. A
/// transition that fails ends the export before the encoder starts: nothing is written. While rendering, the same
/// reader checks every zone frame again (a decoder that runs out later than here).
/// </summary>
internal static class ExportTransitionSourceCheck
{
    public static async Task RunAsync(PlaybackSnapshot snapshot, ExportOutput output, IVideoDecoder decoder, ExportDecodeSettings settings,
        CancellationToken ct)
    {
        var rate = snapshot.FrameRate;
        var first = output.FirstFrame;
        var end = output.FirstFrame + output.FrameCount;
        var zones = ExportFrameSource.ZonesByClip(snapshot);

        foreach (var layer in snapshot.VideoLayers)
        {
            foreach (var span in layer.Spans)
            {
                if (span.Status != SpanStatus.Video || !zones.TryGetValue(span.ClipId, out var zonesOf) ||
                    !snapshot.Assets.TryGetValue(span.AssetId, out var asset))
                    continue;
                var ownFirst = span.TimelineStart.ToFrameCeiling(rate);
                var ownEnd = span.TimelineEnd.ToFrameCeiling(rate);

                // B: its frames before its start — the earliest one exported.
                var before = Math.Max(span.ShownStart.ToFrameCeiling(rate), first);
                if (zonesOf.Before is not null && before < Math.Min(ownFirst, end))
                    await ProbeAsync(span, asset, before);
                // A: its frames after its end — the latest one exported.
                var after = Math.Min(span.ShownEnd.ToFrameCeiling(rate), end) - 1;
                if (zonesOf.After is not null && after >= Math.Max(ownEnd, first))
                    await ProbeAsync(span, asset, after);

                async Task ProbeAsync(PictureSpan s, PlaybackAsset a, long frame)
                {
                    await using var reader = new ExportPictureReader(s, a, rate, decoder, settings, zonesOf);
                    try
                    {
                        await reader.GetAsync(frame, ct);
                    }
                    catch (ExportException ex) when (ex.MissingSource is { } missing)
                    {
                        throw new ExportException(ex.Failure, ex.Message, ex) { MissingSource = missing with { BeforeRendering = true } };
                    }
                }
            }
        }
    }
}
