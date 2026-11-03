using System.Security.Cryptography;
using AiVideoEditor.Media;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>Step 35.3b1: the streamed SHA-256 equals the one-shot hash, reports its progress, honours cancellation, and
/// carries the file's stamp before and after the read.</summary>
public sealed class FileFingerprintServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-fingerprint", Guid.NewGuid().ToString("N"));
    private readonly FileFingerprintService _service = new();

    public FileFingerprintServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string File(string name, int size)
    {
        var path = Path.Combine(_folder, name);
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class Recorder : IProgress<long>
    {
        public List<long> Values { get; } = new();
        public void Report(long value) => Values.Add(value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(20 * 1024 * 1024 + 17)]   // several blocks and a tail
    public async Task The_streamed_hash_equals_the_one_shot_hash(int size)
    {
        var path = File($"f{size}.bin", size);
        var progress = new Recorder();

        var print = await _service.HashAsync(path, progress);

        Assert.NotNull(print);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await System.IO.File.ReadAllBytesAsync(path))).ToLowerInvariant(), print!.Sha256);
        Assert.Equal(size, print.Before.SizeBytes);
        Assert.False(print.ChangedDuringRead);
        if (size > 0) Assert.Equal(size, progress.Values[^1]);
        Assert.Equal(progress.Values.OrderBy(v => v), progress.Values);
    }

    [Fact]
    public async Task A_missing_file_has_no_stamp_and_no_hash()
    {
        var path = Path.Combine(_folder, "none.bin");
        Assert.Null(_service.Stamp(path));
        Assert.Null(await _service.HashAsync(path));
    }

    [Fact]
    public async Task Cancellation_stops_the_read()
    {
        var path = File("big.bin", 8 * 1024 * 1024);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.HashAsync(path, null, cts.Token));
    }

    [Fact]
    public async Task The_stamp_is_the_size_and_the_utc_write_time()
    {
        var path = File("s.bin", 10);
        var when = new DateTime(2026, 9, 27, 7, 30, 26, DateTimeKind.Utc);
        System.IO.File.SetLastWriteTimeUtc(path, when);
        Assert.Equal(new AiVideoEditor.Core.Interfaces.FileStamp(10, when.Ticks), _service.Stamp(path));
        var print = await _service.HashAsync(path);
        Assert.Equal(print!.Before, print.After);
    }
}
