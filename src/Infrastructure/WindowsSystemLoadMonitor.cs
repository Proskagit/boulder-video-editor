using System.Runtime.InteropServices;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Infrastructure;

/// <summary>
/// D078 amendment 1: the computer's load on Windows without a new dependency — the processor from <c>GetSystemTimes</c> (busy =
/// kernel + user − idle, between two samples), the graphics card from the performance counters «GPU Engine» (as Task Manager:
/// per engine type the sum over its instances, the busiest type; any vendor). Anything that fails gives null, never an exception.
/// </summary>
public sealed class WindowsSystemLoadMonitor : ISystemLoadMonitor, IDisposable
{
    private readonly object _gate = new();
    private (ulong Idle, ulong Kernel, ulong User)? _previous;
    private IntPtr _query;
    private IntPtr _gpuCounter;
    private bool _gpuUnavailable;

    public SystemLoad Sample()
    {
        lock (_gate) return new SystemLoad(Cpu(), Gpu());
    }

    private double? Cpu()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
            var now = (Idle: idle, Kernel: kernel, User: user);
            var before = _previous;
            _previous = now;
            if (before is not { } b) return null;
            var total = (now.Kernel - b.Kernel) + (now.User - b.User);   // kernel time includes the idle time
            var idleDelta = now.Idle - b.Idle;
            return total == 0 ? null : Math.Clamp(100.0 * (total - idleDelta) / total, 0, 100);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private double? Gpu()
    {
        if (_gpuUnavailable) return null;
        try
        {
            if (_query == IntPtr.Zero)
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0 ||
                    PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
                {
                    _gpuUnavailable = true;
                    return null;
                }
                PdhCollectQueryData(_query);
                return null;   // a rate counter needs two samples
            }
            if (PdhCollectQueryData(_query) != 0) return null;
            uint size = 0;
            var status = PdhGetFormattedCounterArrayW(_gpuCounter, PdhFmtDouble, ref size, out var count, IntPtr.Zero);
            if (status != PdhMoreData || size == 0) return count == 0 ? 0 : null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(_gpuCounter, PdhFmtDouble, ref size, out count, buffer) != 0) return null;
                var byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var itemSize = Marshal.SizeOf<PdhFmtCounterValueItemDouble>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItemDouble>(buffer + i * itemSize);
                    var name = Marshal.PtrToStringUni(item.Name) ?? "";
                    var at = name.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                    var type = at >= 0 ? name[(at + 8)..] : name;
                    if (item.Value.CStatus is 0 or 1) byType[type] = byType.GetValueOrDefault(type) + item.Value.Value;
                }
                return byType.Count == 0 ? 0 : Math.Clamp(byType.Values.Max(), 0, 100);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _gpuUnavailable = true;
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueDouble
    {
        public uint CStatus;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItemDouble
    {
        public IntPtr Name;
        public PdhFmtCounterValueDouble Value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
