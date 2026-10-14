using System.Collections.Immutable;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.Vision;

/// <summary>
/// The hold segmentation of D034 §2–§3: the pinned model verified once per session (<see cref="HoldModelVerifier"/>),
/// loaded lazily on DirectML; on a known DirectML error (<see cref="HoldBackendException.DeviceError"/>) — when the
/// session is created or a run fails — the operation is retried once on the CPU and the service stays there
/// (<see cref="DeviceChanged"/>). Unknown errors and CPU errors are not recovered. One model run at a time.
/// </summary>
public sealed class OnnxHoldSegmentationService : IHoldSegmentationService, IDisposable
{
    private readonly HoldModelVerifier _verifier;
    private readonly HoldBackendFactory _factory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IHoldModelBackend? _backend;
    private InferenceDevice _device = InferenceDevice.Gpu;

    public OnnxHoldSegmentationService(string modelFolder, ILogger<OnnxHoldSegmentationService>? logger = null)
        : this(new HoldModelVerifier(modelFolder), null, logger)
    {
    }

    /// <summary>For tests: another backend factory (a fake that fails on the GPU, etc.).</summary>
    internal OnnxHoldSegmentationService(HoldModelVerifier verifier, HoldBackendFactory? factory, ILogger? logger)
    {
        _verifier = verifier;
        _factory = factory ?? (device => new OnnxHoldModelBackend(verifier.PathOf(HoldModel.Encoder), verifier.PathOf(HoldModel.Decoder), device));
        _logger = logger ?? NullLogger.Instance;
    }

    public InferenceDevice Device => _device;

    public event EventHandler? DeviceChanged;

    public async Task<HoldModelStatus> EnsureReadyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var status = await _verifier.VerifyAsync(progress, cancellationToken).ConfigureAwait(false);
        if (!status.IsReady)
        {
            _logger.LogWarning("Hold model unavailable: {Message}", status.Message);
            return status;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_backend is null) await Task.Run(CreateBackend, cancellationToken).ConfigureAwait(false);
            return status;
        }
        catch (HoldBackendException ex)
        {
            _logger.LogError(ex, "The hold model could not be loaded.");
            return new HoldModelStatus(HoldModelState.Failed, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IHoldFrame> PrepareFrameAsync(DecodedFrame frame, CancellationToken cancellationToken = default)
    {
        var status = await EnsureReadyAsync(null, cancellationToken).ConfigureAwait(false);
        if (!status.IsReady) throw new InvalidOperationException(status.Message);

        var image = await Task.Run(() => SamImageProcessing.Preprocess(frame), cancellationToken).ConfigureAwait(false);
        var features = await RunAsync(backend => backend.Encode(image, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new Frame(this, features, frame.Width, frame.Height);
    }

    /// <summary>Runs <paramref name="work"/> on the current backend; one CPU retry after a known DirectML error.</summary>
    private async Task<T> RunAsync<T>(Func<IHoldModelBackend, T> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_backend is null) CreateBackend();
            try
            {
                return await Task.Run(() => work(_backend!), cancellationToken).ConfigureAwait(false);
            }
            catch (HoldBackendException ex) when (ex.DeviceError && _device == InferenceDevice.Gpu)
            {
                _logger.LogWarning(ex, "DirectML failed; retrying on the CPU.");
                SwitchToCpu();
                return await Task.Run(() => work(_backend!), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A backend on the current device; a known DirectML error at creation switches to the CPU.</summary>
    private void CreateBackend()
    {
        try
        {
            _backend = _factory(_device);
        }
        catch (HoldBackendException ex) when (ex.DeviceError && _device == InferenceDevice.Gpu)
        {
            _logger.LogWarning(ex, "DirectML is not available; using the CPU.");
            SwitchToCpu();
        }
    }

    private void SwitchToCpu()
    {
        _backend?.Dispose();
        _backend = null;
        _device = InferenceDevice.Cpu;
        _backend = _factory(InferenceDevice.Cpu);
        DeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _backend?.Dispose();
        _backend = null;
        _gate.Dispose();
    }

    private sealed class Frame(OnnxHoldSegmentationService owner, HoldFeatures features, int width, int height) : IHoldFrame
    {
        private HoldFeatures? _features = features;

        public int Width { get; } = width;
        public int Height { get; } = height;

        public async Task<HoldMaskSet> PredictAsync(IReadOnlyList<PointD> points, CancellationToken cancellationToken = default)
        {
            var f = _features ?? throw new ObjectDisposedException(nameof(IHoldFrame));
            if (points.Count == 0) throw new ArgumentException("At least one point is needed.", nameof(points));
            var coords = new float[points.Count * 2];
            for (var i = 0; i < points.Count; i++)
            {
                coords[2 * i] = (float)(points[i].X * HoldModel.InputSize / Width);
                coords[2 * i + 1] = (float)(points[i].Y * HoldModel.InputSize / Height);
            }

            var logits = await owner.RunAsync(backend => backend.Decode(f, coords, cancellationToken), cancellationToken).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                var plane = HoldModel.MaskSize * HoldModel.MaskSize;
                var masks = ImmutableArray.CreateBuilder<HoldMask>(logits.Count);
                for (var k = 0; k < logits.Count; k++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pixels = SamImageProcessing.Upsample(logits.Logits.AsSpan(k * plane, plane), HoldModel.MaskSize, Width, Height);
                    masks.Add(new HoldMask(Width, Height, pixels, logits.Scores[k]));
                }
                return new HoldMaskSet(masks.MoveToImmutable());
            }, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => _features = null;
    }
}
