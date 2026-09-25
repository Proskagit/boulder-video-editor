using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Export;

/// <summary>
/// The fixed export format (D023). There are no user settings for quality, size or rate in Phase 8:
/// the picture is the project canvas at the project frame rate (<see cref="ExportOutput"/>), the
/// quality is constant. Encoder-specific values are named after libx264, which the FFmpeg encoder
/// backend uses; they are part of the product contract, not a tuning knob.
/// </summary>
public static class ExportFormat
{
    public const ExportContainer Container = ExportContainer.Mp4;
    public const ExportVideoCodec VideoCodec = ExportVideoCodec.H264;
    public const ExportAudioCodec AudioCodec = ExportAudioCodec.Aac;

    /// <summary>File extension of the output (MP4).</summary>
    public const string FileExtension = ".mp4";

    /// <summary>Constant quality (libx264 <c>-crf</c>); no bitrate target.</summary>
    public const int VideoCrf = 18;

    /// <summary>Encoder speed/efficiency trade-off (libx264 <c>-preset</c>).</summary>
    public const string VideoPreset = "medium";

    /// <summary>The audio track is always present (silence when the timeline has no audio):
    /// AAC-LC at the playback format, 48 kHz stereo (D013).</summary>
    public const int AudioSampleRate = AudioFormat.SampleRate;
    public const int AudioChannels = AudioFormat.Channels;
    public const int AudioBitrateBps = 192_000;
}

/// <summary>
/// What an export of <see cref="PlaybackSnapshot"/> produces (D023): the canvas size, the exact
/// project frame rate, <see cref="FrameCount"/> frames (timeline frame n is output frame n, rendered at
/// <c>MediaTime.FromFrame(n)</c>) and exactly as many audio samples as those frames last.
/// </summary>
public sealed record ExportOutput(FrameSize Size, FrameRate FrameRate, long FrameCount, MediaTime Duration, long AudioSampleCount)
{
    /// <summary>The output for <paramref name="snapshot"/>. The sequence duration is covered by whole
    /// frames (clip edges lie on the grid, so this is the duration itself); the audio has
    /// <c>ceil(duration · 48000 / 10⁷)</c> samples of the same duration.</summary>
    public static ExportOutput For(PlaybackSnapshot snapshot)
    {
        var rate = snapshot.FrameRate;
        var frames = Math.Max(0, snapshot.Duration.ToFrameCeiling(rate));
        var duration = MediaTime.FromFrame(frames, rate);
        return new ExportOutput(snapshot.Canvas, rate, frames, duration, AudioTiming.CeilingSample(duration));
    }
}
