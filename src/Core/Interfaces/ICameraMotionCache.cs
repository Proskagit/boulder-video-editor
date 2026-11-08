using AiVideoEditor.Core.Motion;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Hold tracking (R2 → E, D4): the analysed camera-motion tracks kept between sessions, like the thumbnails — derived data,
/// never part of the project. A track is stored under a key that names everything it depends on: the video's content
/// (<see cref="Fingerprint"/>), the analysis request and <see cref="CameraMotionSampler.AnalysisVersion"/>. A missing,
/// damaged or stale entry is simply not found, and the track is analysed again.
/// </summary>
public interface ICameraMotionCache
{
    /// <summary>The content identity of a video file — its size, last write time and a hash of its head and tail — or null
    /// when it cannot be read. Reads at most a few megabytes; not for the UI thread.</summary>
    string? Fingerprint(string filePath);

    /// <summary>The track stored under <paramref name="key"/>, or null (none, damaged or written for another key).</summary>
    CameraMotionTrack? TryLoad(string key);

    /// <summary>Stores <paramref name="track"/> under <paramref name="key"/>; a failure is logged, never thrown.</summary>
    void Save(string key, CameraMotionTrack track);
}
