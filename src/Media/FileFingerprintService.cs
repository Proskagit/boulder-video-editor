using System.Security.Cryptography;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Media;

/// <summary>
/// Identifies files for the COROS synchronisation study (Step 35.3b1): the quick stamp (size, last write time UTC) and a
/// streamed SHA-256 with the stamp read before and after, so a file changed during the read is told apart. Large videos
/// are read in 1 MiB blocks, never loaded whole; progress reports the bytes read; cancellation is honoured between blocks.
/// </summary>
public sealed class FileFingerprintService : IFileFingerprintService
{
    private const int BufferSize = 1 << 20;

    public FileStamp? Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }

    public async Task<FileFingerprint?> HashAsync(string path, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (Stamp(path) is not { } before) return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                total += read;
                progress?.Report(total);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        var after = Stamp(path) ?? new FileStamp(-1, -1);   // gone right after the read: it changed
        return new FileFingerprint(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), before, after);
    }
}
