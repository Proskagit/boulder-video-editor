using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;

namespace AiVideoEditor.Vision.People;

/// <summary>
/// The people models of D061 on ONNX Runtime: the files verified once per session (size and SHA-256 — never downloaded,
/// replaced or repaired), the sessions created lazily. The detector and the pose run on DirectML and fall back to the CPU
/// for the rest of the session when DirectML cannot be used (Follow keeps working, slower); the video segmentation needs
/// DirectML (about a second per frame on the CPU), so without it <see cref="CreateSegmenter"/> refuses with a message.
/// Calls are serialized (one analysis at a time uses the models).
/// </summary>
public sealed class OnnxPeopleVision : IPeopleVision, IDisposable
{
    private readonly HoldModelVerifier _detection;
    private readonly HoldModelVerifier _segmentation;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private PersonDetectorSessions? _detector;
    private Sam2Sessions? _sam;
    private bool _gpuFailed;

    public OnnxPeopleVision(string modelFolder, ILogger<OnnxPeopleVision>? logger = null)
    {
        _detection = new HoldModelVerifier(modelFolder, PeopleModel.DetectionFiles, PeopleModel.Id);
        _segmentation = new HoldModelVerifier(modelFolder, PeopleModel.SegmentationFiles, PeopleModel.Id);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string ModelId => PeopleModel.Id;

    public string Folder => _detection.Folder;

    public InferenceDevice DetectorDevice => _detector?.Device ?? (_gpuFailed ? InferenceDevice.Cpu : InferenceDevice.Gpu);

    public IReadOnlyList<PersonDetection> Detect(DecodedFrame frame, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var detector = Detector(cancellationToken);
            try
            {
                return detector.Detect(frame, cancellationToken);
            }
            catch (OnnxRuntimeException ex) when (detector.Device == InferenceDevice.Gpu && OnnxHoldModelBackend.IsKnownDeviceError(ex.Message))
            {
                _logger.LogWarning(ex, "People detection failed on DirectML; the CPU is used for the rest of the session.");
                _gpuFailed = true;
                detector.Dispose();
                _detector = null;
                return Detector(cancellationToken).Detect(frame, cancellationToken);
            }
        }
    }

    private PersonDetectorSessions Detector(CancellationToken cancellationToken)
    {
        if (_detector is not null) return _detector;
        Verify(_detection, cancellationToken);
        if (!_gpuFailed)
        {
            try
            {
                return _detector = new PersonDetectorSessions(_detection.PathOf(PeopleModel.Detector), _detection.PathOf(PeopleModel.Pose), InferenceDevice.Gpu);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
            {
                _logger.LogWarning(ex, "The people detector cannot use DirectML; the CPU is used.");
                _gpuFailed = true;
            }
        }
        try
        {
            return _detector = new PersonDetectorSessions(_detection.PathOf(PeopleModel.Detector), _detection.PathOf(PeopleModel.Pose), InferenceDevice.Cpu);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PeopleModelException(string.Format(Messages.People_ModelLoadFailed, ex.Message), inner: ex);
        }
    }

    public IPersonSegmenter CreateSegmenter(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_sam is null)
            {
                Verify(_segmentation, cancellationToken);
                if (_gpuFailed) throw new PeopleModelException(Messages.People_NeedsGpu, requiresGpu: true);
                try
                {
                    _sam = new Sam2Sessions(_segmentation.PathOf, InferenceDevice.Gpu);
                }
                catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
                {
                    _logger.LogWarning(ex, "The people segmentation cannot use DirectML.");
                    throw new PeopleModelException(Messages.People_NeedsGpu, requiresGpu: true, ex);
                }
                catch (InvalidDataException ex)
                {
                    throw new PeopleModelException(string.Format(Messages.People_ModelLoadFailed, ex.Message), inner: ex);
                }
            }
            return new SerializedSegmenter(new Sam2VideoSegmenter(_sam, ownsSessions: false), _gate);
        }
    }

    private static void Verify(HoldModelVerifier verifier, CancellationToken cancellationToken)
    {
        var status = verifier.VerifyAsync(null, cancellationToken).GetAwaiter().GetResult();
        if (status.IsReady) return;
        throw new PeopleModelException(status.State == HoldModelState.Missing
            ? string.Format(Messages.People_ModelMissing, PeopleModel.Id, verifier.Folder)
            : string.Format(Messages.People_ModelDamaged, PeopleModel.Id, verifier.Folder));
    }

    /// <summary>A segmenter whose steps hold the models' lock (the sessions are shared).</summary>
    private sealed class SerializedSegmenter(IPersonSegmenter inner, object gate) : IPersonSegmenter
    {
        public IReadOnlyList<SegmentedPerson> Step(DecodedFrame frame, IReadOnlyList<SegmenterPrompt> start, IReadOnlyCollection<int> stop,
            int gridWidth, int gridHeight, CancellationToken cancellationToken)
        {
            lock (gate) return inner.Step(frame, start, stop, gridWidth, gridHeight, cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _detector?.Dispose();
            _detector = null;
            _sam?.Dispose();
            _sam = null;
        }
    }
}
