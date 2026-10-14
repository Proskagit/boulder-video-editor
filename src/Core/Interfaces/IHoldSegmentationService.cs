using System.Collections.Immutable;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>Whether the hold model (D034 §2, "sam2.1-large-v1") can be used.</summary>
public enum HoldModelState
{
    /// <summary>The files are present, their SHA-256 match and the sessions are loaded.</summary>
    Ready,

    /// <summary>A model file is missing: only the AI hold markup is unavailable.</summary>
    Missing,

    /// <summary>A file's SHA-256 doesn't match the pinned value (damaged or another version): never loaded.</summary>
    Mismatch,

    /// <summary>The runtime could not load the model (on neither device).</summary>
    Failed
}

/// <summary>The model's state and, unless ready, a message for the user naming what is wrong and where.</summary>
public sealed record HoldModelStatus(HoldModelState State, string? Message)
{
    public bool IsReady => State == HoldModelState.Ready;
}

/// <summary>Where the model runs (D034 §3): DirectML on the GPU, or the CPU fallback.</summary>
public enum InferenceDevice
{
    Gpu,
    Cpu
}

/// <summary>
/// Local hold segmentation (D034 §2–§3): SAM 2.1 Large through ONNX Runtime — DirectML, with a CPU fallback for known
/// DirectML errors. Implemented outside Core (the Vision project); Core holds no ML dependency.
/// </summary>
public interface IHoldSegmentationService
{
    /// <summary>The device in use; switches to <see cref="InferenceDevice.Cpu"/> on a known DirectML failure.</summary>
    InferenceDevice Device { get; }

    /// <summary>Raised (on any thread) when the service falls back to the CPU.</summary>
    event EventHandler? DeviceChanged;

    /// <summary>Checks the model files and their SHA-256 (once per session) and loads the sessions. Never downloads or
    /// replaces anything.</summary>
    Task<HoldModelStatus> EnsureReadyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Encodes one frame (BGRA, the SDR picture the user sees, at its full size) for clicks on it.</summary>
    Task<IHoldFrame> PrepareFrameAsync(DecodedFrame frame, CancellationToken cancellationToken = default);
}

/// <summary>An encoded frame: clicks on it are cheap. Disposable — holds the frame's features.</summary>
public interface IHoldFrame : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>The model's three masks for the positive <paramref name="points"/> (frame pixels), in the model's order.</summary>
    Task<HoldMaskSet> PredictAsync(IReadOnlyList<PointD> points, CancellationToken cancellationToken = default);
}

/// <summary>One mask at the frame's size: one byte per pixel (1 inside, 0 outside).</summary>
public sealed class HoldMask
{
    public HoldMask(int width, int height, byte[] pixels, double score)
    {
        if (pixels.Length != width * height) throw new ArgumentException("The mask size doesn't match its pixels.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
        Score = score;
        long area = 0;
        foreach (var p in pixels) area += p;
        Area = area;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    /// <summary>The model's predicted IoU for the mask.</summary>
    public double Score { get; }

    public long Area { get; }

    public double AreaFraction => (double)Area / ((long)Width * Height);
}

/// <summary>The model's masks for one prompt, in the model's order (alternative 0, 1, 2).</summary>
public sealed record HoldMaskSet(ImmutableArray<HoldMask> Masks);
