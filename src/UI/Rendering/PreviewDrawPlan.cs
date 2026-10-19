using System.Collections.Immutable;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.Rendering;

/// <summary>A Preview-only placeholder (Offline / Unsupported / DecodeError, D019): a box filling
/// <see cref="LocalRect"/> with a label. Not part of the shared composition contract — the export never
/// draws one (D023).</summary>
public sealed record PlaceholderDraw(Guid ClipId, Affine2D Transform, double Opacity, RectD LocalRect, string Label)
    : DrawOperation(ClipId, Transform, Opacity);

/// <summary>
/// What the Preview draws for <see cref="PlaybackFrame.Layers"/> (Phase 7 Step 7, D020): the shared
/// <see cref="CompositionDrawPlan"/> (Core, D018/D023) through the "contain" viewport of the control, plus
/// the Preview's own playback states on top of it:
/// <list type="bullet">
/// <item>Frame (also a late one) and Text: the shared operation, <see cref="CompositionDrawPlan.Operation"/>;</item>
/// <item>Offline / Unsupported / DecodeError: a <see cref="PlaceholderDraw"/> in the layer's <c>PlaceholderArea</c>;</item>
/// <item>Pending: nothing.</item>
/// </list>
/// </summary>
public static class PreviewDrawPlan
{
    /// <summary>The thinnest route outline the Preview draws, in screen pixels (D034 §4; the export is unaffected).</summary>
    public const double MinOutlinePixels = 1;

    public static CompositionDrawPlan Build(FrameSize canvas, double controlWidth, double controlHeight, IEnumerable<LayerPicture> layers)
    {
        if (!canvas.IsValid || !(controlWidth > 0) || !(controlHeight > 0))
            return CompositionDrawPlan.Empty;

        var viewport = CompositionDrawPlan.Viewport(canvas, controlWidth, controlHeight);
        var operations = ImmutableArray.CreateBuilder<DrawOperation>();
        foreach (var picture in layers) // bottom to top
        {
            if (Operation(canvas, viewport, picture) is { } operation)
            {
                operations.Add(operation);
                // D034: the route outline right above its frame, never on a placeholder; a line at least one screen
                // pixel wide here only (the export keeps the exact width).
                if (operation is FrameDraw && picture is { Layer: PictureLayer layer, Frame: { } frame } &&
                    CompositionDrawPlan.Outline(canvas, viewport, layer, new FrameSize(frame.Width, frame.Height), MinOutlinePixels) is { } outline)
                    operations.Add(outline);
            }
        }
        return new CompositionDrawPlan(canvas, viewport, CompositionDrawPlan.Bounds(canvas, viewport), operations.ToImmutable());
    }

    private static DrawOperation? Operation(FrameSize canvas, Affine2D viewport, LayerPicture picture)
    {
        switch (picture.State)
        {
            case LayerPictureState.Text when picture.Layer is TextLayer:
                return CompositionDrawPlan.Operation(canvas, viewport, new ResolvedLayer(picture.Layer, null));

            case LayerPictureState.Frame when picture.Layer is PictureLayer && picture.Frame is { } frame:
                return CompositionDrawPlan.Operation(canvas, viewport, new ResolvedLayer(picture.Layer, frame));

            case LayerPictureState.Offline or LayerPictureState.Unsupported or LayerPictureState.DecodeError:
            {
                var (transform, width, height) = picture.PlaceholderArea(canvas);
                return new PlaceholderDraw(picture.Layer.ClipId, viewport.Compose(transform), picture.Layer.Opacity,
                    new RectD(0, 0, width, height), PlaceholderLabel(picture.State));
            }

            default:
                return null; // Pending
        }
    }

    public static string PlaceholderLabel(LayerPictureState state) => state switch
    {
        LayerPictureState.Offline => Strings.Placeholder_Offline,
        LayerPictureState.Unsupported => Strings.Placeholder_Unsupported,
        LayerPictureState.DecodeError => Strings.Placeholder_DecodeError,
        _ => ""
    };
}
