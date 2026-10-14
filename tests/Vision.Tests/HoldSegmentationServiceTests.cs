using System.Security.Cryptography;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Vision.Tests;

/// <summary>
/// Phase 19 Step 19.4 (D034 §2–§3), without weights or a GPU: the model check (missing, wrong size, wrong SHA-256 —
/// never loaded, nothing replaced), the CPU fallback on a known DirectML error at creation or at a run (one retry, the
/// device event), no recovery from an unknown error or on the CPU, cancellation without a result, the masks of a
/// prompt at the frame's size in the model's order, the points scaled to the model input.
/// </summary>
public sealed class HoldSegmentationServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-model-" + Guid.NewGuid().ToString("N"));
    private readonly HoldModelFile[] _files;

    public HoldSegmentationServiceTests()
    {
        Directory.CreateDirectory(_folder);
        _files = new[] { Write("enc.onnx", 1000), Write("dec.onnx", 500) };
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private HoldModelFile Write(string name, int length)
    {
        var bytes = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(Path.Combine(_folder, name), bytes);
        return new HoldModelFile(name, length, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private OnnxHoldSegmentationService Service(FakeBackend.Plan plan, List<InferenceDevice>? created = null) =>
        new(new HoldModelVerifier(_folder, _files), device =>
        {
            created?.Add(device);
            return new FakeBackend(device, plan);
        }, null);

    private static DecodedFrame Frame(int w = 64, int h = 32) =>
        new(w, h, w * 4, new byte[w * h * 4], new SourceTimestamp(0, new TimeBase(1, 25)));

    [Fact]
    public async Task A_missing_file_disables_the_markup_and_names_the_file_and_folder()
    {
        File.Delete(Path.Combine(_folder, "dec.onnx"));
        using var service = Service(new FakeBackend.Plan());
        var status = await service.EnsureReadyAsync();
        Assert.Equal(HoldModelState.Missing, status.State);
        Assert.Contains("dec.onnx", status.Message);
        Assert.Contains(_folder, status.Message);
        Assert.Contains(HoldModel.Id, status.Message);                  // the expected version (D034 §2)
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareFrameAsync(Frame()));
    }

    [Fact]
    public async Task A_file_with_another_checksum_or_size_is_never_loaded_and_never_replaced()
    {
        var path = Path.Combine(_folder, "enc.onnx");
        var bytes = File.ReadAllBytes(path);
        bytes[10] ^= 0xFF;   // same size, other content
        File.WriteAllBytes(path, bytes);
        var created = new List<InferenceDevice>();
        using var service = Service(new FakeBackend.Plan(), created);
        var status = await service.EnsureReadyAsync();
        Assert.Equal(HoldModelState.Mismatch, status.State);
        Assert.Contains("damaged or another version", status.Message);
        Assert.Empty(created);                                   // no session made
        Assert.Equal(bytes, File.ReadAllBytes(path));            // the file left as it was

        File.WriteAllBytes(path, bytes.Take(10).ToArray());      // another size
        using var other = Service(new FakeBackend.Plan());
        Assert.Equal(HoldModelState.Mismatch, (await other.EnsureReadyAsync()).State);
    }

    [Fact]
    public async Task The_check_reports_progress_and_a_ready_model_loads_on_the_gpu()
    {
        var created = new List<InferenceDevice>();
        using var service = Service(new FakeBackend.Plan(), created);
        var progress = new List<double>();
        var status = await service.EnsureReadyAsync(new SyncProgress(progress.Add));
        Assert.True(status.IsReady);
        Assert.Equal(1.0, progress.Last(), 6);
        Assert.Equal(new[] { InferenceDevice.Gpu }, created);
        Assert.Equal(InferenceDevice.Gpu, service.Device);
    }

    [Fact]
    public async Task A_directml_failure_at_creation_falls_back_to_the_cpu()
    {
        var created = new List<InferenceDevice>();
        using var service = Service(new FakeBackend.Plan { FailCreateOnGpu = true }, created);
        var changed = 0;
        service.DeviceChanged += (_, _) => changed++;
        Assert.True((await service.EnsureReadyAsync()).IsReady);
        Assert.Equal(InferenceDevice.Cpu, service.Device);
        Assert.Equal(1, changed);
        using var frame = await service.PrepareFrameAsync(Frame());
        Assert.Equal(3, (await frame.PredictAsync(new[] { new PointD(10, 10) })).Masks.Length);
    }

    [Fact]
    public async Task A_directml_failure_during_a_run_is_retried_once_on_the_cpu_and_stays_there()
    {
        var plan = new FakeBackend.Plan { FailRunOnGpu = true };
        var created = new List<InferenceDevice>();
        using var service = Service(plan, created);
        var changed = 0;
        service.DeviceChanged += (_, _) => changed++;
        using var frame = await service.PrepareFrameAsync(Frame());
        Assert.Equal(InferenceDevice.Cpu, service.Device);
        Assert.Equal(new[] { InferenceDevice.Gpu, InferenceDevice.Cpu }, created);
        Assert.Equal(1, changed);
        await frame.PredictAsync(new[] { new PointD(1, 1) });
        Assert.Equal(1, changed);                                // already on the CPU
    }

    [Fact]
    public async Task An_unknown_error_or_a_cpu_error_is_not_recovered()
    {
        using var unknown = Service(new FakeBackend.Plan { UnknownError = true });
        await Assert.ThrowsAsync<InvalidDataException>(() => unknown.PrepareFrameAsync(Frame()));
        Assert.Equal(InferenceDevice.Gpu, unknown.Device);

        using var cpu = Service(new FakeBackend.Plan { FailRunOnGpu = true, FailRunOnCpu = true });
        await Assert.ThrowsAsync<HoldBackendException>(() => cpu.PrepareFrameAsync(Frame()));
    }

    [Fact]
    public async Task Cancellation_returns_no_result()
    {
        using var service = Service(new FakeBackend.Plan { Delay = TimeSpan.FromSeconds(10) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareFrameAsync(Frame(), cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Masks_come_at_the_frame_size_in_the_models_order_and_points_are_scaled_to_the_input()
    {
        var plan = new FakeBackend.Plan();
        using var service = Service(plan);
        using var frame = await service.PrepareFrameAsync(Frame(2048, 1024));
        var set = await frame.PredictAsync(new[] { new PointD(1024, 256), new PointD(0, 1024) });
        Assert.Equal(new[] { 512f, 256f, 0f, 1024f }, plan.LastPoints);   // × 1024 / 2048, × 1024 / 1024
        Assert.All(set.Masks, m => Assert.Equal((2048, 1024), (m.Width, m.Height)));
        Assert.Equal(new[] { 0.9, 0.5, 0.7 }, set.Masks.Select(m => Math.Round(m.Score, 6)));
        Assert.True(set.Masks[0].Area > 0);                       // the fake's plane 0 is positive in its centre
        Assert.Equal(0, HoldCandidateRules.Choose(set));
    }

    [Fact]
    public async Task A_relative_model_folder_is_named_in_full()
    {
        var relative = Path.Combine("aive-missing-" + Guid.NewGuid().ToString("N"), "sam2.1-large");
        var status = await new HoldModelVerifier(relative).VerifyAsync(null, CancellationToken.None);
        Assert.Equal(HoldModelState.Missing, status.State);
        Assert.Contains(Path.GetFullPath(relative), status.Message);
    }

    [Theory]
    [InlineData("Non-zero status code returned while running Conv node. ErrorCode: 887A0005 DXGI_ERROR_DEVICE_REMOVED", true)]
    [InlineData("D3D12 device hung (887A0006)", true)]
    [InlineData("Failed to allocate memory: E_OUTOFMEMORY", true)]
    [InlineData("DmlExecutionProvider: the operator failed", true)]
    [InlineData("[ONNXRuntimeError] : 2 : INVALID_ARGUMENT : Got invalid dimensions for input: point_coords", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_known_DirectML_failures_of_a_run_count_as_device_errors(string? message, bool known)
    {
        // D034 §3: the known failures retry on the CPU; an unknown one (a shape error) aborts on the device.
        Assert.Equal(known, OnnxHoldModelBackend.IsKnownDeviceError(message));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>A backend without a model: fixed logits (plane 0 positive in a centre square, planes 1–2 negative except
    /// a corner), scores 0.9 / 0.5 / 0.7; failures and delays on demand.</summary>
    private sealed class FakeBackend(InferenceDevice device, FakeBackend.Plan plan) : IHoldModelBackend
    {
        public sealed class Plan
        {
            public bool FailCreateOnGpu { get; init; }
            public bool FailRunOnGpu { get; init; }
            public bool FailRunOnCpu { get; init; }
            public bool UnknownError { get; init; }
            public TimeSpan Delay { get; init; }
            public float[]? LastPoints { get; set; }
        }

        private readonly bool _created = !(device == InferenceDevice.Gpu && plan.FailCreateOnGpu)
            ? true : throw new HoldBackendException("no DirectML", deviceError: true);

        public InferenceDevice Device { get; } = device;

        private void Fail()
        {
            if (plan.UnknownError) throw new InvalidDataException("unknown");
            if (Device == InferenceDevice.Gpu && plan.FailRunOnGpu) throw new HoldBackendException("device removed", deviceError: true);
            if (Device == InferenceDevice.Cpu && plan.FailRunOnCpu) throw new HoldBackendException("cpu failed", deviceError: false);
        }

        public HoldFeatures Encode(float[] image, CancellationToken cancellationToken)
        {
            Assert.Equal(3 * 1024 * 1024, image.Length);
            if (plan.Delay > TimeSpan.Zero) Task.Delay(plan.Delay, cancellationToken).Wait(cancellationToken);
            Fail();
            return new HoldFeatures(new float[1], new float[1], new float[1]);
        }

        public HoldLogits Decode(HoldFeatures features, float[] pointCoords, CancellationToken cancellationToken)
        {
            Fail();
            plan.LastPoints = pointCoords;
            const int n = 256;
            var logits = new float[3 * n * n];
            for (var k = 0; k < 3; k++)
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                logits[k * n * n + y * n + x] = k == 0
                    ? (Math.Abs(x - 128) < 4 && Math.Abs(y - 128) < 4 ? 5f : -5f)
                    : (x < 2 && y < 2 ? 5f : -5f);
            return new HoldLogits(logits, new[] { 0.9f, 0.5f, 0.7f }, 3);
        }

        public void Dispose() => _ = _created;
    }
}
