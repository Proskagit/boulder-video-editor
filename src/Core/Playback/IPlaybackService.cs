using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;

namespace AiVideoEditor.Core.Playback;

public enum PlaybackState
{
    Paused,
    Playing
}

/// <summary>What the preview shows for a timeline frame.</summary>
public enum PictureKind
{
    /// <summary>No clip at this time (gap) or nothing to play.</summary>
    Black,
    /// <summary>A decoded frame (<see cref="PreviewPicture.Frame"/>).</summary>
    Frame,
    /// <summary>The asset is absent or unavailable (e.g. its file is missing; relink is not implemented).</summary>
    Offline,
    /// <summary>The clip cannot be played (e.g. Speed ≠ 1).</summary>
    Unsupported,
    /// <summary>The decoder failed on this clip (ffmpeg, pipe or decode error).</summary>
    DecodeError
}

/// <summary>A picture for the preview. Offline / Unsupported / DecodeError may look the same in
/// the UI, but the reason is kept distinct.</summary>
public sealed record PreviewPicture(PictureKind Kind, DecodedFrame? Frame = null, Guid? ClipId = null, string? Message = null)
{
    public static readonly PreviewPicture Black = new(PictureKind.Black);
}

/// <summary>State of one composition layer at the current frame.</summary>
public enum LayerPictureState
{
    /// <summary>A decoded frame (<see cref="LayerPicture.Frame"/>); possibly an earlier one while the
    /// decoder is behind (<see cref="LayerPicture.IsCurrent"/> false, D012).</summary>
    Frame,
    /// <summary>A text layer: nothing to decode, the renderer lays the text out.</summary>
    Text,
    /// <summary>The layer's reader has produced nothing yet (e.g. a layer just uncovered without a
    /// seek). The layer is simply not drawn until its first frame arrives.</summary>
    Pending,
    /// <summary>The media is absent (missing file, not in the project, no metadata).</summary>
    Offline,
    /// <summary>The clip cannot be played (e.g. Speed ≠ 1, wrong media kind).</summary>
    Unsupported,
    /// <summary>The decoder failed on this clip.</summary>
    DecodeError
}

/// <summary>
/// One visible layer of the composition at the current frame (bottom to top in
/// <see cref="PlaybackFrame.Layers"/>): the layer with its geometry (D018) and what there is to draw.
/// </summary>
public sealed record LayerPicture(CompositionLayer Layer, LayerPictureState State, DecodedFrame? Frame = null,
    bool IsCurrent = true, string? Message = null)
{
    public bool IsPlaceholder => State is LayerPictureState.Offline or LayerPictureState.Unsupported or LayerPictureState.DecodeError;

    /// <summary>
    /// Where a placeholder for this layer is drawn: the clip's own picture area — the crop-local
    /// rectangle through the layer transform, so Position/Scale/Rotation apply — or, when the
    /// picture size is unknown, the whole canvas. A placeholder never hides the layers below it.
    /// </summary>
    public (Affine2D Transform, double Width, double Height) PlaceholderArea(FrameSize canvas) =>
        Layer is PictureLayer { Geometry: { } g }
            ? (g.Transform, g.SourceRect.Width, g.SourceRect.Height)
            : (Affine2D.Identity, canvas.Width, canvas.Height);
}

/// <summary>
/// Result of one <see cref="IPlaybackService.Update"/>: the clock position, the timeline frame
/// it falls in, and what to show.
/// <para>
/// <see cref="Layers"/> is the composition (D018), bottom to top, with each layer's state.
/// <see cref="Picture"/> / <see cref="IsPictureCurrent"/> are the compatibility view used by the
/// single-picture Preview until it renders layers (Phase 7 Step 7): the topmost picture layer.
/// <see cref="Picture"/> is null while nothing has been decoded for the current seek yet
/// (<see cref="IsBuffering"/>); <see cref="IsPictureCurrent"/> is false when the decoder is behind
/// and <see cref="Picture"/> is the most recent earlier picture (a late frame).
/// </para>
/// </summary>
public readonly record struct PlaybackFrame(
    MediaTime Position, long TimelineFrame, PlaybackState State, bool IsBuffering, PreviewPicture? Picture, bool IsPictureCurrent)
{
    private readonly ImmutableArray<LayerPicture> _layers;

    /// <summary>Visible layers, bottom to top (empty while there is no snapshot).</summary>
    public ImmutableArray<LayerPicture> Layers
    {
        get => _layers.IsDefault ? ImmutableArray<LayerPicture>.Empty : _layers;
        init => _layers = value;
    }

    /// <summary>The project canvas the layers are laid out on.</summary>
    public FrameSize Canvas { get; init; }
}

/// <summary>
/// Timeline playback (D010). Polling model: the UI calls <see cref="Update"/> on every render
/// tick and receives position, state and picture; background decoding never raises UI events.
/// All members must be called from one thread (the UI thread).
/// <para>
/// Source of truth: while playing, the clock (anchor + elapsed); while paused, the last
/// seek position. Seeking re-anchors the clock at the requested position; decoding latency is
/// never added to the position (the clock keeps running while <see cref="IsBuffering"/>).
/// </para>
/// </summary>
public interface IPlaybackService : IAsyncDisposable
{
    PlaybackState State { get; }

    /// <summary>True from a seek (or snapshot update) until the picture for its position is ready.</summary>
    bool IsBuffering { get; }

    /// <summary>False when no decoder backend is available (e.g. ffmpeg not found).</summary>
    bool IsAvailable { get; }

    /// <summary>False when playback runs without sound (no audio device/decoder, or the device
    /// failed); the Stopwatch drives the clock then.</summary>
    bool IsAudioAvailable { get; }

    MediaTime Position { get; }

    MediaTime Duration { get; }

    /// <summary>Raised synchronously on the calling thread when <see cref="State"/> changes
    /// (from Play/Pause/Stop or from <see cref="Update"/> reaching the end).</summary>
    event EventHandler? StateChanged;

    /// <summary>Replaces the timeline copy. Older or equal versions are ignored. Keeps the
    /// state; resyncs decoding at the current position.</summary>
    void UpdateSnapshot(PlaybackSnapshot snapshot);

    /// <summary>Starts playing; at the end of the sequence starts again from 0.</summary>
    void Play();

    void Pause();

    /// <summary>Pause + Seek(0).</summary>
    void Stop();

    /// <summary>Moves to <paramref name="position"/> (clamped to [0, Duration]). Completes with true
    /// when the picture for it is ready, false if a later seek or snapshot superseded it.</summary>
    Task<bool> SeekAsync(MediaTime position, CancellationToken ct = default);

    /// <summary>Advances presentation: detects the end (→ Paused at Duration) and returns what
    /// to show now.</summary>
    PlaybackFrame Update();
}
