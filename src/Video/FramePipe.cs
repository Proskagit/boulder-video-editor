using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Video;

/// <summary>
/// The high-throughput frame transport of D059 E1: a Windows named pipe with a large buffer that one ffmpeg decoder writes
/// its raw frames into (its output URL is <see cref="ClientPath"/>) instead of its standard output. Measured on the target
/// machine (the research of 2026-11-10, §3.2): stdout carries ~410 MB/s of 4K BGRA (12.9 fps) whatever the reader does —
/// ffmpeg writes in ~32 KB blocks into the small anonymous pipe; this pipe carries 3.4–3.7 GB/s. The bytes are the same.
/// <para>
/// The server waits for the connection from the start (before ffmpeg runs), so a client that connects, writes and closes
/// quickly (a short clip) is never missed: its data stays readable after it closed, then the stream ends. One pipe per
/// ffmpeg process; the decode stream owns and disposes it.
/// </para>
/// </summary>
internal sealed class FramePipe : IDisposable
{
    /// <summary>The pipe's input buffer: two 4K BGRA frames (31.6 MB each) — ffmpeg runs ahead by about that much.</summary>
    internal const int BufferBytes = 64 << 20;

    private readonly NamedPipeServerStream _server;
    private readonly Task _connected;
    private bool _disposed;

    private FramePipe(string name, NamedPipeServerStream server)
    {
        Name = name;
        _server = server;
        _connected = server.WaitForConnectionAsync();
        // Disposing before ffmpeg connected ends this wait with an exception nobody else awaits.
        _connected.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Pipes created and not yet disposed (diagnostics/tests).</summary>
    internal static int Live => Volatile.Read(ref _live);
    private static int _live;

    public string Name { get; }

    /// <summary>What ffmpeg opens as its output.</summary>
    public string ClientPath => @"\\.\pipe\" + Name;

    /// <summary>A new pipe, or null where named pipes can't carry ffmpeg's output (not Windows) or one can't be created —
    /// the decoder then uses its standard output (the same frames, slower).</summary>
    public static FramePipe? TryCreate(ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var name = "aive-frames-" + Guid.NewGuid().ToString("N");
        try
        {
            var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                BufferBytes, 4096);
            Interlocked.Increment(ref _live);
            return new FramePipe(name, server);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger.LogWarning(ex, "A frame pipe could not be created; the decoder uses its standard output.");
            return null;
        }
    }

    /// <summary>The connected pipe to read from. Waits for ffmpeg to open it; when ffmpeg ends without opening it
    /// (<paramref name="processExit"/> completes first) → <see cref="IOException"/>. Cancellation propagates.</summary>
    public async Task<Stream> ConnectedAsync(Task processExit, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected.IsCompleted)
        {
            var cancelled = Task.Delay(Timeout.Infinite, ct);
            var first = await Task.WhenAny(_connected, processExit, cancelled);
            if (first == cancelled) ct.ThrowIfCancellationRequested();
            // The process may end right after it opened the pipe (the connection completes on another thread).
            if (first != _connected && await Task.WhenAny(_connected, Task.Delay(500, ct)) != _connected)
                throw new IOException("ffmpeg ended without opening its frame pipe.");
        }
        await _connected;
        return _server;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Decrement(ref _live);
        _server.Dispose();
    }
}
