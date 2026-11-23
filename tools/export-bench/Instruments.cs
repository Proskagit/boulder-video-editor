using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.ExportBench;

/// <summary>Times every rasterizer of the export (thread safe: the pipeline may run several at once).</summary>
internal sealed class RasterTiming
{
    private long _ticks;
    private int _created;
    public double Ms => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _ticks)).TotalMilliseconds;
    public int Created => Volatile.Read(ref _created);

    public Func<ICompositionRasterizer> Wrap(Func<ICompositionRasterizer> factory) => () =>
    {
        Interlocked.Increment(ref _created);
        return new Timed(factory(), this);
    };

    private sealed class Timed(ICompositionRasterizer inner, RasterTiming owner) : ICompositionRasterizer
    {
        public void Render(CompositionDrawPlan plan, Span<byte> target, int stride) => Render(plan, target, stride, CancellationToken.None);

        public void Render(CompositionDrawPlan plan, Span<byte> target, int stride, CancellationToken ct)
        {
            var t = Stopwatch.GetTimestamp();
            inner.Render(plan, target, stride, ct);
            Interlocked.Add(ref owner._ticks, Stopwatch.GetTimestamp() - t);
        }

        public void Dispose() => inner.Dispose();
    }
}

/// <summary>Counts the source frames every decoder delivers and the time spent waiting for them.</summary>
internal sealed class TimedDecoder(IVideoDecoder inner) : IVideoDecoder
{
    private long _ticks;
    private long _frames;
    public double Ms => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _ticks)).TotalMilliseconds;
    public long Frames => Interlocked.Read(ref _frames);

    public async Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default) =>
        new Stream(await inner.OpenAsync(Experiment(request), ct), this);

    /// <summary>FU-EX.2 research only (never the app's behaviour): <c>AIVE_BENCH_HWDEC=1</c> decodes the export's sources with
    /// <c>-hwaccel auto</c>; <c>AIVE_BENCH_DECODE_MAX=WxH</c> has the decoder scale the sources down to that bound (the
    /// painter then draws the smaller frame onto the same rectangle).</summary>
    private static VideoDecodeRequest Experiment(VideoDecodeRequest request)
    {
        if (Environment.GetEnvironmentVariable("AIVE_BENCH_HWDEC") == "1") request = request with { Hardware = HardwareDecoding.Auto };
        if (Environment.GetEnvironmentVariable("AIVE_BENCH_DECODE_MAX") is { Length: > 0 } max && max.Split('x') is [var w, var h])
            request = request with { MaxWidth = int.Parse(w), MaxHeight = int.Parse(h) };
        return request;
    }

    private sealed class Stream(IVideoFrameStream inner, TimedDecoder owner) : IVideoFrameStream
    {
        public async ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default)
        {
            var t = Stopwatch.GetTimestamp();
            var frame = await inner.ReadFrameAsync(ct);
            Interlocked.Add(ref owner._ticks, Stopwatch.GetTimestamp() - t);
            if (frame is not null) Interlocked.Increment(ref owner._frames);
            return frame;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>Times the writes to the encoder; optionally hashes every canvas (SHA-256 over all of them, in order) and tees them
/// into a lossless reference file (yuv420p through the export's own BT.709 conversion, x264 QP 0) for the quality metrics.</summary>
internal sealed class TimedEncoder(IExportEncoder inner, bool hashCanvases, string? referencePath, string ffmpeg) : IExportEncoder
{
    internal bool HashCanvases => hashCanvases;
    internal string? ReferencePath => referencePath;
    internal string Ffmpeg => ffmpeg;
    private long _frameTicks;
    public double FrameMs => Stopwatch.GetElapsedTime(0, _frameTicks).TotalMilliseconds;
    public string? CanvasHash { get; private set; }

    public async Task<IExportEncoding> StartAsync(ExportOutput output, ExportEncoding encoding, string destinationPath, CancellationToken ct = default) =>
        new Encoding(await inner.StartAsync(output, encoding, destinationPath, ct), this, output);

    private sealed class Encoding : IExportEncoding
    {
        private readonly IExportEncoding _inner;
        private readonly TimedEncoder _owner;
        private readonly IncrementalHash? _hash;
        private readonly Process? _reference;

        public Encoding(IExportEncoding inner, TimedEncoder owner, ExportOutput output)
        {
            _inner = inner;
            _owner = owner;
            if (owner.HashCanvases) _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (owner.ReferencePath is { } path)
            {
                var psi = new ProcessStartInfo(owner.Ffmpeg) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = false, CreateNoWindow = true };
                foreach (var a in new[]
                         {
                             "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "bgra",
                             "-s", $"{output.Size.Width}x{output.Size.Height}",
                             "-framerate", $"{output.FrameRate.Numerator}/{output.FrameRate.Denominator}", "-i", "pipe:0",
                             "-vf", "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p," +
                                    "setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
                             "-c:v", "libx264", "-preset", "ultrafast", "-qp", "0", "-pix_fmt", "yuv420p", path
                         })
                    psi.ArgumentList.Add(a);
                _reference = Process.Start(psi);
            }
        }

#if !BASELINE
        public string EncoderDescription => _inner.EncoderDescription;
        public bool IsHardware => _inner.IsHardware;
        public string? FallbackReason => _inner.FallbackReason;
#endif

        public ValueTask WriteAudioAsync(ReadOnlyMemory<float> interleaved, CancellationToken ct = default) => _inner.WriteAudioAsync(interleaved, ct);

        public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> bgra, int stride, CancellationToken ct = default)
        {
            _hash?.AppendData(bgra.Span);
            if (_reference is not null) await _reference.StandardInput.BaseStream.WriteAsync(bgra, ct);
            var t = Stopwatch.GetTimestamp();
            await _inner.WriteFrameAsync(bgra, stride, ct);
            _owner._frameTicks += Stopwatch.GetTimestamp() - t;
        }

        public async Task CompleteAsync(CancellationToken ct = default)
        {
            await _inner.CompleteAsync(ct);
            if (_hash is not null) _owner.CanvasHash = Convert.ToHexString(_hash.GetHashAndReset());
            if (_reference is not null)
            {
                _reference.StandardInput.Close();
                await _reference.WaitForExitAsync(ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            _hash?.Dispose();
            if (_reference is { HasExited: false }) { try { _reference.Kill(true); } catch (InvalidOperationException) { } }
            _reference?.Dispose();
        }
    }
}

/// <summary>Samples the process (CPU time, working set, private bytes), the whole system's CPU and, through
/// <c>nvidia-smi</c>, the GPU (3D / encoder / decoder load and VRAM) while a run lasts.</summary>
internal sealed class Sampler : IDisposable
{
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private readonly Process _me = Process.GetCurrentProcess();
    private readonly Thread _thread;
    private readonly Process? _smi;
    private readonly List<double[]> _gpu = new();
    private readonly double _vramStart;
    private volatile bool _stop;
    private long _idle0, _total0;
    private TimeSpan _cpu0;
    private long _peakPrivate;
    private long _peakWorkingSet;
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public Sampler()
    {
        _vramStart = QueryVram();
        GetSystemTimes(out _idle0, out var k, out var u);
        _total0 = k + u;
        _cpu0 = _me.TotalProcessorTime;
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu,utilization.encoder,utilization.decoder,memory.used --format=csv,noheader,nounits -lms 250")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            _smi = Process.Start(psi);
            if (_smi is not null)
                _ = Task.Run(async () =>
                {
                    while (await _smi.StandardOutput.ReadLineAsync() is { } line)
                    {
                        var parts = line.Split(',').Select(s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
                        if (parts.Length == 4 && parts.All(double.IsFinite)) lock (_gpu) _gpu.Add(parts);
                    }
                });
        }
        catch (System.ComponentModel.Win32Exception) { _smi = null; }
        _thread = new Thread(Loop) { IsBackground = true, Name = "bench-sampler" };
        _thread.Start();
    }

    private static double QueryVram()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return double.Parse(text.Trim().Split('\n')[0], CultureInfo.InvariantCulture);
        }
        catch (Exception) { return double.NaN; }
    }

    private void Loop()
    {
        while (!_stop)
        {
            _me.Refresh();
            _peakPrivate = Math.Max(_peakPrivate, _me.PrivateMemorySize64);
            _peakWorkingSet = Math.Max(_peakWorkingSet, _me.WorkingSet64);
            Thread.Sleep(100);
        }
    }

    public Dictionary<string, object> Stop()
    {
        _stop = true;
        _thread.Join();
        try { _smi?.Kill(); } catch (InvalidOperationException) { }
        var wall = _watch.Elapsed.TotalMilliseconds;
        GetSystemTimes(out var idle1, out var k1, out var u1);
        _me.Refresh();
        var busy = 1.0 - (double)(idle1 - _idle0) / (k1 + u1 - _total0);
        double[][] gpu;
        lock (_gpu) gpu = _gpu.ToArray();
        double Avg(int i) => gpu.Length == 0 ? double.NaN : gpu.Average(g => g[i]);
        double Max(int i) => gpu.Length == 0 ? double.NaN : gpu.Max(g => g[i]);
        return new Dictionary<string, object>
        {
            ["procCpuCores"] = (_me.TotalProcessorTime - _cpu0).TotalMilliseconds / wall,
            ["sysCpuCores"] = busy * Environment.ProcessorCount,
            ["peakWorkingSetMB"] = Math.Max(_peakWorkingSet, _me.PeakWorkingSet64) / 1048576.0,
            ["peakPrivateMB"] = _peakPrivate / 1048576.0,
            ["gpuUtilAvg"] = Avg(0), ["gpuUtilMax"] = Max(0),
            ["encUtilAvg"] = Avg(1), ["encUtilMax"] = Max(1),
            ["decUtilAvg"] = Avg(2),
            ["vramDeltaMaxMB"] = Max(3) - _vramStart,
        };
    }

    public void Dispose()
    {
        _stop = true;
        _smi?.Dispose();
    }
}
