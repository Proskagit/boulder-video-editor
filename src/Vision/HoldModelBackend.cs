using AiVideoEditor.Core.Interfaces;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AiVideoEditor.Vision;

/// <summary>The encoder's outputs for one frame (kept for the clicks on it).</summary>
public sealed record HoldFeatures(float[] ImageEmbed, float[] HighResFeats0, float[] HighResFeats1);

/// <summary>The decoder's outputs: <c>[count, 256, 256]</c> logits and <c>count</c> scores, in the model's order.</summary>
public sealed record HoldLogits(float[] Logits, float[] Scores, int Count);

/// <summary>
/// An error of the model backend. <see cref="DeviceError"/> marks a known DirectML failure (creating the session or a
/// run on the GPU) after which a retry on the CPU is safe; anything else is not recovered.
/// </summary>
public sealed class HoldBackendException(string message, bool deviceError, Exception? inner = null) : Exception(message, inner)
{
    public bool DeviceError { get; } = deviceError;
}

/// <summary>The two model sessions on one device. Encode and Decode take the cancellation token (ONNX Runtime's run
/// termination).</summary>
public interface IHoldModelBackend : IDisposable
{
    InferenceDevice Device { get; }
    HoldFeatures Encode(float[] image, CancellationToken cancellationToken);

    /// <param name="pointCoords">Points in the 1024 × 1024 input space, <c>[x0, y0, x1, y1, …]</c>.</param>
    HoldLogits Decode(HoldFeatures features, float[] pointCoords, CancellationToken cancellationToken);
}

/// <summary>Creates the backend for a device (the real one: ONNX Runtime sessions of the verified files).</summary>
public delegate IHoldModelBackend HoldBackendFactory(InferenceDevice device);

/// <summary>
/// The ONNX Runtime backend: the pinned encoder and decoder (D034 §2) on DirectML (adapter 0) or the CPU. On the GPU every
/// ONNX Runtime error is reported as a device error (a CPU retry is safe: the sessions are read-only and the inputs are
/// kept); on the CPU nothing is recovered.
/// </summary>
public sealed class OnnxHoldModelBackend : IHoldModelBackend
{
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;

    public OnnxHoldModelBackend(string encoderPath, string decoderPath, InferenceDevice device)
    {
        Device = device;
        try
        {
            _encoder = new InferenceSession(encoderPath, Options(device));
            _decoder = new InferenceSession(decoderPath, Options(device));
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new HoldBackendException($"The hold model could not be loaded on the {Describe(device)}: {ex.Message}", device == InferenceDevice.Gpu, ex);
        }
    }

    public InferenceDevice Device { get; }

    private static SessionOptions Options(InferenceDevice device)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (device == InferenceDevice.Gpu)
        {
            // DirectML needs these (ONNX Runtime's DirectML documentation): no memory pattern, sequential execution.
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(0);
        }
        return options;
    }

    public HoldFeatures Encode(float[] image, CancellationToken cancellationToken)
    {
        var input = new DenseTensor<float>(image, new[] { 1, 3, HoldModel.InputSize, HoldModel.InputSize });
        using var results = Run(_encoder, new[] { NamedOnnxValue.CreateFromTensor("image", input) }, cancellationToken);
        float[] Out(string name) => results.First(r => r.Name == name).AsTensor<float>().ToArray();
        return new HoldFeatures(Out("image_embed"), Out("high_res_feats_0"), Out("high_res_feats_1"));
    }

    public HoldLogits Decode(HoldFeatures features, float[] pointCoords, CancellationToken cancellationToken)
    {
        var n = pointCoords.Length / 2;
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embed", new DenseTensor<float>(features.ImageEmbed, new[] { 1, 256, 64, 64 })),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", new DenseTensor<float>(features.HighResFeats0, new[] { 1, 32, 256, 256 })),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", new DenseTensor<float>(features.HighResFeats1, new[] { 1, 64, 128, 128 })),
            NamedOnnxValue.CreateFromTensor("point_coords", new DenseTensor<float>(pointCoords, new[] { 1, n, 2 })),
            NamedOnnxValue.CreateFromTensor("point_labels", new DenseTensor<float>(Enumerable.Repeat(1f, n).ToArray(), new[] { 1, n })),
            NamedOnnxValue.CreateFromTensor("mask_input", new DenseTensor<float>(new float[HoldModel.MaskSize * HoldModel.MaskSize], new[] { 1, 1, 256, 256 })),
            NamedOnnxValue.CreateFromTensor("has_mask_input", new DenseTensor<float>(new float[1], new[] { 1 }))
        };
        using var results = Run(_decoder, inputs, cancellationToken);
        var masks = results.First(r => r.Name == "masks").AsTensor<float>();
        var scores = results.First(r => r.Name == "iou_predictions").AsTensor<float>();
        return new HoldLogits(masks.ToArray(), scores.ToArray(), masks.Dimensions[1]);
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(() => runOptions.Terminate = true);
        try
        {
            return session.Run(inputs, session.OutputNames, runOptions);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OnnxRuntimeException ex)
        {
            throw new HoldBackendException($"The hold model failed on the {Describe(Device)}: {ex.Message}", Device == InferenceDevice.Gpu, ex);
        }
    }

    private static string Describe(InferenceDevice device) => device == InferenceDevice.Gpu ? "GPU (DirectML)" : "CPU";

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
