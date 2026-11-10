using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Timeline.Playback;

public sealed class PlaybackSettings
{
    /// <summary>Decoded frames kept ahead per clip.</summary>
    public int BufferFrames { get; init; } = 8;

    /// <summary>How long before a picture change the next clip starts decoding.</summary>
    public TimeSpan PrefetchWindow { get; init; } = TimeSpan.FromSeconds(1);

    public int MaxWidth { get; init; } = 1280;
    public int MaxHeight { get; init; } = 720;
    public HardwareDecoding Hardware { get; init; } = HardwareDecoding.Auto;

    /// <summary>Decoded audio kept ahead per clip.</summary>
    public TimeSpan AudioBuffer { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How far ahead of the mix position audio clips start decoding.</summary>
    public TimeSpan AudioLookahead { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>While scrubbing (D057 §7.4): how long a waiting seek leaves the previous seek's finished picture to be shown
    /// when no Update starts it first (normally the Preview's next Update does).</summary>
    public TimeSpan InterimPictureGrace { get; init; } = TimeSpan.FromMilliseconds(40);
}
