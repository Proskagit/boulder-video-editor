using System.Collections.Immutable;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.UI.Rendering;

public enum DrawKind
{
    /// <summary>Part of a decoded frame (<see cref="DrawOperation.FrameSourceRect"/>) drawn into
    /// <see cref="DrawOperation.LocalRect"/>.</summary>
    Frame,
    /// <summary>Text laid out by the renderer and centred on the local origin.</summary>
    Text,
    /// <summary>A placeholder box filling <see cref="DrawOperation.LocalRect"/> with a label.</summary>
    Placeholder
}

/// <summary>
/// One thing to draw, in local coordinates: the renderer pushes <see cref="Transform"/> (local →
/// Preview control) and <see cref="Opacity"/>, then draws. Renderer-neutral apart from carrying the
/// decoded frame.
/// </summary>
public sealed record DrawOperation(
    DrawKind Kind,
    Guid ClipId,
    Affine2D Transform,
    double Opacity,
    RectD LocalRect,
    DecodedFrame? Frame = null,
    RectD? FrameSourceRect = null,
    TextProperties? Text = null,
    string? Label = null);

/// <summary>
/// What the Preview draws for one composition (Phase 7 Step 7): the layers of
/// <see cref="PlaybackFrame.Layers"/>, bottom to top, mapped from composition coordinates (the
/// project canvas, D018) to the Preview control. The chain is
/// <c>layer-local → canvas (the layer's D018 transform) → control (<see cref="CanvasToControl"/>)</c>;
/// the composition itself is never rescaled for the UI. Everything is clipped to <see cref="CanvasBounds"/>.
/// <list type="bullet">
/// <item>Frame: the decoded frame's <c>NormalizedSourceRect</c> (in the decoded frame's own pixels)
/// is drawn into the crop rectangle of the layer geometry; a layer without geometry (source size
/// unknown) is laid out from the decoded frame's size with the same D018 rule.</item>
/// <item>Text: the layer's text transform; the renderer lays out and centres the text.</item>
/// <item>Offline / Unsupported / DecodeError: a placeholder in the layer's <c>PlaceholderArea</c>.</item>
/// <item>Pending: nothing.</item>
/// </list>
/// Opacity is per layer.
/// </summary>
public sealed record CompositionDrawPlan(Affine2D CanvasToControl, RectD CanvasBounds, ImmutableArray<DrawOperation> Operations)
{
    public static readonly CompositionDrawPlan Empty =
        new(Affine2D.Identity, new RectD(0, 0, 0, 0), ImmutableArray<DrawOperation>.Empty);

    /// <summary>Uniform "contain" mapping of the canvas into a control of the given size, centred.</summary>
    public static Affine2D Viewport(FrameSize canvas, double controlWidth, double controlHeight)
    {
        var scale = Math.Min(controlWidth / canvas.Width, controlHeight / canvas.Height);
        return new Affine2D(scale, 0, 0, scale,
            (controlWidth - canvas.Width * scale) / 2, (controlHeight - canvas.Height * scale) / 2);
    }

    public static CompositionDrawPlan Build(FrameSize canvas, double controlWidth, double controlHeight, IEnumerable<LayerPicture> layers)
    {
        if (!canvas.IsValid || !(controlWidth > 0) || !(controlHeight > 0))
            return Empty;

        var viewport = Viewport(canvas, controlWidth, controlHeight);
        var bounds = new RectD(viewport.Tx, viewport.Ty, canvas.Width * viewport.A, canvas.Height * viewport.D);
        var operations = ImmutableArray.CreateBuilder<DrawOperation>();

        foreach (var picture in layers) // bottom to top
        {
            if (Operation(canvas, viewport, picture) is { } operation)
                operations.Add(operation);
        }

        return new CompositionDrawPlan(viewport, bounds, operations.ToImmutable());
    }

    private static DrawOperation? Operation(FrameSize canvas, Affine2D viewport, LayerPicture picture)
    {
        var layer = picture.Layer;
        switch (picture.State)
        {
            case LayerPictureState.Pending:
                return null;

            case LayerPictureState.Text when layer is TextLayer text:
                return new DrawOperation(DrawKind.Text, layer.ClipId, viewport.Compose(text.Transform), layer.Opacity,
                    new RectD(0, 0, 0, 0), Text: text.Text);

            case LayerPictureState.Frame when layer is PictureLayer pictureLayer && picture.Frame is { } frame:
            {
                var geometry = pictureLayer.Geometry
                    ?? CompositionMath.Layout(canvas, new FrameSize(frame.Width, frame.Height), pictureLayer.Span.Visual);
                var n = geometry.NormalizedSourceRect;
                return new DrawOperation(DrawKind.Frame, layer.ClipId, viewport.Compose(geometry.Transform), layer.Opacity,
                    new RectD(0, 0, geometry.SourceRect.Width, geometry.SourceRect.Height),
                    frame, new RectD(n.X * frame.Width, n.Y * frame.Height, n.Width * frame.Width, n.Height * frame.Height));
            }

            case LayerPictureState.Offline or LayerPictureState.Unsupported or LayerPictureState.DecodeError:
            {
                var (transform, width, height) = picture.PlaceholderArea(canvas);
                return new DrawOperation(DrawKind.Placeholder, layer.ClipId, viewport.Compose(transform), layer.Opacity,
                    new RectD(0, 0, width, height), Label: PlaceholderLabel(picture.State));
            }

            default:
                return null;
        }
    }

    public static string PlaceholderLabel(LayerPictureState state) => state switch
    {
        LayerPictureState.Offline => "Media offline",
        LayerPictureState.Unsupported => "Unsupported clip",
        LayerPictureState.DecodeError => "Cannot decode media",
        _ => ""
    };
}
