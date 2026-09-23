using AiVideoEditor.Core.Common;

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

/// <summary>
/// Result of one <see cref="IPlaybackService.Update"/>: the clock position, the timeline frame
/// it falls in, and the picture to show. <see cref="Picture"/> is null while nothing has been
/// decoded for the current seek yet (<see cref="IsBuffering"/>); the UI decides what to show then.
/// <see cref="IsPictureCurrent"/> is false when the decoder is behind and <see cref="Picture"/>
/// is the most recent earlier picture (a late frame).
/// </summary>
public readonly record struct PlaybackFrame(
    MediaTime Position, long TimelineFrame, PlaybackState State, bool IsBuffering, PreviewPicture? Picture, bool IsPictureCurrent);

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
