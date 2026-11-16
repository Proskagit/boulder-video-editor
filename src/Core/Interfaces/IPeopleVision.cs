using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>Why the people models cannot be used — a message for the user names what is wrong and where.</summary>
public sealed class PeopleModelException(string message, bool requiresGpu = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The segmentation needs a DirectML GPU and none is usable (D061 §5): Follow still works, Occlusion does not.</summary>
    public bool RequiresGpu { get; } = requiresGpu;
}

/// <summary>A person to start segmenting on the current frame: its id (the track's) and its box there.</summary>
public readonly record struct SegmenterPrompt(int PersonId, NormalizedBox Box);

/// <summary>One person's segmentation on one frame: its coverage on the mask grid (0..255, row by row) and whether the model
/// sees the person on this frame at all (<see cref="Visible"/> false: an empty mask — hidden or out of the frame).</summary>
public sealed record SegmentedPerson(int PersonId, bool Visible, byte[] Coverage);

/// <summary>
/// The local people models (D061): a person detector with a pose for the torso centre (Follow, the occlusion's prompts)
/// and a video segmenter with memory (Occlusion). Implemented outside Core (the Vision project, ONNX Runtime with DirectML
/// and a CPU fallback for the detector). Never downloads anything; every model file is pinned by size and SHA-256.
/// </summary>
public interface IPeopleVision
{
    /// <summary>The model set's identity — part of every cached analysis's key.</summary>
    string ModelId { get; }

    /// <summary>The device the detector runs on (after <see cref="Detect"/> loaded it).</summary>
    InferenceDevice DetectorDevice { get; }

    /// <summary>The people on one frame (BGRA, any size; the display-oriented picture): boxes in normalized coordinates, the
    /// detector's score, the torso centre, the appearance. Not for the UI thread. Throws <see cref="PeopleModelException"/>
    /// when a model is missing or damaged.</summary>
    IReadOnlyList<PersonDetection> Detect(DecodedFrame frame, CancellationToken cancellationToken);

    /// <summary>A new video segmentation (its own memory per person). Throws <see cref="PeopleModelException"/> (with
    /// <see cref="PeopleModelException.RequiresGpu"/>) without a usable DirectML GPU — on the CPU it would take about a second
    /// per frame.</summary>
    IPersonSegmenter CreateSegmenter(CancellationToken cancellationToken);
}

/// <summary>A video segmentation in progress (D061 §4): frames in time order; each person is started with a box and
/// followed by the model's memory until stopped. Holds GPU memory — dispose it.</summary>
public interface IPersonSegmenter : IDisposable
{
    /// <summary>Segments one frame: starts <paramref name="start"/> on it, forgets <paramref name="stop"/>, returns every
    /// followed person's coverage on a <paramref name="gridWidth"/> × <paramref name="gridHeight"/> grid — separate masks,
    /// a pixel belonging to at most one person (the most confident one, the one in front).</summary>
    IReadOnlyList<SegmentedPerson> Step(DecodedFrame frame, IReadOnlyList<SegmenterPrompt> start, IReadOnlyCollection<int> stop,
        int gridWidth, int gridHeight, CancellationToken cancellationToken);

    /// <summary>D061 amendment 1: prepares <paramref name="frame"/> for its <see cref="Step"/> ahead, on another thread (the
    /// image's preprocessing), so the CPU's work overlaps the model's; the result is the same. Nothing by default.</summary>
    void Prepare(DecodedFrame frame)
    {
    }
}

/// <summary>
/// The people analyses kept between sessions (D061 §5), like the camera-motion tracks: derived data under a key naming the
/// video's content, the model and the analysis version; a missing, damaged or stale entry is simply not found.
/// </summary>
public interface IPeopleAnalysisCache
{
    /// <summary>The content identity of a video file (size, last write time, a hash of its head and tail), or null.</summary>
    string? Fingerprint(string filePath);

    PersonTracks? TryLoadTracks(string key);
    void SaveTracks(string key, PersonTracks tracks);

    PersonMaskTrack? TryLoadMasks(string key);
    void SaveMasks(string key, PersonMaskTrack masks);
}
